using L = NimCap.Screenshot.Localization.UiText;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Views;
using System.Windows;

namespace NimCap.Screenshot.Services;

public sealed class CaptureCoordinator
{
    private ICaptureService _captureService;
    private readonly SettingsService? _settings;
    private readonly List<SelectionWindow> _windows = [];
    private static bool _selectingScrollingRegion;
    private static CaptureCoordinator? _preparingCoordinator;
    private CancellationTokenSource? _pendingCapture;
    private bool _disposed;
    private sealed record PreparedCapture(CapturedScreen Screen, IReadOnlyList<WindowCandidate> Candidates);

    public event EventHandler<CaptureResult>? CaptureCompleted;
    public event EventHandler? CaptureCanceled;
    public event Action<string, Window>? SettingsRequested;
    public bool IsActive => _windows.Count > 0 || _selectingScrollingRegion || _preparingCoordinator is not null;

    public CaptureCoordinator(ICaptureService? captureService = null, SettingsService? settings = null)
    {
        _captureService = captureService ?? new ScreenCaptureService();
        _settings = settings;
    }

    public void SetCaptureService(ICaptureService captureService)
    {
        ArgumentNullException.ThrowIfNull(captureService);
        if (IsActive) throw new InvalidOperationException("Cannot change capture backends during a capture.");
        _captureService = captureService;
    }

    // Retained for synchronous callers/tests. Production entry points use BeginAsync.
    public void Begin(CaptureMode mode, nint targetWindow = default, bool scrollingSelection = false)
    {
        if (_windows.Count > 0 || _pendingCapture is not null || _disposed) return;
        Present(Prepare(_captureService, mode, targetWindow, CancellationToken.None), mode, scrollingSelection);
    }

    public async Task BeginAsync(CaptureMode mode, nint targetWindow = default, bool scrollingSelection = false,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task>? beforeCapture = null)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.VerifyAccess();
        if (_windows.Count > 0 || _preparingCoordinator is not null || _disposed ||
            System.Windows.Application.Current.Windows.OfType<SelectionWindow>().Any()) return;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pendingCapture = pending;
        _preparingCoordinator = this; // Occupy before hiding windows or the first await.
        var backend = _captureService;
        var escape = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
            { Interval = TimeSpan.FromMilliseconds(30) };
        escape.Tick += (_, _) =>
        {
            if ((NimCap.Screenshot.Native.NativeMethods.GetAsyncKeyState(0x1B) & 0x8000) != 0) pending.Cancel();
        };
        try
        {
            escape.Start();
            if (beforeCapture is not null) await beforeCapture(pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            var prepared = await Task.Run(() => Prepare(backend, mode, targetWindow, pending.Token), pending.Token);
            await dispatcher.InvokeAsync(() =>
            {
                pending.Token.ThrowIfCancellationRequested();
                if (!_disposed) Present(prepared, mode, scrollingSelection);
            });
        }
        finally
        {
            void ReleaseReservation()
            {
                escape.Stop();
                _pendingCapture = null;
                if (ReferenceEquals(_preparingCoordinator, this)) _preparingCoordinator = null;
            }
            if (dispatcher.CheckAccess()) ReleaseReservation();
            else await dispatcher.InvokeAsync(ReleaseReservation);
        }
    }

    public void CancelPendingCapture() => _pendingCapture?.Cancel();

    public void Shutdown()
    {
        _disposed = true;
        CancelPendingCapture();
        CloseAll();
    }

    private static PreparedCapture Prepare(ICaptureService backend, CaptureMode mode, nint target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var screen = mode == CaptureMode.ActiveWindow ? backend.CaptureWindow(target, token)
            : backend.CaptureVirtualScreen(token);
        if (screen is null) throw new InvalidOperationException(L.Get("CouldNotGetTheActiveWindow"));
        if (!screen.Image.IsFrozen) { var image = screen.Image.Clone(); image.Freeze(); screen = screen with { Image = image }; }
        token.ThrowIfCancellationRequested();
        var candidates = mode == CaptureMode.Region ? backend.EnumerateTopLevelWindows() : [];
        token.ThrowIfCancellationRequested();
        return new PreparedCapture(screen, candidates);
    }

    private void Present(PreparedCapture capture, CaptureMode mode, bool scrollingSelection)
    {
        if (mode != CaptureMode.Region) { CompleteDirectCapture(capture.Screen, mode); return; }
        var screens = new[] { capture.Screen };
        var candidates = capture.Candidates;
        foreach (var screen in screens)
        {
            var window = new SelectionWindow(screen, candidates, _captureService, scrollingSelection, settings: _settings);
            window.PrecisionPreferencesChanged += OnPrecisionPreferencesChanged;
            window.CaptureAccepted += OnAccepted;
            window.SettingsRequested += category => SettingsRequested?.Invoke(category, window);
            window.CaptureCanceled += OnCanceled;
            window.Closed += OnCanceled;
            _windows.Add(window);
        }
        foreach (var window in _windows) window.Show();
        if (NimCap.Screenshot.Native.NativeMethods.GetCursorPos(out var cursor))
        {
            var index = screens.ToList().FindIndex(screen => screen.Bounds.Contains(cursor.X, cursor.Y));
            if (index >= 0) { _windows[index].Activate(); _windows[index].Focus(); }
        }
    }

    private void CompleteDirectCapture(CapturedScreen capture, CaptureMode mode)
    {
        CaptureCompleted?.Invoke(this, new CaptureResult(capture.Image, capture.Bounds,
            new System.Drawing.Rectangle(0, 0, capture.Bounds.Width, capture.Bounds.Height)) { Mode = mode });
    }

    private void OnPrecisionPreferencesChanged(object? sender, EventArgs e)
    {
        foreach (var window in _windows) window.RefreshPrecisionPreferences();
    }

    private void OnAccepted(object? sender, CaptureResult result)
    {
        CloseAll();
        CaptureCompleted?.Invoke(this, result);
    }

    private void OnCanceled(object? sender, EventArgs e)
    {
        CloseAll();
        CaptureCanceled?.Invoke(this, EventArgs.Empty);
    }

    // Use the ordinary multi-monitor region overlay, keeping our own windows out of the screenshot.
    public static async Task<CaptureResult?> SelectScrollingRegionAsync()
    {
        var app = System.Windows.Application.Current;
        if (_selectingScrollingRegion || _preparingCoordinator is not null || app.Windows.OfType<SelectionWindow>().Any()) return null;
        if (app.Windows.OfType<ScrollCaptureWindow>().Any(x => x.IsCapturing) ||
            app.Windows.OfType<RecordingWindow>().Any(x => x.IsRecording))
            throw new InvalidOperationException(L.Get("StopTheCurrentScrollingCaptureOrRecording"));
        _selectingScrollingRegion = true;
        var hidden = app.Windows.OfType<System.Windows.Window>().Where(x => x.IsVisible).ToArray();
        var settings = new SettingsService();
        var coordinator = new CaptureCoordinator();
        try
        {
            settings.Load();
            coordinator = new CaptureCoordinator(new ResilientCaptureService(settings.Current.PreferWgcCapture, false), settings);
            var completion = new TaskCompletionSource<CaptureResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.CaptureCompleted += (_, result) => completion.TrySetResult(result);
            coordinator.CaptureCanceled += (_, _) => completion.TrySetResult(null);
            foreach (var window in hidden) window.Hide();
            await Task.Delay(200);
            await coordinator.BeginAsync(CaptureMode.Region, scrollingSelection: true);
            if (coordinator._windows.Count == 0 && !completion.Task.IsCompleted) return null;
            return await completion.Task;
        }
        finally
        {
            coordinator.CloseAll();
            _selectingScrollingRegion = false;
            foreach (var window in hidden.Where(x => x.IsLoaded)) window.Show();
        }
    }

    private void CloseAll()
    {
        var windows = _windows.ToArray();
        _windows.Clear();
        foreach (var window in windows)
        {
            window.CaptureAccepted -= OnAccepted;
            window.CaptureCanceled -= OnCanceled;
            window.Closed -= OnCanceled;
            window.PrecisionPreferencesChanged -= OnPrecisionPreferencesChanged;
            window.Close();
        }
    }
}

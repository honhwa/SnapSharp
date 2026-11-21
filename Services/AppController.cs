using L = NimCap.Screenshot.Localization.UiText;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Views;
using NimCap.Screenshot.Native;
using NimCap.Screenshot.Plugins;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace NimCap.Screenshot.Services;

public sealed class AppController : IDisposable
{
    private readonly TrayService _tray;
    private readonly GlobalHotkeyHost _hotkey;
    private readonly CaptureCoordinator _capture;
    private readonly SettingsService _settings;
    private readonly SkiaImageService _images;
    private readonly HistoryService _history;
    private readonly IClipboardService _clipboard;
    private readonly LocalRecognitionService _captureRecognition = new();
    private readonly PluginCatalog _plugins;
    private readonly StartupRegistrationService _startup = new();
    private readonly List<Window> _openWindows = [];
    private readonly List<PinWindow> _pins = [];
    private readonly Stack<(PinContent Content, string? Group)> _closedPins = [];
    private readonly PinWorkspace _pinWorkspace = new();
    private CancellationTokenSource? _delayedScreenshot;
    private SettingsWindow? _settingsWindow;
    private ScreenWordService? _screenWords;
    private RecordingWindow? _recordingWindow;
    private readonly AppUpdateSession _updates = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private bool _captureBackendRefreshPending;
    private long _captureRequestId;

    public AppController()
    {
        _settings = new SettingsService();
        _settings.Load();
        LocalizationService.Apply(_settings.Current.InterfaceLanguage);
        _tray = new TrayService();
        _hotkey = new GlobalHotkeyHost();
        try
        {
            var licenseFailures = ThirdPartyLicenseService.BackfillManagedModelLicenses();
            if (licenseFailures.Count > 0)
            {
                Directory.CreateDirectory(AppPaths.DiagnosticsRoot);
                File.WriteAllLines(Path.Combine(AppPaths.DiagnosticsRoot, "license-backfill.txt"), licenseFailures);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not backfill model licenses or record failures ({0}).", error.GetType().Name);
        }
        _plugins = new PluginCatalog(new PluginLoadOptions(AppPaths.PluginsRoot,
            _settings.Current.EnableExternalPlugins, _settings.Current.RequireSignedPlugins));
        _plugins.DiscoverExternalPlugins();
        _plugins.RegisterBuiltIn(new OfflineRecognitionPlugin(() => _settings.Current));
        _plugins.RegisterBuiltIn(new OfflineRecognitionPlugin(() => _settings.Current, true));
        RecognitionUi.OpenRequested = OpenRecognition;
        _capture = new CaptureCoordinator(new ResilientCaptureService(
            _settings.Current.PreferWgcCapture, _settings.Current.IncludeCursor), _settings);
        _images = new SkiaImageService();
        _history = new HistoryService(_images, _settings);
        _clipboard = new ClipboardService();
    }

    public void Start()
    {
        _screenWords = new ScreenWordService(() => _settings.Current, () => _capture.IsActive || _settingsWindow is not null || _openWindows.OfType<ScrollCaptureWindow>().Any(x => x.IsCapturing) || Application.Current.Windows.OfType<Window>().Any(x => x.IsActive));
        _tray.ActionRequested += ExecuteAction;
        _screenWords.Failed += error => { _tray.SetScreenWordsChecked(false); _tray.ShowMessage(L.Get("ScreenTextCaptureStopped"), error, System.Windows.Forms.ToolTipIcon.Warning); };
        _hotkey.ScreenRulerRequested += (_, _) => ShowScreenRuler();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _hotkey.PinActionRequested += ExecuteAction;
        _tray.ExitRequested += async (_, _) => { if (_recordingWindow is null || await _recordingWindow.StopAndWait()) Application.Current.Shutdown(); };
        _hotkey.CaptureRequested += RequestCapture;
        _capture.CaptureCompleted += OnCaptureCompleted;
        _capture.SettingsRequested += (category, owner) => ShowSettings(category, owner);
        _tray.UpdateHotkeyLabels(_settings.Current);
        if (_plugins.Failures.Count > 0)
            _tray.ShowMessage(L.Get("PluginLoading"), L.Format("0PluginsCouldNotBeLoaded", _plugins.Failures.Count),
                System.Windows.Forms.ToolTipIcon.Warning);
        Application.Current.Dispatcher.BeginInvoke(_tray.Show,
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try
        {
            _hotkey.Register(_settings.Current);
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.WriteAllText(Path.Combine(AppPaths.Root, "startup-error.log"), ex.ToString());
            }
            catch { }
            _tray.ShowMessage(L.Get("ShortcutRegistrationFailed"), ex.Message, System.Windows.Forms.ToolTipIcon.Warning);
        }
        _ = CheckStartupUpdate();
    }

    private void ExecuteAction(GlobalAction action)
    {
        if (_disposed) return;
        switch (action)
        {
            case GlobalAction.Region: RequestCapture(CaptureMode.Region); break;
            case GlobalAction.FullScreen: RequestCapture(CaptureMode.FullScreen); break;
            case GlobalAction.ActiveWindow: RequestCapture(CaptureMode.ActiveWindow); break;
            case GlobalAction.ClipboardPin: CreatePin(PinCommand.Clipboard); break;
            case GlobalAction.TogglePins: TogglePins(); break;
            case GlobalAction.RestorePin: RestorePin(); break;
            case GlobalAction.RestoreInteraction: RestorePinInteraction(); break;
            case GlobalAction.ClosePins: CreatePin(PinCommand.CloseAll); break;
            case GlobalAction.ScreenRuler: ShowScreenRuler(); break;
            case GlobalAction.ScreenWords:
                var enabled = !(_screenWords?.IsEnabled ?? false);
                _screenWords?.SetEnabled(enabled); _tray.SetScreenWordsChecked(enabled);
                if (enabled) _tray.ShowMessage(L.Get("ScreenTextCapture"), L.Get("ScreenWordsEnabledHint"));
                break;
            case GlobalAction.RecognizeClipboard:
                try
                {
                    var image = _clipboard.GetImage() ?? throw new InvalidOperationException(L.Get("NoReadableImageInTheClipboardCopy"));
                    OnnxImageRuntime.ValidateImage(image); OpenRecognitionCore(image, null, "OCR", false);
                }
                catch (Exception ex) { _tray.ShowMessage(L.Get("RecognizeClipboardImage"), ex.GetBaseException().Message); }
                break;
            case GlobalAction.Delay3: ScheduleDelayedCapture(CaptureMode.Region, 3); break;
            case GlobalAction.Delay5: ScheduleDelayedCapture(CaptureMode.Region, 5); break;
            case GlobalAction.Delay10: ScheduleDelayedCapture(CaptureMode.Region, 10); break;
            case GlobalAction.CancelDelay: CancelDelayedScreenshot(showMessage: true); break;
            case GlobalAction.History: ShowHistory(); break;
            case GlobalAction.ScrollingStart: RequestCapture(CaptureMode.Region, true); break;
            case GlobalAction.ScrollingStop: foreach (var window in _openWindows.OfType<ScrollCaptureWindow>()) window.StopCapture(); break;
            case GlobalAction.OpenScrollProject: OpenScrollProject(); break;
            case GlobalAction.RecordingRegion: RequestCapture(CaptureMode.Region); break;
            case GlobalAction.RecordingMonitor: OpenRecording(System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds); break;
            case GlobalAction.RecordingPause: _recordingWindow?.PauseRecording(); break;
            case GlobalAction.RecordingStop: _recordingWindow?.StopRecording(); break;
        }
    }

    private async Task CheckStartupUpdate()
    {
        await _updates.CheckOnStartup(_lifetime.Token);
        if (_disposed) return;
        if (_updates.Release is { } release && release.IsNewerThan(typeof(AppController).Assembly.GetName().Version ?? new Version(0, 0)))
        {
            if (MessageBox.Show(L.Format("AppUpdateStartupPrompt", release.Tag), L.Get("AppUpdateTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes) ShowSettings("Info");
        }
        else _tray.ShowMessage(L.Get("NimCapAppTitle"), _updates.StatusText);
    }

    private void RequestCapture(object? sender, CaptureModeEventArgs e) => RequestCapture(e.Mode);

    private async void RequestCapture(CaptureMode mode, bool scrolling = false)
    {
        if (_disposed) return;
        if (!Application.Current.Dispatcher.CheckAccess() ||
            SynchronizationContext.Current is not System.Windows.Threading.DispatcherSynchronizationContext)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(() => RequestCapture(mode, scrolling));
            return;
        }
        if (_recordingWindow?.IsRecording == true) { _tray.ShowMessage(L.Get("Recording"), L.Get("RecordingOrSavingIsInProgressStop")); return; }
        if (_openWindows.OfType<ScrollCaptureWindow>().Any(x => x.IsCapturing)) { _tray.ShowMessage(L.Get("ScrollingCapture"), L.Get("ScrollingCaptureIsInProgressPressEsc")); return; }
        if (_settingsWindow is not null) { _tray.ShowMessage(L.Get("Settings"), L.Get("CloseSettingsBeforeStartingACapture")); return; }
        CancelDelayedScreenshot(showMessage: false);
        if (_capture.IsActive) return;
        ++_captureRequestId;
        try
        {
            if (_captureBackendRefreshPending)
            {
                _capture.SetCaptureService(new ResilientCaptureService(_settings.Current.PreferWgcCapture, _settings.Current.IncludeCursor));
                _captureBackendRefreshPending = false;
            }
            if (scrolling)
            {
                var selection = await CaptureCoordinator.SelectScrollingRegionAsync();
                if (selection is not null) OpenScrolling(selection);
                return;
            }

            var targetWindow = mode == CaptureMode.ActiveWindow ? GetCaptureTargetWindow() : nint.Zero;
            var hiddenWindows = Application.Current.Windows.Cast<Window>()
                .Where(window => window.IsVisible && window is not SelectionWindow)
                .ToArray();

            try
            {
                await _capture.BeginAsync(mode, targetWindow, beforeCapture: async token =>
                {
                    foreach (var window in hiddenWindows) window.Hide();
                    if (hiddenWindows.Length > 0) await Task.Delay(200, token);
                });
            }
            finally
            {
                if (!_disposed) foreach (var window in hiddenWindows.Where(window => window.IsLoaded)) window.Show();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.WriteAllText(Path.Combine(AppPaths.Root, "capture-error.log"), ex.ToString());
            }
            catch { }
            _tray.ShowMessage(L.Get("CouldNotStartCapture"), ex.Message, System.Windows.Forms.ToolTipIcon.Error);
        }
    }

    private nint GetCaptureTargetWindow()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (foreground != nint.Zero && processId != (uint)Environment.ProcessId) return foreground;
        return new ScreenCaptureService().EnumerateTopLevelWindows().FirstOrDefault()?.Handle ?? nint.Zero;
    }

    private async void ScheduleDelayedCapture(CaptureMode mode, int seconds)
    {
        CancelDelayedScreenshot(showMessage: false);
        var cancellation = new CancellationTokenSource();
        _delayedScreenshot = cancellation;
        _tray.SetDelayedScreenshotPending(true);
        _tray.ShowMessage(L.Get("DelayedCapture"), L.Format("CaptureStartsIn0SecondsCancelFrom", seconds));

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellation.Token);
            if (_delayedScreenshot == cancellation)
            {
                _delayedScreenshot = null;
                _tray.SetDelayedScreenshotPending(false);
                RequestCapture(mode);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    public void HandleLaunchArguments(IReadOnlyList<string> args, bool fromSecondaryInstance)
    {
        if (!LaunchCommand.TryParse(args, out var command, out var error))
        {
            _tray.ShowMessage(L.Get("InvalidCommandLineArguments"), error, System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }

        var mode = command.Mode;
        if (mode is null && fromSecondaryInstance) mode = CaptureMode.Region;
        if (mode is null) return;
        if (command.DelaySeconds > 0) ScheduleDelayedCapture(mode.Value, command.DelaySeconds);
        else RequestCapture(mode.Value);
    }

    private void CancelDelayedScreenshot(object? sender, EventArgs e) => CancelDelayedScreenshot(showMessage: true);

    private void CancelDelayedScreenshot(bool showMessage)
    {
        var cancellation = Interlocked.Exchange(ref _delayedScreenshot, null);
        if (cancellation is null) return;
        cancellation.Cancel();
        _tray.SetDelayedScreenshotPending(false);
        if (showMessage) _tray.ShowMessage(L.Get("DelayedCapture"), L.Get("DelayedCaptureCanceled"));
    }

    private async void OnCaptureCompleted(object? sender, CaptureResult result)
    {
        if (result.Action == CaptureAction.Recording)
        {
            OpenRecording(new System.Drawing.Rectangle(result.ScreenBounds.Left + result.PixelRegion.Left, result.ScreenBounds.Top + result.PixelRegion.Top, result.PixelRegion.Width, result.PixelRegion.Height)); return;
        }
        if (result.Action is CaptureAction.OCR or CaptureAction.Cutout or CaptureAction.Summary)
        {
            OpenRecognition(result.Image, sender as Window, result.Action switch
            { CaptureAction.Cutout => "Cutout", CaptureAction.Summary => "Summary", _ => "OCR" });
            return;
        }
        if (result.Action == CaptureAction.Scrolling) { OpenScrolling(result); return; }
        if (_settings.Current.AutoSaveImages)
        {
            try
            {
                var options = _settings.Current;
                var name = OutputFileService.NewName(options.FileNameTemplate, result.Image.PixelWidth, result.Image.PixelHeight);
                var bytes = _images.Encode(result.Image, options.OutputFormat, options.ImageQuality, options.WebpLossless);
                OutputFileService.WriteUnique(OutputFileService.AutomaticDirectory(options, DateTime.Now), name, options.OutputFormat, bytes);
            }
            catch (Exception ex) { _tray.ShowMessage(L.Get("AutomaticSaveFailed"), ex.Message, System.Windows.Forms.ToolTipIcon.Warning); }
        }
        try { _history.Add(result.Image); }
        catch (Exception ex) { _tray.ShowMessage(L.Get("HistoryCouldNotBeSaved"), ex.Message, System.Windows.Forms.ToolTipIcon.Warning); }

        if (result.Action == CaptureAction.Copy)
        {
            var requestId = _captureRequestId;
            if (result.Mode != CaptureMode.Region)
            {
                string? text = null;
                try { text = await _captureRecognition.ReadQrCode(result.Image, _settings.Current.BarcodeRotate, _lifetime.Token); }
                catch (OperationCanceledException) when (_disposed) { return; }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Screenshot QR recognition failed ({0}).", ex.GetType().Name); }
                if (_disposed || requestId != _captureRequestId) return;
                // Direct captures have no selection overlay to close or return to.
                if (!string.IsNullOrEmpty(text) && CaptureQrActions.Handle(text, null, () => { },
                    status => _tray.ShowMessage(L.Get("CaptureComplete"), status)).Copied) return;
            }
            if (_disposed || requestId != _captureRequestId) return;
            try
            {
                _clipboard.SetImage(result.Image);
                _tray.ShowMessage(L.Get("CaptureComplete"), L.Get("ImageCopiedToClipboard"));
            }
            catch (Exception ex) { _tray.ShowMessage(L.Get("CopyFailed"), ex.Message, System.Windows.Forms.ToolTipIcon.Warning); }
            return;
        }

        if (result.Action == CaptureAction.Save)
        {
            SaveImageWindow.Open(result.Image, _images, _settings, sender as Window);
            return;
        }

        OpenPin(result.Image);
    }

    private ScrollCaptureWindow CreateScrollWindow(System.Drawing.Rectangle region, BitmapSource initial)
    {
        var window = new ScrollCaptureWindow(region, initial, _settings);
        _openWindows.Add(window);
        window.Closed += (_, _) => _openWindows.Remove(window);
        window.OutputRequested += (image, action) => OnCaptureCompleted(window, new CaptureResult(image, region,
            new System.Drawing.Rectangle(0, 0, region.Width, region.Height), action));
        window.EditRequested += image =>
        {
            try { _history.Add(image); } catch (Exception ex) { _tray.ShowMessage(L.Get("HistoryCouldNotBeSaved"), ex.Message, System.Windows.Forms.ToolTipIcon.Warning); }
            var editor = new EditorWindow(new CaptureResult(image, region, new System.Drawing.Rectangle(0, 0, region.Width, region.Height)), OpenPin, _clipboard);
            _openWindows.Add(editor); editor.Closed += (_, _) => _openWindows.Remove(editor); editor.Show();
        };
        return window;
    }
    private void OpenScrolling(CaptureResult result)
    {
        if (_recordingWindow?.IsRecording == true) { _tray.ShowMessage(L.Get("Recording"), L.Get("StopRecordingBeforeStartingAScrollingCapture")); return; }
        var region = new System.Drawing.Rectangle(result.ScreenBounds.Left + result.PixelRegion.Left,
            result.ScreenBounds.Top + result.PixelRegion.Top, result.PixelRegion.Width, result.PixelRegion.Height);
        CreateScrollWindow(region, result.Image).BeginManualCapture();
    }
    private async void OpenScrollProject()
    {
        if (_openWindows.OfType<ScrollCaptureWindow>().Any(x => x.IsCapturing)) { _tray.ShowMessage(L.Get("ScrollingCapture"), L.Get("StopTheCurrentCaptureFirst")); return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = L.Get("NimCapScrollingCaptureProjectSsscroll") };
        if (WindowDialogService.ShowDialog(dialog) != true) return;
        var placeholder = BitmapSource.Create(32, 32, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[32 * 32 * 4], 32 * 4); placeholder.Freeze();
        var window = CreateScrollWindow(System.Drawing.Rectangle.Empty, placeholder);
        window.Show(); await window.LoadProject(dialog.FileName);
    }

    private void OpenPin(BitmapSource image)
        => OpenPinContent(new PinContent(image));

    private void OpenPinContent(PinContent content, string? group = null)
    {
        var pin = new PinWindow(content.Image, _images, _settings, _clipboard, _pinWorkspace, content);
        if (group is not null) _pinWorkspace.Assign(pin, group);
        _pins.Add(pin);
        _openWindows.Add(pin);
        pin.Closed += (_, _) =>
        {
            _pins.Remove(pin);
            _openWindows.Remove(pin);
            if (Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown) _closedPins.Push((pin.ContentInfo with { Image = pin.Image }, pin.GroupName));
        };
        pin.Show();
    }

    private void CreatePin(PinCommand command)
    {
        if (_openWindows.OfType<ScrollCaptureWindow>().Any(x => x.IsCapturing)) { _tray.ShowMessage(L.Get("ScrollingCapture"), L.Get("StopCaptureBeforeUsingPins")); return; }
        try
        {
            if (command == PinCommand.CloseAll) { foreach (var pin in _pins.ToArray()) pin.Close(); return; }
            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
            var image = PinContentService.TryParseColor(text, out _) ? null : _clipboard.GetImage();
            OpenPinContent(PinContentService.FromClipboard(image, text, _settings.Current));
        }
        catch (Exception ex) { _tray.ShowMessage(L.Get("CouldNotCreatePin"), ex.Message, System.Windows.Forms.ToolTipIcon.Warning); }
    }

    private void ShowHistory()
    {
        if (_openWindows.OfType<ScrollCaptureWindow>().Any(x => x.IsCapturing)) return;
        var existing = _openWindows.OfType<HistoryWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        var window = new HistoryWindow(_history, _images, _settings, _clipboard, OpenPin);
        _openWindows.Add(window);
        window.Closed += (_, _) => _openWindows.Remove(window);
        window.Show();
    }

    private void ShowScreenRuler()
    {
        var existing = _openWindows.OfType<ScreenRulerWindow>().FirstOrDefault();
        if (existing is not null) { existing.Show(); existing.Activate(); return; }
        var ruler = new ScreenRulerWindow(_settings);
        _openWindows.Add(ruler);
        ruler.Closed += (_, _) => _openWindows.Remove(ruler);
        ruler.Show();
    }

    private void OpenRecognition(BitmapSource image, Window? owner, string mode)
        => OpenRecognitionCore(image, owner, mode, true);

    private void OpenRecognitionCore(BitmapSource image, Window? owner, string mode, bool autoRun)
    {
        var window = new RecognitionWindow(image, _settings, _plugins, mode, autoRun);
        if (owner?.IsVisible == true) window.Owner = owner;
        window.SettingsRequested += category => ShowSettings(category, window);
        window.OutputRequested += (output, action) => OnCaptureCompleted(window, new CaptureResult(output, System.Drawing.Rectangle.Empty, System.Drawing.Rectangle.Empty, action));
        _openWindows.Add(window); window.Closed += (_, _) => _openWindows.Remove(window); window.Show();
    }

    private void OpenRecording(System.Drawing.Rectangle region)
    {
        if (_capture.IsActive) { _tray.ShowMessage(L.Get("Recording"), L.Get("StopTheCurrentCaptureFirst")); return; }
        if (_recordingWindow is not null) { _recordingWindow.ShowRecordingTools(); return; }
        if (_openWindows.OfType<ScrollCaptureWindow>().Any(x => x.IsCapturing)) { _tray.ShowMessage(L.Get("Recording"), L.Get("StopTheScrollingCaptureFirst")); return; }
        var window = new RecordingWindow(region, _settings); _recordingWindow = window;
        _screenWords?.SetEnabled(false); _tray.SetScreenWordsChecked(false);
        _openWindows.Add(window); window.Closed += (_, _) => { _recordingWindow = null; _openWindows.Remove(window); }; window.ShowRecordingTools();
    }

    private void ShowSettings(string? category = null, Window? owner = null)
    {
        if (_capture.IsActive && owner is not SelectionWindow) { _tray.ShowMessage(L.Get("Settings"), L.Get("StopCaptureBeforeOpeningSettings")); return; }
        if (_openWindows.OfType<ScrollCaptureWindow>().Any(x => x.IsCapturing)) { _tray.ShowMessage(L.Get("ScrollingCapture"), L.Get("StopCaptureBeforeOpeningSettings")); return; }
        if (_settingsWindow is not null)
        {
            if (category is not null) _settingsWindow.SelectCategory(category);
            _settingsWindow.Activate();
            return;
        }
        var window = new SettingsWindow(_settings, _startup, _hotkey, _updates);
        if (category is not null) window.SelectCategory(category);
        _settingsWindow = window;
        try
        {
            if (WindowDialogService.ShowDialog(window, owner) == true)
            {
                _tray.UpdateHotkeyLabels(_settings.Current);
                if (_capture.IsActive) _captureBackendRefreshPending = true;
                else
                {
                    _capture.SetCaptureService(new ResilientCaptureService(_settings.Current.PreferWgcCapture, _settings.Current.IncludeCursor));
                    _captureBackendRefreshPending = false;
                }
            }
        }
        finally { _settingsWindow = null; }
    }

    private void TogglePins()
    {
        var shouldShow = _pins.All(x => !x.IsVisible);
        foreach (var pin in _pins)
        {
            if (shouldShow) pin.Show(); else pin.Hide();
        }
    }

    private void RestorePin()
    {
        if (_closedPins.TryPop(out var pin)) OpenPinContent(pin.Content, pin.Group);
        else _tray.ShowMessage(L.Get("Pins"), L.Get("NoClosedPinToRestore"));
    }

    private void RestorePinInteraction()
    {
        foreach (var pin in _pins.Where(x => x.IsClickThrough)) pin.SetClickThrough(false);
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel(); _lifetime.Dispose();
        _capture.Shutdown();
        RecognitionUi.OpenRequested = null;
        _screenWords?.Dispose();
        CancelDelayedScreenshot(showMessage: false);
        foreach (var window in _openWindows.ToArray())
        {
            if (window is RecordingWindow recording) recording.CloseSession(); else window.Close();
        }
        _capture.CaptureCompleted -= OnCaptureCompleted;
        _hotkey.Dispose();
        _tray.Dispose();
    }
}

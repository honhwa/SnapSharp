using L = NimCap.Screenshot.Localization.UiText;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Native;
using NimCap.Screenshot.Services;
using NimCap.Screenshot.ViewModels;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NimCap.Screenshot.Views;

public partial class SelectionWindow : Window
{
    private enum SelectionShape { Rectangle, Ellipse, Lasso, Polygon }
    private enum OverlayMode { Region, Window, FullScreen, Element }

    private readonly CapturedScreen _screen;
    private readonly Rectangle[] _displayBounds;
    private readonly IReadOnlyList<WindowCandidate> _windows;
    private readonly ICaptureService _captureService;
    private readonly SkiaImageService _skia = new();
    private readonly AnnotationTemplateService _templates = new();
    private readonly List<Point> _path = [];
    private readonly List<Point> _normalizedPath = [];
    private Point _start;
    private Point _lastPointer;
    private Rect _selection;
    private Rect _moveOrigin;
    private bool _dragging;
    private bool _editing;
    private bool _moving;
    private bool _shiftRatio;
    private double? _ratio;
    private double _dragRatio;
    private SelectionShape _shape;
    private OverlayMode _mode;
    private WindowCandidate? _suggestedWindow;
    private bool _includeWindowFrame;
    private EditorViewModel? _editor;
    private readonly SensitiveRedactionAction _redaction = new();
    private PixelRect _pixelSelection;
    private string _sampledColor = "#000000";
    private ScreenColorFormat _colorFormat;
    private readonly BitmapSource _colorPixels;
    private readonly bool _scrollingSelection;

    public event EventHandler<CaptureResult>? CaptureAccepted;
    public event EventHandler? CaptureCanceled;

    public SelectionWindow(CapturedScreen screen, IReadOnlyList<WindowCandidate> windows, ICaptureService captureService, bool scrollingSelection = false,
        IElementDetectionService? elementDetectionService = null, SettingsService? settings = null)
    {
        _precisionSettings = settings;
        _elementDetectionService = elementDetectionService ?? new ElementDetectionService();
        InitializeComponent();
        InitializeTextRecognition();
        Closed += (_, _) => CancelQrScan();
        _scrollingSelection = scrollingSelection;
        if (scrollingSelection)
        {
            Title = L.Get("SelectScrollingCaptureRegion");
            CaptureHintText.Text = L.Get("DragToSelectAScrollingRegionAdjust");
            PrimaryCaptureButton.Content = new TextBlock { Text = L.Get("StartScrollingCapture") };
            PrimaryCaptureButton.Width = double.NaN;
            PrimaryCaptureButton.ToolTip = L.Get("ConfirmRegionAndStartScrollingCaptureEnter");
            foreach (var button in new[] { ScrollCaptureButton, PinCaptureButton, RedactCaptureButton, CutoutCaptureButton, RecordingCaptureButton, SaveCaptureButton })
                button.Visibility = Visibility.Collapsed;
            ShapePicker.IsEnabled = false;
        }
        _screen = screen;
        _displayBounds = DisplayMonitorService.Enumerate().Select(m => Rectangle.Intersect(m.Bounds, screen.Bounds))
            .Where(r => r.Width > 0 && r.Height > 0).ToArray();
        _colorPixels = screen.Image.Format == PixelFormats.Bgra32 ? screen.Image : new FormatConvertedBitmap(screen.Image, PixelFormats.Bgra32, null, 0);
        if (!_colorPixels.IsFrozen) _colorPixels.Freeze();
        _windows = windows;
        _captureService = captureService;
        RefreshPrecisionPreferences();
        GotKeyboardFocus += (_, _) => UpdateMovementHint();
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(UpdateMovementHint);
        Activated += (_, _) => UpdateMovementHint();
        Deactivated += (_, _) => UpdateMovementHint();
        InitializeElementDetection();
        Screenshot.Source = screen.Image;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyScreenBounds,
            System.Windows.Threading.DispatcherPriority.Loaded);
        for (var x = 0; x <= 19; x++)
            MagnifierPixelGrid.Children.Add(new System.Windows.Shapes.Line { X1 = x * 14, X2 = x * 14, Y2 = 154, Stroke = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255)), StrokeThickness = .5 });
        for (var y = 0; y <= 11; y++)
            MagnifierPixelGrid.Children.Add(new System.Windows.Shapes.Line { Y1 = y * 14, Y2 = y * 14, X2 = 266, Stroke = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255)), StrokeThickness = .5 });
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            ApplyScreenBounds();
            if (NativeMethods.GetCursorPos(out var activeCursor) && _screen.Bounds.Contains(activeCursor.X, activeCursor.Y)) { Activate(); Focus(); }
            UpdateShade();
            UpdatePointer(NativeMethods.GetCursorPos(out var cursor) ? DesktopToLocal(cursor.X, cursor.Y) : new Point(ActualWidth / 2, ActualHeight / 2));
            if (!_scrollingSelection) CaptureModePicker.SelectedIndex = 1;
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        var bounds = _screen.Bounds;
        NativeMethods.SetWindowPos(source.Handle, NativeMethods.HwndTopMost,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        Dispatcher.BeginInvoke(ApplyScreenBounds,
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ApplyScreenBounds()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        var bounds = _screen.Bounds;
        // A spanning HWND has one render DPI. Never apply the cursor monitor's DPI to global coordinates.
        var dpi = NativeMethods.GetDpiForWindow(source.Handle);
        var scale = dpi > 0 ? dpi / 96d : VisualTreeHelper.GetDpi(this).DpiScaleX;
        Width = Math.Max(1, bounds.Width / scale);
        Height = Math.Max(1, bounds.Height / scale);
        NativeMethods.SetWindowPos(source.Handle, NativeMethods.HwndTopMost,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_textRecognitionActive) return;
        if (IsCaptureControl(e.OriginalSource as DependencyObject)) return;
        var point = Clamp(GetLogicalPointer(e));
        _lastPointer = point;
        if (_editing)
        {
            if (!_moving || !_selection.Contains(point)) return;
            _start = point;
            _moveOrigin = _selection;
            _dragging = true;
            Root.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (_mode == OverlayMode.FullScreen)
        {
            _selection = new Rect(0, 0, ActualWidth, ActualHeight);
            BeginInlineEditing();
            e.Handled = true;
            return;
        }
        if (_mode == OverlayMode.Window)
        {
            _selection = SuggestedWindowAt(point);
            if (!_selection.IsEmpty) BeginInlineEditing();
            e.Handled = true;
            return;
        }
        if (_shape == SelectionShape.Polygon)
        {
            _path.Add(SnapPoint(point));
            UpdatePathPreview(point);
            if (e.ClickCount >= 2 && _path.Count >= 3) FinishPathSelection();
            e.Handled = true;
            return;
        }

        if (_mode == OverlayMode.Element) CancelElementQuery();
        _start = SnapPoint(point);
        _selection = new Rect(_start, _start);
        _path.Clear();
        if (_shape == SelectionShape.Lasso) _path.Add(_start);
        _dragRatio = CurrentRatio(1);
        _dragging = true;
        Root.CaptureMouse();
        UpdateSelection();
        e.Handled = true;
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject toolbarSource &&
            (ToolPanel.IsAncestorOf(toolbarSource) || SecondaryToolPanel.IsAncestorOf(toolbarSource) || RecognitionActionsPanel.IsAncestorOf(toolbarSource)))
        {
            AnnotationView.CommitInlineEdit();
            return;
        }
        if (e.OriginalSource is not DependencyObject source ||
            !ReferenceEquals(source, AnnotationView) && !AnnotationView.IsAncestorOf(source)) return;
        if (!AnnotationView.AllowsWindowDoubleClickCopy(e.GetPosition(AnnotationView))) return;
        var point = Clamp(GetLogicalPointer(e));
        if (!TryCompleteDoubleClickCopy(e.ClickCount, point)) return;
        e.Handled = true;
    }

    private bool TryCompleteDoubleClickCopy(int clickCount, Point point)
    {
        if (clickCount < 2 || !_editing || _scrollingSelection || _dragging || _moving ||
            !IsValidSelection() || !_selection.Contains(point) || _editor is null ||
            _editor.ContinuationTool is not null ||
            !SupportsDoubleClickCopy(_editor.SelectedTool)) return false;
        Complete(CaptureAction.Copy);
        return true;
    }

    private static bool SupportsDoubleClickCopy(AnnotationTool tool) => tool is
        AnnotationTool.Select or AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Line or AnnotationTool.Arrow or
        AnnotationTool.Pen or AnnotationTool.Highlight or AnnotationTool.Polyline or AnnotationTool.Mosaic or
        AnnotationTool.Blur or AnnotationTool.Spotlight or AnnotationTool.Magnifier;

    private void Window_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_textRecognitionActive) { ExitTextRecognition(); e.Handled = true; return; }
        if (!_editing) return;
        RestartRegionSelection();
        e.Handled = true;
    }

    private void RestartRegionSelection()
    {
        CancelQrScan();
        _lastQrText = null;
        ExitTextRecognition();
        _redaction.Cancel();
        _pixelNudgeActive = false;
        // Discard this selection and its annotations, retaining the original desktop snapshot.
        // Clear editing before releasing capture so drag completion cannot rebuild the editor.
        _editing = false;
        _dragging = false;
        _moving = false;
        AnnotationView.CancelInlineEdit();
        if (_editor is not null) _editor.PropertyChanged -= Editor_PropertyChanged;
        _editor = null;
        AnnotationView.DataContext = null;
        DataContext = null;
        if (Mouse.Captured is DependencyObject captured && IsAncestorOf(captured)) Mouse.Capture(null);

        foreach (var toggle in new[] { PrimaryArrowStyleDropDownToggle, ArrowStyleDropDownToggle,
            StrokeStyleDropDownToggle, FillOpacityDropDownToggle, ThicknessDropDownToggle,
            FontFamilyDropDownToggle, FontSizeDropDownToggle, TextOutlineDropDownToggle, EffectDropDownToggle })
            toggle.IsChecked = false;
        foreach (var group in ToolButtons.Children.OfType<ToolGroupButton>()) group.CloseDropDown();

        _pixelSelection = default;
        _moveOrigin = Rect.Empty;
        _start = default;
        EditedImageView.Source = null;
        EditedImageView.Clip = null;
        EditedImageView.Visibility = Visibility.Collapsed;
        AnnotationView.Clip = null;
        AnnotationView.Visibility = Visibility.Collapsed;
        AnnotationView.IsHitTestVisible = true;
        Handles.Visibility = Visibility.Collapsed;
        ToolPanel.Visibility = Visibility.Collapsed;
        SecondaryToolPanel.Visibility = Visibility.Collapsed;
        RecognitionActionsPanel.Visibility = Visibility.Collapsed;
        MoveSelectionButton.Background = Brushes.Transparent;
        Hint.Visibility = Visibility.Visible;
        CapturePanel.Visibility = Visibility.Visible;
        Magnifier.Visibility = Visibility.Visible;
        Cursor = PrecisionCursor.Cross;

        _mode = OverlayMode.Region;
        CaptureModePicker.SelectedIndex = 0;
        ResetDraft();
        UpdatePointer(_lastPointer);
        Focus();
    }

    internal static bool RunReselectionSelfTest(BitmapSource source)
    {
        var window = new SelectionWindow(new CapturedScreen(
            new Rectangle(0, 0, source.PixelWidth, source.PixelHeight), source), [], new ScreenCaptureService());
        try
        {
            var canceled = false;
            window.CaptureCanceled += (_, _) => canceled = true;
            var beforeSelection = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                { RoutedEvent = PreviewMouseRightButtonDownEvent };
            window.Window_PreviewMouseRightButtonDown(window, beforeSelection);
            if (beforeSelection.Handled) return false;

            // Exercise repeated resets, including non-rectangular selection and fullscreen mode.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                window._editing = true;
                window._dragging = true;
                window._moving = true;
                window._selection = new Rect(2, 2, 40, 30);
                window._path.Add(new Point(2, 2));
                window._normalizedPath.Add(new Point(0, 0));
                window._editor = new EditorViewModel(source);
                window._editor.PropertyChanged += window.Editor_PropertyChanged;
                window.DataContext = window._editor;
                window.AnnotationView.DataContext = window._editor;
                window.EditedImageView.Source = source;
                window.ToolPanel.Visibility = Visibility.Visible;
                window.SecondaryToolPanel.Visibility = Visibility.Visible;
                window.Handles.Visibility = Visibility.Visible;
                window.CaptureModePicker.SelectedIndex = 3;

                var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = PreviewMouseRightButtonDownEvent };
                window.Window_PreviewMouseRightButtonDown(window.AnnotationView, rightClick);
                if (!rightClick.Handled || canceled || window._editing || window._dragging || window._moving ||
                    window._editor is not null || window.DataContext is not null || window.AnnotationView.DataContext is not null ||
                    !window._selection.IsEmpty || window._path.Count != 0 || window._normalizedPath.Count != 0 ||
                    window.EditedImageView.Source is not null || window.ToolPanel.Visibility != Visibility.Collapsed ||
                    window.SecondaryToolPanel.Visibility != Visibility.Collapsed || window.Handles.Visibility != Visibility.Collapsed ||
                    window.AnnotationView.Visibility != Visibility.Collapsed || !window.AnnotationView.IsHitTestVisible ||
                    window.CapturePanel.Visibility != Visibility.Visible || window.Cursor != PrecisionCursor.Cross ||
                    window._mode != OverlayMode.Region) return false;
            }
            return true;
        }
        finally { window.Close(); }
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (IsCaptureControl(e.OriginalSource as DependencyObject) && !_dragging)
        {
            PrecisionGuides.ShowCrosshair = false;
            PrecisionGuides.InvalidateVisual();
            return;
        }
        if (!_editing && IsVisible && !IsActive) { Activate(); Focus(); }
        var point = Clamp(GetLogicalPointer(e));
        if (_pixelNudgeActive && (point - _lastPointer).Length > .01) _pixelNudgeActive = false;
        _lastPointer = point;
        UpdatePrecisionGuides(point);
        if (_editing)
        {
            if (_moving && _dragging)
            {
                var moved = new Rect(_moveOrigin.X + point.X - _start.X, _moveOrigin.Y + point.Y - _start.Y,
                    _moveOrigin.Width, _moveOrigin.Height);
                _selection = SnapAndClampMove(moved);
                UpdateSelection();
            }
            return;
        }
        if (_shape == SelectionShape.Polygon && _path.Count > 0)
        {
            UpdatePathPreview(point);
            UpdatePointer(point);
            return;
        }
        if (_dragging)
        {
            if (_shape == SelectionShape.Lasso)
            {
                if (_path.Count == 0 || (_path[^1] - point).Length >= 2) _path.Add(point);
                UpdatePathPreview(point);
            }
            else
            {
                _selection = CreateRect(_start, SnapPoint(point), CurrentRatio(_dragRatio));
                UpdateSelection();
            }
        }
        else
        {
            _selection = _mode == OverlayMode.FullScreen ? new Rect(0, 0, ActualWidth, ActualHeight)
                : _mode == OverlayMode.Element ? SuggestedElementAt(point)
                : _mode == OverlayMode.Window || _shape is SelectionShape.Rectangle or SelectionShape.Ellipse
                    ? SuggestedWindowAt(point) : Rect.Empty;
            UpdateSelection();
        }
        UpdatePointer(point);
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        if (_editing)
        {
            _dragging = false;
            Root.ReleaseMouseCapture();
            RebuildEditor(preserveScreenPosition: !_moving);
            e.Handled = true;
            return;
        }
        if (_shape == SelectionShape.Lasso)
        {
            _dragging = false;
            Root.ReleaseMouseCapture();
            FinishPathSelection();
            e.Handled = true;
            return;
        }
        // Use the last point that was actually rendered. This avoids the small
        // mouse-up jump that makes precise selections feel unstable.
        var end = SnapPoint(_lastPointer);
        var dragged = Math.Abs(end.X - _start.X) >= 3 || Math.Abs(end.Y - _start.Y) >= 3;
        _selection = dragged ? CreateRect(_start, end, CurrentRatio(_dragRatio))
            : _mode == OverlayMode.Element ? SuggestedElementAt(end) : SuggestedWindowAt(end);
        _dragging = false;
        var validSelection = IsValidSelection();
        if (validSelection) _editing = true;
        Root.ReleaseMouseCapture();
        if (validSelection) BeginInlineEditing(); else UpdateSelection();
        e.Handled = true;
    }

    private void FinishPathSelection()
    {
        if (_path.Count < 3) return;
        var points = _shape == SelectionShape.Lasso ? Simplify(_path, 1.5) : _path.ToList();
        _selection = new Rect(new Point(points.Min(p => p.X), points.Min(p => p.Y)),
            new Point(points.Max(p => p.X), points.Max(p => p.Y)));
        if (!IsValidSelection()) return;
        _normalizedPath.Clear();
        _normalizedPath.AddRange(points.Select(p => new Point(
            (p.X - _selection.Left) / _selection.Width, (p.Y - _selection.Top) / _selection.Height)));
        _path.Clear();
        BeginInlineEditing();
    }

    private void BeginInlineEditing()
    {
        if (!IsValidSelection()) return;
        _elementHoverTimer?.Stop();
        CancelElementQuery();
        _editing = true;
        Cursor = Cursors.Arrow;
        Hint.Visibility = Visibility.Collapsed;
        CapturePanel.Visibility = Visibility.Collapsed;
        RecognitionActionsPanel.Visibility = _scrollingSelection ? Visibility.Collapsed : Visibility.Visible;
        PositionRecognitionActions();
        Magnifier.Visibility = Visibility.Collapsed;
        Handles.Visibility = Visibility.Visible;
        ToolPanel.Visibility = Visibility.Visible;
        SecondaryToolPanel.Visibility = Visibility.Visible;
        RebuildEditor();
        UpdateSelection();
        UpdateSelectedTool(AnnotationTool.Rectangle);
    }

    private void RebuildEditor(bool preserveScreenPosition = true)
    {
        var previousSelection = _pixelSelection;
        var nextSelection = ToPixelSelection(_selection);
        if (nextSelection.IsEmpty) return;
        BitmapSource cropped = new CroppedBitmap(_screen.Image, nextSelection.ToInt32Rect());
        cropped.Freeze();
        var selectedWindow = _mode == OverlayMode.Element ? _elementWindow : _mode == OverlayMode.Window ? _suggestedWindow : null;
        var desktopSelection = new Rectangle(_screen.Bounds.Left + nextSelection.X, _screen.Bounds.Top + nextSelection.Y,
            nextSelection.Width, nextSelection.Height);
        // Only automatic element/window selections inherit a silhouette. Manual region captures
        // and selections expanded outside the window must retain the requested desktop pixels.
        if (selectedWindow?.CaptureShape is { } shape && selectedWindow.Bounds.Contains(desktopSelection))
            cropped = _skia.ApplyWindowShape(cropped, desktopSelection, shape);
        if (_editor is null)
        {
            _editor = new EditorViewModel(cropped);
            _editor.PropertyChanged += Editor_PropertyChanged;
            DataContext = _editor;
            AnnotationView.DataContext = _editor;
        }
        else
        {
            var dx = preserveScreenPosition ? previousSelection.X - nextSelection.X : 0;
            var dy = preserveScreenPosition ? previousSelection.Y - nextSelection.Y : 0;
            var canvas = new Rect(0, 0, nextSelection.Width, nextSelection.Height);
            var annotations = _editor.Annotations
                .Select(annotation => AnnotationEditing.Move(annotation, new Vector(dx, dy)))
                .Where(annotation => AnnotationEditing.Bounds(annotation).IntersectsWith(canvas))
                .ToArray();
            _editor.ApplyImageEdit(cropped, annotations);
        }
        _pixelSelection = nextSelection;
        UpdateEditorSurface();
        ScanSelectionQr();
    }

    private void UpdateEditorSurface()
    {
        if (_textRecognitionActive) ExitTextRecognition();
        if (_editor is null) return;
        EditedImageView.Source = _editor.Image;
        EditedImageView.Width = _editor.Image.PixelWidth;
        EditedImageView.Height = _editor.Image.PixelHeight;
        EditedImageView.Visibility = Visibility.Visible;
        AnnotationView.ScreenPixelBounds = ScreenCoordinateMapper.ImagePixelsToScreenPixels(_screen.Bounds, _pixelSelection);
        AnnotationView.Width = _editor.Image.PixelWidth;
        AnnotationView.Height = _editor.Image.PixelHeight;
        Geometry? clip = null;
        if (_shape == SelectionShape.Ellipse)
            clip = new EllipseGeometry(new Rect(0, 0, _editor.Image.PixelWidth, _editor.Image.PixelHeight));
        else if (_shape is SelectionShape.Lasso or SelectionShape.Polygon && _normalizedPath.Count >= 3)
            clip = CreatePathGeometry(_normalizedPath.Select(point => new Point(
                point.X * _editor.Image.PixelWidth, point.Y * _editor.Image.PixelHeight)));
        EditedImageView.Clip = clip;
        AnnotationView.Clip = clip;
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(_selection.Width / _editor.Image.PixelWidth, _selection.Height / _editor.Image.PixelHeight));
        transform.Children.Add(new TranslateTransform(_selection.X, _selection.Y));
        EditedImageView.RenderTransform = transform;
        EditedImageView.RenderTransformOrigin = new Point(0, 0);
        AnnotationView.RenderTransform = transform;
        AnnotationView.RenderTransformOrigin = new Point(0, 0);
        AnnotationView.Visibility = Visibility.Visible;
    }

    private void Editor_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_changingSecondaryStyle) return;
        if (e.PropertyName == nameof(EditorViewModel.Image)) UpdateEditorSurface();
        else if (e.PropertyName == nameof(EditorViewModel.SelectedTool) && _editor is not null)
            UpdateSelectedTool(_editor.SelectedTool);
        else if (e.PropertyName is nameof(EditorViewModel.SelectedAnnotation)
                 or nameof(EditorViewModel.SelectedColor)
                 or nameof(EditorViewModel.StrokeThickness)
                 or nameof(EditorViewModel.FillColor)
                 or nameof(EditorViewModel.FontSize)
                 or nameof(EditorViewModel.FontFamilyName)
                 or nameof(EditorViewModel.TextOutlineColor)
                 or nameof(EditorViewModel.TextOutlineThickness)
                 or nameof(EditorViewModel.StrokeStyle)
                 or nameof(EditorViewModel.ArrowStyle)
                 or nameof(EditorViewModel.EffectStrength)
                 or nameof(EditorViewModel.SpotlightDarkness)
                 or nameof(EditorViewModel.MagnifierZoom)
                 or nameof(EditorViewModel.NextNumber)
                 or nameof(EditorViewModel.NumberStyle)
                 or nameof(EditorViewModel.ContinuousNumbering))
            UpdateSecondaryToolbar();
    }

    private void Handle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string edge }) return;
        var left = _selection.Left;
        var top = _selection.Top;
        var right = _selection.Right;
        var bottom = _selection.Bottom;
        if (edge.Contains("Left")) left += e.HorizontalChange;
        if (edge.Contains("Right")) right += e.HorizontalChange;
        if (edge.Contains("Top")) top += e.VerticalChange;
        if (edge.Contains("Bottom")) bottom += e.VerticalChange;
        var anchor = new Point(edge.Contains("Left") ? _selection.Right : _selection.Left,
            edge.Contains("Top") ? _selection.Bottom : _selection.Top);
        var moving = new Point(edge.Contains("Left") ? left : right, edge.Contains("Top") ? top : bottom);
        if (edge is "Top" or "Bottom") moving.X = anchor.X + (right - left);
        if (edge is "Left" or "Right") moving.Y = anchor.Y + (bottom - top);
        var next = CreateRect(anchor, SnapPoint(Clamp(moving)), CurrentRatio(_selection.Width / Math.Max(1, _selection.Height)), edge);
        if (next.Width < 20 || next.Height < 20) return;
        _selection = next;
        UpdateSelection();
    }

    private void Handle_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_editing && !e.Canceled) RebuildEditor();
    }

    private void MoveSelection_Click(object sender, RoutedEventArgs e)
    {
        _moving = !_moving;
        AnnotationView.IsHitTestVisible = !_moving;
        Cursor = _moving ? Cursors.SizeAll : Cursors.Arrow;
        if (_moving) MoveSelectionButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ToolbarActiveBrush");
        else MoveSelectionButton.Background = Brushes.Transparent;
    }

    private void CaptureMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CaptureModePicker?.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse(item.Tag?.ToString(), out OverlayMode mode)) return;
        _mode = mode;
        ResetElementDetection();
        _suggestedWindow = null;
        _includeWindowFrame = false;
        ResetDraft();
        if (ShapePicker is not null) ShapePicker.IsEnabled = !_scrollingSelection;
        if (mode != OverlayMode.Region && ShapePicker is not null && ShapePicker.SelectedIndex != 0)
        {
            _changingCaptureOptions = true;
            try { ShapePicker.SelectedIndex = 0; }
            finally { _changingCaptureOptions = false; }
        }
        if (RatioPicker is not null)
            RatioPicker.IsEnabled = _shape is SelectionShape.Rectangle or SelectionShape.Ellipse;
        if (!IsLoaded) return;
        if (!_scrollingSelection) CaptureHintText.Text = L.Get(mode == OverlayMode.Element ? "ElementSelectionHint"
            : mode == OverlayMode.Window ? "WindowSelectionClientHint" : "DragToSelectClickToSelectWindow");
        if (mode == OverlayMode.Element && !_editing) _elementHoverTimer?.Start();
        _selection = mode == OverlayMode.FullScreen ? new Rect(0, 0, ActualWidth, ActualHeight)
            : mode == OverlayMode.Element ? SuggestedElementAt(_lastPointer)
            : mode == OverlayMode.Window ? SuggestedWindowAt(_lastPointer) : Rect.Empty;
        UpdateSelection();
    }

    private void Shape_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ShapePicker?.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse(item.Tag?.ToString(), out SelectionShape shape)) return;
        if (IsLoaded) UseRegionOptions();
        _shape = shape;
        ResetDraft();
        if (RatioPicker is not null) RatioPicker.IsEnabled = shape is SelectionShape.Rectangle or SelectionShape.Ellipse;
    }

    private void Ratio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RatioPicker?.SelectedItem is not ComboBoxItem item) return;
        if (IsLoaded) UseRegionOptions();
        _ratio = double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var ratio) ? ratio : null;
    }

    private void ResetDraft()
    {
        if (_editing) return;
        _path.Clear();
        _normalizedPath.Clear();
        _selection = Rect.Empty;
        if (ShapeOutline is not null) ShapeOutline.Visibility = Visibility.Collapsed;
        if (Shade is not null) UpdateSelection();
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is not null && sender is FrameworkElement element &&
            Enum.TryParse<AnnotationTool>(element.Tag?.ToString(), out var tool))
        {
            AnnotationView.CommitInlineEdit();
            _moving = false;
            MoveSelectionButton.Background = Brushes.Transparent;
            AnnotationView.IsHitTestVisible = true;
            Cursor = Cursors.Arrow;
            if (tool == AnnotationTool.Bubble && _editor.FillColor is null)
                _editor.FillColor = Color.FromArgb(242, 255, 255, 255);
            if (tool != AnnotationTool.Select)
                _editor.SelectedAnnotation = null;
            _editor.ActivateTool(tool);
            UpdateSelectedTool(tool);
        }
    }

    private void ToolGroup_ToolSelected(object sender, EventArgs e)
    {
        if (sender is ToolGroupButton group)
            Tool_Click(new System.Windows.Controls.Button { Tag = group.CurrentTool.ToString() }, new RoutedEventArgs());
    }

    private void UpdateSelectedTool(AnnotationTool selected)
    {
        foreach (var button in ToolButtons.Children.OfType<System.Windows.Controls.Button>())
        {
            if (button.Tag is not string tag) continue;
            var active = string.Equals(tag, selected.ToString(), StringComparison.Ordinal);
            if (active) button.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ToolbarActiveBrush");
            else button.Background = Brushes.Transparent;
            button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, active ? "AccentForegroundBrush" : "TextBrush");
        }
        UpdateSecondaryToolbar();
    }

    private void ObjectCopy_Click(object sender, RoutedEventArgs e) => _editor?.CopySelected();
    private void ObjectPaste_Click(object sender, RoutedEventArgs e) => _editor?.PasteSelected();
    private void ObjectDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.SelectedAnnotation is { } selected) _editor.Remove(selected);
    }
    private void LayerUp_Click(object sender, RoutedEventArgs e) => _editor?.MoveLayer(1);
    private void LayerDown_Click(object sender, RoutedEventArgs e) => _editor?.MoveLayer(-1);
    private void LayerTop_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is not null) _editor.MoveLayerTo(_editor.Annotations.Count - 1);
    }
    private void LayerBottom_Click(object sender, RoutedEventArgs e) => _editor?.MoveLayerTo(0);

    private void RotateClockwise_Click(object sender, RoutedEventArgs e) => RotateImage(true);
    private void RotateCounterClockwise_Click(object sender, RoutedEventArgs e) => RotateImage(false);

    private void RotateImage(bool clockwise)
    {
        if (_editor is null) return;
        var width = _editor.Image.PixelWidth;
        var height = _editor.Image.PixelHeight;
        Point Map(Point point) => clockwise ? new Point(height - point.Y, point.X) : new Point(point.Y, width - point.X);
        var annotations = _editor.Annotations.Select(annotation =>
            AnnotationEditing.Transform(annotation, Map, 1, 1, clockwise ? 90 : -90)).ToArray();
        _editor.ApplyImageEdit(_skia.Rotate90(_editor.Image, clockwise), annotations);
        UpdateEditorSurface();
    }

    private void FlipHorizontal_Click(object sender, RoutedEventArgs e) => FlipImage(true);
    private void FlipVertical_Click(object sender, RoutedEventArgs e) => FlipImage(false);

    private void FlipImage(bool horizontal)
    {
        if (_editor is null) return;
        var width = _editor.Image.PixelWidth;
        var height = _editor.Image.PixelHeight;
        Point Map(Point point) => horizontal ? new Point(width - point.X, point.Y) : new Point(point.X, height - point.Y);
        var annotations = _editor.Annotations.Select(annotation =>
        {
            var transformed = AnnotationEditing.Transform(annotation, Map);
            return transformed with { Rotation = AnnotationEditing.NormalizeAngle(-annotation.Rotation) };
        }).ToArray();
        _editor.ApplyImageEdit(_skia.Flip(_editor.Image, horizontal), annotations);
        UpdateEditorSurface();
    }

    private void ExpandCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null) return;
        var dialog = new CanvasExpandDialog(_editor.Image.PixelWidth, _editor.Image.PixelHeight) { Owner = this };
        if (WindowDialogService.ShowDialog(dialog, this) != true) return;
        var annotations = _editor.Annotations.Select(annotation =>
            AnnotationEditing.Move(annotation, new Vector(dialog.OffsetX, dialog.OffsetY))).ToArray();
        var image = _skia.ExpandCanvas(_editor.Image, dialog.CanvasWidth, dialog.CanvasHeight,
            dialog.OffsetX, dialog.OffsetY, dialog.BackgroundColor);
        _editor.ApplyImageEdit(image, annotations);
        UpdateEditorSurface();
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null || _editor.Annotations.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = L.Get("NimCapAnnotationTemplateSstemplate"), DefaultExt = ".sstemplate" };
        if (WindowDialogService.ShowDialog(dialog, this) == true)
            _templates.Save(dialog.FileName, _editor.Annotations, _editor.Image.PixelWidth, _editor.Image.PixelHeight);
    }

    private void LoadTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = L.Get("NimCapAnnotationTemplateSstemplate") };
        if (WindowDialogService.ShowDialog(dialog, this) != true) return;
        try
        {
            _editor.ReplaceAll(_templates.Load(dialog.FileName, _editor.Image.PixelWidth, _editor.Image.PixelHeight), append: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L.Get("ImportAnnotationTemplate2"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Copy);
    private void Save_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Save);
    private void Pin_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Pin);
    private async void Recognition_Click(object sender, RoutedEventArgs e) => await RecognizeTextInPlace();
    private async void Summary_Click(object sender, RoutedEventArgs e) => await SummarizeInPlace();

    private void PositionRecognitionActions()
    {
        var viewport = ChromeViewport(new Point(_selection.X + _selection.Width / 2, _selection.Y));
        RecognitionActionsPanel.Measure(new System.Windows.Size(viewport.Width, viewport.Height));
        var size = RecognitionActionsPanel.DesiredSize;
        CanvasPosition(RecognitionActionsPanel, viewport.Left + Math.Max(0, (viewport.Width - size.Width) / 2), viewport.Top + 20);
    }
    private async void Redact_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is not { } editor) return;
        AnnotationView.CommitInlineEdit();
        await _redaction.Run(this, RedactCaptureButton, editor, PrecisionPreferences,
            () => _editing && ReferenceEquals(_editor, editor));
    }
    private void Cutout_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Cutout);
    private void Recording_Click(object sender, RoutedEventArgs e)
    {
        if (_shape != SelectionShape.Rectangle) { MessageBox.Show(this, L.Get("RecordingSupportsRectangularRegionsOnlySwitchTo")); return; }
        if (_editor is null) return;
        CaptureAccepted?.Invoke(this, new CaptureResult(_editor.Image, _screen.Bounds, _pixelSelection.ToDrawingRectangle(), CaptureAction.Recording));
    }
    private void ScrollCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_shape != SelectionShape.Rectangle) { MessageBox.Show(this, L.Get("ScrollingCaptureRequiresARectangularRegionSwitch"), L.Get("ScrollingCapture")); return; }
        if (_editor is null) return;
        if (_pixelSelection.Width < 32 || _pixelSelection.Height < 32)
        { MessageBox.Show(this, L.Get("ScrollingCaptureRequiresAtLeast3232"), L.Get("ScrollingCapture")); return; }
        var initial = _skia.Crop(_screen.Image, _pixelSelection.ToInt32Rect());
        CaptureAccepted?.Invoke(this, new CaptureResult(initial, _screen.Bounds, _pixelSelection.ToDrawingRectangle(), CaptureAction.Scrolling));
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CaptureCanceled?.Invoke(this, EventArgs.Empty);

    private async void Complete(CaptureAction action)
    {
        if (_scrollingSelection) { ScrollCapture_Click(this, new RoutedEventArgs()); return; }
        if (_editor is null) return;
        if (_qrDetection is { IsCompleted: false } detection)
        {
            if (_waitingForQrOutput) return;
            _waitingForQrOutput = true;
            try
            {
                if (await detection || !ReferenceEquals(_qrDetection, detection) || !IsVisible || _editor is null) return;
            }
            finally { _waitingForQrOutput = false; }
        }
        var output = RenderCaptureImage();
        CaptureAccepted?.Invoke(this, new CaptureResult(output, _screen.Bounds, _pixelSelection.ToDrawingRectangle(), action));
    }

    private BitmapSource RenderCaptureImage()
    {
        AnnotationView.CommitInlineEdit();
        return ApplyCaptureMask(_skia.Render(_editor!.Image, _editor.Annotations));
    }

    private BitmapSource ApplyCaptureMask(BitmapSource output)
    {
        if (_shape == SelectionShape.Ellipse) output = _skia.ApplySelectionMask(output, [], true);
        else if (_shape is SelectionShape.Lasso or SelectionShape.Polygon)
        {
            var points = _normalizedPath.Select(p => new Point(p.X * output.PixelWidth, p.Y * output.PixelHeight)).ToArray();
            output = _skia.ApplySelectionMask(output, points, false);
        }
        return output;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_textRecognitionActive)
        {
            HandleTextRecognitionKey(e);
            return;
        }
        if (e.OriginalSource is System.Windows.Controls.TextBox or System.Windows.Controls.Primitives.TextBoxBase ||
            CaptureModePicker.IsDropDownOpen || ShapePicker.IsDropDownOpen || RatioPicker.IsDropDownOpen) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_editing && !_scrollingSelection && Keyboard.Modifiers == ModifierKeys.None && key is Key.O or Key.J or Key.D)
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                if (key == Key.D) Redact_Click(this, new RoutedEventArgs());
                else if (key == Key.O) Recognition_Click(this, new RoutedEventArgs());
                else Summary_Click(this, new RoutedEventArgs());
            }
            return;
        }
        if (key == Key.R && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (!e.IsRepeat) Ruler_Click(this, new RoutedEventArgs());
            e.Handled = true; return;
        }
        if (key == Key.X && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (!e.IsRepeat) Crosshair_Click(this, new RoutedEventArgs());
            e.Handled = true; return;
        }
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down &&
            (Keyboard.Modifiers & ~ModifierKeys.Shift) == 0)
        {
            if (!CanNudgeCapture(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) return;
            NudgeCapture(e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0,
                e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true; return;
        }
        if (_scrollingSelection && _editing && e.Key == Key.Enter && e.OriginalSource is not System.Windows.Controls.TextBox)
        {
            ScrollCapture_Click(this, new RoutedEventArgs()); e.Handled = true; return;
        }
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            if (!_editing && !_dragging && !_shiftRatio && !e.IsRepeat)
            {
                _colorFormat = ScreenColorFormatter.Next(_colorFormat);
                UpdatePointer(_lastPointer);
                e.Handled = true;
            }
            _shiftRatio = true;
        }
        if (e.Key == Key.Escape)
        {
            if (!_editing && _shape == SelectionShape.Polygon && _path.Count > 0)
            {
                _path.RemoveAt(_path.Count - 1);
                UpdatePathPreview(_lastPointer);
            }
            else CaptureCanceled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (!_editing && !_dragging && e.Key == Key.Tab)
        {
            CaptureModePicker.SelectedIndex = (CaptureModePicker.SelectedIndex + 1) % CaptureModePicker.Items.Count;
            e.Handled = true;
            return;
        }
        if (!_editing && _shape == SelectionShape.Polygon && e.Key == Key.Enter)
        {
            FinishPathSelection();
            e.Handled = true;
            return;
        }
        if (!_editing && e.Key == Key.C && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            e.Handled = true;
            CopySampledColor(System.Windows.Clipboard.SetText);
            return;
        }
        if (_editor is null) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) _editor.UndoCommand.Execute(null);
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) _editor.RedoCommand.Execute(null);
        if (e.Key == Key.Delete && _editor.SelectedAnnotation is { } selected)
        {
            _editor.Remove(selected); e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && _editor.SelectedTool == AnnotationTool.Select && _editor.SelectedAnnotation is not null)
        {
            _editor.CopySelected(); e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            _editor.PasteSelected(); e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) Complete(CaptureAction.Copy);
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) _shiftRatio = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
    }

    private void CopySampledColor(Action<string> write)
    {
        try { write(_sampledColor); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            CaptureHintText.Text = L.Get("ClipboardIsBusyCaptureRemainsOpenPress");
            return;
        }
        // Finish the entire multi-monitor capture session without producing an image.
        CaptureCanceled?.Invoke(this, EventArgs.Empty);
    }

    internal static bool RunColorPickerSelfTest()
    {
        if (!ScreenColorFormatter.RunSelfTest()) throw new InvalidOperationException("Color conversion failed: " + string.Join("; ", Enum.GetValues<ScreenColorFormat>().Select(format => ScreenColorFormatter.Format(Color.FromRgb(255, 87, 51), format))));
        var bytes = new byte[40 * 30 * 4];
        for (var i = 0; i < bytes.Length; i += 4) { bytes[i] = 51; bytes[i + 1] = 87; bytes[i + 2] = 255; bytes[i + 3] = 255; }
        var source = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, bytes, 40 * 4); source.Freeze();
        var window = new SelectionWindow(new CapturedScreen(new Rectangle(-800, -100, 40, 30), source), [], new ScreenCaptureService()) { Width = 640, Height = 480 };
        try
        {
            // Coordinate conversion uses the window's arranged DIP surface. Laying
            // out Root alone leaves Window.ActualWidth/ActualHeight at zero.
            window.Show(); window.UpdateLayout();
            foreach (var point in new[] { new Point(0, 0), new Point(window.Root.ActualWidth - 1, window.Root.ActualHeight - 1), new Point(window.Root.ActualWidth / 2, window.Root.ActualHeight / 2) })
            {
                window.UpdatePointer(point);
                if (window._sampledColor != "#FF5733" || window.ZoomImage.Source is not BitmapSource zoom || zoom.PixelWidth != 19 || zoom.PixelHeight != 11) throw new InvalidOperationException($"Color sampler failed at {point}: {window._sampledColor}, window {window.ActualWidth}x{window.ActualHeight}");
                var center = new byte[4]; zoom.CopyPixels(new Int32Rect(9, 5, 1, 1), center, 4, 0);
                if (!center.SequenceEqual(new byte[] { 51, 87, 255, 255 })) throw new InvalidOperationException("Magnifier center mismatch: " + string.Join(",", center));
            }
            window.UpdatePointer(new Point(0, 0));
            if (window.ColorCoordinates.Text != "(-800, -100)") return false;
            for (var i = 0; i < 7; i++)
            {
                if (window._sampledColor != ScreenColorFormatter.Format(Color.FromRgb(255, 87, 51), (ScreenColorFormat)i).Value) return false;
                window._colorFormat = ScreenColorFormatter.Next(window._colorFormat); window.UpdatePointer(new Point(0, 0));
            }
            var canceled = 0; var accepted = false; var copied = "";
            window.CaptureCanceled += (_, _) => canceled++;
            window.CaptureAccepted += (_, _) => accepted = true;
            window.CopySampledColor(_ => throw new System.Runtime.InteropServices.ExternalException("busy"));
            if (canceled != 0) return false;
            window.CopySampledColor(value => copied = value);
            return canceled == 1 && !accepted && copied == "#FF5733";
        }
        finally { window.Close(); }
    }

    private Rect SuggestedWindowAt(Point point)
    {
        var physical = ToScreenPoint(point);
        var candidate = _captureService.FindWindowAt(physical) ?? _windows.FirstOrDefault(w => w.Bounds.Contains(physical));
        if (candidate != _suggestedWindow)
        {
            _suggestedWindow = candidate;
            _includeWindowFrame = false;
        }
        return candidate is null ? Rect.Empty : FromScreenRect(Rectangle.Intersect(candidate.SelectionBounds(_includeWindowFrame), _screen.Bounds));
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_editing || _dragging || e.Delta == 0) return;
        if (e.OriginalSource is DependencyObject source && CapturePanel.IsAncestorOf(source)) return;
        var point = Clamp(e.GetPosition(Root));
        if (_mode == OverlayMode.Element)
        {
            AdjustElementScope(point, e.Delta);
            e.Handled = true;
            return;
        }
        if (_mode != OverlayMode.Window) return;
        AdjustWindowScope(point, e.Delta);
        e.Handled = true;
    }

    private void AdjustWindowScope(Point point, int delta)
    {
        SuggestedWindowAt(point);
        if (_suggestedWindow is null) return;
        // Wheel down expands to the frame; wheel up returns to the client area.
        _includeWindowFrame = delta < 0;
        _selection = SuggestedWindowAt(point);
        UpdateSelection();
    }

    private Point DesktopToLocal(double x, double y) => ScreenCoordinateMapper.DesktopPixelsToSurface(
        new Point(x, y), _screen.Bounds, new System.Windows.Size(ActualWidth, ActualHeight));

    private Rect ChromeViewport(Point localPointer)
    {
        var point = ToScreenPoint(localPointer);
        var bounds = _displayBounds.OrderBy(r =>
            Math.Pow(point.X - Math.Clamp(point.X, r.Left, r.Right), 2) +
            Math.Pow(point.Y - Math.Clamp(point.Y, r.Top, r.Bottom), 2)).FirstOrDefault();
        return FromScreenRect(bounds.Width > 0 && bounds.Height > 0 ? bounds : _screen.Bounds);
    }

    private Rect FromScreenRect(Rectangle rect) => new(DesktopToLocal(rect.Left, rect.Top), DesktopToLocal(rect.Right, rect.Bottom));

    private System.Drawing.Point ToScreenPoint(Point point)
    {
        var desktop = ScreenCoordinateMapper.SurfaceToDesktopPixels(point, _screen.Bounds, new System.Windows.Size(ActualWidth, ActualHeight));
        return new((int)Math.Round(desktop.X), (int)Math.Round(desktop.Y));
    }

    private PixelRect ToPixelSelection(Rect selection) => ScreenCoordinateMapper.DipRectToImagePixels(selection,
        new System.Windows.Size(ActualWidth, ActualHeight), _screen.Image.PixelWidth, _screen.Image.PixelHeight);

    private void UpdateSelection()
    {
        if (IsLoaded) UpdatePrecisionGuides(_lastPointer);
        UpdateMovementHint();
        if (!IsValidSelection())
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            InfoText.Text = L.Get("SelectRegion");
        }
        else
        {
            SelectionBorder.Visibility = _shape == SelectionShape.Rectangle ? Visibility.Visible : Visibility.Collapsed;
            SelectionBorder.Width = _selection.Width;
            SelectionBorder.Height = _selection.Height;
            CanvasPosition(SelectionBorder, _selection.X, _selection.Y);
            var pixels = ToPixelSelection(_selection);
            InfoText.Text = $"{pixels.X + _screen.Bounds.Left},{pixels.Y + _screen.Bounds.Top}  {pixels.Width} x {pixels.Height} px";
            if (_mode == OverlayMode.Element && !_editing && !_dragging)
                InfoText.Text += "  " + ElementDescription();
            CanvasPosition(InfoPanel, _selection.X, Math.Max(4, _selection.Y - (MovementHintText.Visibility == Visibility.Visible ? 52 : 32)));
        }
        UpdateShapeOutline();
        UpdateShade();
        if (_editing) PositionChrome();
    }

    private void UpdateShapeOutline()
    {
        Geometry? geometry = null;
        if (IsValidSelection() && _shape == SelectionShape.Ellipse) geometry = new EllipseGeometry(_selection);
        else if (IsValidSelection() && _shape is SelectionShape.Lasso or SelectionShape.Polygon && _normalizedPath.Count >= 3)
            geometry = CreatePathGeometry(_normalizedPath.Select(p => new Point(
                _selection.Left + p.X * _selection.Width, _selection.Top + p.Y * _selection.Height)));
        ShapeOutline.Data = geometry;
        ShapeOutline.Visibility = geometry is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdatePathPreview(Point pointer)
    {
        var points = _path.ToList();
        if (_shape == SelectionShape.Polygon && points.Count > 0) points.Add(pointer);
        var outline = CreatePathGeometry(points, false);
        if (_shape == SelectionShape.Polygon)
        {
            var markers = new GeometryGroup();
            markers.Children.Add(outline);
            foreach (var node in _path) markers.Children.Add(new EllipseGeometry(node, 3, 3));
            ShapeOutline.Data = markers;
        }
        else ShapeOutline.Data = outline;
        ShapeOutline.Visibility = points.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static PathGeometry CreatePathGeometry(IEnumerable<Point> points, bool close = true)
    {
        var array = points.ToArray();
        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        if (array.Length == 0) return geometry;
        var figure = new PathFigure { StartPoint = array[0], IsClosed = close, IsFilled = close };
        figure.Segments.Add(new PolyLineSegment(array.Skip(1), true));
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void PositionChrome()
    {
        PositionRecognitionActions();
        if (SummaryResultPanel.Visibility == Visibility.Visible) PositionSummaryPanel();
        if (_textRecognitionActive) PositionTextSelectionLayer();
        const double half = 6;
        CanvasPosition(TopLeftHandle, _selection.Left - half, _selection.Top - half);
        CanvasPosition(TopCenterHandle, _selection.Left + _selection.Width / 2 - half, _selection.Top - half);
        CanvasPosition(TopRightHandle, _selection.Right - half, _selection.Top - half);
        CanvasPosition(MiddleLeftHandle, _selection.Left - half, _selection.Top + _selection.Height / 2 - half);
        CanvasPosition(MiddleRightHandle, _selection.Right - half, _selection.Top + _selection.Height / 2 - half);
        CanvasPosition(BottomLeftHandle, _selection.Left - half, _selection.Bottom - half);
        CanvasPosition(BottomCenterHandle, _selection.Left + _selection.Width / 2 - half, _selection.Bottom - half);
        CanvasPosition(BottomRightHandle, _selection.Right - half, _selection.Bottom - half);
        SetHandleAvailable(TopCenterHandle, _selection.Width >= 80);
        SetHandleAvailable(BottomCenterHandle, _selection.Width >= 80);
        SetHandleAvailable(MiddleLeftHandle, _selection.Height >= 80);
        SetHandleAvailable(MiddleRightHandle, _selection.Height >= 80);
        var viewport = ChromeViewport(_lastPointer);
        var available = Math.Max(120, viewport.Width - 16);
        var primarySize = MeasureToolbar(ToolPanel, available);
        var width = primarySize.Width;
        var x = Math.Clamp(_selection.Left + (_selection.Width - width) / 2, viewport.Left + 8,
            Math.Max(viewport.Left + 8, viewport.Right - width - 8));
        var height = primarySize.Height;
        var secondaryVisible = SecondaryToolPanel.Visibility == Visibility.Visible;
        var secondaryWidth = 0d;
        var secondaryHeight = 0d;
        if (secondaryVisible)
        {
            var secondarySize = MeasureSecondaryToolbar(available);
            secondaryWidth = secondarySize.Width;
            secondaryHeight = secondarySize.Height;
        }

        const double gap = 6;
        var belowY = _selection.Bottom + 12;
        var totalHeight = height + (secondaryVisible ? gap + secondaryHeight : 0);
        var mainY = belowY + totalHeight <= viewport.Bottom - 8 && belowY >= viewport.Top + 8
            ? belowY
            : Math.Clamp(_selection.Top - totalHeight - 12, viewport.Top + 8,
                Math.Max(viewport.Top + 8, viewport.Bottom - totalHeight - 8));
        CanvasPosition(ToolPanel, x, mainY);
        if (secondaryVisible)
        {
            var secondaryX = Math.Clamp(x, viewport.Left + 8, Math.Max(viewport.Left + 8, viewport.Right - secondaryWidth - 8));
            CanvasPosition(SecondaryToolPanel, secondaryX, mainY + height + gap);
        }
    }

    internal static bool RunScrollingSelectionSelfTest(BitmapSource source)
    {
        var bounds = new Rectangle(-1200, -200, source.PixelWidth, source.PixelHeight);
        var window = new SelectionWindow(new CapturedScreen(bounds, source), [], new ScreenCaptureService(), true);
        try
        {
            if (window.PrimaryCaptureButton.Content is not TextBlock { Text: "开始长截图 ↵" } ||
                window.SaveCaptureButton.Visibility != Visibility.Collapsed || window.ShapePicker.IsEnabled) return false;
            CaptureResult? accepted = null;
            var canceled = false;
            window.CaptureAccepted += (_, result) => accepted = result;
            window.CaptureCanceled += (_, _) => canceled = true;
            window.Cancel_Click(window, new RoutedEventArgs());
            if (!canceled || accepted is not null) return false;
            window._pixelSelection = new(10, 12, 40, 36);
            window._editor = new EditorViewModel(source);
            window._editor.Add(new ShapeAnnotation(AnnotationTool.Rectangle, new Point(0, 0), new Point(40, 36), Colors.Red, 8));
            // The primary confirmation must retain physical coordinates and ignore screenshot annotations.
            window.Copy_Click(window.PrimaryCaptureButton, new RoutedEventArgs());
            if (accepted is null || accepted.Action != CaptureAction.Scrolling || accepted.ScreenBounds != bounds ||
                accepted.PixelRegion != new Rectangle(10, 12, 40, 36)) return false;
            var expected = window._skia.Crop(source, new Int32Rect(10, 12, 40, 36));
            var left = new byte[40 * 36 * 4]; var right = new byte[left.Length];
            new FormatConvertedBitmap(expected, PixelFormats.Bgra32, null, 0).CopyPixels(left, 40 * 4, 0);
            new FormatConvertedBitmap(accepted.Image, PixelFormats.Bgra32, null, 0).CopyPixels(right, 40 * 4, 0);
            return left.SequenceEqual(right);
        }
        finally { window.Close(); }
    }

    internal static bool RunDoubleClickCopySelfTest(BitmapSource source)
    {
        var window = new SelectionWindow(new CapturedScreen(
            new Rectangle(0, 0, source.PixelWidth, source.PixelHeight), source), [], new ScreenCaptureService())
            { Width = source.PixelWidth, Height = source.PixelHeight };
        try
        {
            // A transparent HWND lets WPF finish Window layout without showing test content.
            // Arranging only the child grid leaves Window.ActualWidth/ActualHeight at zero.
            window.Opacity = 0;
            window.ShowActivated = false;
            window.Show();
            window.UpdateLayout();
            window._selection = new Rect(2, 2, Math.Max(3, source.PixelWidth - 4), Math.Max(3, source.PixelHeight - 4));
            window.BeginInlineEditing();
            CaptureResult? accepted = null;
            window.CaptureAccepted += (_, result) => accepted = result;
            var center = new Point(window._selection.Left + window._selection.Width / 2,
                window._selection.Top + window._selection.Height / 2);
            if (!window.TryCompleteDoubleClickCopy(2, center) || accepted is not { Action: CaptureAction.Copy }) return false;

            accepted = null;
            window._editor!.SelectedTool = AnnotationTool.Select;
            if (!window.AnnotationView.AllowsWindowDoubleClickCopy(new Point(1, 1)) ||
                !window.TryCompleteDoubleClickCopy(2, center) || accepted is not { Action: CaptureAction.Copy }) return false;

            accepted = null;
            window._editor!.SelectedTool = AnnotationTool.Number;
            return !window.TryCompleteDoubleClickCopy(2, center) && accepted is null &&
                   !window.TryCompleteDoubleClickCopy(2, new Point(window._selection.Right + 1, center.Y));
        }
        finally { window.Close(); }
    }

    internal static bool RunPrimaryToolbarSelfTest(BitmapSource source)
    {
        var window = new SelectionWindow(new CapturedScreen(new Rectangle(0, 0, source.PixelWidth, source.PixelHeight), source), [], new ScreenCaptureService());
        try
        {
            window._editor = new EditorViewModel(source);
            window.DataContext = window._editor;
            var groups = window.ToolButtons.Children.OfType<ToolGroupButton>().ToArray();
            if (groups.Length != 4 || groups.SelectMany(group => group.Tools).Distinct().Count() != 12) return false;
            foreach (var group in groups)
            foreach (var tool in group.Tools)
            {
                window._moving = true;
                window.AnnotationView.IsHitTestVisible = false;
                group.ChooseTool(tool);
                if (window._editor.SelectedTool != tool || window._moving || !window.AnnotationView.IsHitTestVisible ||
                    window.EffectiveTool() != tool || group.CurrentTool != tool) return false;
                var font = tool is AnnotationTool.Text or AnnotationTool.Bubble or AnnotationTool.Watermark;
                if ((window.FontOptions.Visibility == Visibility.Visible) != font) return false;
                window.Tool_Click(new System.Windows.Controls.Button { Tag = "Select" }, new RoutedEventArgs());
                group.ActivateCurrentTool();
                if (window._editor.SelectedTool != tool) return false;
            }
            foreach (var button in window.ToolButtons.Children.OfType<System.Windows.Controls.Button>().Where(button => button.Tag is string))
            {
                window.Tool_Click(button, new RoutedEventArgs());
                if (window._editor.SelectedTool.ToString() != button.Tag.ToString()) return false;
            }
            foreach (var style in Enum.GetValues<AnnotationArrowStyle>())
            {
                window.PrimaryArrowStyleButton_Click(new System.Windows.Controls.Button { Tag = style.ToString() }, new RoutedEventArgs());
                if (window._editor.SelectedTool != AnnotationTool.Arrow || window._editor.ArrowStyle != style) return false;
            }
            // Exercise the actual XAML-wired parameter handlers, not just model setters.
            window._editor.Add(new ShapeAnnotation(AnnotationTool.Rectangle, new Point(2, 2), new Point(40, 30), Colors.Red, 2));
            window.UpdateSecondaryToolbar();
            window.ThicknessSlider.Value = 11;
            window.FillToggle.IsChecked = true;
            window.Fill_Changed(window.FillToggle, new RoutedEventArgs());
            window.FillOpacitySlider.Value = 66;
            window.StrokeStyleButton_Click(new System.Windows.Controls.Button { Tag = "Dash" }, new RoutedEventArgs());
            if (window._editor.SelectedAnnotation is not ShapeAnnotation { StrokeThickness: 11, StrokeStyle: AnnotationStrokeStyle.Dash, FillColor: { A: 168 } }) return false;

            window._editor.Add(new TextAnnotation(new Point(2, 2), "test", Colors.Red, 24));
            window.UpdateSecondaryToolbar();
            window.FontSizeSlider.Value = 40;
            window.OutlineEnabledToggle.IsChecked = true;
            window.OutlineEnabled_Changed(window.OutlineEnabledToggle, new RoutedEventArgs());
            window.TextOutlineSlider.Value = 3;
            if (window._editor.SelectedAnnotation is not TextAnnotation { FontSize: 40, TextOutlineThickness: 3, TextOutlineColor: not null }) return false;

            window._editor.Add(new NumberAnnotation(new Point(20, 20), 3, Colors.Red, 12));
            window.UpdateSecondaryToolbar();
            window.NumberValueBox.Text = "27"; window.CommitNumberValue();
            window.NumberStylePicker.SelectedIndex = 2;
            if (window._editor.SelectedAnnotation is not NumberAnnotation { Number: 27, NumberStyle: AnnotationNumberStyle.LowerAlpha }) return false;

            foreach (var effect in new Annotation[]
            {
                new RegionEffectAnnotation(AnnotationTool.Mosaic, new Rect(2, 2, 30, 20), 8),
                new RegionEffectAnnotation(AnnotationTool.Blur, new Rect(2, 2, 30, 20), 8),
                new SpotlightAnnotation(new Rect(2, 2, 30, 20)),
                new MagnifierAnnotation(new Point(20, 20), new Rect(40, 20, 30, 20))
            })
            {
                window._editor.Add(effect); window.UpdateSecondaryToolbar();
                var value = effect is SpotlightAnnotation ? 70 : effect is MagnifierAnnotation ? 45 : 17;
                window.EffectSlider.Value = value;
                var correct = window._editor.SelectedAnnotation switch
                {
                    RegionEffectAnnotation region => region.Strength == 17,
                    SpotlightAnnotation spotlight => Math.Abs(spotlight.Darkness - 0.7) < 0.001,
                    MagnifierAnnotation lens => Math.Abs(lens.Zoom - 4.5) < 0.001,
                    _ => false
                };
                if (!correct) return false;
            }
            foreach (var available in new[] { 1280d, 320d, 960d, 639.2, 1920d })
            {
                window.ToolPanel.Visibility = Visibility.Visible;
                var size = window.MeasureToolbar(window.ToolPanel, available);
                window.ToolPanel.Arrange(new Rect(new Point(), size));
                foreach (var child in window.ToolButtons.Children.OfType<FrameworkElement>())
                {
                    if (child.Visibility != Visibility.Visible) continue;
                    var bounds = child.TransformToAncestor(window.ToolPanel).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
                    if (bounds.Right > window.ToolPanel.ActualWidth + 1 || bounds.Bottom > window.ToolPanel.ActualHeight + 1) return false;
                }
            }
            return true;
        }
        finally { window.Close(); }
    }

    internal static bool RunSecondaryToolbarLayoutSelfTest(BitmapSource source)
    {
        var window = new SelectionWindow(new CapturedScreen(
            new Rectangle(0, 0, source.PixelWidth, source.PixelHeight), source), [], new ScreenCaptureService());
        try
        {
            window._editor = new EditorViewModel(source);
            // Reverse the list on alternate passes to catch both growing and shrinking toolbars.
            var widths = new[] { 1280d, 320d, 960d, 480d, 640d, 1920d, 639.2, 479.333333, 730.285714 };
            var tools = Enum.GetValues<AnnotationTool>();
            foreach (var available in widths)
            {
                foreach (var tool in tools.Concat(tools.Reverse()))
                foreach (var selected in new[] { false, true })
                {
                    var model = window._editor;
                    model.SelectedTool = selected ? AnnotationTool.Select : tool;
                    model.SelectedAnnotation = selected ? tool switch
                    {
                        AnnotationTool.Text => new TextAnnotation(new Point(2, 2), "test", Colors.Red, 96),
                        AnnotationTool.Watermark => new WatermarkAnnotation(new Point(2, 2), "test", Colors.Red, 96, 0.5, 0, true, 96, 64),
                        AnnotationTool.Number => new NumberAnnotation(new Point(20, 20), 9999, Colors.Red, 12),
                        AnnotationTool.Bubble => new BubbleAnnotation(new Rect(2, 2, 40, 30), new Point(50, 40), "test", Colors.Red, 4, Colors.White, 96),
                        AnnotationTool.Mosaic or AnnotationTool.Blur => new RegionEffectAnnotation(tool, new Rect(2, 2, 40, 30), 8),
                        AnnotationTool.Spotlight => new SpotlightAnnotation(new Rect(2, 2, 40, 30)),
                        AnnotationTool.Magnifier => new MagnifierAnnotation(new Point(2, 2), new Rect(10, 10, 30, 30)),
                        AnnotationTool.Pen or AnnotationTool.Highlight => new PenAnnotation([new Point(2, 2), new Point(40, 30)], Colors.Red, 4, tool == AnnotationTool.Highlight),
                        AnnotationTool.Polygon => new PolygonAnnotation([new Point(2, 2), new Point(40, 2), new Point(20, 30)], Colors.Red, 4),
                        AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Line or AnnotationTool.Arrow =>
                            new ShapeAnnotation(tool, new Point(2, 2), new Point(40, 30), Colors.Red, 4),
                        _ => null
                    } : null;
                    window.UpdateSecondaryToolbar();
                    if (window.SecondaryToolPanel.Visibility == Visibility.Collapsed) continue;
                    var size = window.MeasureSecondaryToolbar(available);
                    window.SecondaryToolPanel.Arrange(new Rect(new Point(), size));
                    if (size.Width > available + 0.1 || size.Height < 46)
                        throw new InvalidOperationException($"Invalid secondary toolbar size: {tool}, selected={selected}, width={available}, size={size}");
                    CheckChildren(window.SecondaryToolButtons, tool, selected, available);
                    if (window.EffectiveTool() == AnnotationTool.Watermark &&
                        (window.FontFamilyControl.Visibility != Visibility.Collapsed || window.TextOutlineControl.Visibility != Visibility.Collapsed)) return false;
                }
            }
            return true;

            void CheckChildren(System.Windows.Controls.Panel panel, AnnotationTool tool, bool selected, double available)
            {
                foreach (var child in panel.Children.OfType<FrameworkElement>())
                {
                    if (child.Visibility != Visibility.Visible || child is System.Windows.Controls.Primitives.Popup) continue;
                    var bounds = child.TransformToAncestor(window.SecondaryToolPanel)
                        .TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
                    if (bounds.Left < -0.1 || bounds.Right > window.SecondaryToolPanel.ActualWidth + 0.1 ||
                        bounds.Top < -0.1 || bounds.Bottom > window.SecondaryToolPanel.ActualHeight + 0.1)
                        throw new InvalidOperationException($"Clipped secondary toolbar: {tool}, selected={selected}, width={available}, child={child.Name}, bounds={bounds}");
                    if (child is System.Windows.Controls.Panel nested) CheckChildren(nested, tool, selected, available);
                }
            }
        }
        finally { window.Close(); }
    }

    private System.Windows.Size MeasureSecondaryToolbar(double available)
        => MeasureToolbar(SecondaryToolPanel, available);

    private System.Windows.Size MeasureToolbar(Border panel, double available)
    {
        // Never measure against the previous tool's explicit width. A short toolbar
        // (e.g. mosaic) must not constrain the next, longer one (e.g. text/bubble).
        panel.ClearValue(WidthProperty);
        panel.MaxWidth = available;
        panel.InvalidateMeasure();
        panel.Measure(new System.Windows.Size(available, double.PositiveInfinity));
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // One device pixel of breathing room prevents rounding from rewrapping the last item.
        var width = Math.Min(available, (Math.Ceiling(panel.DesiredSize.Width * dpi) + 1) / dpi);
        panel.Width = width;
        panel.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        return new System.Windows.Size(width, Math.Max(46, panel.DesiredSize.Height));
    }

    private static void SetHandleAvailable(UIElement handle, bool available)
    {
        handle.Opacity = available ? 1 : 0;
        handle.IsHitTestVisible = available;
    }

    private void UpdateShade()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        geometry.AddGeometry(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        if (IsValidSelection())
        {
            if (_shape == SelectionShape.Ellipse) geometry.AddGeometry(new EllipseGeometry(_selection));
            else if (_shape is SelectionShape.Lasso or SelectionShape.Polygon && _normalizedPath.Count >= 3)
                geometry.AddGeometry(CreatePathGeometry(_normalizedPath.Select(p => new Point(
                    _selection.Left + p.X * _selection.Width, _selection.Top + p.Y * _selection.Height))));
            else geometry.AddGeometry(new RectangleGeometry(_selection));
        }
        Shade.Data = geometry;
    }

    private void UpdatePointer(Point point)
    {
        UpdatePrecisionGuides(point);
        var viewWidth = Root.ActualWidth; var viewHeight = Root.ActualHeight;
        if (_editing || viewWidth <= 0 || viewHeight <= 0) return;
        _lastPointer = point;
        var px = Math.Clamp((int)Math.Floor(point.X * _screen.Image.PixelWidth / viewWidth + 1e-7), 0, _screen.Image.PixelWidth - 1);
        var py = Math.Clamp((int)Math.Floor(point.Y * _screen.Image.PixelHeight / viewHeight + 1e-7), 0, _screen.Image.PixelHeight - 1);
        const int cropWidth = 19;
        const int cropHeight = 11;
        // Pad outside the monitor instead of shifting the crop: the sampled pixel stays centered at every edge.
        var originX = px - cropWidth / 2; var originY = py - cropHeight / 2;
        var left = Math.Max(0, originX); var top = Math.Max(0, originY);
        var right = Math.Min(_colorPixels.PixelWidth, originX + cropWidth); var bottom = Math.Min(_colorPixels.PixelHeight, originY + cropHeight);
        var zoomPixels = new byte[cropWidth * cropHeight * 4];
        for (var i = 3; i < zoomPixels.Length; i += 4) zoomPixels[i] = 255;
        _colorPixels.CopyPixels(new Int32Rect(left, top, right - left, bottom - top), zoomPixels, cropWidth * 4, ((top - originY) * cropWidth + left - originX) * 4);
        ZoomImage.Source = BitmapSource.Create(cropWidth, cropHeight, 96, 96, PixelFormats.Bgra32, null, zoomPixels, cropWidth * 4);
        var pixel = new byte[4];
        _colorPixels.CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);
        var color = Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        var formatted = ScreenColorFormatter.Format(color, _colorFormat);
        _sampledColor = formatted.Value; ColorText.Text = formatted.Value; ColorFormatText.Text = formatted.Label;
        ColorCoordinates.Text = $"({px + _screen.Bounds.Left}, {py + _screen.Bounds.Top})";
        MagnifierColorSwatch.Background = new SolidColorBrush(color);
        // Virtual desktops can have holes. Keep interactive chrome on the pointer's real display.
        var viewport = ChromeViewport(point);
        CapturePanel.MaxWidth = viewport.Width;
        CapturePanel.Measure(new System.Windows.Size(viewport.Width, viewport.Height));
        var panelWidth = Math.Min(viewport.Width, CapturePanel.DesiredSize.Width);
        CanvasPosition(CapturePanel, viewport.Left + (viewport.Width - panelWidth) / 2, viewport.Top + 66);
        var scale = Math.Min(1, Math.Min(viewport.Width / Magnifier.Width, viewport.Height / Magnifier.Height));
        Magnifier.LayoutTransform = scale < 1 ? new ScaleTransform(scale, scale) : Transform.Identity;
        var width = Magnifier.Width * scale; var height = Magnifier.Height * scale;
        var x = point.X + 22 + width > viewport.Right ? point.X - width - 22 : point.X + 22;
        var maxY = Math.Max(viewport.Top, viewport.Bottom - height);
        var chromeBottom = viewport.Top + 66 + CapturePanel.ActualHeight + 8;
        var y = Math.Clamp(point.Y + 22, Math.Min(chromeBottom, maxY), maxY);
        CanvasPosition(Magnifier, Math.Clamp(x, viewport.Left, Math.Max(viewport.Left, viewport.Right - width)), y);
    }

    private Rect CreateRect(Point anchor, Point pointer, double ratio, string? edge = null)
    {
        var dx = pointer.X - anchor.X;
        var dy = pointer.Y - anchor.Y;
        if (ratio > 0)
        {
            if (edge is "Top" or "Bottom") dx = Math.CopySign(Math.Abs(dy) * ratio, dx == 0 ? 1 : dx);
            else if (edge is "Left" or "Right") dy = Math.CopySign(Math.Abs(dx) / ratio, dy == 0 ? 1 : dy);
            else if (Math.Abs(dx) / Math.Max(0.001, Math.Abs(dy)) > ratio) dy = Math.CopySign(Math.Abs(dx) / ratio, dy == 0 ? 1 : dy);
            else dx = Math.CopySign(Math.Abs(dy) * ratio, dx == 0 ? 1 : dx);
        }
        if (ratio > 0)
        {
            var scaleX = dx > 0 ? (ActualWidth - anchor.X) / dx : dx < 0 ? -anchor.X / dx : 1;
            var scaleY = dy > 0 ? (ActualHeight - anchor.Y) / dy : dy < 0 ? -anchor.Y / dy : 1;
            var scale = Math.Clamp(Math.Min(scaleX, scaleY), 0, 1);
            dx *= scale;
            dy *= scale;
        }
        return Normalize(anchor, Clamp(new Point(anchor.X + dx, anchor.Y + dy)));
    }

    private double CurrentRatio(double fallback) => (_mode == OverlayMode.Region ? _ratio : null) ??
        (_shiftRatio || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Math.Max(0.001, fallback) : 0);

    private Point SnapPoint(Point point)
    {
        if (_pixelNudgeActive) return point;
        var thresholdX = 8d * ActualWidth / Math.Max(1, _screen.Bounds.Width);
        var thresholdY = 8d * ActualHeight / Math.Max(1, _screen.Bounds.Height);
        var (xs, ys) = GetSnapAxes();
        var x = xs.OrderBy(v => Math.Abs(v - point.X)).First();
        var y = ys.OrderBy(v => Math.Abs(v - point.Y)).First();
        return new Point(Math.Abs(x - point.X) <= thresholdX ? x : point.X,
            Math.Abs(y - point.Y) <= thresholdY ? y : point.Y);
    }

    private Rect SnapAndClampMove(Rect rect)
    {
        var thresholdX = 8d * ActualWidth / Math.Max(1, _screen.Bounds.Width);
        var thresholdY = 8d * ActualHeight / Math.Max(1, _screen.Bounds.Height);
        var (xs, ys) = GetSnapAxes();
        var dx = BestSnapDelta([rect.Left, rect.Right], xs, thresholdX);
        var dy = BestSnapDelta([rect.Top, rect.Bottom], ys, thresholdY);
        return new Rect(Math.Clamp(rect.X + dx, 0, Math.Max(0, ActualWidth - rect.Width)),
            Math.Clamp(rect.Y + dy, 0, Math.Max(0, ActualHeight - rect.Height)), rect.Width, rect.Height);
    }

    private (List<double> X, List<double> Y) GetSnapAxes()
    {
        var xs = new List<double> { 0, ActualWidth };
        var ys = new List<double> { 0, ActualHeight };
        foreach (var candidate in _windows)
        {
            var clipped = Rectangle.Intersect(candidate.Bounds, _screen.Bounds);
            if (clipped.IsEmpty) continue;
            var rect = FromScreenRect(clipped);
            xs.Add(rect.Left); xs.Add(rect.Right); ys.Add(rect.Top); ys.Add(rect.Bottom);
        }
        return (xs, ys);
    }

    private static double BestSnapDelta(IEnumerable<double> edges, IEnumerable<double> targets, double threshold)
    {
        var candidates = from edge in edges from target in targets
                         let delta = target - edge
                         where Math.Abs(delta) <= threshold
                         orderby Math.Abs(delta)
                         select delta;
        return candidates.Cast<double?>().FirstOrDefault() ?? 0;
    }

    private static List<Point> Simplify(IReadOnlyList<Point> points, double tolerance)
    {
        if (points.Count <= 2) return points.ToList();
        var max = 0d;
        var index = 0;
        for (var i = 1; i < points.Count - 1; i++)
        {
            var distance = DistanceToSegment(points[i], points[0], points[^1]);
            if (distance > max) { max = distance; index = i; }
        }
        if (max <= tolerance) return [points[0], points[^1]];
        var left = Simplify(points.Take(index + 1).ToArray(), tolerance);
        var right = Simplify(points.Skip(index).ToArray(), tolerance);
        return left.Take(left.Count - 1).Concat(right).ToList();
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var length = (b - a).LengthSquared;
        if (length == 0) return (p - a).Length;
        var t = Math.Clamp(Vector.Multiply(p - a, b - a) / length, 0, 1);
        return (p - (a + (b - a) * t)).Length;
    }

    private bool IsValidSelection() => !_selection.IsEmpty && ActualWidth > 0 && ActualHeight > 0 &&
        _selection.Width * _screen.Image.PixelWidth / ActualWidth >= 1 - 1e-7 &&
        _selection.Height * _screen.Image.PixelHeight / ActualHeight >= 1 - 1e-7;
    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, ActualWidth), Math.Clamp(point.Y, 0, ActualHeight));
    private Point GetLogicalPointer(MouseEventArgs e) => NativeMethods.GetCursorPos(out var cursor)
        ? DesktopToLocal(cursor.X, cursor.Y) : e.GetPosition(Root);
    private static Rect Normalize(Point a, Point b) => new(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)),
        new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    private static void CanvasPosition(FrameworkElement element, double x, double y)
    {
        element.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        element.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        element.Margin = new Thickness(0);
        element.RenderTransformOrigin = new Point(0, 0);
        element.RenderTransform = new TranslateTransform(x, y);
    }
}

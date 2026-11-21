using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Services;
using NimCap.Screenshot.Views;
using ComboBox = System.Windows.Controls.ComboBox;
using Point = System.Windows.Point;

namespace NimCap.Screenshot.WindowChecks;

internal static partial class Program
{
    private static void CapturePrecisionChecks()
    {
        var image = PrecisionImage();
        var screen = new CapturedScreen(new System.Drawing.Rectangle(0, 0, image.PixelWidth, image.PixelHeight), image);
        var originalCursor = System.Windows.Forms.Cursor.Position;
        try
        {
            foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2d })
            {
                var overlay = new SelectionWindow(screen, [], new PrecisionCapture());
                try
                {
                    overlay.Show(); overlay.UpdateLayout();
                    overlay.Width = image.PixelWidth / scale; overlay.Height = image.PixelHeight / scale; overlay.UpdateLayout();
                    var mode = (ComboBox)overlay.FindName("CaptureModePicker");
                    var shape = (ComboBox)overlay.FindName("ShapePicker");
                    var ratio = (ComboBox)overlay.FindName("RatioPicker");
                    var initialGuides = (PixelGuides)overlay.FindName("PrecisionGuides");
                    Require(!initialGuides.ShowRulers && !initialGuides.ShowCrosshair, "Capture guides are visible before the user enables them.");
                    Require(!initialGuides.InsetOrigin && initialGuides.MeasurementBounds.TopLeft == new Point(),
                        "Full-screen capture rulers no longer start at the screen edge.");
                    Require(mode.SelectedIndex == 1 && shape.IsEnabled && ratio.IsEnabled, "Element mode disabled shape/ratio options.");
                    shape.SelectedIndex = 1;
                    Require(mode.SelectedIndex == 0 && ratio.IsEnabled, "Ellipse selection did not switch to region mode.");
                    mode.SelectedIndex = 1; ratio.SelectedIndex = 2;
                    Require(mode.SelectedIndex == 0, "Aspect ratio selection did not switch to region mode.");
                    mode.SelectedIndex = 1;
                    Invoke(overlay, "CapturePicker_Closed", shape, EventArgs.Empty);
                    Require(mode.SelectedIndex == 0 && shape.IsEnabled, "Selecting the existing rectangle did not switch to region mode.");
                    shape.SelectedIndex = 2;
                    Require(!ratio.IsEnabled, "Lasso incorrectly advertises an aspect ratio.");
                    shape.SelectedIndex = 0; ratio.SelectedIndex = 0;

                    var point = new Point(100 * overlay.ActualWidth / image.PixelWidth, 120 * overlay.ActualHeight / image.PixelHeight);
                    Invoke(overlay, "UpdatePointer", point);
                    Invoke(overlay, "NudgeCapture", 1, 0, false);
                    Require(((TextBlock)overlay.FindName("ColorCoordinates")).Text == "(101, 120)", "Pointer nudge was not 1 physical pixel at scale " + scale);
                    Invoke(overlay, "NudgeCapture", 0, -1, false);
                    Require(((TextBlock)overlay.FindName("ColorCoordinates")).Text == "(101, 119)", "Vertical pointer nudge was not 1 pixel.");
                    Invoke(overlay, "UpdatePointer", new Point(overlay.ActualWidth, overlay.ActualHeight));
                    Invoke(overlay, "NudgeCapture", 1, 1, false);
                    Require(((TextBlock)overlay.FindName("ColorCoordinates")).Text == "(959, 719)", "Pointer escaped the last monitor pixel.");

                    // Precision changes bypass the ordinary eight-pixel edge snapping.
                    Invoke(overlay, "UpdatePointer", new Point(2 * overlay.ActualWidth / image.PixelWidth, 2 * overlay.ActualHeight / image.PixelHeight));
                    Invoke(overlay, "NudgeCapture", 1, 0, false);
                    var exact = new Point(3 * overlay.ActualWidth / image.PixelWidth, 2 * overlay.ActualHeight / image.PixelHeight);
                    Require((Point)Invoke(overlay, "SnapPoint", exact)! == exact, "Edge snapping overrode a keyboard nudge.");

                    var selected = new Rect(100 * overlay.ActualWidth / image.PixelWidth, 120 * overlay.ActualHeight / image.PixelHeight,
                        300 * overlay.ActualWidth / image.PixelWidth, 200 * overlay.ActualHeight / image.PixelHeight);
                    typeof(SelectionWindow).GetField("_selection", PrivateInstance)!.SetValue(overlay, selected);
                    Invoke(overlay, "BeginInlineEditing");
                    Invoke(overlay, "NudgeCapture", 1, -1, false);
                    var pixels = (PixelRect)typeof(SelectionWindow).GetField("_pixelSelection", PrivateInstance)!.GetValue(overlay)!;
                    Require(pixels == new PixelRect(101, 119, 300, 200), "Selection movement was not 1 physical pixel at scale " + scale);
                    Invoke(overlay, "NudgeCapture", 1, -1, true);
                    pixels = (PixelRect)typeof(SelectionWindow).GetField("_pixelSelection", PrivateInstance)!.GetValue(overlay)!;
                    Require(pixels == new PixelRect(101, 119, 301, 199), "Selection resizing was not 1 physical pixel.");
                    Invoke(overlay, "Ruler_Click", overlay, new RoutedEventArgs());
                    Invoke(overlay, "Crosshair_Click", overlay, new RoutedEventArgs());
                    var guides = (PixelGuides)overlay.FindName("PrecisionGuides");
                    Require(guides.ShowRulers && guides.ShowCrosshair && guides.PixelWidth == 960, "Editing ruler or crosshair state is incorrect.");
                    CaptureResult? capture = null;
                    overlay.CaptureAccepted += (_, result) => capture = result;
                    Invoke(overlay, "Complete", CaptureAction.Copy);
                    WaitForCaptureOutput(() => capture is not null);
                    Require(capture?.PixelRegion == new System.Drawing.Rectangle(101, 119, 301, 199), "Output ignored the nudged selection.");
                    var outputPixel = new byte[4]; var sourcePixel = new byte[4];
                    capture!.Image.CopyPixels(new Int32Rect(0, 0, 1, 1), outputPixel, 4, 0);
                    image.CopyPixels(new Int32Rect(101, 119, 1, 1), sourcePixel, 4, 0);
                    Require(outputPixel.SequenceEqual(sourcePixel), "The ruler/crosshair was included in capture output.");
                    Invoke(overlay, "RestartRegionSelection");
                    Require(guides.ShowCrosshair && guides.ShowRulers, "Restarting selection lost precision guides.");
                    if (scale == 1)
                    {
                        Invoke(overlay, "UpdatePointer", new Point(420, 380));
                        SavePreview((FrameworkElement)overlay.Content, Path.Combine(Root, "capture-precision.png"));
                    }
                }
                finally { overlay.Close(); }
            }

            var ruler = new ScreenRulerWindow();
            try
            {
                ruler.Show(); ruler.UpdateLayout();
                Invoke(ruler, "UpdateMeasurements");
                var guides = (PixelGuides)typeof(ScreenRulerWindow).GetField("_guides", PrivateInstance)!.GetValue(ruler)!;
                var surface = (FrameworkElement)typeof(ScreenRulerWindow).GetField("_surface", PrivateInstance)!.GetValue(ruler)!;
                var dpi = VisualTreeHelper.GetDpi(ruler);
                var bounds = guides.MeasurementBounds;
                Require(guides.InsetOrigin && bounds.TopLeft == new Point(24, 24) &&
                    bounds.Width == surface.ActualWidth - 24 && bounds.Height == surface.ActualHeight - 24,
                    "Desktop ruler zero is not inside the scale bands.");
                Require(guides.PixelWidth == (int)Math.Round(bounds.Width * dpi.DpiScaleX) &&
                    guides.PixelHeight == (int)Math.Round(bounds.Height * dpi.DpiScaleY), "Desktop ruler counts scale bands as measuring space or uses DIPs instead of pixels.");
                var readout = (TextBlock)typeof(ScreenRulerWindow).GetField("_readout", PrivateInstance)!.GetValue(ruler)!;
                string Reading(int x, int y) => NimCap.Screenshot.Localization.UiText.Get("ScreenRuler") + "  " +
                    NimCap.Screenshot.Localization.UiText.Format("ScreenRulerReadout", guides.PixelWidth, guides.PixelHeight, x, y);
                guides.Pointer = bounds.TopLeft; Invoke(ruler, "UpdateMeasurements");
                Require(readout.Text == Reading(0, 0), "The marked inset origin does not read zero.");
                SavePreview((FrameworkElement)ruler.Content, Path.Combine(Root, "screen-ruler-origin.png"));
                guides.Pointer = new Point(5, 5); Invoke(ruler, "UpdateMeasurements");
                Require(readout.Text == Reading(0, 0), "Hovering the scale bands produces negative measurements.");
                guides.Pointer = bounds.TopLeft + new Vector(100, 60); Invoke(ruler, "UpdateMeasurements");
                var measured = readout.Text;
                var expected = Reading((int)Math.Floor(100 * dpi.DpiScaleX), (int)Math.Floor(60 * dpi.DpiScaleY));
                Require(measured == expected, "Desktop ruler distances do not start at the marked inset origin.");
                ruler.Left += 137; ruler.Top += 51; ruler.UpdateLayout(); Invoke(ruler, "UpdateMeasurements");
                Require(readout.Text == measured && guides.OriginX == 0 && guides.OriginY == 0,
                    "Moving the ruler changed its zero or measured distance.");
                var width = guides.PixelWidth;
                ruler.Width += 50; ruler.UpdateLayout(); Invoke(ruler, "UpdateMeasurements");
                Require(guides.PixelWidth > width, "Resizing the ruler did not update its scale.");
                guides.Pointer = new Point(surface.ActualWidth, surface.ActualHeight); Invoke(ruler, "UpdateMeasurements");
                Require(readout.Text == NimCap.Screenshot.Localization.UiText.Get("ScreenRuler") + "  " +
                    NimCap.Screenshot.Localization.UiText.Format("ScreenRulerReadout", guides.PixelWidth, guides.PixelHeight, guides.PixelWidth, guides.PixelHeight),
                    "The far ruler corner does not match the ruler's own dimensions.");
                guides.Pointer = guides.MeasurementBounds.TopLeft; Invoke(ruler, "UpdateMeasurements");
                Require(readout.Text == Reading(0, 0), "Resizing the ruler changed its zero.");
                guides.Pointer = guides.MeasurementBounds.TopLeft + new Vector(100, 60); Invoke(ruler, "UpdateMeasurements");
                SavePreview((FrameworkElement)ruler.Content, Path.Combine(Root, "screen-ruler.png"));
            }
            finally { ruler.Close(); }
            Console.WriteLine("PASS: capture shape/ratio switching; pointer/selection movement and resizing at 100/125/150/175/200%; edge clamping; keyboard snap bypass; capture and desktop rulers.");
            Console.WriteLine("PASS: desktop ruler has an inset zero; scale bands excluded from physical pixel measurements; zero/readout stable after moving and resizing; endpoint distances match effective dimensions.");
            Console.WriteLine("Previews: " + Root);
        }
        finally { System.Windows.Forms.Cursor.Position = originalCursor; }
    }

    private static BitmapSource PrecisionImage()
    {
        const int width = 960, height = 720;
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            var grid = x % 40 == 0 || y % 40 == 0;
            bytes[offset] = grid ? (byte)170 : (byte)(70 + x / 10);
            bytes[offset + 1] = grid ? (byte)150 : (byte)(45 + y / 12);
            bytes[offset + 2] = grid ? (byte)125 : (byte)30;
            bytes[offset + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        image.Freeze(); return image;
    }

    private static void SavePreview(FrameworkElement content, string path)
    {
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class PrecisionCapture : ICaptureService
    {
        public WindowCandidate? FindWindowAt(System.Drawing.Point point) => null;
        public IReadOnlyList<WindowCandidate> EnumerateTopLevelWindows() => [];
        public IReadOnlyList<CapturedScreen> CaptureAllScreens() => throw new NotSupportedException();
        public BitmapSource CaptureRegion(System.Drawing.Rectangle bounds) => throw new NotSupportedException();
        public CapturedScreen CaptureVirtualScreen() => throw new NotSupportedException();
        public CapturedScreen? CaptureWindow(nint hwnd) => throw new NotSupportedException();
    }
}

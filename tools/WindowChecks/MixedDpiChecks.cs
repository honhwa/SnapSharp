using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Services;
using NimCap.Screenshot.Views;
using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Windows.Size;
using ComboBox = System.Windows.Controls.ComboBox;

namespace NimCap.Screenshot.WindowChecks;

internal static partial class Program
{
    private static void MixedDpiChecks()
    {
        var combinations = 0;
        foreach (var sourceScale in new[] { 1d, 1.25, 1.5, 1.75, 2 })
        foreach (var targetScale in new[] { 1d, 1.25, 1.5, 1.75, 2 })
        foreach (var portrait in new[] { false, true })
        foreach (var negative in new[] { false, true })
        {
            var left = new MonitorGeometry(new Rectangle(negative ? -1920 : 0, -100, 1920, 1080), 96 * sourceScale, 96 * sourceScale);
            var right = new MonitorGeometry(new Rectangle(left.Bounds.Right, -200, portrait ? 1080 : 2560, portrait ? 1920 : 1440),
                96 * targetScale, 96 * targetScale);
            var bounds = Rectangle.Union(left.Bounds, right.Bounds);
            var start = new Point(left.Bounds.Right - 111, left.Bounds.Top + 123);
            var end = new Point(right.Bounds.Left + 417, right.Bounds.Top + 531);
            var sourceDip = left.FromDesktop(start);
            var targetDip = left.ToMonitor(sourceDip, right);
            Require((right.ToDesktop(targetDip) - start).Length < 1e-7, "Source/target DPI conversion shifted desktop coordinates.");
            foreach (var overlayScale in new[] { sourceScale, targetScale })
            {
                var surface = new Size(bounds.Width / overlayScale, bounds.Height / overlayScale);
                var localStart = ScreenCoordinateMapper.DesktopPixelsToSurface(start, bounds, surface);
                var localEnd = ScreenCoordinateMapper.DesktopPixelsToSurface(end, bounds, surface);
                var region = ScreenCoordinateMapper.DipRectToImagePixels(new Rect(localStart, localEnd), surface, bounds.Width, bounds.Height);
                var physical = ScreenCoordinateMapper.ImagePixelsToScreenPixels(bounds, region);
                Require(Math.Abs(physical.Left - start.X) <= 1 && Math.Abs(physical.Top - start.Y) <= 1 &&
                    Math.Abs(physical.Right - end.X) <= 1 && Math.Abs(physical.Bottom - end.Y) <= 1,
                    $"Mixed DPI edges exceed one physical pixel: {sourceScale}/{targetScale}, portrait={portrait}.");
                var back = ScreenCoordinateMapper.SurfaceToDesktopPixels(localEnd, bounds, surface);
                Require((back - end).Length < 1e-7, "Cursor mapping round trip changed coordinates.");
                var reverse = ScreenCoordinateMapper.DipRectToImagePixels(new Rect(localEnd, localStart), surface, bounds.Width, bounds.Height);
                Require(reverse == region, "Reverse drag produced a different crop.");
            }
            combinations++;
        }
        CompositionChecks();
        RotationChecks();
        CoordinatorCrossScreenChecks();
        var monitors = DisplayMonitorService.Enumerate();
        Require(monitors.All(x => x.Bounds.Width > 0 && x.Bounds.Height > 0 && x.DpiX > 0 && x.DpiY > 0), "Invalid live monitor metadata.");
        File.WriteAllLines(Path.Combine(Root, "monitor-dpi.txt"), monitors.Select(m =>
            $"bounds={m.Bounds}; dpi={m.DpiX}x{m.DpiY}; known={m.DpiKnown}"));
        Console.WriteLine($"PASS: {combinations} mixed DPI/orientation/origin configurations, <=1px edges, synthetic adapter composition and cross-screen production crop; live monitor metadata saved.");
    }

    private static BitmapSource MonitorImage(int width, int height, byte blue)
    {
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            bytes[i] = blue; bytes[i + 1] = (byte)x; bytes[i + 2] = (byte)y; bytes[i + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        image.Freeze(); return image;
    }

    private static void CompositionChecks()
    {
        // Three independent output buffers stand in for different adapters; no GPU hardware is implied.
        var outputs = new[]
        {
            new CapturedScreen(new Rectangle(-80, 0, 80, 60), MonitorImage(80, 60, 20)),
            new CapturedScreen(new Rectangle(0, -20, 60, 100), MonitorImage(60, 100, 90)),
            new CapturedScreen(new Rectangle(80, 0, 80, 60), MonitorImage(80, 60, 180))
        };
        var desktop = DesktopCaptureComposer.Compose(outputs);
        Require(desktop.Bounds == new Rectangle(-80, -20, 240, 100), "Wrong virtual bounds for portrait/negative outputs.");
        foreach (var output in outputs)
        {
            var crop = new CroppedBitmap(desktop.Image, new Int32Rect(output.Bounds.Left - desktop.Bounds.Left,
                output.Bounds.Top - desktop.Bounds.Top, output.Bounds.Width, output.Bounds.Height));
            var actual = new byte[output.Bounds.Width * output.Bounds.Height * 4];
            var expected = new byte[actual.Length];
            crop.CopyPixels(actual, output.Bounds.Width * 4, 0); output.Image.CopyPixels(expected, output.Bounds.Width * 4, 0);
            Require(actual.SequenceEqual(expected), "Composition rescaled, rotated or moved adapter output pixels.");
        }
        var gap = new byte[4]; desktop.Image.CopyPixels(new Int32Rect(145, 20, 1, 1), gap, 4, 0);
        Require(gap.SequenceEqual(new byte[] { 0, 0, 0, 255 }), "Monitor gap is not opaque black.");
        try
        {
            DesktopCaptureComposer.Compose([new CapturedScreen(new Rectangle(0, 0, 100, 60), MonitorImage(60, 100, 1))]);
            throw new Exception("Unnormalized portrait output was accepted.");
        }
        catch (InvalidOperationException) { }
    }

    private static void CoordinatorCrossScreenChecks()
    {
        var backend = new MixedCapture();
        var coordinator = new CaptureCoordinator(backend);
        CaptureResult? accepted = null;
        coordinator.CaptureCompleted += (_, result) => accepted = result;
        coordinator.Begin(CaptureMode.Region);
        try
        {
            var windows = System.Windows.Application.Current.Windows.OfType<SelectionWindow>().ToArray();
            Require(windows.Length == 1 && backend.VirtualCalls == 1 && backend.PerScreenCalls == 0,
                "Coordinator still creates separate monitor overlays that truncate drags.");
            var overlay = windows.Single(); overlay.UpdateLayout();
            ((ComboBox)overlay.FindName("CaptureModePicker")).SelectedIndex = 0;
            foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2 })
            {
                overlay.Width = backend.Desktop.Bounds.Width / scale;
                overlay.Height = backend.Desktop.Bounds.Height / scale; overlay.UpdateLayout();
                var start = (Point)Invoke(overlay, "DesktopToLocal", -31d, 23d)!;
                var end = (Point)Invoke(overlay, "DesktopToLocal", 73d, 111d)!;
                // Run production drag geometry with monitor-edge snapping, not just conversion arithmetic.
                var snapped = (Point)Invoke(overlay, "SnapPoint", end)!;
                var local = (Rect)Invoke(overlay, "CreateRect", start, snapped, 0d, "")!;
                var pixels = (PixelRect)Invoke(overlay, "ToPixelSelection", local)!;
                Require(pixels == new PixelRect(769, 23, 104, 88), "Production selection changed cross-monitor pixel edges.");
                typeof(SelectionWindow).GetField("_selection", PrivateInstance)!.SetValue(overlay, local);
            }
            Invoke(overlay, "BeginInlineEditing");
            Invoke(overlay, "Complete", CaptureAction.Copy);
            WaitForCaptureOutput(() => accepted is not null);
            Require(accepted?.PixelRegion == new Rectangle(769, 23, 104, 88) && accepted.ScreenBounds == backend.Desktop.Bounds,
                "Production output clipped the selection at the starting monitor.");
            var source = new CroppedBitmap(backend.Desktop.Image, new Int32Rect(769, 23, 104, 88));
            var expected = new byte[104 * 88 * 4]; var actual = new byte[expected.Length];
            source.CopyPixels(expected, 104 * 4, 0); accepted!.Image.CopyPixels(actual, 104 * 4, 0);
            Require(actual.SequenceEqual(expected), "Cross-monitor output did not preserve original pixels.");
        }
        finally
        {
            foreach (var window in System.Windows.Application.Current.Windows.OfType<SelectionWindow>().ToArray()) window.Close();
        }
    }

    private static void RotationChecks()
    {
        var image = MonitorImage(6, 4, 90);
        var source = new byte[6 * 4 * 4]; image.CopyPixels(source, 6 * 4, 0);
        var normalize = typeof(DxgiCaptureService).GetMethod("NormalizeRotation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var degrees in new[] { 90, 180, 270 })
        {
            var bounds = new Rectangle(-600, -10, degrees == 180 ? 6 : 4, degrees == 180 ? 4 : 6);
            var rotated = (CapturedScreen)normalize.Invoke(null, [new CapturedScreen(bounds, image), bounds, degrees.ToString()])!;
            Require(rotated.Image.PixelWidth == bounds.Width && rotated.Image.PixelHeight == bounds.Height,
                "Rotated DXGI output did not match physical monitor dimensions.");
            var actual = new byte[source.Length]; rotated.Image.CopyPixels(actual, bounds.Width * 4, 0);
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 6; x++)
            {
                var targetX = degrees == 90 ? 3 - y : degrees == 180 ? 5 - x : y;
                var targetY = degrees == 90 ? x : degrees == 180 ? 3 - y : 5 - x;
                Require(source.AsSpan((y * 6 + x) * 4, 4).SequenceEqual(actual.AsSpan((targetY * bounds.Width + targetX) * 4, 4)),
                    $"DXGI {degrees} degree rotation changed a pixel or its destination coordinate.");
            }
        }
    }

    private sealed class MixedCapture : ICaptureService
    {
        internal CapturedScreen Desktop { get; } = DesktopCaptureComposer.Compose([
            new(new Rectangle(-800, 0, 800, 600), MonitorImage(800, 600, 20)),
            new(new Rectangle(0, 0, 600, 800), MonitorImage(600, 800, 180))]);
        internal int VirtualCalls, PerScreenCalls;
        public CapturedScreen CaptureVirtualScreen() { VirtualCalls++; return Desktop; }
        public IReadOnlyList<CapturedScreen> CaptureAllScreens() { PerScreenCalls++; throw new Exception("Per-monitor overlay path used."); }
        public BitmapSource CaptureRegion(Rectangle bounds) => throw new NotSupportedException();
        public CapturedScreen? CaptureWindow(nint handle) => null;
        public IReadOnlyList<WindowCandidate> EnumerateTopLevelWindows() => [];
        public WindowCandidate? FindWindowAt(System.Drawing.Point point) => null;
    }
}

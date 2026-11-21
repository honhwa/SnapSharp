using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Services;
using NimCap.Screenshot.ViewModels;
using NimCap.Screenshot.Views;
using Rectangle = System.Drawing.Rectangle;
using Point = System.Windows.Point;
using ComboBox = System.Windows.Controls.ComboBox;

namespace NimCap.Screenshot.WindowChecks;

internal static partial class Program
{
    private static void WindowShapeChecks()
    {
        var images = new SkiaImageService();
        var source = PrecisionImage();
        byte Alpha(BitmapSource image, int x, int y)
        {
            var pixels = new byte[4];
            new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
            return pixels[3];
        }
        var bounds = new Rectangle(-760, 20, 380, 320);
        var shape = new WindowCaptureShape(bounds, 12); // 8 DIP at 150% scaling.
        var cropped = images.Crop(source, new Int32Rect(40, 20, 380, 320));
        var masked = images.ApplyWindowShape(cropped, bounds, shape);
        foreach (var (x, y) in new[] { (0, 0), (379, 0), (0, 319), (379, 319) })
            Require(Alpha(masked, x, y) == 0, "Rounded dialog export retains desktop pixels outside a corner.");
        Require(Alpha(masked, 190, 0) == 255 && Alpha(masked, 0, 160) == 255 && Alpha(masked, 190, 160) == 255,
            "Window silhouette removes straight edges or interior pixels.");
        var small = images.Crop(source, new Int32Rect(60, 60, 80, 30));
        var child = images.ApplyWindowShape(small, new Rectangle(-740, 60, 80, 30), shape);
        Require(Alpha(child, 0, 0) == 255 && Alpha(child, 79, 29) == 255,
            "A rectangular child element inherits artificial rounded corners.");
        var clippedBounds = new Rectangle(-740, 20, 360, 320);
        var clipped = images.ApplyWindowShape(images.Crop(source, new Int32Rect(60, 20, 360, 320)), clippedBounds, shape);
        Require(Alpha(clipped, 0, 0) == 255 && Alpha(clipped, 359, 0) == 0,
            "Clipping a window at a monitor edge moves its corner origin.");
        var candidate = new WindowCandidate(bounds, 0, bounds) { CaptureShape = shape };
        var screen = new CapturedScreen(new Rectangle(-800, 0, source.PixelWidth, source.PixelHeight), source);
        var overlay = new SelectionWindow(screen, [candidate], new PrecisionCapture());
        try
        {
            overlay.Show(); overlay.UpdateLayout();
            var picker = (ComboBox)overlay.FindName("CaptureModePicker"); picker.SelectedIndex = 1;
            var pointer = new Point(100 * overlay.ActualWidth / source.PixelWidth, 100 * overlay.ActualHeight / source.PixelHeight);
            var suggestion = (Rect)Invoke(overlay, "SuggestedElementAt", pointer)!;
            typeof(SelectionWindow).GetField("_selection", PrivateInstance)!.SetValue(overlay, suggestion);
            Invoke(overlay, "BeginInlineEditing");
            var editor = (EditorViewModel)typeof(SelectionWindow).GetField("_editor", PrivateInstance)!.GetValue(overlay)!;
            Require(editor.Image.PixelWidth == 380 && editor.Image.PixelHeight == 320 && Alpha(editor.Image, 0, 0) == 0,
                "Element editing still uses the rectangular desktop crop.");
            CaptureResult? accepted = null; overlay.CaptureAccepted += (_, result) => accepted = result;
            Invoke(overlay, "Complete", CaptureAction.Copy);
            WaitForCaptureOutput(() => accepted is not null);
            Require(accepted is not null && Alpha(accepted.Image, 379, 319) == 0,
                "Final element export loses window transparency.");
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(accepted!.Image));
            using (var stream = File.Create(Path.Combine(Root, "rounded-dialog.png"))) png.Save(stream);
            var decoded = new BitmapImage(); decoded.BeginInit(); decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.UriSource = new Uri(Path.Combine(Root, "rounded-dialog.png")); decoded.EndInit();
            Require(Alpha(decoded, 0, 0) == 0, "PNG encoding restores desktop background in the corners.");
            Invoke(overlay, "RestartRegionSelection");
            picker.SelectedIndex = 0;
            typeof(SelectionWindow).GetField("_selection", PrivateInstance)!.SetValue(overlay, suggestion);
            Invoke(overlay, "BeginInlineEditing");
            var regionEditor = (EditorViewModel)typeof(SelectionWindow).GetField("_editor", PrivateInstance)!.GetValue(overlay)!;
            Require(Alpha(regionEditor.Image, 0, 0) == 255, "An ordinary rectangular region unexpectedly inherits the window mask.");
        }
        finally { overlay.Close(); }
        // Native custom regions must be copied before the selection overlay changes the desktop.
        var host = new Window { Width = 360, Height = 240, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try
        {
            host.Show(); host.UpdateLayout();
            var hwnd = new WindowInteropHelper(host).Handle;
            var region = CreateRoundRectRgn(0, 0, 200, 150, 32, 32);
            if (SetWindowRgn(hwnd, region, true) == 0) { DeleteShapeObject(region); throw new InvalidOperationException("Unable to create native shape fixture."); }
            Require(GetShapeWindowRect(hwnd, out var native), "Unable to read native window bounds.");
            var nativeBounds = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
            var nativeShape = WindowShapeService.Snapshot(hwnd, nativeBounds);
            Require(nativeShape?.RegionRectangles is { Count: > 1 }, "Native rounded regions are ignored.");
            var nativeImage = images.ApplyWindowShape(images.Crop(source, new Int32Rect(0, 0, 200, 150)),
                new Rectangle(native.Left, native.Top, 200, 150), nativeShape!);
            Require(Alpha(nativeImage, 0, 0) == 0 && Alpha(nativeImage, 100, 75) == 255,
                "Native region coordinates do not match the exported pixels.");
            SetWindowRgn(hwnd, 0, true);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var preference = 2;
                if (SetShapeDwmAttribute(hwnd, 33, ref preference, sizeof(int)) == 0)
                {
                    var round = WindowShapeService.Snapshot(hwnd, nativeBounds);
                    Require(round is { CornerRadius: > 0, RegionRectangles: null }, "DWM rounded corners are ignored.");
                    preference = 1; SetShapeDwmAttribute(hwnd, 33, ref preference, sizeof(int));
                    Require(WindowShapeService.Snapshot(hwnd, nativeBounds) is null, "Explicit square windows are rounded anyway.");
                    preference = 2; SetShapeDwmAttribute(hwnd, 33, ref preference, sizeof(int));
                    host.WindowState = WindowState.Maximized; host.UpdateLayout();
                    Require(WindowShapeService.Snapshot(hwnd, nativeBounds) is null, "Maximized windows retain artificial rounded corners.");
                }
            }
        }
        finally { host.Close(); }
        Console.WriteLine("PASS: dialog silhouette, native regions/DWM preferences, transparent corners, straight edges, rectangular child elements, negative coordinates, clipped monitor origins and element PNG export.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct ShapeRect { public int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteShapeObject(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetShapeWindowRect(nint hwnd, out ShapeRect rect);
    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")] private static extern int SetShapeDwmAttribute(nint hwnd, int attribute, ref int value, int size);
}

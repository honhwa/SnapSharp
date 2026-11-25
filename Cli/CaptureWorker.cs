using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Services;

namespace NimCap.Screenshot.Cli;

internal static class CaptureWorker
{
    internal const int FrameMagic = 0x4E434150;
    internal const int MaximumEncodedBytes = 512_000_000;

    // This entry point runs on Main's STA thread, with no Application/controller or user settings.
    internal static int Run()
    {
        try
        {
            var command = JsonSerializer.Deserialize<CliCommand>(Console.ReadLine() ?? "")
                ?? throw CliFailure.Capture("Missing capture request.");
            ICaptureService backend = command.Backend switch
            {
                "gdi" => new ScreenCaptureService(command.IncludeCursor),
                "wgc" => new WgcCaptureService(command.IncludeCursor),
                _ => new ResilientCaptureService(true, command.IncludeCursor)
            };
            if (command.Target == "probe")
            {
                var primary = DesktopInventory.Monitors()[0].Bounds;
                command = command with { Target = "rect", Rectangle = new(primary.X, primary.Y, 1, 1) };
            }
            if (command.Target == "monitor")
            {
                var monitor = DesktopInventory.Monitors().FirstOrDefault(m => m.Index == command.MonitorIndex)
                    ?? throw CliFailure.Target("The monitor index is no longer available. Run 'nimcap monitors --json'.");
                command = command with { Target = "rect", Rectangle = monitor.Bounds };
            }
            CapturedScreen capture;
            if (command.Target == "full") capture = backend.CaptureVirtualScreen();
            else if (command.Target == "rect" && command.Rectangle is { } rectangle)
            {
                var bounds = new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
                var desktop = new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
                if (desktop.Width <= 0 || desktop.Height <= 0) throw CliFailure.Capture("No active desktop displays are available.");
                if (!desktop.Contains(bounds)) throw CliFailure.Target("The rectangle must be entirely inside the virtual desktop bounds.");
                capture = new(bounds, backend.CaptureRegion(bounds));
            }
            else
            {
                var handle = command.Target switch
                {
                    "window" => GetForegroundWindow(),
                    "window-match" => DesktopInventory.SelectWindow(command.ProcessName, command.WindowTitle),
                    _ => (nint)command.WindowHandle
                };
                if (handle == 0 || !IsWindow(handle) || !IsWindowVisible(handle) || IsIconic(handle))
                    throw CliFailure.Target("The target window is missing, hidden or minimized.");
                capture = backend.CaptureWindow(handle) ?? throw CliFailure.Target("The target window is no longer available for capture.");
            }
            var bytes = new SkiaImageService().Encode(capture.Image, Path.GetExtension(command.Output));
            if (bytes.Length > MaximumEncodedBytes) throw CliFailure.Capture("The encoded image is too large.");
            using var output = Console.OpenStandardOutput();
            using var writer = new BinaryWriter(output);
            writer.Write(FrameMagic);
            writer.Write(capture.Bounds.X); writer.Write(capture.Bounds.Y);
            writer.Write(capture.Image.PixelWidth); writer.Write(capture.Image.PixelHeight);
            writer.Write(bytes.Length); writer.Write(bytes);
            writer.Flush();
            return 0;
        }
        catch (Exception error)
        {
            var failure = error as CliFailure ?? CliFailure.Capture(error.Message);
            Console.Error.WriteLine(JsonSerializer.Serialize(new CliResult(false, failure.ExitCode,
                Error: new(failure.Code, failure.Message)), CliResult.JsonOptions));
            return failure.ExitCode;
        }
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
}

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Services;
using NimCap.Screenshot.Views;
using Rectangle = System.Drawing.Rectangle;

namespace NimCap.Screenshot.RegressionChecks;

internal static partial class Program
{
    // Opt-in desktop test. Only the isolated local page receives input. The left
    // pixel ruler encodes absolute document Y, independently of Match/Stitch.
    private static void LiveVerticalChecks(string browser)
    {
        browser = Path.GetFullPath(browser);
        Require(File.Exists(browser), "Desktop browser executable missing.");
        var frame = new DispatcherFrame();
        Exception? failure = null;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            try { await CaptureLiveVertical(browser); }
            catch (Exception error) { failure = error; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task CaptureLiveVertical(string browser)
    {
        var directory = Path.Combine(Root, "desktop-browser");
        Directory.CreateDirectory(directory);
        var token = "NimCapLive-" + Guid.NewGuid().ToString("N");
        var page = Path.Combine(directory, "article.html");
        File.WriteAllText(page, """
            <!doctype html><meta charset="utf-8">
            <style>html{scroll-behavior:auto}body{margin:0;overflow-x:hidden;background:white}
            canvas{display:block}header{position:fixed;top:0;left:0;right:0;height:40px;background:#225588;color:white;z-index:2}</style>
            <header>Local vertical capture acceptance: fixed navigation (excluded from content)</header>
            <canvas id="article" width="720" height="3600"></canvas>
            <script>
            const c=article.getContext('2d');c.fillStyle='white';c.fillRect(0,0,720,3600);
            for(let y=0;y<3600;y++){c.fillStyle=`rgb(${y%256},${Math.floor(y/256)},127)`;c.fillRect(0,y,24,1)}
            c.font='16px Arial';
            for(let i=0;i<150;i++){c.fillStyle='#222';c.fillText(`Row ${i}: ${i*7919} / public local acceptance paragraph ${i%7}`,32,i*24+18);
            c.fillStyle=`hsl(${i*47%360},65%,60%)`;c.fillRect(620,i*24,80,16)}
            function status(){document.title='TOKEN|'+JSON.stringify({y:scrollY,dpr:devicePixelRatio,w:innerWidth,h:innerHeight})}
            addEventListener('scroll',status);addEventListener('resize',status);status();
            </script>
            """.Replace("TOKEN", token));
        var start = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var option in new[] { "--user-data-dir=" + Path.Combine(directory, "profile"), "--no-first-run", "--no-default-browser-check",
            "--disable-background-networking", "--disable-sync", "--disable-extensions", "--disable-smooth-scrolling",
            "--window-position=40,40", "--window-size=900,780", "--app=" + new Uri(page).AbsoluteUri }) start.ArgumentList.Add(option);
        using var host = Process.Start(start) ?? throw new InvalidOperationException("Browser did not start.");
        var originalCursor = System.Windows.Forms.Cursor.Position;
        ScrollCaptureWindow? preview = null;
        nint target = 0;
        var results = new List<object>();
        string? manualBackend = null;
        try
        {
            await LiveUntil(() =>
            {
                var found = new ScreenCaptureService().EnumerateTopLevelWindows().Where(w => LiveTitle(w.Handle).StartsWith(token + "|", StringComparison.Ordinal)).ToArray();
                if (found.Length != 1) return false;
                target = found[0].Handle;
                return true;
            }, "Isolated browser did not expose the fixture window.", 20000);
            Require(SetWindowPos(target, new nint(-1), 0, 0, 0, 0, 0x0043), "Cannot raise isolated browser fixture.");
            SetForegroundWindow(target);
            await Task.Delay(500);
            var metrics = LiveMetrics(target, token);
            // A physical row marker has one exact sample at integral DPI scales.
            // Other scales require separately reviewed samples rather than guessed truth.
            Require(metrics.Dpr == Math.Round(metrics.Dpr) && metrics.Dpr is >= 1 and <= 2,
                "This pixel-ruler fixture requires 100% or 200% browser scaling; use reviewed samples for other DPI values.");
            Require(GetClientRect(target, out var client), "Cannot read browser client bounds.");
            var origin = new LivePoint();
            Require(ClientToScreen(target, ref origin), "Cannot map browser client bounds.");
            var chromeHeight = client.Bottom - client.Top - (int)Math.Round(metrics.H * metrics.Dpr);
            var region = new Rectangle(origin.X + (int)(4 * metrics.Dpr),
                origin.Y + chromeHeight + (int)(52 * metrics.Dpr), (int)(600 * metrics.Dpr), (int)(320 * metrics.Dpr));
            Require(System.Windows.Forms.Screen.FromHandle(target).Bounds.Contains(region), "Fixture region is outside the monitor.");
            FocusLiveTarget(target, region);
            await Task.Delay(300);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            BitmapSource Initial() => new ScreenCaptureService().CaptureRegion(region);
            var initial = Initial();
            new SkiaImageService().Save(initial, Path.Combine(directory, "initial.png"));
            File.WriteAllText(Path.Combine(directory, "viewport.json"), JsonSerializer.Serialize(new { metrics, region, client, origin, chromeHeight }));
            preview = new ScrollCaptureWindow(region, initial);
            preview.Show();
            preview.BeginManualCapture();
            await LiveUntil(() => !string.IsNullOrWhiteSpace(((System.Windows.Controls.TextBlock)preview.FindName("ActiveBackendInfo")).Text),
                "Manual capture did not acquire its baseline.", 20000);
            await Task.Delay(200);
            Require(preview.IsCapturing, "Manual capture ended before wheel input.");
            // A down/up reversal revisits existing rows without duplicating them.
            foreach (var delta in new[] { -120, -120, 120, -120 })
            {
                await LiveWheel(target, region, token, delta);
                Require(preview.IsCapturing, "Manual capture stopped before the reversal sequence completed.");
            }
            preview.StopCapture();
            await LiveUntil(() => !preview.IsCapturing, "Manual capture did not return to preview.", 20000);
            var manual = LiveSession(preview);
            manualBackend = ((System.Windows.Controls.TextBlock)preview.FindName("ActiveBackendInfo")).Text;
            ValidateLiveSession(manual, metrics.Dpr, "manual-reversal", directory, results, browser, requireReverse: true);

            // Canceling a second capture must keep the original image and history.
            var previous = LiveSession(preview);
            preview.BeginManualCapture(true);
            await Task.Delay(900);
            Require(preview.IsCapturing, "Manual recapture did not start.");
            Require(GetForegroundWindow() == target, "Test fixture lost foreground before Escape.");
            keybd_event(0x1B, 0, 0, 0); keybd_event(0x1B, 0, 2, 0);
            await LiveUntil(() => !preview.IsCapturing, "Escape did not cancel recapture.");
            Require(ReferenceEquals(previous, LiveSession(preview)), "Canceled recapture replaced the previous session.");
            results.Add(new { scenario = "manual-recapture-cancel", preserved = true });
            // A successful new manual session starts a fresh frame union while
            // retaining the same fixed region. Extend above its initial viewport.
            preview.BeginManualCapture(true);
            await Task.Delay(1000);
            Require(preview.IsCapturing, "Successful manual recapture did not start.");
            foreach (var delta in new[] { 120, -120, -120 }) await LiveWheel(target, region, token, delta);
            preview.StopCapture();
            await LiveUntil(() => !preview.IsCapturing, "Successful manual recapture did not return to preview.", 20000);
            Require(!ReferenceEquals(previous, LiveSession(preview)), "Successful recapture retained the old session.");
            ValidateLiveSession(LiveSession(preview), metrics.Dpr, "manual-recapture-upward", directory, results, browser, requireReverse: true);
            preview.Close(); preview = null;

            // New automatic sessions use the same production backend and wheel
            // driver; start near the end to verify clean end detection as well.
            SetForegroundWindow(target);
            for (var i = 0; i < 5; i++) await LiveWheel(target, region, token, -360);
            var trace = new List<string>();
            var automatic = await new ScrollingCaptureService("GDI").CaptureSessionAsync(region,
                new ScrollCaptureOptions(ScrollDirection.Vertical, DelayMilliseconds: 500, WheelNotches: 1, MaximumFrames: 30),
                cancellationToken: deadline.Token, diagnostic: trace.Add);
            ValidateLiveSession(automatic, metrics.Dpr, "automatic-to-end", directory, results, browser);
            Require(automatic.Frames.Count < 30, "Automatic capture failed to detect the page end.");
            Require(automatic.Matches.All(m => m.Reliable), "Automatic capture ended on an uncertain seam instead of page end.");
            File.WriteAllLines(Path.Combine(directory, "automatic-trace.txt"), trace);
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now, baselineCommit = LiveCommit(), workingTreeModified = true, application = Path.GetFileName(browser),
                version = FileVersionInfo.GetVersionInfo(browser).FileVersion, os = Environment.OSVersion.VersionString,
                dpi = metrics.Dpr * 96, manualBackend, automaticBackend = "GDI", region, results,
                limits = "Local canvas/text page; fixed navigation excluded by region. Does not establish Office/Electron, mixed-monitor, dynamic-list or all-driver compatibility."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS: live desktop wheel capture, upward revisit/extension, manual recapture/cancel, automatic vertical capture and page end.");
            Console.WriteLine("Evidence: " + directory);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString());
            Console.Error.WriteLine("Desktop evidence: " + directory);
            throw;
        }
        finally
        {
            if (preview is not null)
            {
                preview.Close();
                var cleanup = Stopwatch.StartNew();
                while (preview.IsCapturing && cleanup.ElapsedMilliseconds < 5000) await Task.Delay(50);
            }
            System.Windows.Forms.Cursor.Position = originalCursor;
            if (!host.HasExited)
            {
                host.CloseMainWindow();
                if (!await Task.Run(() => host.WaitForExit(3000))) host.Kill(entireProcessTree: true);
            }
        }
    }

    private static ScrollCaptureSession LiveSession(ScrollCaptureWindow window) =>
        (ScrollCaptureSession)typeof(ScrollCaptureWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void ValidateLiveSession(ScrollCaptureSession session, double dpr, string name, string directory, List<object> results, string browser, bool requireReverse = false)
    {
        var evidenceDirectory = directory;
        directory = Path.Combine(directory, "samples", name);
        Directory.CreateDirectory(directory);
        var images = new SkiaImageService();
        for (var i = 0; i < session.Frames.Count; i++) images.Save(session.Frames[i], Path.Combine(directory, $"{name}-{i:D3}.png"));
        File.WriteAllText(Path.Combine(evidenceDirectory, name + "-session.json"), JsonSerializer.Serialize(new { frames = session.Frames.Count, session.Direction, session.StopReason, session.Offsets }));
        Require(session.Direction == ScrollDirection.Vertical && session.Frames.Count >= 2, name + ": no vertical frames.");
        var positions = session.Frames.Select(f =>
        {
            var pixel = new byte[4];
            var bgra = new FormatConvertedBitmap(f, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            bgra.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Require(pixel[0] == 127 && pixel[3] == 255, name + ": viewport/ruler origin incorrect.");
            return (pixel[2] + pixel[1] * 256) * (int)dpr;
        }).ToArray();
        var offsets = positions.Skip(1).Select((p, i) => p - positions[i]).ToArray();
        Require(session.Matches.All(m => m.Reliable) && session.Offsets.SequenceEqual(offsets),
            name + ": offsets differ from independently encoded document coordinates: " + string.Join(",", offsets) + " vs " + string.Join(",", session.Offsets));
        if (requireReverse) Require(offsets.Any(o => o < 0) && offsets.Any(o => o > 0), name + ": missing actual upward/downward scrolling.");
        var width = session.Frames[0].PixelWidth; var height = session.Frames[0].PixelHeight;
        var expected = new byte[checked(width * (positions.Max() - positions.Min() + height) * 4)];
        var seen = new bool[positions.Max() - positions.Min() + height];
        foreach (var (image, index) in session.Frames.Select((image, index) => (image, index)))
        {
            var bytes = VerticalBytes(image);
            for (var y = 0; y < height; y++)
            {
                var targetY = positions[index] - positions.Min() + y;
                if (seen[targetY]) continue;
                bytes.AsSpan(y * width * 4, width * 4).CopyTo(expected.AsSpan(targetY * width * 4));
                seen[targetY] = true;
            }
        }
        Require(seen.All(v => v), name + ": document contains uncaptured rows.");
        var output = new ScrollingStitcher().Stitch(session).Image;
        Require(output.PixelWidth == width && output.PixelHeight == seen.Length && VerticalBytes(output).SequenceEqual(expected),
            name + ": output duplicates, loses or changes captured rows.");
        images.Save(output, Path.Combine(directory, name + "-output.png"));
        var projects = new ScrollProjectService(); var path = Path.Combine(evidenceDirectory, name + ".ssscroll");
        projects.Save(path, new(session, new(), new()));
        AssertVerticalPixels(new ScrollingStitcher().Stitch(projects.Load(path).Session).Image, output, name + " project");
        File.WriteAllText(Path.Combine(directory, name + "-sample.json"), JsonSerializer.Serialize(new VerticalSample(
            Path.GetFileName(browser), FileVersionInfo.GetVersionInfo(browser).FileVersion ?? "unknown", name + ": real wheel; expected offsets decoded from absolute document pixel ruler",
            Enumerable.Range(0, session.Frames.Count).Select(i => $"{name}-{i:D3}.png").ToArray(), offsets,
            Enumerable.Repeat(true, offsets.Length).ToArray(), new(), name + "-output.png"), new JsonSerializerOptions { WriteIndented = true }));
        results.Add(new { scenario = name, frames = session.Frames.Count, offsets, positions, width = output.PixelWidth, height = output.PixelHeight, session.StopReason });
    }

    private sealed record LiveViewport(double Y, double Dpr, double W, double H);
    private static string LiveTitle(nint handle)
    {
        var text = new StringBuilder(2048); GetWindowText(handle, text, text.Capacity); return text.ToString();
    }
    private static LiveViewport LiveMetrics(nint handle, string token)
    {
        var text = LiveTitle(handle); var begin = text.IndexOf('{'); var end = text.LastIndexOf('}');
        Require(text.StartsWith(token + "|", StringComparison.Ordinal) && begin >= 0 && end > begin, "Lost isolated fixture window.");
        return JsonSerializer.Deserialize<LiveViewport>(text[begin..(end + 1)], new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    private static async Task LiveWheel(nint handle, Rectangle region, string token, int delta)
    {
        FocusLiveTarget(handle, region);
        var before = LiveMetrics(handle, token).Y;
        SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
        mouse_event(0x0800, 0, 0, delta, 0);
        await LiveUntil(() => LiveMetrics(handle, token).Y != before, "Wheel input did not scroll the isolated page.");
        await Task.Delay(700);
    }
    private static void FocusLiveTarget(nint handle, Rectangle region)
    {
        SetForegroundWindow(handle);
        var point = new LivePoint { X = region.X + region.Width / 2, Y = region.Y + region.Height / 2 };
        Require(SetCursorPos(point.X, point.Y), "Cannot position cursor over isolated fixture.");
        Require(GetAncestor(WindowFromPoint(point), 2) == handle, "Another window covers the fixture; no input sent.");
        if (GetForegroundWindow() != handle)
        {
            mouse_event(0x0002, 0, 0, 0, 0); mouse_event(0x0004, 0, 0, 0, 0);
        }
        Require(GetForegroundWindow() == handle, "Input would target another application.");
    }
    private static async Task LiveUntil(Func<bool> condition, string error, int milliseconds = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds >= milliseconds) throw new TimeoutException(error);
            await Task.Delay(50);
        }
    }
    private static string LiveCommit()
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        return process!.StandardOutput.ReadToEnd().Trim();
    }
    // Configure the test process before AppPaths is initialized. Restore the
    // runner's previous pointer on exit; never touch an installed app's settings.
    private sealed class LiveDataScope : IDisposable
    {
        private readonly string _path = Path.Combine(AppContext.BaseDirectory, "data-location.json");
        private readonly byte[]? _previous;
        public LiveDataScope(string directory)
        {
            _previous = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
            File.WriteAllText(_path, JsonSerializer.Serialize(new { CurrentDirectory = directory }));
        }
        public void Dispose()
        {
            if (_previous is not null) File.WriteAllBytes(_path, _previous);
            else File.Delete(_path);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct LiveRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct LivePoint { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out LiveRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref LivePoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(LivePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, int delta, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}

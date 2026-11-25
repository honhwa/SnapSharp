using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NimCap.Screenshot.Cli;

namespace NimCap.Screenshot.CliChecks;

internal static class Program
{
    private static string _cli = "";
    private static readonly string Root = Path.GetFullPath(Path.Combine(".artifacts", "cli-checks", Guid.NewGuid().ToString("N")));
    private sealed record Result(int Code, string Output, string Error);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--window-fixture"]) return WindowFixture();
        if (args is ["--hanging-worker", var marker])
        {
            Console.ReadLine();
            File.WriteAllText(marker, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        if (args is ["--truncated-worker"])
        {
            Console.ReadLine();
            using var writer = new BinaryWriter(Console.OpenStandardOutput());
            foreach (var field in new[] { 0x4E434150, -10, 20, 1, 1, 8 }) writer.Write(field);
            writer.Write(new byte[] { 1, 2 });
            return 0;
        }
        try
        {
            var option = Array.IndexOf(args, "--cli");
            Require(option >= 0 && option + 1 < args.Length, "Pass --cli <nimcap.exe>.");
            _cli = Path.GetFullPath(args[option + 1]);
            Require(File.Exists(_cli), "CLI executable is missing: " + _cli);
            Directory.CreateDirectory(Root);
            DiscoveryChecks().GetAwaiter().GetResult();
            ContractChecks().GetAwaiter().GetResult();
            WorkerLifetimeChecks().GetAwaiter().GetResult();
            if (args.Contains("--hardware")) HardwareChecks().GetAwaiter().GetResult();
            Console.WriteLine("PASS: CLI commands, discovery JSON, doctor failure, timeout, output collision, worker cancellation and truncated IPC" +
                (args.Contains("--hardware") ? ", live selectors/ambiguity/minimized targets, monitor bounds, doctor probes, real capture and overwrite." : "."));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task WorkerLifetimeChecks()
    {
        var marker = PathFor("worker.pid");
        var command = CliCommand.Parse(["capture", "--full", "--output", PathFor("canceled.png")]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew();
        var capture = CaptureProcess.ExecuteAsync(SelfStart("--hanging-worker", marker), command, cancellation.Token);
        while (!File.Exists(marker) && !capture.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Require(File.Exists(marker), "The hanging worker did not reach capture.");
        var processId = int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture);
        cancellation.Cancel();
        try { await capture; throw new InvalidOperationException("Canceled native capture returned an image."); }
        catch (OperationCanceledException) { }
        try
        {
            using var child = Process.GetProcessById(processId);
            Require(child.HasExited, "Cancel left the native capture process running.");
        }
        catch (ArgumentException) { }
        Require(!File.Exists(command.Output), "Canceled capture wrote a destination file.");

        try
        {
            await CaptureProcess.ExecuteAsync(SelfStart("--truncated-worker"), command, CancellationToken.None);
            throw new InvalidOperationException("A partial image was accepted from the worker.");
        }
        catch (CliFailure error) { Require(error.ExitCode == 4, "A truncated frame must be a capture failure."); }

        try
        {
            var missing = SelfStart();
            missing.FileName = PathFor("missing-worker.exe");
            await CaptureProcess.ExecuteAsync(missing, command, CancellationToken.None);
            throw new InvalidOperationException("A missing worker executable was accepted.");
        }
        catch (CliFailure error) { Require(error.ExitCode == 4, "Worker startup failure must be a capture failure."); }
    }

    private static ProcessStartInfo SelfStart(params string[] args)
    {
        var executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task ContractChecks()
    {
        var help = await Run("--help");
        Require(help.Code == 0 && help.Output.Contains("capture") && help.Error == "", "Help must describe capture and exit successfully.");
        var version = await Run("--version");
        Require(version.Code == 0 && Version.TryParse(version.Output.Trim(), out _), "Version must be machine-readable.");
        string[][] invalid =
        [
            [], ["unknown"], ["capture"], ["capture", "--full"],
            ["capture", "--full", "--rect", "0,0,10,10", "--output", PathFor("bad.png")],
            ["capture", "--full", "--full", "--output", PathFor("bad.png")],
            ["capture", "--rect", "0,0,0,10", "--output", PathFor("bad.png")],
            ["capture", "--rect", "2147483647,0,10,10", "--output", PathFor("bad.png")],
            ["capture", "--rect", "0,2147483647,10,10", "--output", PathFor("bad.png")],
            ["capture", "--rect", "0,0,10", "--output", PathFor("bad.png")],
            ["capture", "--window-handle", "0", "--output", PathFor("bad.png")],
            ["capture", "--window-handle", "0xno", "--output", PathFor("bad.png")],
            ["capture", "--full", "--output", PathFor("bad.avif")],
            ["capture", "--full", "--output", "--json"],
            ["capture", "--full", "--output", PathFor("bad.png"), "--timeout", "0"],
            ["capture", "--full", "--output", PathFor("bad.png"), "--delay", "-1"],
            ["capture", "--full", "--output", PathFor("bad.png"), "--output", PathFor("other.png")],
            ["capture", "--full", "--output", PathFor("bad.png"), "--backend", "magic"],
            ["capture", "--full", "--output", PathFor("bad.png"), "--unknown"]
        ];
        foreach (var args in invalid)
        {
            var result = await Run([.. args, "--json"]);
            Error(result, 2, "invalid_arguments");
        }
        Require(!File.Exists(PathFor("bad.png")), "Invalid commands wrote a destination file.");

        var existing = PathFor("保留 existing.png");
        await File.WriteAllTextAsync(existing, "preserve these bytes");
        Error(await Run("capture", "--full", "--output", existing, "--json"), 5, "output_failed");
        Require(await File.ReadAllTextAsync(existing) == "preserve these bytes", "A rejected capture changed an existing file.");
        Error(await Run("capture", "--full", "--output", Root, "--json"), 2, "invalid_arguments");

        var targetDirectory = PathFor("directory.png");
        Directory.CreateDirectory(targetDirectory);
        Error(await Run("capture", "--full", "--output", targetDirectory, "--json"), 5, "output_failed");
        Error(await Run("capture", "--full", "--output", Path.Combine(existing, "image.png"), "--json"), 5, "output_failed");

        var watch = Stopwatch.StartNew();
        Error(await Run("capture", "--rect", "-1920,-100,64,48", "--output", PathFor("timeout.png"),
            "--delay", "3", "--timeout", "1", "--json"), 6, "timed_out");
        Require(watch.Elapsed < TimeSpan.FromSeconds(5) && !File.Exists(PathFor("timeout.png")), "Timeout did not interrupt delay cleanly.");
        Error(await Run("capture", "--window-handle", "0x123456789", "--output", PathFor("missing.png"), "--json"), 3, "target_not_found");
        Require(!File.Exists(PathFor("missing.png")), "Missing window produced an output file.");
    }

    private static async Task DiscoveryChecks()
    {
        foreach (var name in new[] { "windows", "monitors", "doctor" })
        {
            var help = await Run(name, "--help");
            Require(help.Code == 0 && help.Output.Contains(name), name + " help is unavailable.");
        }
        var windows = await Run("windows", "--json");
        Require(windows.Code == 0 && windows.Error == "", "Window discovery failed: " + windows.Output + windows.Error);
        using (var json = JsonDocument.Parse(windows.Output))
        {
            Require(json.RootElement.GetProperty("schemaVersion").GetInt32() == 1 &&
                json.RootElement.GetProperty("windows").ValueKind == JsonValueKind.Array, "Window list schema is invalid.");
            foreach (var window in json.RootElement.GetProperty("windows").EnumerateArray())
                Require(window.GetProperty("handle").GetString()!.StartsWith("0x") &&
                    window.GetProperty("processId").GetInt32() > 0 &&
                    window.TryGetProperty("title", out _) && window.TryGetProperty("minimized", out _), "Window metadata is incomplete.");
        }
        string[][] invalid = [
            ["windows", "--full"], ["monitors", "--output", "x"], ["windows", "--json"],
            ["doctor", "--backend", "magic"], ["doctor", "--timeout", "0"],
            ["capture", "--monitor", "0", "--output", PathFor("bad.png")],
            ["capture", "--monitor", "-1", "--output", PathFor("bad.png")],
            ["capture", "--monitor", "1", "--full", "--output", PathFor("bad.png")],
            ["capture", "--process", "app", "--window", "--output", PathFor("bad.png")],
            ["capture", "--full", "--window-title", "app", "--output", PathFor("bad.png")],
            ["capture", "--process", " ", "--output", PathFor("bad.png")]
        ];
        foreach (var args in invalid) Error(await Run([.. args, "--json"]), 2, "invalid_arguments");
        Error(await Run("capture", "--process", "nimcap-nonexistent-" + Guid.NewGuid().ToString("N"),
            "--output", PathFor("no-process.png"), "--json"), 3, "target_not_found");
        Error(await Run("capture", "--monitor", "2147483647", "--output", PathFor("no-monitor.png"), "--json"), 3, "target_not_found");
        var blocker = PathFor("doctor-blocker");
        File.WriteAllText(blocker, "preserve");
        var doctor = await Run("doctor", "--output-dir", blocker, "--backend", "gdi", "--timeout", "1", "--json");
        Error(doctor, 4, "diagnostic_failed");
        using (var json = JsonDocument.Parse(doctor.Output))
            Require(json.RootElement.GetProperty("checks").EnumerateArray().Any(c =>
                c.GetProperty("name").GetString() == "outputDirectory" && !c.GetProperty("success").GetBoolean()),
                "Doctor did not report the unwritable directory.");
        Require(File.ReadAllText(blocker) == "preserve", "Doctor modified an existing file.");
    }

    private static async Task HardwareChecks()
    {
        var start = SelfStart("--window-fixture");
        using var fixture = Process.Start(start)!;
        try
        {
            var line = await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(line is not null, "Fixture did not publish window bounds.");
            using var state = JsonDocument.Parse(line!);
            var root = state.RootElement;
            var handle = root.GetProperty("handle").GetString()!;
            var x = root.GetProperty("x").GetInt32(); var y = root.GetProperty("y").GetInt32();
            var width = root.GetProperty("width").GetInt32(); var height = root.GetProperty("height").GetInt32();
            await Task.Delay(300);
            await DiscoveryHardwareChecks(fixture, handle, x, y, width, height);
            var path = PathFor("截图 result.png");
            var capture = await Run("capture", "--window-handle", handle, "--backend", "gdi", "--output", path, "--json");
            Success(capture, path, width, height, x, y);
            CheckImage(path, width, height, true);
            Error(await Run("capture", "--window-handle", handle, "--output", path, "--json"), 5, "output_failed");
            Success(await Run("capture", "--window-handle", handle, "--output", path, "--overwrite", "--json"), path, width, height, x, y);
            CheckImage(path, width, height, true);

            var rect = string.Create(CultureInfo.InvariantCulture, $"{x + 20},{y + 20},64,48");
            foreach (var format in new[] { "png", "jpg", "webp", "tiff", "bmp", "pdf" })
            {
                var output = PathFor("region." + format);
                Success(await Run("capture", "--rect", rect, "--backend", "gdi", "--output", output, "--json"), output, 64, 48, x + 20, y + 20);
                Require(new FileInfo(output).Length > 16, "Empty encoded output: " + format);
                if (format == "png") CheckImage(output, 64, 48, true);
            }
            var relative = "相对 路径.png";
            Success(await RunFrom(Root, "capture", "--rect", rect, "--output", relative, "--json"),
                PathFor(relative), 64, 48, x + 20, y + 20);
            Error(await Run("capture", "--rect", "-100000,-100000,10,10", "--output", PathFor("outside.png"), "--json"), 3, "target_not_found");

            var full = PathFor("full.png");
            var result = await Run("capture", "--full", "--output", full, "--json");
            Require(result.Code == 0 && result.Error == "", "Full desktop capture failed: " + result.Output + result.Error);
            using var json = JsonDocument.Parse(result.Output);
            var bounds = json.RootElement.GetProperty("bounds");
            CheckImage(full, bounds.GetProperty("width").GetInt32(), bounds.GetProperty("height").GetInt32(), false);
            var before = File.ReadAllBytes(path);
            var racers = await Task.WhenAll(Run("capture", "--rect", rect, "--output", PathFor("race.png"), "--json"),
                Run("capture", "--rect", rect, "--output", PathFor("race.png"), "--json"));
            Require(racers.Count(r => r.Code == 0) == 1 && racers.Count(r => r.Code == 5) == 1, "Concurrent captures did not preserve no-overwrite semantics.");
            Require(before.SequenceEqual(File.ReadAllBytes(path)), "Unrelated captures changed the previous file.");
        }
        finally
        {
            if (!fixture.HasExited) fixture.Kill(true);
            await fixture.WaitForExitAsync();
        }
    }

    private static async Task DiscoveryHardwareChecks(Process fixture, string handle, int x, int y, int width, int height)
    {
        var processName = fixture.ProcessName;
        var inventory = await Run("windows", "--json");
        Require(inventory.Code == 0, "Window inventory failed.");
        using (var json = JsonDocument.Parse(inventory.Output))
        {
            var window = json.RootElement.GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("handle").GetString() == handle);
            Require(window.GetProperty("title").GetString() == "NimCap CLI fixture" &&
                window.GetProperty("processId").GetInt32() == fixture.Id &&
                window.GetProperty("processName").GetString() == processName && !window.GetProperty("minimized").GetBoolean(),
                "Window inventory does not describe the live fixture.");
        }
        var output = PathFor("selected-window.png");
        Success(await Run("capture", "--process", processName.ToUpperInvariant() + ".EXE", "--window-title", "cLi FiXtUrE",
            "--backend", "gdi", "--output", output, "--json"), output, width, height, x, y);
        CheckImage(output, width, height, true);
        var titleOutput = PathFor("selected-title.png");
        Success(await Run("capture", "--window-title", "NimCap CLI fixture", "--backend", "gdi", "--output", titleOutput, "--json"),
            titleOutput, width, height, x, y);
        var processOutput = PathFor("selected-process.png");
        Success(await Run("capture", "--process", processName, "--backend", "gdi", "--output", processOutput, "--json"),
            processOutput, width, height, x, y);
        Error(await Run("capture", "--process", "no-such-process", "--window-title", "CLI fixture", "--output", output,
            "--overwrite", "--json"), 3, "target_not_found");

        using (var duplicate = Process.Start(SelfStart("--window-fixture"))!)
        {
            try
            {
                Require(await duplicate.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) is not null, "Second fixture did not open.");
                Error(await Run("capture", "--window-title", "CLI fixture", "--output", PathFor("ambiguous.png"), "--json"), 3, "ambiguous_target");
                Error(await Run("capture", "--process", processName, "--window-title", "CLI fixture", "--output", PathFor("ambiguous.png"), "--json"), 3, "ambiguous_target");
                Require(!File.Exists(PathFor("ambiguous.png")), "Ambiguous selection wrote an image.");
            }
            finally { if (!duplicate.HasExited) duplicate.Kill(true); await duplicate.WaitForExitAsync(); }
        }
        var hwnd = (nint)long.Parse(handle[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        ShowWindow(hwnd, 6);
        try
        {
            await Task.Delay(150);
            using (var json = JsonDocument.Parse((await Run("windows", "--json")).Output))
                Require(json.RootElement.GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("handle").GetString() == handle)
                    .GetProperty("minimized").GetBoolean(), "Window inventory lost the minimized state.");
            Error(await Run("capture", "--window-title", "CLI fixture", "--output", PathFor("minimized.png"), "--json"), 3, "target_not_found");
            Error(await Run("capture", "--window-handle", handle, "--output", PathFor("minimized.png"), "--json"), 3, "target_not_found");
        }
        finally { ShowWindow(hwnd, 9); }
        await Task.Delay(300);
        var monitors = await Run("monitors", "--json");
        Require(monitors.Code == 0 && monitors.Error == "", "Monitor discovery failed.");
        using (var json = JsonDocument.Parse(monitors.Output))
        {
            var list = json.RootElement.GetProperty("monitors").EnumerateArray().ToArray();
            Require(list.Length > 0 && list[0].GetProperty("primary").GetBoolean(), "Primary monitor must be first.");
            for (var i = 0; i < list.Length; i++)
            {
                var monitor = list[i]; var bounds = monitor.GetProperty("bounds");
                Require(monitor.GetProperty("index").GetInt32() == i + 1 && monitor.GetProperty("dpiX").GetInt32() > 0 &&
                    monitor.GetProperty("deviceName").GetString()!.Length > 0, "Monitor metadata is invalid.");
                var path = PathFor("monitor-" + (i + 1) + ".png");
                Success(await Run("capture", "--monitor", (i + 1).ToString(CultureInfo.InvariantCulture), "--backend", "gdi", "--output", path, "--json"),
                    path, bounds.GetProperty("width").GetInt32(), bounds.GetProperty("height").GetInt32(), bounds.GetProperty("x").GetInt32(), bounds.GetProperty("y").GetInt32());
            }
        }
        var before = Directory.GetFiles(Root).Order().ToArray();
        var doctor = await Run("doctor", "--output-dir", Root, "--backend", "gdi", "--timeout", "10", "--json");
        Require(doctor.Code == 0 && doctor.Error == "", "GDI doctor probe failed: " + doctor.Output + doctor.Error);
        using (var json = JsonDocument.Parse(doctor.Output))
            Require(json.RootElement.GetProperty("checks").GetArrayLength() == 3 &&
                json.RootElement.GetProperty("checks").EnumerateArray().All(c => c.GetProperty("success").GetBoolean()), "Doctor skipped a real check.");
        Require(before.SequenceEqual(Directory.GetFiles(Root).Order()), "Doctor left temporary output files.");
        var all = await Run("doctor", "--output-dir", Root, "--timeout", "2", "--json");
        using (var json = JsonDocument.Parse(all.Output))
        {
            var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToArray();
            Require(checks.Length == 5 && new[] { "capture:gdi", "capture:wgc", "capture:auto" }.All(name =>
                checks.Any(c => c.GetProperty("name").GetString() == name)), "Doctor all skipped a backend.");
            Require(all.Code == (checks.All(c => c.GetProperty("success").GetBoolean()) ? 0 : 4), "Doctor concealed a partial backend failure.");
        }
        Require(before.SequenceEqual(Directory.GetFiles(Root).Order()), "Doctor all saved a screenshot or leaked a probe file.");
    }

    private static void Success(Result result, string path, int width, int height, int x, int y)
    {
        Require(result.Code == 0 && result.Error == "", "Capture failed: " + result.Output + result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        Require(root.GetProperty("success").GetBoolean() && root.GetProperty("exitCode").GetInt32() == 0 &&
            root.GetProperty("output").GetString() == path, "Wrong success schema or output path.");
        var bounds = root.GetProperty("bounds");
        Require(bounds.GetProperty("x").GetInt32() == x && bounds.GetProperty("y").GetInt32() == y &&
            bounds.GetProperty("width").GetInt32() == width && bounds.GetProperty("height").GetInt32() == height, "Wrong physical capture bounds.");
        Require(root.GetProperty("bytes").GetInt64() == new FileInfo(path).Length, "Completion returned before file commit.");
    }

    private static void Error(Result result, int code, string kind)
    {
        Require(result.Code == code, $"Expected exit {code}, got {result.Code}: {result.Output} {result.Error}");
        Require(result.Output.TrimStart().StartsWith('{'), "CLI did not emit a JSON error result: " + result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        Require(!root.GetProperty("success").GetBoolean() && root.GetProperty("exitCode").GetInt32() == code &&
            root.GetProperty("error").GetProperty("code").GetString() == kind &&
            !string.IsNullOrWhiteSpace(root.GetProperty("error").GetProperty("message").GetString()) && result.Error.Length > 0,
            "Invalid JSON error result or missing stderr diagnostic.");
    }

    private static Task<Result> Run(params string[] args) => RunFrom(Environment.CurrentDirectory, args);

    private static async Task<Result> RunFrom(string directory, params string[] args)
    {
        var start = new ProcessStartInfo(_cli) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return new(process.ExitCode, await output, await error);
    }

    private static void CheckImage(string path, int width, int height, bool checkPixel)
    {
        using var stream = File.OpenRead(path);
        var image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Require(image.PixelWidth == width && image.PixelHeight == height, "Encoded dimensions differ from the requested physical bounds.");
        if (!checkPixel) return;
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        bgra.CopyPixels(new Int32Rect(width / 2, height / 2, 1, 1), pixel, 4, 0);
        Require(pixel[0] == 0xCC && pixel[1] == 0x66 && pixel[2] == 0x33, "Capture did not contain the fixture's actual pixels.");
    }

    private static int WindowFixture()
    {
        var app = new Application();
        var window = new Window { Left = 80, Top = 80, Width = 256, Height = 192, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Topmost = true, ShowActivated = false,
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0xCC)), Title = "NimCap CLI fixture" };
        // ContentRendered may never fire on an occluded/disconnected desktop; Loaded
        // establishes a valid HWND. Pixel checks below still require actual rendering.
        window.Loaded += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            GetWindowRect(handle, out var bounds);
            Console.WriteLine(JsonSerializer.Serialize(new { handle = "0x" + handle.ToInt64().ToString("X"),
                x = bounds.Left, y = bounds.Top, width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top }));
            Console.Out.Flush();
        };
        return app.Run(window);
    }

    private static string PathFor(string name) => Path.Combine(Root, name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
}

using System.Reflection;
using System.Text;
using NimCap.Screenshot.Services;

namespace NimCap.Screenshot.Cli;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args is ["--capture-worker"]) return CaptureWorker.Run();
        if (args is ["--help"] or ["-h"] ||
            (args.Length == 2 && (args[0] is "capture" or "windows" or "monitors" or "doctor") && (args[1] is "--help" or "-h")))
        { Console.WriteLine(Help); return 0; }
        if (args is ["--version"]) { Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version!.ToString(3)); return 0; }
        var json = args.Contains("--json");
        try
        {
            if (args.Length > 0 && args[0] is "windows" or "monitors")
            {
                if (args.Length > 2 || (args.Length == 2 && args[1] != "--json"))
                    throw CliFailure.Usage("List commands accept only --json or --help.");
                if (args[0] == "windows") new CliResult(true, 0, Windows: DesktopInventory.Windows()).Write(json);
                else new CliResult(true, 0, Monitors: DesktopInventory.Monitors()).Write(json);
                return 0;
            }
            if (args.Length > 0 && args[0] == "doctor")
                return DoctorCommand.Parse(args).RunAsync(json).GetAwaiter().GetResult();
            var command = CliCommand.Parse(args);
            command.CheckDestination();
            return CaptureAsync(command, json).GetAwaiter().GetResult();
        }
        catch (CliFailure failure)
        {
            new CliResult(false, failure.ExitCode, Error: new(failure.Code, failure.Message)).Write(json);
            return failure.ExitCode;
        }
        catch (Exception error)
        {
            new CliResult(false, 1, Error: new("internal_error", error.Message)).Write(json);
            return 1;
        }
    }

    private static async Task<int> CaptureAsync(CliCommand command, bool json)
    {
        using var canceled = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(command.TimeoutSeconds));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(canceled.Token, deadline.Token);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; canceled.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            if (command.DelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(command.DelaySeconds), lifetime.Token).ConfigureAwait(false);
            var image = await CaptureProcess.ExecuteAsync(CaptureProcess.StartInfo(), command, lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            try { OutputFileService.Write(command.Output, image.Bytes, command.Overwrite); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { throw CliFailure.Output(error.Message); }
            new CliResult(true, 0, command.Output, image.Bounds, image.Bytes.LongLength).Write(json);
            return 0;
        }
        catch (OperationCanceledException)
        {
            var code = canceled.IsCancellationRequested ? 7 : 6;
            new CliResult(false, code, Error: new(code == 7 ? "canceled" : "timed_out",
                code == 7 ? "Capture was canceled." : "Capture exceeded its timeout.")).Write(json);
            return code;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private const string Help = """
        NimCap Screenshot CLI

        Usage:
          nimcap windows [--json]
          nimcap monitors [--json]
          nimcap doctor [--output-dir <existing-directory>] [--backend all|auto|wgc|gdi]
                        [--timeout <seconds-per-probe>] [--json]
          nimcap capture --full --output <path> [options]
          nimcap capture --rect <x,y,width,height> --output <path> [options]
          nimcap capture --window --output <path> [options]
          nimcap capture --window-handle <decimal|0xHEX> --output <path> [options]
          nimcap capture --monitor <index> --output <path> [options]
          nimcap capture --process <name> [--window-title <text>] --output <path> [options]
          nimcap capture --window-title <text> --output <path> [options]

        Targets:
          --full             Entire virtual desktop (all monitors).
          --rect             Physical desktop pixels; negative coordinates are supported.
          --window           Foreground window at capture time, including after a delay.
          --window-handle    Capture a specific visible, non-minimized window.
          --monitor          1-based index from monitors; primary first, then device name.
          --process          Exact process name, optional .exe; case-insensitive.
          --window-title     Title substring, case-insensitive; may refine --process.
                             No match or multiple matches fail; resolved after --delay.

        Options:
          --output <path>    Required. PNG, JPG/JPEG, WebP, TIFF/TIF, BMP or PDF.
          --overwrite        Replace an existing file; otherwise collisions fail.
          --delay <seconds>  Wait before capture (0-3600; default 0).
          --timeout <sec>    Capture deadline including delay (1-3600; default 30).
          --backend <name>  auto (WGC with GDI fallback), wgc or gdi; default auto.
          --cursor           Include the mouse cursor; default off.
          --json             Emit one JSON result on stdout; diagnostics go to stderr.
          --help, -h         Show help.
          --version          Show version.

        Exit codes: 0 success; 1 internal error; 2 arguments; 3 target unavailable;
                    4 capture failed; 5 output failed; 6 timeout; 7 canceled (Ctrl+C).

        Requires an interactive Windows desktop. Output paths are relative to the
        caller's working directory. The CLI does not start the tray application.
        windows lists visible top-level capture candidates, including minimized state.
        monitors reports physical bounds, device names, DPI and scale (1.0 = 100%).
        doctor defaults: current directory, all backends, 10 seconds per probe.
        It tests desktop access, directory writes and real 1x1 captures without saving
        screenshots. Any failed check returns exit 4 / diagnostic_failed; all includes
        forced gdi, forced wgc and auto, so an unavailable forced backend fails doctor.
        """;
}

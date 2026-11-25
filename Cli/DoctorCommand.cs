using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NimCap.Screenshot.Cli;

internal sealed record DiagnosticCheck(string Name, bool Success, string Message, string? Code = null);

internal sealed record DoctorCommand(string OutputDirectory, string Backend, int TimeoutSeconds)
{
    internal static DoctorCommand Parse(string[] args)
    {
        var directory = Environment.CurrentDirectory;
        var backend = "all";
        var timeout = 10;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) throw CliFailure.Usage("Repeated option: " + option);
            if (option == "--json") continue;
            if (option is not ("--output-dir" or "--backend" or "--timeout")) throw CliFailure.Usage("Unknown option: " + option);
            if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                throw CliFailure.Usage("Missing value for " + option);
            switch (option)
            {
                case "--output-dir":
                    try { directory = Path.GetFullPath(args[i]); }
                    catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
                    { throw CliFailure.Usage("Invalid output directory: " + error.Message); }
                    break;
                case "--backend":
                    backend = args[i].ToLowerInvariant();
                    if (backend is not ("all" or "auto" or "gdi" or "wgc")) throw CliFailure.Usage("--backend must be all, auto, gdi or wgc.");
                    break;
                case "--timeout":
                    if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out timeout) || timeout is < 1 or > 3600)
                        throw CliFailure.Usage("--timeout requires an integer from 1 to 3600 seconds per capture probe.");
                    break;
            }
        }
        return new(directory, backend, timeout);
    }

    internal async Task<int> RunAsync(bool json)
    {
        var checks = new List<DiagnosticCheck>();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
            if (desktop == 0)
                checks.Add(new("desktop", false, new Win32Exception(Marshal.GetLastWin32Error()).Message, "desktop_unavailable"));
            else
            {
                CloseDesktop(desktop);
                checks.Add(new("desktop", true, "The input desktop can be opened. Capture probes below test pixel access."));
            }
            try
            {
                if (!Directory.Exists(OutputDirectory)) throw new IOException("The output directory must already exist.");
                var probe = Path.Combine(OutputDirectory, ".nimcap-doctor-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.DeleteOnClose))
                {
                    file.WriteByte(1);
                    file.Flush(flushToDisk: true);
                }
                checks.Add(new("outputDirectory", true, OutputDirectory));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { checks.Add(new("outputDirectory", false, error.Message, "output_failed")); }

            foreach (var backend in Backend == "all" ? new[] { "gdi", "wgc", "auto" } : [Backend])
            {
                cancellation.Token.ThrowIfCancellationRequested();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                try
                {
                    // Resolve the primary monitor and its tiny region inside the STA worker.
                    var request = new CliCommand("probe", null, 0, "probe.png", false, 0, TimeoutSeconds, backend, false);
                    var image = await CaptureProcess.ExecuteAsync(CaptureProcess.StartInfo(), request, deadline.Token).ConfigureAwait(false);
                    checks.Add(new("capture:" + backend, true, $"Captured and encoded {image.Bounds.Width}x{image.Bounds.Height} pixels; no screenshot saved."));
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { checks.Add(new("capture:" + backend, false, "Capture probe exceeded its timeout.", "timed_out")); }
                catch (CliFailure error)
                { checks.Add(new("capture:" + backend, false, error.Message, error.Code)); }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var success = checks.All(check => check.Success);
            var result = new CliResult(success, success ? 0 : 4, Checks: checks,
                Error: success ? null : new("diagnostic_failed", "One or more diagnostic checks failed. Inspect checks for details."));
            result.Write(json);
            return result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            new CliResult(false, 7, Error: new("canceled", "Diagnostics were canceled."), Checks: checks).Write(json);
            return 7;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
}

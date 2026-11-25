using System.Globalization;
using NimCap.Screenshot.Models;

namespace NimCap.Screenshot.Cli;

internal sealed record CliBounds(int X, int Y, int Width, int Height);

internal sealed record CliCommand(string Target, CliBounds? Rectangle, long WindowHandle, string Output,
    bool Overwrite, int DelaySeconds, int TimeoutSeconds, string Backend, bool IncludeCursor,
    int MonitorIndex = 0, string? ProcessName = null, string? WindowTitle = null)
{
    internal static CliCommand Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "capture") throw CliFailure.Usage("Expected 'capture'. Use 'nimcap --help' for usage.");
        string? target = null, output = null;
        CliBounds? rectangle = null;
        long handle = 0;
        var monitorIndex = 0;
        string? processName = null, windowTitle = null;
        var overwrite = false; var cursor = false;
        var delay = 0; var timeout = 30; var backend = "auto";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) throw CliFailure.Usage("Repeated option: " + option);
            switch (option)
            {
                case "--full": SetTarget("full"); break;
                case "--window": SetTarget("window"); break;
                case "--monitor":
                    SetTarget("monitor");
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out monitorIndex) || monitorIndex < 1)
                        throw CliFailure.Usage("--monitor requires a positive 1-based display index from 'nimcap monitors'.");
                    break;
                case "--process":
                    SetSelector();
                    processName = Value();
                    if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) processName = processName[..^4];
                    if (string.IsNullOrWhiteSpace(processName) || processName.IndexOfAny(['/', '\\', ':']) >= 0)
                        throw CliFailure.Usage("--process requires a process name, optionally ending in .exe, without a path.");
                    break;
                case "--window-title": SetSelector(); windowTitle = Value(); break;
                case "--window-handle":
                    SetTarget("window-handle");
                    var value = Value();
                    var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                    if (!long.TryParse(hex ? value[2..] : value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                        CultureInfo.InvariantCulture, out handle) || handle <= 0)
                        throw CliFailure.Usage("--window-handle requires a positive decimal or hexadecimal handle.");
                    break;
                case "--rect":
                    SetTarget("rect");
                    var fields = Value().Split(',');
                    var numbers = new int[4];
                    if (fields.Length != 4) throw CliFailure.Usage("--rect requires x,y,width,height in physical desktop pixels.");
                    for (var j = 0; j < 4; j++)
                        if (!int.TryParse(fields[j], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[j]))
                            throw CliFailure.Usage("Rectangle coordinates must be 32-bit integers.");
                    if (numbers[2] < 1 || numbers[3] < 1 || numbers[2] > 16384 || numbers[3] > 16384 ||
                        (long)numbers[2] * numbers[3] > 64_000_000 ||
                        (long)numbers[0] + numbers[2] > int.MaxValue || (long)numbers[1] + numbers[3] > int.MaxValue)
                        throw CliFailure.Usage("Rectangle dimensions must be 1–16384, at most 64 million pixels, without coordinate overflow.");
                    rectangle = new(numbers[0], numbers[1], numbers[2], numbers[3]);
                    break;
                case "--output": output = Value(); break;
                case "--overwrite": overwrite = true; break;
                case "--cursor": cursor = true; break;
                case "--json": break;
                case "--delay": delay = Seconds(Value(), option, 0); break;
                case "--timeout": timeout = Seconds(Value(), option, 1); break;
                case "--backend":
                    backend = Value().ToLowerInvariant();
                    if (backend is not ("auto" or "wgc" or "gdi")) throw CliFailure.Usage("--backend must be auto, wgc or gdi.");
                    break;
                default: throw CliFailure.Usage("Unknown option: " + option);
            }
            string Value()
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw CliFailure.Usage("Missing value for " + option);
                return args[i];
            }
        }
        if (target is null) throw CliFailure.Usage("Specify a capture target. Use 'nimcap capture --help'.");
        if (output is null) throw CliFailure.Usage("--output <path> is required.");
        try { output = Path.GetFullPath(output); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw CliFailure.Usage("Invalid output path: " + error.Message); }
        if (!OutputFormat.IsSupported(Path.GetExtension(output)))
            throw CliFailure.Usage("Output extension must be PNG, JPG/JPEG, WebP, TIFF/TIF, BMP or PDF.");
        return new(target, rectangle, handle, output, overwrite, delay, timeout, backend, cursor, monitorIndex, processName, windowTitle);

        void SetSelector()
        {
            if (target is null) target = "window-match";
            else if (target != "window-match") throw CliFailure.Usage("Window selectors cannot be combined with another capture target.");
        }

        void SetTarget(string value)
        {
            if (target is not null) throw CliFailure.Usage("Specify exactly one capture target.");
            target = value;
        }
    }

    private static int Seconds(string value, string option, int minimum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < minimum || seconds > 3600)
            throw CliFailure.Usage($"{option} requires an integer from {minimum} to 3600 seconds.");
        return seconds;
    }

    internal void CheckDestination()
    {
        if (Directory.Exists(Output)) throw CliFailure.Output("The output path is a directory.");
        if (!Overwrite && File.Exists(Output)) throw CliFailure.Output("The output file already exists. Use --overwrite to replace it.");
        for (var parent = Path.GetDirectoryName(Output); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent)) throw CliFailure.Output("An output parent path is a file: " + parent);
    }
}

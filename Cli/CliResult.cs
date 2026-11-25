using System.Text.Json;
using System.Text.Json.Serialization;

namespace NimCap.Screenshot.Cli;

internal sealed record CliError(string Code, string Message);

internal sealed record CliResult(bool Success, int ExitCode, string? Output = null, CliBounds? Bounds = null,
    long? Bytes = null, CliError? Error = null, IReadOnlyList<CliWindow>? Windows = null,
    IReadOnlyList<CliMonitor>? Monitors = null, IReadOnlyList<DiagnosticCheck>? Checks = null)
{
    public int SchemaVersion => 1;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal void Write(bool json)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(this, JsonOptions));
        else if (Windows is not null)
            foreach (var window in Windows) Console.WriteLine($"{window.Handle}\t{window.ProcessId}\t{window.ProcessName}\t{window.Bounds.X},{window.Bounds.Y},{window.Bounds.Width},{window.Bounds.Height}\t{(window.Minimized ? "minimized" : "candidate")}\t{Clean(window.Title)}");
        else if (Monitors is not null)
            foreach (var monitor in Monitors) Console.WriteLine($"{monitor.Index}\t{monitor.DeviceName}\t{monitor.Bounds.X},{monitor.Bounds.Y},{monitor.Bounds.Width},{monitor.Bounds.Height}\tDPI {monitor.DpiX},{monitor.DpiY}\t{(monitor.Primary ? "primary" : "secondary")}");
        else if (Checks is not null)
            foreach (var check in Checks) Console.WriteLine($"{check.Name}\t{(check.Success ? "OK" : "FAIL")}\t{Clean(check.Message)}");
        else if (Success) Console.WriteLine(Output);
        if (Error is { } error) Console.Error.WriteLine(error.Code + ": " + error.Message);
    }

    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}

internal sealed class CliFailure(int exitCode, string code, string message) : Exception(message)
{
    internal int ExitCode { get; } = exitCode;
    internal string Code { get; } = code;
    internal static CliFailure Usage(string message) => new(2, "invalid_arguments", message);
    internal static CliFailure Target(string message) => new(3, "target_not_found", message);
    internal static CliFailure Capture(string message) => new(4, "capture_failed", message);
    internal static CliFailure Output(string message) => new(5, "output_failed", message);
}

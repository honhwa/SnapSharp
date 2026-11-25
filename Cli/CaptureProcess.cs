using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace NimCap.Screenshot.Cli;

internal sealed record CapturedImage(CliBounds Bounds, byte[] Bytes);

internal static class CaptureProcess
{
    internal static ProcessStartInfo StartInfo()
    {
        var executable = Environment.ProcessPath ?? throw CliFailure.Capture("Cannot locate the CLI executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--capture-worker");
        return start;
    }

    internal static async Task<CapturedImage> ExecuteAsync(ProcessStartInfo start, CliCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Process launched;
        try { launched = Process.Start(start) ?? throw CliFailure.Capture("Could not start the capture worker."); }
        catch (Win32Exception startupError) { throw CliFailure.Capture("Could not start the capture worker: " + startupError.Message); }
        using var worker = launched;
        var image = ReadFrameAsync(worker.StandardOutput.BaseStream, token);
        var error = worker.StandardError.ReadToEndAsync(token);
        try
        {
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), token).ConfigureAwait(false);
            worker.StandardInput.Close();
            await worker.WaitForExitAsync(token).ConfigureAwait(false);
            var diagnostic = await error.ConfigureAwait(false);
            if (worker.ExitCode != 0)
            {
                try
                {
                    var result = JsonSerializer.Deserialize<CliResult>(diagnostic, CliResult.JsonOptions);
                    if (result is { ExitCode: 3 or 4, Error: { } detail })
                        throw new CliFailure(result.ExitCode, detail.Code, detail.Message);
                }
                catch (JsonException) { }
                throw CliFailure.Capture("The capture worker exited unexpectedly (exit " + worker.ExitCode + ").");
            }
            return await image.ConfigureAwait(false);
        }
        finally
        {
            if (!worker.HasExited)
            {
                try { worker.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            try { await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            // Observe canceled/failed pipe reads after terminating the worker.
            try { await Task.WhenAll(image, error).ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    private static async Task<CapturedImage> ReadFrameAsync(Stream input, CancellationToken token)
    {
        var header = new byte[24];
        try
        {
            await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var magic = BinaryPrimitives.ReadInt32LittleEndian(header);
            var x = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            var y = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            var width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
            var height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
            var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
            if (magic != CaptureWorker.FrameMagic || width is < 1 or > 16384 || height is < 1 or > 16384 ||
                (long)width * height > 64_000_000 || count is < 1 or > CaptureWorker.MaximumEncodedBytes)
                throw CliFailure.Capture("Invalid capture worker frame.");
            var bytes = new byte[count];
            await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            return new(new(x, y, width, height), bytes);
        }
        catch (EndOfStreamException) { throw CliFailure.Capture("The capture worker stopped before returning an image."); }
    }
}

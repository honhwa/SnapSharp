using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NimCap.Screenshot.Services;

namespace NimCap.Screenshot.Cli;

internal sealed record CliWindow(string Handle, string Title, int ProcessId, string? ProcessName,
    CliBounds Bounds, bool Minimized);
internal sealed record CliMonitor(int Index, string DeviceName, bool Primary, CliBounds Bounds,
    uint DpiX, uint DpiY, bool DpiKnown, double ScaleX, double ScaleY);

internal static class DesktopInventory
{
    internal static IReadOnlyList<CliWindow> Windows()
    {
        var result = new List<CliWindow>();
        foreach (var candidate in new ScreenCaptureService().EnumerateTopLevelWindows())
        {
            var handle = candidate.Handle;
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == 0 || pid > int.MaxValue) continue;
            var title = new StringBuilder(Math.Clamp(GetWindowTextLength(handle) + 1, 1, 32768));
            GetWindowText(handle, title, title.Capacity);
            string? name = null;
            try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException) { }
            if (!IsWindow(handle) || !IsWindowVisible(handle)) continue;
            var bounds = candidate.Bounds;
            result.Add(new("0x" + handle.ToInt64().ToString("X", CultureInfo.InvariantCulture), title.ToString(), (int)pid, name,
                new(bounds.X, bounds.Y, bounds.Width, bounds.Height), IsIconic(handle)));
        }
        return result;
    }

    internal static nint SelectWindow(string? processName, string? title)
    {
        var matches = Windows().Where(w => !w.Minimized &&
            (processName is null || string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) &&
            (title is null || w.Title.Contains(title, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length == 0) throw CliFailure.Target("No visible, non-minimized window matches the selectors.");
        if (matches.Length > 1) throw new CliFailure(3, "ambiguous_target",
            $"{matches.Length} windows match. Refine --process/--window-title or use a handle from 'nimcap windows --json'.");
        return (nint)long.Parse(matches[0].Handle[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    internal static IReadOnlyList<CliMonitor> Monitors()
    {
        var result = DisplayMonitorService.Enumerate().Select(monitor =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfo(monitor.Handle, ref info)) throw CliFailure.Capture("Cannot read monitor information.");
            var bounds = monitor.Bounds;
            return new CliMonitor(0, info.Device, (info.Flags & 1) != 0,
                new(bounds.X, bounds.Y, bounds.Width, bounds.Height), monitor.DpiX, monitor.DpiY,
                monitor.DpiKnown, monitor.DpiX / 96d, monitor.DpiY / 96d);
        }).OrderByDescending(m => m.Primary).ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
          .Select((monitor, index) => monitor with { Index = index + 1 }).ToArray();
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
}

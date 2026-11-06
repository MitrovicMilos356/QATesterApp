using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using UiInspector.Core.Recording;

namespace UiInspector.Core.Reporting;

/// <summary>An app that a recording touched.</summary>
public sealed record AppInfo(string ProcessName, string? ProductName, string? Version, string? Path);

public sealed record MonitorInfo(int Width, int Height, int ScalePercent, bool IsPrimary);

/// <summary>The "Environment" part of a bug report: where the steps were recorded.</summary>
public sealed record EnvironmentInfo
{
    /// <summary>E.g. "Windows 11 Pro 23H2 (build 22631.3447, 64-bit)".</summary>
    public required string Windows { get; init; }

    public IReadOnlyList<AppInfo> Apps { get; init; } = [];

    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = [];

    /// <summary>E.g. "English (United States), en-US".</summary>
    public required string Language { get; init; }

    public DateTime RecordedAt { get; init; }

    public required string TimeZone { get; init; }

    public static EnvironmentInfo Collect(IReadOnlyList<RecordedStep> steps, DateTime recordedAt) => new()
    {
        Windows = DescribeWindows(),
        Apps = steps
            .Select(s => (s.Target.ProcessId, Name: s.Selector?.ProcessName ?? s.Target.ProcessName, s.Selector?.ProcessPath))
            .Where(a => a.Name.Length > 0)
            .DistinctBy(a => a.Name)
            .Select(a => DescribeApp(a.Name, a.ProcessPath ?? TryGetPath(a.ProcessId)))
            .ToList(),
        Monitors = EnumerateMonitors(),
        Language = $"{CultureInfo.CurrentUICulture.DisplayName}, {CultureInfo.CurrentUICulture.Name}",
        RecordedAt = recordedAt,
        TimeZone = TimeZoneInfo.Local.DisplayName,
    };

    // ------------------------------------------------------------------ Windows version

    private static string DescribeWindows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var release = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string;
            var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
            var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";

            // Windows 11 still reports "Windows 10" as its product name; the build number tells them apart.
            if (int.TryParse(build, out var number) && number >= 22000) product = product.Replace("Windows 10", "Windows 11");
            var bits = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
            return $"{product}{(release is null ? "" : " " + release)} (build {build}{ubr}, {bits})";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return RuntimeInformation.OSDescription;
        }
    }

    // ------------------------------------------------------------------ apps

    private static AppInfo DescribeApp(string processName, string? path)
    {
        if (path is null || !File.Exists(path)) return new AppInfo(processName, null, null, path);
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            // Windows' own programs (Notepad, PowerShell…) all name the OS as their product; their
            // file description is the useful name there.
            var product = string.IsNullOrWhiteSpace(version.ProductName) || version.ProductName.Contains("Operating System", StringComparison.OrdinalIgnoreCase)
                ? version.FileDescription ?? version.ProductName
                : version.ProductName;
            var number = string.IsNullOrWhiteSpace(version.ProductVersion) ? version.FileVersion : version.ProductVersion;
            return new AppInfo(processName, NullIfBlank(product), NullIfBlank(number), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppInfo(processName, null, null, path);
        }
    }

    private static string? TryGetPath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null; // the app has exited, or runs elevated
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ------------------------------------------------------------------ monitors

    private static List<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var dpi = GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? (int)x : 96;
                monitors.Add(new MonitorInfo(
                    info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top,
                    (int)Math.Round(dpi * 100.0 / 96), (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return monitors.OrderByDescending(m => m.IsPrimary).ToList();
    }

    private const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}

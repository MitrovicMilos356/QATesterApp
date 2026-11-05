using System.Drawing;
using System.Runtime.InteropServices;

namespace UiInspector.Core;

/// <summary>
/// Cursor and monitor DPI queries. Coordinates are physical screen pixels as long as the
/// host process is per-monitor-DPI aware (v2), which matches what UI Automation uses.
/// </summary>
public static class ScreenInfo
{
    public static Point GetCursorPosition() =>
        GetCursorPos(out var p) ? new Point(p.X, p.Y) : throw new InvalidOperationException("GetCursorPos failed.");

    /// <summary>Effective DPI of the monitor containing <paramref name="point"/> (96 = 100 %).</summary>
    public static int GetDpiAt(Point point)
    {
        var monitor = MonitorFromPoint(new POINT(point.X, point.Y), MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? (int)dpiX : 96;
    }

    /// <summary>False for points outside every monitor (e.g. windows parked at -32000).</summary>
    public static bool IsOnAnyMonitor(Point point) =>
        MonitorFromPoint(new POINT(point.X, point.Y), MONITOR_DEFAULTTONULL) != IntPtr.Zero;

    /// <summary>The DPI awareness this process actually runs with (useful to verify the manifest took effect).</summary>
    public static string GetProcessDpiAwarenessName()
    {
        var context = GetThreadDpiAwarenessContext();
        if (AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return "PerMonitorV2";
        if (AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE)) return "PerMonitor";
        if (AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_SYSTEM_AWARE)) return "SystemAware";
        return "Unaware";
    }

    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = -2;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct POINT(int X, int Y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
}

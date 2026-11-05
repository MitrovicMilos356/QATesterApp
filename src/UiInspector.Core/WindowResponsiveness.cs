using System.Drawing;
using System.Runtime.InteropServices;

namespace UiInspector.Core;

/// <summary>
/// Detects whether the top-level window at a screen point is hung. UIA surfaces hung targets
/// as unrelated COM errors (e.g. 0x80070002 "file not found"), so callers use this to explain
/// a failed inspection in plain words.
/// </summary>
public static class WindowResponsiveness
{
    private const uint GA_ROOT = 2;
    private const uint WM_NULL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint ProbeTimeoutMs = 250;

    public static bool IsWindowAtPointHung(Point screenPoint)
    {
        var window = WindowFromPoint(new POINT(screenPoint.X, screenPoint.Y));
        if (window == IntPtr.Zero) return false;

        var root = GetAncestor(window, GA_ROOT);
        return IsHung(root == IntPtr.Zero ? window : root);
    }

    public static bool IsForegroundWindowHung()
    {
        var window = GetForegroundWindow();
        return window != IntPtr.Zero && IsHung(window);
    }

    private static bool IsHung(IntPtr window) =>
        IsHungAppWindow(window)
        || SendMessageTimeout(window, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, ProbeTimeoutMs, out _) == IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct POINT(int X, int Y);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
}

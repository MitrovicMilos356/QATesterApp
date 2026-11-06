using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace UiInspector.Core.Reporting;

/// <summary>A captured window: the image, and where it was on screen (to place element outlines).</summary>
public sealed record WindowImage(byte[] Png, Rectangle ScreenBounds, double Scale, int Width, int Height);

/// <summary>A step's screenshot: the window as it was, and the same with the step's element outlined.</summary>
public sealed record StepScreenshot(byte[] Png, byte[] HighlightedPng, int Width, int Height);

/// <summary>
/// Screenshots for bug reports. Only the app's own window is captured (PrintWindow asks the
/// window to draw itself), never the screen, so other apps and overlapping windows don't end
/// up in a report.
/// </summary>
public static class WindowCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x2; // also captures DirectComposition content (Chromium, WinUI)
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint GA_ROOT = 2;
    private const int MaxWidth = 1600;             // keeps reports small; larger windows are scaled down
    private static readonly Color HighlightColor = Color.FromArgb(220, 38, 38);

    /// <param name="window">Any window handle; its top-level window is captured.</param>
    /// <returns>Null if the window can't be captured (minimized, gone, no handle).</returns>
    public static WindowImage? CaptureWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return null;
        window = GetAncestor(window, GA_ROOT) is var root && root != IntPtr.Zero ? root : window;
        if (IsIconic(window) || !GetWindowRect(window, out var outer)) return null;

        // GetWindowRect includes the invisible resize borders; the DWM frame bounds are what you see.
        var visible = DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0
            ? frame : outer;
        var width = outer.Right - outer.Left;
        var height = outer.Bottom - outer.Top;
        if (width <= 0 || height <= 0 || width * (long)height > 64_000_000) return null;

        // Two ways to get the picture, each blind in one case:
        // - the compositor's copy (PW_RENDERFULLCONTENT) is the only one with Chromium, WPF and WinUI
        //   content, but for parts of a window that are off every monitor it's old or empty, because
        //   Windows never repaints those parts;
        // - asking the window to paint itself (plain PrintWindow) is always current for classic
        //   Win32/WinForms windows, but DirectX-drawn content comes out black.
        // So: the compositor's copy for windows fully on screen, the window's own painting otherwise,
        // and the other one whenever the first comes out blank.
        var client = ClientArea(window, outer);
        uint[] order = IsFullyOnScreen(outer) ? [PW_RENDERFULLCONTENT, 0] : [0, PW_RENDERFULLCONTENT];
        Bitmap? full = null;
        foreach (var flags in order)
        {
            var attempt = Print(window, width, height, flags);
            if (attempt is null) continue;
            full?.Dispose();
            full = attempt;
            if (!IsBlank(attempt, client)) break;
        }
        if (full is null) return null;
        using var _ = full;

        var crop = Rectangle.Intersect(
            new Rectangle(visible.Left - outer.Left, visible.Top - outer.Top, visible.Right - visible.Left, visible.Bottom - visible.Top),
            new Rectangle(0, 0, width, height));
        if (crop.Width <= 0 || crop.Height <= 0) return null;

        var scale = crop.Width > MaxWidth ? (double)MaxWidth / crop.Width : 1.0;
        using var image = new Bitmap(Math.Max(1, (int)(crop.Width * scale)), Math.Max(1, (int)(crop.Height * scale)), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(image))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, new Rectangle(0, 0, image.Width, image.Height), crop, GraphicsUnit.Pixel);
        }
        var bounds = new Rectangle(outer.Left + crop.X, outer.Top + crop.Y, crop.Width, crop.Height);
        return new WindowImage(Encode(image), bounds, scale, image.Width, image.Height);
    }

    /// <summary>The step's screenshot: the window image, plus a copy with <paramref name="element"/> (screen pixels) outlined.</summary>
    public static StepScreenshot Mark(WindowImage window, Rectangle? element)
    {
        if (element is not { Width: > 0, Height: > 0 } rect || !rect.IntersectsWith(window.ScreenBounds))
        {
            return new StepScreenshot(window.Png, window.Png, window.Width, window.Height);
        }

        using var stream = new MemoryStream(window.Png);
        using var decoded = new Bitmap(stream);
        using var image = new Bitmap(decoded);
        using (var g = Graphics.FromImage(image))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(HighlightColor, 3);
            var x = (rect.X - window.ScreenBounds.X) * window.Scale;
            var y = (rect.Y - window.ScreenBounds.Y) * window.Scale;
            g.DrawRectangle(pen, (float)x - 3, (float)y - 3, (float)(rect.Width * window.Scale) + 6, (float)(rect.Height * window.Scale) + 6);
        }
        return new StepScreenshot(window.Png, Encode(image), window.Width, window.Height);
    }

    private static bool IsFullyOnScreen(RECT r) =>
        new[] { new Point(r.Left, r.Top), new Point(r.Right - 1, r.Top), new Point(r.Left, r.Bottom - 1), new Point(r.Right - 1, r.Bottom - 1) }
            .All(ScreenInfo.IsOnAnyMonitor);

    private static Bitmap? Print(IntPtr window, int width, int height, uint flags)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        var hdc = g.GetHdc();
        try
        {
            if (PrintWindow(window, hdc, flags)) return bitmap;
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        bitmap.Dispose();
        return null;
    }

    /// <summary>The client area (inside the frame) in bitmap coordinates.</summary>
    private static Rectangle ClientArea(IntPtr window, RECT outer)
    {
        if (!GetClientRect(window, out var rect)) return Rectangle.Empty;
        var origin = new POINT();
        if (!ClientToScreen(window, ref origin)) return Rectangle.Empty;
        return new Rectangle(origin.X - outer.Left, origin.Y - outer.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>True when the client area is a single color: nothing was drawn there.</summary>
    private static bool IsBlank(Bitmap bitmap, Rectangle client)
    {
        client.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (client.Width < 4 || client.Height < 4) return false;
        const int grid = 24;
        var first = bitmap.GetPixel(client.X + client.Width / 2, client.Y + client.Height / 2).ToArgb();
        for (var i = 0; i < grid; i++)
        {
            for (var j = 0; j < grid; j++)
            {
                var x = client.X + (int)((i + 0.5) * client.Width / grid);
                var y = client.Y + (int)((j + 0.5) * client.Height / grid);
                if (bitmap.GetPixel(x, y).ToArgb() != first) return false;
            }
        }
        return true;
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}

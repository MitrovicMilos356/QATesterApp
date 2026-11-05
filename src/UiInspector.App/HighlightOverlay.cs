using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DrawingRectangle = System.Drawing.Rectangle;

namespace UiInspector.App;

public interface IHighlighter
{
    /// <summary>Draws a frame around <paramref name="screenRect"/> (physical pixels) with a caption.</summary>
    void Show(DrawingRectangle screenRect, string caption, bool preview);

    void Hide();
}

/// <summary>
/// A click-through, never-activated, topmost window that frames an element on screen.
/// It is positioned with SetWindowPos in physical pixels, so it lines up with UIA rectangles
/// on every monitor regardless of scaling. WS_EX_TRANSPARENT keeps it out of hit-testing,
/// so it never ends up being the element the inspector finds under the cursor.
/// </summary>
public sealed class HighlightOverlay : Window, IHighlighter
{
    private const int FramePadding = 2;

    private static readonly Color SelectionColor = Color.FromRgb(0xDC, 0x26, 0x26);
    private static readonly Color PreviewColor = Color.FromRgb(0xF5, 0x9E, 0x0B);

    private readonly Border _frame;
    private readonly TextBlock _caption;
    private IntPtr _hwnd;

    public HighlightOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;

        _caption = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            Padding = new Thickness(4, 1, 4, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _frame = new Border { BorderThickness = new Thickness(2), Child = _caption };
        Content = _frame;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            style |= Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
        };
    }

    public void Show(DrawingRectangle screenRect, string caption, bool preview)
    {
        if (screenRect.Width <= 0 || screenRect.Height <= 0)
        {
            Hide();
            return;
        }

        var color = preview ? PreviewColor : SelectionColor;
        _frame.BorderBrush = new SolidColorBrush(color);
        _frame.Background = new SolidColorBrush(Color.FromArgb(0x1A, color.R, color.G, color.B));
        _caption.Background = new SolidColorBrush(color);
        _caption.Text = caption;
        _caption.Visibility = string.IsNullOrEmpty(caption) ? Visibility.Collapsed : Visibility.Visible;

        if (!IsVisible) base.Show();

        var r = DrawingRectangle.Inflate(screenRect, FramePadding, FramePadding);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public new void Hide()
    {
        if (IsVisible) base.Hide();
    }
}

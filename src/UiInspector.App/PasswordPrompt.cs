using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace UiInspector.App;

/// <summary>Asks for the password a replayed step types. Recordings never contain passwords.</summary>
public static class PasswordPrompt
{
    /// <returns>The password, or null if the person cancelled.</returns>
    public static string? Ask(Window? owner, string step)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 8, 0, 12), MinWidth = 300 };
        AutomationProperties.SetAutomationId(box, "ReplayPassword");
        AutomationProperties.SetName(box, "Password");
        var ok = new Button { Content = "Play", IsDefault = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 3, 14, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var text = new TextBlock
        {
            Text = $"This step types a password, which isn't stored in the recording:\n{step}\n\nEnter it to replay; it's only used for this replay.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };

        var dialog = new Window
        {
            Title = "Password for the replay",
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Thickness(16), Children = { text, box, buttons } },
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => box.Focus();
        return dialog.ShowDialog() == true ? box.Password : null;
    }
}

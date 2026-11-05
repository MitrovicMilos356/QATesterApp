using System.Text;
using UiInspector.Core;
using DrawingRectangle = System.Drawing.Rectangle;

namespace UiInspector.App;

internal static class Format
{
    private const int MaxNameLength = 80;

    public static string Quote(string name) => string.IsNullOrEmpty(name) ? "(no name)" : $"\"{Clean(name, MaxNameLength)}\"";

    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public static string Clean(string text, int max) => Truncate(text.ReplaceLineEndings(" ⏎ ").Replace('\t', ' '), max);

    public static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    /// <summary>Short label such as <c>Hyperlink "Dyno (direct message)"</c>.</summary>
    public static string Describe(ElementInfo e) => $"{Or(e.ControlType, "?")} {Quote(e.Name)}";

    public static string Rect(DrawingRectangle r) => r.IsEmpty ? "(empty)" : $"x={r.X} y={r.Y} w={r.Width} h={r.Height}";

    public static string State(ElementInfo e)
    {
        var parts = new List<string> { e.IsEnabled ? "enabled" : "DISABLED", e.IsOffscreen ? "OFFSCREEN" : "on-screen" };
        if (e.IsKeyboardFocusable) parts.Add("focusable");
        if (e.HasKeyboardFocus) parts.Add("has focus");
        if (e.IsPassword) parts.Add("password");
        return string.Join(" · ", parts);
    }

    public static IReadOnlyList<(string Name, string Value)> Properties(ElementInfo e)
    {
        var rows = new List<(string, string)>
        {
            ("Name", Or(e.Name, "—")),
            ("AutomationId", Or(e.AutomationId, "—")),
            ("ControlType", string.IsNullOrEmpty(e.LocalizedControlType) ? Or(e.ControlType, "—") : $"{Or(e.ControlType, "—")}  (localized: \"{e.LocalizedControlType}\")"),
            ("ClassName", Or(e.ClassName, "—")),
            ("FrameworkId", Or(e.FrameworkId, "—")),
        };
        if (!string.IsNullOrEmpty(e.AriaRole)) rows.Add(("AriaRole", e.AriaRole));
        rows.Add(("Process", $"{Or(e.ProcessName, "?")} (pid {e.ProcessId})"));
        rows.Add(("BoundingRectangle", Rect(e.BoundingRectangle)));
        rows.Add(("State", State(e)));
        if (!string.IsNullOrEmpty(e.HelpText)) rows.Add(("HelpText", e.HelpText));
        rows.Add(("NativeWindowHandle", e.NativeWindowHandle == IntPtr.Zero ? "—" : $"0x{e.NativeWindowHandle:X}"));
        rows.Add(("RuntimeId", e.RuntimeId.Count == 0 ? "—" : string.Join('.', e.RuntimeId) + "  (session only)"));
        rows.Add(("Patterns", e.Patterns.Count == 0 ? "—" : string.Join(", ", e.Patterns)));
        return rows;
    }

    /// <summary>Plain-text report for the clipboard: properties plus the path from the desktop.</summary>
    public static string Report(ElementInfo element, IReadOnlyList<ElementInfo> pathFromRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Describe(element));
        foreach (var (name, value) in Properties(element)) sb.AppendLine($"  {name,-20}{value}");

        if (pathFromRoot.Count > 0)
        {
            sb.AppendLine().AppendLine("Path (desktop → element)");
            for (var i = 0; i < pathFromRoot.Count; i++)
            {
                var node = pathFromRoot[i];
                var indent = i == 0 ? "  " : "  " + new string(' ', (i - 1) * 3) + "└─ ";
                var id = string.IsNullOrEmpty(node.AutomationId) ? "" : $"  #{node.AutomationId}";
                var cls = string.IsNullOrEmpty(node.ClassName) ? "" : $"  .{node.ClassName}";
                sb.AppendLine($"{indent}{Describe(node)}{id}{cls}");
            }
        }
        return sb.ToString();
    }
}

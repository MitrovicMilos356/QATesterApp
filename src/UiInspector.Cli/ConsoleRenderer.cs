using System.Drawing;
using UiInspector.Core;
using UiInspector.Core.Selectors;
using UiInspector.Core.Web;

namespace UiInspector.Cli;

/// <summary>Formats inspection results for the console. All writes are serialized.</summary>
public sealed class ConsoleRenderer
{
    private const int MaxTextLength = 80;
    private const int LabelWidth = 20;
    private readonly Lock _gate = new();

    public void Info(string message) => Write(ConsoleColor.Gray, message);

    public void Warning(string message) => Write(ConsoleColor.Yellow, message);

    public void Error(string message) => Write(ConsoleColor.Red, message);

    public void Success(string message) => Write(ConsoleColor.Green, message);

    public void WaitingForWebContent() =>
        Write(ConsoleColor.DarkGray, "Web page is still building its accessibility tree, waiting…");

    public void Render(InspectionResult result)
    {
        lock (_gate)
        {
            var e = result.Element;
            Console.WriteLine();
            WriteColored(ConsoleColor.DarkCyan, $"━━ {Header(result)} ━━");
            Console.WriteLine();

            Console.Write("  ");
            WriteColored(ConsoleColor.Cyan, Or(e.ControlType, "(unknown type)"));
            WriteColored(ConsoleColor.White, $"  {Quote(e.Name)}");
            Console.WriteLine();

            Row("AutomationId", Or(e.AutomationId, "—"));
            Row("ControlType", string.IsNullOrEmpty(e.LocalizedControlType)
                ? Or(e.ControlType, "—")
                : $"{Or(e.ControlType, "—")}  (localized: \"{e.LocalizedControlType}\")");
            Row("ClassName", Or(e.ClassName, "—"));
            Row("FrameworkId", Or(e.FrameworkId, "—"));
            if (!string.IsNullOrEmpty(e.AriaRole)) Row("AriaRole", e.AriaRole);
            Row("Process", $"{Or(e.ProcessName, "?")} (pid {e.ProcessId})");
            Row("BoundingRectangle", Rect(e.BoundingRectangle));
            Row("State", State(e));
            if (!string.IsNullOrEmpty(e.HelpText)) Row("HelpText", Clean(e.HelpText));
            Row("NativeWindowHandle", e.NativeWindowHandle == IntPtr.Zero ? "—" : $"0x{e.NativeWindowHandle:X}");
            Row("RuntimeId", e.RuntimeId.Count == 0 ? "—" : string.Join('.', e.RuntimeId) + "  (session only)");
            Row("Patterns", e.Patterns.Count == 0 ? "—" : string.Join(", ", e.Patterns));

            if (result.Ancestors.Count > 0) RenderAncestors(result);
            if (result.WebContentPending) RenderWebContentHint(e);
        }
    }

    private static void RenderWebContentHint(ElementInfo page)
    {
        var app = string.IsNullOrEmpty(page.ProcessName) ? "the app" : page.ProcessName;
        Console.WriteLine();
        WriteColored(ConsoleColor.Yellow,
            $"  ⚠ This is an empty web page root: {app} hasn't exposed its page content to UI Automation.\n" +
            $"    Chromium/Electron apps build it only for assistive tools. Restart {app} with the\n" +
            "    --force-renderer-accessibility command-line switch, then inspect again.");
        Console.WriteLine();
    }

    public void RenderWeb(WebResult web, IReadOnlyList<CodeSnippet> snippets)
    {
        lock (_gate)
        {
            Console.WriteLine();
            WriteColored(ConsoleColor.DarkCyan, "━━ Web page (Chrome DevTools Protocol) ━━");
            Console.WriteLine();

            if (!web.Connected)
            {
                WriteColored(ConsoleColor.Yellow, $"  {web.Problem}");
                Console.WriteLine();
                return;
            }

            Row("Page", $"{web.PageTitle}  ({web.PageUrl})  via port {web.Port}");
            if (web.Element is { } element) Row("DOM element", element.Describe());
            foreach (var note in web.Notes)
            {
                WriteColored(ConsoleColor.Yellow, $"  ⚠ {note}");
                Console.WriteLine();
            }

            if (web.Best is { } best)
            {
                var ok = best.Verification.Status == VerificationStatus.Unique;
                WriteColored(ok ? ConsoleColor.Green : ConsoleColor.Red, $"  {(ok ? "✔" : "✖")} Playwright: {best.Verification.Describe()}");
                WriteColored(ConsoleColor.DarkGray, $"   Strategy: {best.Strategy}");
                Console.WriteLine();
                foreach (var frame in best.Frames) Row("Frame", frame.ToString());
                foreach (var step in best.Steps) Row(step == best.Steps[^1] ? "Target" : "Inside", step.ToString());
                foreach (var warning in best.Warnings)
                {
                    WriteColored(ConsoleColor.Yellow, $"  ⚠ {warning}");
                    Console.WriteLine();
                }
            }
            if (web.Selenium is { } selenium)
            {
                var ok = selenium.Verification.Status == VerificationStatus.Unique;
                WriteColored(ok ? ConsoleColor.Green : ConsoleColor.Red, $"  {(ok ? "✔" : "✖")} Selenium: {selenium.Verification.Describe()}");
                WriteColored(ConsoleColor.DarkGray, $"   CSS: {selenium.Target}");
                Console.WriteLine();
            }

            foreach (var snippet in snippets)
            {
                Console.WriteLine();
                WriteColored(ConsoleColor.DarkCyan, $"  ── {snippet.Title} ──");
                Console.WriteLine();
                foreach (var line in snippet.Code.TrimEnd().Split('\n')) Console.WriteLine("  " + line.TrimEnd('\r'));
            }
        }
    }

    public void RenderSelector(SelectorResult result, IReadOnlyList<CodeSnippet> snippets)
    {
        lock (_gate)
        {
            Console.WriteLine();
            WriteColored(ConsoleColor.DarkCyan, "━━ Selector ━━");
            Console.WriteLine();

            if (result.Best is not { } best)
            {
                WriteColored(ConsoleColor.Red, $"  {result.Problem ?? "No selector found."}");
                Console.WriteLine();
                return;
            }

            var ok = best.Verification.Status == VerificationStatus.Unique;
            WriteColored(ok ? ConsoleColor.Green : ConsoleColor.Red, $"  {(ok ? "✔" : "✖")} {best.Verification.Describe()}");
            WriteColored(ConsoleColor.DarkGray, $"   Strategy: {best.Strategy}");
            Console.WriteLine();
            Row("Window", best.Window.ToString());
            for (var i = 0; i < best.Steps.Count; i++)
            {
                Row(i == best.Steps.Count - 1 ? "Target" : "Inside", best.Steps[i].ToString());
            }
            foreach (var warning in best.Warnings)
            {
                WriteColored(ConsoleColor.Yellow, $"  ⚠ {warning}");
                Console.WriteLine();
            }
            if (result.Occlusion is { } occlusion)
            {
                WriteColored(occlusion.IsCovered ? ConsoleColor.Yellow : ConsoleColor.DarkGray,
                    $"  {(occlusion.IsCovered ? "⚠" : "·")} Click check: {occlusion.Message}");
                Console.WriteLine();
            }
            foreach (var alternative in result.Alternatives)
            {
                WriteColored(ConsoleColor.DarkGray, $"  Alternative: {alternative.Strategy} ({alternative.Verification.Status})");
                Console.WriteLine();
            }

            foreach (var snippet in snippets)
            {
                Console.WriteLine();
                WriteColored(ConsoleColor.DarkCyan, $"  ── {snippet.Title} ──");
                Console.WriteLine();
                foreach (var line in snippet.Code.TrimEnd().Split('\n'))
                {
                    Console.WriteLine("  " + line.TrimEnd('\r'));
                }
            }
        }
    }

    /// <summary>One-line summary used by hover tracking.</summary>
    public void RenderCompact(InspectionResult result)
    {
        lock (_gate)
        {
            var e = result.Element;
            WriteColored(ConsoleColor.DarkGray, "[hover] ");
            WriteColored(ConsoleColor.Cyan, Or(e.ControlType, "?"));
            WriteColored(ConsoleColor.White, $" {Quote(e.Name)}");
            if (!string.IsNullOrEmpty(e.AutomationId)) WriteColored(ConsoleColor.Green, $" #{e.AutomationId}");
            WriteColored(ConsoleColor.DarkGray,
                $"  {Or(e.FrameworkId, "?")}, {Or(e.ProcessName, "?")}  {Rect(e.BoundingRectangle)}  {result.Duration.TotalMilliseconds:0} ms");
            if (result.WebContentPending) WriteColored(ConsoleColor.Yellow, "  (page content not ready yet)");
            Console.WriteLine();
        }
    }

    private static void RenderAncestors(InspectionResult result)
    {
        Console.WriteLine();
        WriteColored(ConsoleColor.DarkCyan, "  Ancestors (desktop → element)");
        Console.WriteLine();

        // Ancestors come nearest-first; print root-first so it reads like a path.
        var chain = result.Ancestors.Reverse().Append(result.Element).ToList();
        if (result.AncestorsTruncated)
        {
            WriteColored(ConsoleColor.Yellow, "  … (depth limit reached; root not shown)");
            Console.WriteLine();
        }

        for (var i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            var isTarget = i == chain.Count - 1;
            var indent = i == 0 ? "  " : "  " + new string(' ', (i - 1) * 3) + "└─ ";

            Console.Write(indent);
            if (isTarget) WriteColored(ConsoleColor.Magenta, "▶ ");
            WriteColored(ConsoleColor.Cyan, Or(node.ControlType, "?"));
            WriteColored(isTarget ? ConsoleColor.White : ConsoleColor.Gray, $" {Quote(node.Name)}");
            if (!string.IsNullOrEmpty(node.AutomationId)) WriteColored(ConsoleColor.Green, $"  #{node.AutomationId}");
            if (!string.IsNullOrEmpty(node.ClassName)) WriteColored(ConsoleColor.DarkGray, $"  .{node.ClassName}");

            // A change of UI framework marks a boundary worth knowing about, e.g. a
            // Chromium/WebView2 surface hosted inside a Win32 or WPF window.
            if (i > 0 && !string.IsNullOrEmpty(node.FrameworkId) && !string.IsNullOrEmpty(chain[i - 1].FrameworkId)
                && node.FrameworkId != chain[i - 1].FrameworkId)
            {
                WriteColored(ConsoleColor.Yellow, $"  ⟵ framework: {chain[i - 1].FrameworkId} → {node.FrameworkId}");
            }
            else if (i == 1)
            {
                WriteColored(ConsoleColor.DarkGray, $"  [{Or(node.FrameworkId, "?")}, {Or(node.ProcessName, "?")}]");
            }
            Console.WriteLine();
        }
    }

    private static string Header(InspectionResult result)
    {
        var timing = $"{result.Duration.TotalMilliseconds:0} ms";
        if (result.Source == InspectionSource.Focus || result.ScreenPoint is not { } point)
            return $"Focused element · {timing}";

        var dpi = ScreenInfo.GetDpiAt(point);
        return $"Element at ({point.X}, {point.Y}) · monitor {dpi} DPI ({dpi * 100 / 96}%) · {timing}";
    }

    private static string State(ElementInfo e)
    {
        var parts = new List<string>
        {
            e.IsEnabled ? "enabled" : "DISABLED",
            e.IsOffscreen ? "OFFSCREEN" : "on-screen",
        };
        if (e.IsKeyboardFocusable) parts.Add("focusable");
        if (e.HasKeyboardFocus) parts.Add("has focus");
        if (e.IsPassword) parts.Add("password");
        return string.Join(" · ", parts);
    }

    private static void Row(string label, string value)
    {
        WriteColored(ConsoleColor.DarkGray, "  " + label.PadRight(LabelWidth));
        Console.WriteLine(value);
    }

    private void Write(ConsoleColor color, string message)
    {
        lock (_gate)
        {
            WriteColored(color, message);
            Console.WriteLine();
        }
    }

    private static void WriteColored(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }

    private static string Rect(Rectangle r) =>
        r.IsEmpty ? "(empty)" : $"x={r.X} y={r.Y} w={r.Width} h={r.Height}";

    private static string Quote(string name) => string.IsNullOrEmpty(name) ? "(no name)" : $"\"{Clean(name)}\"";

    private static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static string Clean(string text)
    {
        var single = text.ReplaceLineEndings(" ⏎ ").Replace('\t', ' ');
        return single.Length <= MaxTextLength ? single : single[..(MaxTextLength - 1)] + "…";
    }
}

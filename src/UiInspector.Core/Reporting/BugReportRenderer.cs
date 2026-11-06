using System.Globalization;
using System.Net;
using System.Text;
using UiInspector.Core.Recording;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Reporting;

/// <summary>A rendered Markdown report and the image files it links to (relative path → PNG).</summary>
public sealed record MarkdownReport(string Text, IReadOnlyDictionary<string, byte[]> Files);

/// <summary>
/// Renders a <see cref="BugReport"/> as a self-contained HTML page (images embedded, works
/// offline and as an e-mail attachment), as Markdown with an image folder, or as plain Markdown
/// text for pasting into a ticket. Each part appears only if its option is on.
/// </summary>
public static class BugReportRenderer
{
    private const string Placeholder = "Not filled in yet.";

    // ------------------------------------------------------------------ shared content

    private sealed record StepEntry(int Number, string? Where, string Text, byte[]? Image, RecordedStep Step);

    private static List<StepEntry> Steps(BugReport report, BugReportOptions o)
    {
        var entries = new List<StepEntry>();
        ElementInfo? lastWindow = null;
        for (var i = 0; i < report.Steps.Count; i++)
        {
            var step = report.Steps[i];
            var window = step.Path.Count > 1 ? step.Path[1] : null;
            var where = window is not null && (lastWindow is null || !window.IsSameElement(lastWindow)) ? StepWording.Window(step) : null;
            lastWindow = window ?? lastWindow;

            var image = o.Screenshots && step.Screenshot is { } shot && !o.HiddenScreenshots.Contains(step.Id)
                ? (o.HighlightElement ? shot.HighlightedPng : shot.Png)
                : null;
            entries.Add(new StepEntry(i + 1, where, StepWording.Describe(step), image, step));
        }
        return entries;
    }

    private static List<(string Label, string Value)> EnvironmentRows(EnvironmentInfo env, BugReportOptions o)
    {
        var rows = new List<(string, string)>();
        if (o.WindowsVersion) rows.Add(("Windows", env.Windows));
        if (o.AppVersion || o.AppPath)
        {
            foreach (var app in env.Apps)
            {
                var parts = new List<string>();
                if (o.AppVersion) parts.Add($"{app.ProductName ?? app.ProcessName}{(app.Version is null ? "" : " " + app.Version)}");
                if (o.AppPath && app.Path is not null) parts.Add(app.Path);
                if (parts.Count > 0) rows.Add((env.Apps.Count > 1 ? $"App ({app.ProcessName})" : "App", string.Join(", ", parts)));
            }
        }
        if (o.Screens && env.Monitors.Count > 0)
        {
            var screens = env.Monitors.Select(m => $"{m.Width}×{m.Height} at {m.ScalePercent} %{(m.IsPrimary && env.Monitors.Count > 1 ? " (primary)" : "")}");
            rows.Add((env.Monitors.Count == 1 ? "Screen" : $"Screens ({env.Monitors.Count})", string.Join("; ", screens)));
        }
        if (o.Language) rows.Add(("Language", env.Language));
        if (o.DateTime) rows.Add(("Recorded", $"{env.RecordedAt.ToString("f", CultureInfo.CurrentCulture)}, {env.TimeZone}"));
        return rows;
    }

    private static string ElementDetail(RecordedStep step)
    {
        if (step.ChosenSelector is not { } s) return $"{CodeTemplates.Describe(step.Target)}: no selector ({step.Problem ?? "still being generated"})";
        var chain = string.Join(" › ", s.Steps.Prepend(s.Window));
        return $"{CodeTemplates.Describe(step.Target)}: {chain} ({s.Strategy}; {s.Verification.Describe()})";
    }

    private static CodeSnippet? Script(BugReport report, BugReportOptions o) =>
        o.Script ? report.Scripts.FirstOrDefault(s => s.Id == o.ScriptLanguage) ?? report.Scripts.FirstOrDefault() : null;

    private static string Title(BugReport report) => string.IsNullOrWhiteSpace(report.Title) ? "Untitled bug" : report.Title.Trim();

    private static bool ShowSeverity(BugReport report, BugReportOptions o) => o.Severity && report.Severity != Severity.None;

    // ------------------------------------------------------------------ HTML

    public static string Html(BugReport report, BugReportOptions o)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="X-UA-Compatible" content="IE=edge">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            """);
        sb.Append($"<title>{H(Title(report))}</title>\n");
        sb.Append("""
            <style>
              body { font-family: "Segoe UI", Arial, sans-serif; font-size: 15px; line-height: 1.5; color: #1f2937; background: #ffffff; margin: 0; }
              main { max-width: 900px; margin: 0 auto; padding: 28px 24px 40px; }
              h1 { font-size: 26px; margin: 0 0 6px; }
              h2 { font-size: 18px; margin: 28px 0 8px; padding-bottom: 4px; border-bottom: 1px solid #e5e7eb; }
              .meta { color: #6b7280; margin: 0 0 8px; }
              .severity { display: inline-block; padding: 1px 10px; border-radius: 10px; font-weight: 600; font-size: 13px; color: #ffffff; margin-right: 8px; }
              .sev-Low { background: #2563eb; } .sev-Medium { background: #d97706; } .sev-High { background: #dc2626; } .sev-Critical { background: #7f1d1d; }
              .result { display: table; width: 100%; border-collapse: separate; border-spacing: 12px 0; margin: 0 -12px; }
              .result > div { display: table-cell; width: 50%; padding: 10px 14px; border-radius: 6px; vertical-align: top; }
              .expected { background: #ecfdf5; border: 1px solid #a7f3d0; }
              .actual { background: #fef2f2; border: 1px solid #fecaca; }
              .label { font-weight: 600; display: block; margin-bottom: 2px; }
              .empty { color: #9ca3af; font-style: italic; }
              ol.steps { padding-left: 26px; }
              ol.steps li { margin: 0 0 14px; }
              .where { color: #6b7280; font-size: 13px; }
              .shot { display: block; max-width: 100%; height: auto; margin: 6px 0 0; border: 1px solid #d1d5db; border-radius: 4px; }
              table.env { border-collapse: collapse; }
              table.env td { padding: 4px 16px 4px 0; vertical-align: top; }
              table.env td:first-child { color: #6b7280; white-space: nowrap; }
              pre { background: #f3f4f6; padding: 12px; border-radius: 6px; overflow: auto; font: 12px Consolas, monospace; }
              ul.details li { font: 12px Consolas, monospace; margin-bottom: 4px; }
              .notes { white-space: pre-wrap; }
              footer { margin-top: 36px; color: #9ca3af; font-size: 12px; }
            </style>
            </head>
            <body>
            <main>
            """);

        sb.Append($"<h1>{H(Title(report))}</h1>\n");
        var meta = new List<string>();
        if (ShowSeverity(report, o)) meta.Add($"<span class=\"severity sev-{report.Severity}\">{report.Severity}</span>");
        if (o.Steps) meta.Add($"{report.Steps.Count} step{(report.Steps.Count == 1 ? "" : "s")} to reproduce");
        if (meta.Count > 0) sb.Append($"<p class=\"meta\">{string.Join(" ", meta)}</p>\n");

        if (o.ExpectedActual)
        {
            sb.Append("<div class=\"result\">\n");
            sb.Append($"<div class=\"expected\"><span class=\"label\">Expected result</span>{Paragraph(report.Expected)}</div>\n");
            sb.Append($"<div class=\"actual\"><span class=\"label\">Actual result</span>{Paragraph(report.Actual)}</div>\n");
            sb.Append("</div>\n");
        }

        if (o.Steps)
        {
            sb.Append("<h2>Steps to reproduce</h2>\n");
            var steps = Steps(report, o);
            if (steps.Count == 0) sb.Append("<p class=\"empty\">No steps recorded yet. Record them in the Recorder tab.</p>\n");
            else
            {
                sb.Append("<ol class=\"steps\">\n");
                foreach (var step in steps)
                {
                    sb.Append("<li>");
                    if (step.Where is not null) sb.Append($"<div class=\"where\">{H(step.Where)}</div>");
                    sb.Append(H(step.Text));
                    if (step.Image is { } png)
                    {
                        sb.Append($"<img class=\"shot\" alt=\"Screenshot of step {step.Number}\" src=\"data:image/png;base64,{Convert.ToBase64String(png)}\">");
                    }
                    sb.Append("</li>\n");
                }
                sb.Append("</ol>\n");
            }
        }

        if (o.Notes && !string.IsNullOrWhiteSpace(report.Notes))
        {
            sb.Append($"<h2>Notes</h2>\n<p class=\"notes\">{H(report.Notes.Trim())}</p>\n");
        }

        if (o.Environment && report.Environment is { } env && EnvironmentRows(env, o) is { Count: > 0 } rows)
        {
            sb.Append("<h2>Environment</h2>\n<table class=\"env\">\n");
            foreach (var (label, value) in rows) sb.Append($"<tr><td>{H(label)}</td><td>{H(value)}</td></tr>\n");
            sb.Append("</table>\n");
        }

        if (o.ElementDetails && report.Steps.Count > 0)
        {
            sb.Append("<h2>Element details</h2>\n<ul class=\"details\">\n");
            for (var i = 0; i < report.Steps.Count; i++) sb.Append($"<li>Step {i + 1}: {H(ElementDetail(report.Steps[i]))}</li>\n");
            sb.Append("</ul>\n");
        }

        if (Script(report, o) is { } script && report.Steps.Count > 0)
        {
            sb.Append($"<h2>Automation script ({H(script.Title)})</h2>\n<pre><code>{H(script.Code)}</code></pre>\n");
        }

        sb.Append("<footer>Created with UiInspector</footer>\n</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string Paragraph(string text) =>
        string.IsNullOrWhiteSpace(text) ? $"<span class=\"empty\">{Placeholder}</span>" : $"<span class=\"notes\">{H(text.Trim())}</span>";

    // Only the characters HTML needs: the file is UTF-8, so “quotes”, ×, · and accents stay readable.
    private static string H(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    // ------------------------------------------------------------------ Markdown

    /// <param name="imageFolder">Folder (relative to the .md file) the screenshots are saved in.</param>
    public static MarkdownReport Markdown(BugReport report, BugReportOptions o, string imageFolder = "report_files") =>
        RenderMarkdown(report, o, imageFolder);

    /// <summary>Markdown without images, for pasting into Jira, Azure DevOps or GitHub (attach the screenshots there).</summary>
    public static string Text(BugReport report, BugReportOptions o) => RenderMarkdown(report, o, null).Text;

    private static MarkdownReport RenderMarkdown(BugReport report, BugReportOptions o, string? imageFolder)
    {
        var sb = new StringBuilder();
        var files = new Dictionary<string, byte[]>();
        sb.AppendLine($"# {Title(report)}");
        sb.AppendLine();
        if (ShowSeverity(report, o))
        {
            sb.AppendLine($"**Severity:** {report.Severity}");
            sb.AppendLine();
        }

        if (o.ExpectedActual)
        {
            sb.AppendLine("**Expected result:**");
            sb.AppendLine(string.IsNullOrWhiteSpace(report.Expected) ? $"_{Placeholder}_" : report.Expected.Trim());
            sb.AppendLine();
            sb.AppendLine("**Actual result:**");
            sb.AppendLine(string.IsNullOrWhiteSpace(report.Actual) ? $"_{Placeholder}_" : report.Actual.Trim());
            sb.AppendLine();
        }

        if (o.Steps)
        {
            sb.AppendLine("## Steps to reproduce");
            sb.AppendLine();
            var steps = Steps(report, o);
            if (steps.Count == 0) sb.AppendLine("_No steps recorded yet._");
            var screenshots = 0;
            foreach (var step in steps)
            {
                if (step.Where is not null) sb.AppendLine($"_{step.Where}_").AppendLine();
                sb.AppendLine($"{step.Number}. {step.Text}");
                if (step.Image is { } png)
                {
                    screenshots++;
                    if (imageFolder is not null)
                    {
                        var file = $"{imageFolder}/step-{step.Number:00}.png";
                        files[file] = png;
                        sb.AppendLine().AppendLine($"   ![Step {step.Number}]({file})").AppendLine();
                    }
                }
            }
            if (imageFolder is null && screenshots > 0)
            {
                sb.AppendLine().AppendLine($"_{screenshots} screenshot{(screenshots == 1 ? "" : "s")} in the saved report._");
            }
            sb.AppendLine();
        }

        if (o.Notes && !string.IsNullOrWhiteSpace(report.Notes))
        {
            sb.AppendLine("## Notes").AppendLine().AppendLine(report.Notes.Trim()).AppendLine();
        }

        if (o.Environment && report.Environment is { } env && EnvironmentRows(env, o) is { Count: > 0 } rows)
        {
            sb.AppendLine("## Environment").AppendLine();
            foreach (var (label, value) in rows) sb.AppendLine($"- **{label}:** {value}");
            sb.AppendLine();
        }

        if (o.ElementDetails && report.Steps.Count > 0)
        {
            sb.AppendLine("## Element details").AppendLine();
            for (var i = 0; i < report.Steps.Count; i++) sb.AppendLine($"- Step {i + 1}: `{ElementDetail(report.Steps[i]).Replace("`", "'")}`");
            sb.AppendLine();
        }

        if (Script(report, o) is { } script && report.Steps.Count > 0)
        {
            var fence = script.Id == "flaui" ? "csharp" : "python";
            sb.AppendLine($"## Automation script ({script.Title})").AppendLine();
            sb.AppendLine($"```{fence}").Append(script.Code.TrimEnd()).AppendLine().AppendLine("```").AppendLine();
        }

        sb.AppendLine("_Created with UiInspector_");
        return new MarkdownReport(sb.ToString(), files);
    }
}

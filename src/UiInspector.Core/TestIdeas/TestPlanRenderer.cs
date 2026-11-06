using System.Text;
using UiInspector.Core.Reporting;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.TestIdeas;

/// <summary>
/// The test plan as one HTML page (to read and share), CSV (for Excel and test management tools
/// such as TestRail, Xray or Zephyr) and Markdown (for a wiki or a ticket).
/// </summary>
public static class TestPlanRenderer
{
    private static string H(string text) => BugReportRenderer.H(text);

    private static string Title(TestPlan plan) =>
        $"Test plan: {(string.IsNullOrWhiteSpace(plan.Window.Name) ? plan.Window.ProcessName : CodeTemplates.OneLine(plan.Window.Name.Trim()))}";

    private const string Intro =
        "Suggested from the structure of the page: review it, drop what doesn't apply, and check the expected results against the app's rules (minimum lengths, exact messages).";

    public static string CategoryText(TestCategory c) => c switch
    {
        TestCategory.Positive => "Works",
        TestCategory.Negative => "Bad input",
        TestCategory.Boundary => "Limits",
        TestCategory.Security => "Security",
        TestCategory.Accessibility => "Accessibility",
        _ => "Usability",
    };

    /// <summary>The script shown with each feature: Playwright (Python) for web pages, FlaUI (C#) for desktop apps.</summary>
    public static CodeSnippet? MainScript(TestFeature f, bool isWeb) =>
        (isWeb ? f.Scripts.FirstOrDefault(s => s.Id == "playwright-py") : null) ?? f.Scripts.FirstOrDefault(s => s.Id == "flaui") ?? f.Scripts.FirstOrDefault();

    // ------------------------------------------------------------------ HTML

    public static string Html(TestPlan plan)
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
        sb.Append($"<title>{H(Title(plan))}</title>\n");
        sb.Append("""
            <style>
              body { font-family: "Segoe UI", Arial, sans-serif; font-size: 15px; line-height: 1.5; color: #1f2937; background: #ffffff; margin: 0; }
              main { max-width: 1100px; margin: 0 auto; padding: 28px 24px 40px; }
              h1 { font-size: 26px; margin: 0 0 6px; }
              h2 { font-size: 19px; margin: 34px 0 4px; padding-bottom: 4px; border-bottom: 1px solid #e5e7eb; }
              .meta, .where, .note { color: #6b7280; }
              .note { font-size: 13px; }
              ul.toc li { margin-bottom: 2px; }
              table.cases { border-collapse: collapse; width: 100%; font-size: 14px; margin-top: 8px; }
              table.cases th { text-align: left; background: #f3f4f6; padding: 6px 8px; border-bottom: 1px solid #d1d5db; }
              table.cases td { padding: 6px 8px; border-bottom: 1px solid #e5e7eb; vertical-align: top; }
              table.cases td.id { white-space: nowrap; color: #6b7280; font-size: 12px; }
              table.cases ol { margin: 0; padding-left: 18px; }
              code { font: 12px Consolas, monospace; background: #f3f4f6; padding: 1px 4px; border-radius: 3px; word-break: break-all; }
              .cat { display: inline-block; padding: 0 7px; border-radius: 9px; font-size: 12px; white-space: nowrap; }
              .Positive { background: #dcfce7; color: #166534; } .Negative { background: #fee2e2; color: #991b1b; }
              .Boundary { background: #fef3c7; color: #92400e; } .Security { background: #ede9fe; color: #5b21b6; }
              .Accessibility { background: #dbeafe; color: #1e40af; } .Usability { background: #f3f4f6; color: #374151; }
              .prio-High { font-weight: 600; } .prio-Low { color: #6b7280; }
              .warning { background: #fef3c7; border: 1px solid #f59e0b; border-radius: 4px; padding: 6px 10px; margin: 8px 0; font-size: 14px; }
              details { margin-top: 10px; } summary { cursor: pointer; color: #1d4ed8; }
              pre { background: #f3f4f6; padding: 12px; border-radius: 6px; overflow: auto; font: 12px Consolas, monospace; }
              .risk { margin-top: 6px; padding: 4px 8px; background: #fef2f2; border-left: 3px solid #dc2626; font-size: 13px; }
              footer { margin-top: 36px; color: #9ca3af; font-size: 12px; }
            </style>
            </head>
            <body>
            <main>
            """);
        sb.Append($"<h1>{H(Title(plan))}</h1>\n");
        sb.Append($"<p class=\"meta\">{plan.Features.Count} features · {plan.CaseCount} test cases · {plan.Features.Sum(f => f.Cases.Count(c => c.Priority == TestPriority.High))} high priority · {plan.CreatedAt:g}</p>\n");
        sb.Append($"<p class=\"note\">{H(Intro)}</p>\n");

        sb.Append("<ul class=\"toc\">\n");
        for (var i = 0; i < plan.Features.Count; i++)
        {
            var f = plan.Features[i];
            sb.Append($"<li><a href=\"#f{i + 1}\">{H(f.Title)}</a> <span class=\"note\">{f.Cases.Count} cases{(f.Where.Length > 0 ? $", {H(f.Where)}" : "")}</span></li>\n");
        }
        sb.Append("</ul>\n");

        for (var i = 0; i < plan.Features.Count; i++)
        {
            var f = plan.Features[i];
            sb.Append($"<h2 id=\"f{i + 1}\">{H(f.Title)}</h2>\n");
            if (f.Where.Length > 0) sb.Append($"<div class=\"where\">{H(char.ToUpperInvariant(f.Where[0]) + f.Where[1..])}</div>\n");
            foreach (var warning in f.Warnings) sb.Append($"<div class=\"warning\">{H(warning)}</div>\n");
            sb.Append("<table class=\"cases\"><tr><th>ID</th><th>Test</th><th>Kind</th><th>Priority</th><th>Steps</th><th>Data</th><th>Expected</th></tr>\n");
            foreach (var c in f.Cases)
            {
                sb.Append($"<tr><td class=\"id\">{H(c.Id)}</td><td>{H(c.Title)}</td><td><span class=\"cat {c.Category}\">{H(CategoryText(c.Category))}</span></td>");
                sb.Append($"<td class=\"prio-{c.Priority}\">{c.Priority}</td><td><ol>{string.Concat(c.Steps.Select(s => $"<li>{H(s)}</li>"))}</ol></td>");
                sb.Append($"<td>{(c.Data is null ? "" : $"<code>{H(c.Data)}</code>")}</td><td>{H(c.Expected)}{(c.IfItFails is null ? "" : $"<div class=\"risk\"><b>If it fails:</b> {H(c.IfItFails)}</div>")}</td></tr>\n");
            }
            sb.Append("</table>\n");
            if (MainScript(f, plan.IsWeb) is { } script)
            {
                sb.Append($"<details><summary>Script for the main flow ({H(script.Title)}); every language is in the app</summary><pre><code>{H(script.Code)}</code></pre></details>\n");
            }
        }

        sb.Append("<footer>Created with UiInspector, from the page's structure; nothing in the app was clicked.</footer>\n</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ CSV

    /// <summary>
    /// One row per test case, with a header row, UTF-8 with a BOM (so Excel shows accents) and the
    /// steps numbered on separate lines inside one cell, as test management tools import them.
    /// </summary>
    public static string Csv(TestPlan plan)
    {
        var sb = new StringBuilder("\uFEFF");
        sb.Append("ID,Feature,Title,Kind,Priority,Steps,Test data,Exact value,Expected result,If it fails,Automate\r\n");
        foreach (var f in plan.Features)
        {
            foreach (var c in f.Cases)
            {
                var steps = string.Join("\n", c.Steps.Select((s, i) => $"{i + 1}. {s}"));
                string[] cells = [c.Id, f.Title, c.Title, CategoryText(c.Category), c.Priority.ToString(), steps, c.Data ?? "", c.Value ?? "", c.Expected, c.IfItFails ?? "", c.Automatable ? "yes" : "no"];
                sb.Append(string.Join(",", cells.Select(Cell))).Append("\r\n");
            }
        }
        return sb.ToString();
    }

    // Quoted when it holds a comma, quote or line break; formulas are defused, since a cell starting
    // with = + - @ runs as a formula in Excel (test data like "-1" or "=1+1" would).
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    // ------------------------------------------------------------------ Markdown

    public static string Markdown(TestPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Title(plan)}");
        sb.AppendLine();
        sb.AppendLine($"{plan.Features.Count} features, {plan.CaseCount} test cases. {Intro}");
        foreach (var f in plan.Features)
        {
            sb.AppendLine();
            sb.AppendLine($"## {f.Title}{(f.Where.Length > 0 ? $" ({f.Where})" : "")}");
            foreach (var warning in f.Warnings) sb.AppendLine($"> ⚠ {warning}");
            sb.AppendLine();
            sb.AppendLine("| ID | Test | Kind | Priority | Steps | Data | Expected |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
            foreach (var c in f.Cases)
            {
                var steps = string.Join("<br>", c.Steps.Select((s, i) => $"{i + 1}. {Md(s)}"));
                sb.AppendLine($"| {c.Id} | {Md(c.Title)} | {CategoryText(c.Category)} | {c.Priority} | {steps} | {(c.Data is null ? "" : $"`{c.Data.Replace("`", "'").Replace("|", "\\|")}`")} | {Md(c.Expected)}{(c.IfItFails is null ? "" : $"<br>**If it fails:** {Md(c.IfItFails)}")} |");
            }
        }
        return sb.ToString();
    }

    private static string Md(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}

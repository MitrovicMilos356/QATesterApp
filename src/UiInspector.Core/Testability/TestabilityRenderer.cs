using System.Text;
using UiInspector.Core.Reporting;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Testability;

/// <summary>
/// The testability report for developers: a score, what to fix first, the window with every
/// control numbered, and per control how tests find it, what's fragile and the fix.
/// HTML is one self-contained file; the text form is Markdown for a ticket.
/// </summary>
public static class TestabilityRenderer
{
    private static readonly (TestabilityProblem Problem, string One, string Many)[] Summaries =
    [
        (TestabilityProblem.Position, "control is only found by its position", "controls are only found by their position"),
        (TestabilityProblem.NotFound, "control can't be found reliably at all", "controls can't be found reliably at all"),
        (TestabilityProblem.ChangingId, "control has an id that changes between runs", "controls have an id that changes between runs"),
        (TestabilityProblem.DuplicateId, "control shares its id with other controls", "controls share their id with other controls"),
        (TestabilityProblem.NearbyText, "control is only found by text near it (row data, headings)", "controls are only found by text near them (row data, headings)"),
        (TestabilityProblem.OwnText, "control is found by its text, which changes with the wording and language", "controls are found by their text, which changes with the wording and language"),
        (TestabilityProblem.TypeOnly, "control is found only by its type", "controls are found only by their type"),
        (TestabilityProblem.MissingName, "control has no accessible name (screen readers can't announce it either)", "controls have no accessible name (screen readers can't announce them either)"),
    ];

    private static string H(string text) => BugReportRenderer.H(text);

    private static string Title(TestabilityReport r) =>
        $"Testability: {(string.IsNullOrWhiteSpace(r.Window.Name) ? r.Window.ProcessName : CodeTemplates.OneLine(r.Window.Name.Trim()))}";

    private static string Element(TestabilityFinding f)
    {
        var text = StepWording.Element(f.Element with { Name = TestabilityAnalyzer.Visible(f.Element.Name) });
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string GradeText(TestabilityGrade grade) => grade switch
    {
        TestabilityGrade.Good => "Good",
        TestabilityGrade.Fair => "Works, with caveats",
        _ => "Fragile",
    };

    private const int TopFixCount = 3;

    /// <summary>
    /// "In short": the few changes that help most, in plain words. The most fragile and largest
    /// groups first: "25 links like “C++ Developer”… (in the main content): only found by their
    /// position. Give each an id."
    /// </summary>
    public static IReadOnlyList<string> TopFixes(TestabilityReport r) => ToImprove(r)
        .Where(g => g.Grade != TestabilityGrade.Good || g.First.MissingName)
        .Take(TopFixCount)
        .Select(g =>
        {
            var many = g.Count > 1;
            var p = g.First.Problems;
            var why = p.HasFlag(TestabilityProblem.NotFound) ? (many ? "they can't be found reliably" : "it can't be found reliably")
                : p.HasFlag(TestabilityProblem.Position) ? (many ? "only their position tells them apart" : "only its position tells it apart")
                : p.HasFlag(TestabilityProblem.ChangingId) ? (many ? "their ids change between runs" : "its id changes between runs")
                : p.HasFlag(TestabilityProblem.DuplicateId) ? (many ? "they share their ids" : "it shares its id with other controls")
                : p.HasFlag(TestabilityProblem.NearbyText) ? (many ? "they're only found by text near them" : "it's only found by text near it")
                : p.HasFlag(TestabilityProblem.TypeOnly) ? (many ? "they're found only by their type" : "it's found only by its type")
                : p.HasFlag(TestabilityProblem.OwnText) ? (many ? "they're found by their text, which changes with the language" : "it's found by its text, which changes with the language")
                : p.HasFlag(TestabilityProblem.MissingName) ? (many ? "they have no accessible name" : "it has no accessible name")
                : "";
            var fix = g.First.NeedsId
                ? (many ? "Give each of them its own id." : "Give it an id.")
                : (many ? "Give them names that say what they do." : "Give it a name that says what it does.");
            return $"{g.Title}{(g.Where.Length > 0 ? $" ({g.Where})" : "")}: {why}. {fix}";
        })
        .ToList();

    /// <summary>The lines of "What to fix first": how many controls have each problem.</summary>
    public static IReadOnlyList<string> Summary(TestabilityReport r)
    {
        var lines = new List<string>();
        var needId = r.Findings.Count(f => f.NeedsId);
        if (needId > 0) lines.Add($"{needId} of {r.Findings.Count} controls need a stable, unique id.");
        foreach (var (problem, one, many) in Summaries)
        {
            var count = r.Findings.Count(f => f.Problems.HasFlag(problem));
            if (count > 0) lines.Add($"{count} {(count == 1 ? one : many)}.");
        }
        return lines;
    }

    private static string Meta(TestabilityReport r)
    {
        var parts = new List<string>();
        if (r.Window.ProcessName.Length > 0) parts.Add(r.Window.ProcessName);
        if (r.Frameworks.Count > 0) parts.Add(string.Join(", ", r.Frameworks.Select(FrameworkName)));
        parts.Add($"{r.Findings.Count} controls graded ({r.ElementsScanned} elements read)");
        parts.Add(r.CreatedAt.ToString("g"));
        return string.Join(" · ", parts);
    }

    internal static string FrameworkName(string id) => id switch
    {
        "WinForm" => "WinForms",
        "Chrome" => "Chromium (web)",
        "Gecko" => "Firefox (web)",
        "XAML" or "DirectUI" => "XAML (UWP / WinUI)",
        _ => id,
    };

    // ------------------------------------------------------------------ HTML

    public static string Html(TestabilityReport r)
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
        sb.Append($"<title>{H(Title(r))}</title>\n");
        sb.Append("""
            <style>
              body { font-family: "Segoe UI", Arial, sans-serif; font-size: 15px; line-height: 1.5; color: #1f2937; background: #ffffff; margin: 0; }
              main { max-width: 960px; margin: 0 auto; padding: 28px 24px 40px; }
              h1 { font-size: 26px; margin: 0 0 6px; }
              h2 { font-size: 18px; margin: 28px 0 8px; padding-bottom: 4px; border-bottom: 1px solid #e5e7eb; }
              .meta { color: #6b7280; margin: 0 0 16px; }
              .score { display: table; border-spacing: 0; margin: 8px 0 4px; }
              .score > div { display: table-cell; vertical-align: middle; padding-right: 22px; }
              .big { font-size: 44px; font-weight: 700; line-height: 1; }
              .big small { font-size: 18px; color: #6b7280; font-weight: 400; }
              .bar { width: 420px; max-width: 100%; height: 14px; border-radius: 7px; overflow: hidden; background: #e5e7eb; font-size: 0; }
              .bar span { display: inline-block; height: 14px; }
              .counts { font-size: 14px; color: #374151; margin-top: 6px; }
              .dot { display: inline-block; width: 10px; height: 10px; border-radius: 5px; margin: 0 4px 0 10px; }
              .Good { background: #15803d; } .Fair { background: #d97706; } .Fragile { background: #dc2626; }
              ul.fix-first li { margin-bottom: 4px; }
              .short { background: #eff6ff; border: 1px solid #bfdbfe; border-radius: 6px; padding: 10px 14px; margin: 14px 0; }
              .short ol { margin: 6px 0 0; padding-left: 22px; } .short li { margin-bottom: 4px; }
              .shot { display: block; max-width: 100%; height: auto; border: 1px solid #d1d5db; border-radius: 4px; }
              .finding { border: 1px solid #e5e7eb; border-left-width: 5px; border-radius: 6px; padding: 10px 14px; margin: 0 0 12px; }
              .finding.g-Good { border-left-color: #15803d; } .finding.g-Fair { border-left-color: #d97706; } .finding.g-Fragile { border-left-color: #dc2626; }
              .finding h3 { font-size: 15px; margin: 0 0 2px; }
              .num { display: inline-block; min-width: 22px; padding: 0 5px; border-radius: 4px; color: #ffffff; font-size: 12px; text-align: center; margin-right: 6px; }
              .pill { font-size: 12px; font-weight: 600; margin-left: 8px; }
              .pill.Good { color: #15803d; background: none; } .pill.Fair { color: #b45309; background: none; } .pill.Fragile { color: #b91c1c; background: none; }
              .found { color: #6b7280; font-size: 13px; }
              .finding ul { margin: 6px 0; padding-left: 20px; }
              .fix { background: #eff6ff; border: 1px solid #bfdbfe; border-radius: 4px; padding: 6px 10px; font-size: 14px; }
              .fix b { color: #1d4ed8; }
              .similar { color: #6b7280; font-size: 13px; }
              .good-list { color: #374151; }
              .note { color: #6b7280; font-size: 13px; }
              footer { margin-top: 36px; color: #9ca3af; font-size: 12px; }
            </style>
            </head>
            <body>
            <main>
            """);

        sb.Append($"<h1>{H(Title(r))}</h1>\n<p class=\"meta\">{H(Meta(r))}</p>\n");

        var total = Math.Max(1, r.Findings.Count);
        sb.Append("<div class=\"score\">");
        sb.Append($"<div class=\"big\">{r.Score}<small> / 100</small></div>");
        sb.Append("<div><div class=\"bar\">");
        foreach (var grade in Enum.GetValues<TestabilityGrade>())
        {
            var share = 100.0 * r.Count(grade) / total;
            if (share > 0) sb.Append($"<span class=\"{grade}\" style=\"width:{share.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%\"></span>");
        }
        sb.Append("</div><div class=\"counts\">");
        foreach (var grade in Enum.GetValues<TestabilityGrade>())
        {
            sb.Append($"<span class=\"dot {grade}\"></span>{r.Count(grade)} {H(GradeText(grade).ToLowerInvariant())}");
        }
        sb.Append("</div></div></div>\n");
        sb.Append("<p class=\"note\">Good: a stable id finds it, whatever the language, data or layout. Works, with caveats: found by its text or by text near it, which can change. Fragile: only its position tells it apart, or nothing finds it.</p>\n");
        if (r.Truncated) sb.Append("<p class=\"note\">This window has more controls than are analysed at once; the rest weren't graded.</p>\n");

        var top = TopFixes(r);
        if (top.Count > 0)
        {
            sb.Append("<div class=\"short\"><b>In short: the changes that help most</b><ol>\n");
            foreach (var line in top) sb.Append($"<li>{H(line)}</li>\n");
            sb.Append("</ol></div>\n");
        }

        var summary = Summary(r);
        if (summary.Count > 0)
        {
            sb.Append("<h2>All problems, counted</h2>\n<ul class=\"fix-first\">\n");
            foreach (var line in summary) sb.Append($"<li>{H(line)}</li>\n");
            sb.Append("</ul>\n");
        }

        if (r.Screenshot is { } shot)
        {
            sb.Append("<h2>The window</h2>\n");
            sb.Append($"<img class=\"shot\" alt=\"The window with every graded control numbered\" src=\"data:image/png;base64,{Convert.ToBase64String(shot.HighlightedPng)}\">\n");
        }

        var toFix = ToImprove(r);
        if (toFix.Count > 0)
        {
            sb.Append($"<h2>Controls to improve ({toFix.Sum(g => g.Count)}, in {toFix.Count} group{(toFix.Count == 1 ? "" : "s")})</h2>\n");
            sb.Append("<p class=\"note\">Controls built the same way with the same problem are one entry with one fix. The numbers match the screenshot.</p>\n");
            foreach (var g in toFix) AppendGroup(sb, g);
        }

        var good = r.Findings.Where(f => f.Grade == TestabilityGrade.Good && f.Issues.Count == 0).ToList();
        if (good.Count > 0)
        {
            sb.Append($"<h2>Already easy to automate ({good.Count})</h2>\n<p class=\"good-list\">");
            sb.Append(string.Join(", ", good.Select(f => $"<span class=\"num Good\">{f.Number}</span>{H(Element(f))}")));
            sb.Append("</p>\n");
        }

        sb.Append("<footer>Created with UiInspector. The grades come from the selectors it builds for each control; nothing in the app was clicked.</footer>\n</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>The groups that need work: everything but the controls that are already good without remarks.</summary>
    public static IReadOnlyList<TestabilityGroup> ToImprove(TestabilityReport r) =>
        r.Groups.Where(g => g.Grade != TestabilityGrade.Good || g.First.Issues.Count > 0).ToList();

    private const int NumbersShown = 30;

    /// <summary>"Numbers 4, 5, 6" (as on the screenshot), shortened for big groups.</summary>
    public static string Numbers(TestabilityGroup g)
    {
        var numbers = g.Findings.Select(f => f.Number).ToList();
        var shown = string.Join(", ", numbers.Take(NumbersShown));
        if (numbers.Count > NumbersShown) shown += $" and {numbers.Count - NumbersShown} more";
        var unlisted = g.Findings.Sum(f => f.SimilarNotListed);
        if (unlisted > 0) shown += $" (plus {unlisted} look-alike{(unlisted == 1 ? "" : "s")} next to them, not numbered)";
        return (numbers.Count == 1 ? "Number " : "Numbers ") + shown;
    }

    private static void AppendGroup(StringBuilder sb, TestabilityGroup g)
    {
        var f = g.First;
        var many = g.Count > 1;
        sb.Append($"<div class=\"finding g-{g.Grade}\">");
        sb.Append($"<h3>{(many ? "" : $"<span class=\"num {g.Grade}\">{f.Number}</span>")}{H(g.Title)}<span class=\"pill {g.Grade}\">{H(GradeText(g.Grade))}</span></h3>");
        var where = g.Where.Length > 0 ? g.Where : (!many ? f.Where : "");
        if (where.Length > 0) sb.Append($"<div class=\"found\">{H(char.ToUpperInvariant(where[0]) + where[1..])}</div>");
        sb.Append($"<div class=\"found\">{H(f.FoundBy is null ? "No selector finds it." : $"Found now by: {f.FoundBy}{(many ? " (the first of them)" : "")}")}</div>");
        if (f.Issues.Count > 0)
        {
            sb.Append("<ul>");
            foreach (var issue in f.Issues) sb.Append($"<li>{H(issue)}</li>");
            sb.Append("</ul>");
        }
        if (f.Fix is { } fix) sb.Append($"<div class=\"fix\"><b>Fix{(many ? " (for each of them)" : "")}:</b> {H(fix)}</div>");
        if (many) sb.Append($"<div class=\"similar\">{H(Numbers(g))}</div>");
        sb.Append("</div>\n");
    }

    // ------------------------------------------------------------------ Markdown

    /// <summary>Markdown without the screenshot, for a ticket or a chat.</summary>
    public static string Text(TestabilityReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Title(r)}");
        sb.AppendLine();
        sb.AppendLine($"{Meta(r)}");
        sb.AppendLine();
        sb.AppendLine($"**Score: {r.Score}/100** — {r.Count(TestabilityGrade.Good)} good, {r.Count(TestabilityGrade.Fair)} with caveats, {r.Count(TestabilityGrade.Fragile)} fragile.");
        var top = TopFixes(r);
        if (top.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**In short, the changes that help most:**");
            sb.AppendLine();
            for (var i = 0; i < top.Count; i++) sb.AppendLine($"{i + 1}. {top[i]}");
        }
        var summary = Summary(r);
        if (summary.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## All problems, counted");
            sb.AppendLine();
            foreach (var line in summary) sb.AppendLine($"- {line}");
        }

        var toFix = ToImprove(r);
        if (toFix.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Controls to improve");
            foreach (var g in toFix)
            {
                var f = g.First;
                var many = g.Count > 1;
                sb.AppendLine();
                sb.AppendLine($"**{(many ? "" : $"{f.Number}. ")}{g.Title}** — {GradeText(g.Grade)}{(g.Where.Length > 0 ? $", {g.Where}" : "")}");
                foreach (var issue in f.Issues) sb.AppendLine($"- {issue}");
                if (f.Fix is { } fix) sb.AppendLine($"- **Fix{(many ? " (for each)" : "")}:** {fix}");
                if (many) sb.AppendLine($"- {Numbers(g)}");
            }
        }
        return sb.ToString();
    }
}

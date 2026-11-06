using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UiInspector.Core;
using UiInspector.Core.Reporting;
using UiInspector.Core.Selectors;
using UiInspector.Core.Testability;
using UiInspector.Core.TestIdeas;

namespace UiInspector.App;

/// <summary>One row in the list of groups: controls with the same problem, built the same way.</summary>
public sealed record GroupRow(TestabilityGroup Group, string Title, string Detail, string Tone)
{
    public override string ToString() => Title;
}

/// <summary>One control of the selected group.</summary>
public sealed record FindingRow(TestabilityFinding Finding, string Title, string Detail)
{
    public override string ToString() => Title;
}

/// <summary>One feature in the test plan's list.</summary>
public sealed record FeatureRow(TestFeature Feature, string Title, string Detail)
{
    public override string ToString() => Title;
}

/// <summary>One test case in the table; the steps, the exact value and what a failure means show when the row is selected.</summary>
public sealed record CaseRow(TestCase Case, string Id, string Title, string Kind, string Priority, string Data, string Expected, string StepsText)
{
    /// <summary>What Copy value copies: the exact text, or the description when the tester chooses it.</summary>
    public string CopyText => Case.Value ?? Case.Data ?? "";

    public bool HasData => Case.Data is not null;

    /// <summary>"exact text" or "describes what to type", above the value box.</summary>
    public string DataHint => Case.Value is null
        ? "Describes what to type (use your app's own value):"
        : Case.Value.Length > 200 ? $"Exact text to type ({Case.Value.Length:N0} characters):" : "Exact text to type:";

    /// <summary>The value as shown in the box: long ones are cut (Copy value copies all of it).</summary>
    public string ShownValue => CopyText.Length > 300 ? CopyText[..300] + "…" : CopyText;

    public string IfItFails => Case.IfItFails ?? "";

    public bool HasRisk => Case.IfItFails is not null;

    public override string ToString() => $"{Id} {Title}";
}

/// <summary>
/// The Testability tab: grades every control of a window for test automation and shows the
/// report developers get (score, what to fix first, each control with its fix).
/// </summary>
public sealed partial class TestabilityViewModel : ObservableObject, IDisposable
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    private readonly TestabilityAnalyzer _analyzer;
    private readonly InspectionService _service;
    private readonly IHighlighter _highlighter;
    private readonly Func<ElementInfo?> _selectedElement;
    private readonly DispatcherTimer _flashTimer;
    private readonly string _previewFolder = Path.Combine(Path.GetTempPath(), $"UiInspector-testability-{Environment.ProcessId}");
    private TestabilityReport? _report;
    private int _previewNumber;

    public TestabilityViewModel(TestabilityAnalyzer analyzer, InspectionService service, IHighlighter highlighter, Dispatcher dispatcher, Func<ElementInfo?> selectedElement)
    {
        _analyzer = analyzer;
        _service = service;
        _highlighter = highlighter;
        _selectedElement = selectedElement;
        _flashTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Normal, (_, _) => EndFlash(), dispatcher) { IsEnabled = false };
    }

    public ObservableCollection<GroupRow> Groups { get; } = [];

    /// <summary>The controls of the selected group.</summary>
    public ObservableCollection<FindingRow> Members { get; } = [];

    public IReadOnlyList<string> Filters { get; } = ["Controls to improve", "Fragile only", "All controls"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeUnderCursorCommand), nameof(AnalyzeSelectedCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Grades every control of a window for test automation, and says what the developers can change. Nothing in the app is clicked.";

    [ObservableProperty]
    private string _analyzeButtonText = "Analyze window under cursor (3 s)";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveHtmlCommand), nameof(CopyTextCommand), nameof(SavePlanHtmlCommand), nameof(SavePlanCsvCommand), nameof(CopyPlanMarkdownCommand))]
    private bool _hasReport;

    [ObservableProperty]
    private string _scoreText = "";

    [ObservableProperty]
    private string _countsText = "";

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private string _selectedFilter = "Controls to improve";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMembers))]
    private GroupRow? _selectedGroup;

    [ObservableProperty]
    private FindingRow? _selectedMember;

    [ObservableProperty]
    private string _membersHeader = "";

    public bool HasMembers => SelectedGroup is not null;

    // ---------------------------------------------------------------- test plan

    public ObservableCollection<FeatureRow> Features { get; } = [];

    public ObservableCollection<CaseRow> Cases { get; } = [];

    public ObservableCollection<CodeSnippet> Scripts { get; } = [];

    public IReadOnlyList<string> CaseFilters { get; } =
    [
        "All cases", "High priority", "Worth automating", "Works (happy path)", "Bad input", "Limits", "Security", "Accessibility", "Usability",
    ];

    [ObservableProperty] private string _planSummary = "Analyze a window or page to get its test plan: the features found, with test cases for each.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeature))]
    private FeatureRow? _selectedFeature;

    public bool HasFeature => SelectedFeature is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyValueCommand), nameof(CopyStepsCommand), nameof(CopyCaseCommand))]
    private CaseRow? _selectedCase;

    [ObservableProperty] private string _featureHeader = "";
    [ObservableProperty] private string _featureWarnings = "";
    [ObservableProperty] private string _selectedCaseFilter = "All cases";
    [ObservableProperty] private string _caseCountText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScriptText))]
    [NotifyCanExecuteChangedFor(nameof(CopyPlanScriptCommand), nameof(SavePlanScriptCommand))]
    private CodeSnippet? _selectedScript;

    public string ScriptText => SelectedScript?.Code ?? (SelectedFeature is null ? "" : "No script for this one: it's checked by hand (see the test cases).");

    /// <summary>A file name for the script: test suites follow their runner's naming (login_test.py, login.spec.js).</summary>
    private string ScriptFileName(CodeSnippet script)
    {
        var slug = string.Join('_', new string((SelectedFeature?.Feature.Title ?? "feature").ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4));
        return script.Id switch
        {
            "suite-playwright-py" or "suite-uiautomation" => $"{slug}_test.py",
            "suite-playwright-js" => $"{slug}.spec.js",
            "suite-flaui" => $"{string.Concat(slug.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..]))}Suite.cs",
            "flaui" or "playwright-cs" => "MainFlow.cs",
            "playwright-js" => "main_flow.js",
            "selenium-java" => "MainFlow.java",
            _ => "main_flow.py",
        };
    }

    private string? _preferredScriptId;

    private void FillPlan(TestPlan plan)
    {
        Features.Clear();
        foreach (var f in plan.Features)
        {
            var high = f.Cases.Count(c => c.Priority == TestPriority.High);
            var detail = $"{f.Cases.Count} test cases, {high} high priority{(f.Where.Length > 0 ? $" · {f.Where}" : "")}";
            Features.Add(new FeatureRow(f, f.Title, detail));
        }
        PlanSummary = $"{plan.Features.Count} features with {plan.CaseCount} test cases, most important first. They're suggestions from the page's structure: drop what doesn't apply, and check expected results against the app's rules.";
        _preferredScriptId ??= plan.IsWeb ? "suite-playwright-py" : "suite-uiautomation";
        SelectedFeature = Features.FirstOrDefault();
    }

    partial void OnSelectedFeatureChanged(FeatureRow? value)
    {
        Cases.Clear();
        Scripts.Clear();
        if (value is null)
        {
            SelectedScript = null;
            return;
        }
        var f = value.Feature;
        FeatureHeader = $"{f.Title}{(f.Where.Length > 0 ? $" · {f.Where}" : "")}";
        FeatureWarnings = string.Join("\n", f.Warnings.Select(w => "⚠ " + w));
        FillCases();
        // The data-driven suites first: they run every case; the main-flow scripts are the starting point for your own.
        foreach (var s in f.Suites.Concat(f.Scripts)) Scripts.Add(s);
        SelectedScript = Scripts.FirstOrDefault(s => s.Id == _preferredScriptId) ?? Scripts.FirstOrDefault();
        OnPropertyChanged(nameof(ScriptText));

        // Show where it is in the app.
        var control = f.Fields.Select(x => x.Control).FirstOrDefault() ?? f.Action ?? f.Controls.FirstOrDefault();
        _ = OutlineAsync(control?.Element, Format.Clean(f.Title, 50));
    }

    partial void OnSelectedCaseChanged(CaseRow? value)
    {
        if (value?.Case.Target is { } target) _ = OutlineAsync(target.Element, $"{value.Id} · {Format.Clean(FeatureDetector.Label(target.Element), 40)}");
    }

    private int _outlineRequest;

    /// <summary>
    /// Outlines the element in the app, like a Ctrl pick, and keeps the outline until something else
    /// is selected or the tab is left. A field further down the page is scrolled into view first.
    /// </summary>
    private async Task OutlineAsync(ElementInfo? element, string caption)
    {
        var request = ++_outlineRequest;
        _flashTimer.Stop();
        if (element is null)
        {
            _highlighter.Hide();
            return;
        }
        System.Drawing.Rectangle? bounds;
        try
        {
            bounds = await _service.RevealAsync(element);
        }
        catch (Exception ex) when (ex is UiaTimeoutException or OperationCanceledException)
        {
            bounds = null;
        }
        if (request != _outlineRequest) return; // another row was selected meanwhile
        _highlighter.Show(bounds ?? element.BoundingRectangle, bounds is null ? caption + " (where it was; the page has changed)" : caption, preview: false);
    }

    /// <summary>Removes the outline (the tab was left, or the inspector is picking something else).</summary>
    public void HideOutline()
    {
        _outlineRequest++;
        _flashTimer.Stop();
        _highlighter.Hide();
    }

    private bool HasCase() => SelectedCase is not null;

    [RelayCommand(CanExecute = nameof(HasCase))]
    private void CopyValue()
    {
        if (SelectedCase is not { } c) return;
        if (!c.HasData)
        {
            Status = $"{c.Id} has no data to type (it's about leaving the field empty, or about the page itself).";
            return;
        }
        CopyToClipboard(c.CopyText, c.Case.Value is null
            ? $"Copied {c.Id}'s description of what to type; use your app's own value."
            : $"Copied {c.Id}'s value ({c.CopyText.Length:N0} character{(c.CopyText.Length == 1 ? "" : "s")}). Paste it into the field.");
    }

    [RelayCommand(CanExecute = nameof(HasCase))]
    private void CopySteps()
    {
        if (SelectedCase is { } c) CopyToClipboard(c.StepsText, $"Copied {c.Id}'s steps.");
    }

    [RelayCommand(CanExecute = nameof(HasCase))]
    private void CopyCase()
    {
        if (SelectedCase is not { } c) return;
        var text = new System.Text.StringBuilder();
        text.AppendLine($"{c.Id}: {c.Title} ({c.Kind}, {c.Priority} priority)");
        text.AppendLine(c.StepsText);
        if (c.HasData) text.AppendLine($"Data: {c.Case.Data}");
        text.AppendLine($"Expected: {c.Expected}");
        if (c.HasRisk) text.AppendLine($"If it fails: {c.IfItFails}");
        CopyToClipboard(text.ToString(), $"Copied {c.Id} as text, for a ticket or a test management tool.");
    }

    partial void OnSelectedCaseFilterChanged(string value) => FillCases();

    partial void OnSelectedScriptChanged(CodeSnippet? value)
    {
        if (value is not null) _preferredScriptId = value.Id;
    }

    private void FillCases()
    {
        Cases.Clear();
        if (SelectedFeature is null) return;
        var all = SelectedFeature.Feature.Cases;
        var shown = SelectedCaseFilter switch
        {
            "High priority" => all.Where(c => c.Priority == TestPriority.High),
            "Worth automating" => all.Where(c => c.Automatable),
            "Works (happy path)" => all.Where(c => c.Category == TestCategory.Positive),
            "Bad input" => all.Where(c => c.Category == TestCategory.Negative),
            "Limits" => all.Where(c => c.Category == TestCategory.Boundary),
            "Security" => all.Where(c => c.Category == TestCategory.Security),
            "Accessibility" => all.Where(c => c.Category == TestCategory.Accessibility),
            "Usability" => all.Where(c => c.Category == TestCategory.Usability),
            _ => all,
        };
        foreach (var c in shown)
        {
            var steps = string.Join("\n", c.Steps.Select((s, i) => $"{i + 1}. {s}"));
            Cases.Add(new CaseRow(c, c.Id, c.Title, TestPlanRenderer.CategoryText(c.Category), c.Priority.ToString(), c.Data ?? "", c.Expected, steps));
        }
        CaseCountText = Cases.Count == all.Count ? $"{all.Count} cases; select one to see its steps" : $"{Cases.Count} of {all.Count} cases";
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void SavePlanHtml() => SavePlan("html", "Web page (*.html)|*.html", TestPlanRenderer.Html, "the whole test plan as one page");

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void SavePlanCsv() => SavePlan("csv", "CSV (*.csv)|*.csv", TestPlanRenderer.Csv, "one row per test case; open it in Excel or import it into your test management tool");

    private void SavePlan(string extension, string filter, Func<TestPlan, string> render, string what)
    {
        if (_report?.Plan is not { } plan) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"test plan - {SafeName(plan.Window)}.{extension}", Filter = filter };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, render(plan));
            Status = $"Saved {dialog.FileName}: {what}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't save: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void CopyPlanMarkdown()
    {
        if (_report?.Plan is { } plan) CopyToClipboard(TestPlanRenderer.Markdown(plan), "Copied the test plan as Markdown tables.");
    }

    private bool HasScript() => SelectedScript is not null;

    [RelayCommand(CanExecute = nameof(HasScript))]
    private void CopyPlanScript()
    {
        if (SelectedScript is { } script) CopyToClipboard(script.Code, $"Copied the {script.Title} script.");
    }

    [RelayCommand(CanExecute = nameof(HasScript))]
    private void SavePlanScript()
    {
        if (SelectedScript is not { } script) return;
        var fileName = ScriptFileName(script);
        var extension = Path.GetExtension(fileName).TrimStart('.');
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = fileName, Filter = $"Script (*.{extension})|*.{extension}|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, script.Code);
            Status = $"Saved {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't save: {ex.Message}";
        }
    }

    private void CopyToClipboard(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            Status = done;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "The clipboard is busy (another app has it open). Try again.";
        }
    }

    private static string SafeName(ElementInfo window)
    {
        var name = string.Concat((string.IsNullOrWhiteSpace(window.Name) ? window.ProcessName : window.Name).Trim()
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        return name.Length > 50 ? name[..50] : name;
    }

    /// <summary>The preview file to show; a new file each time, so the browser always reloads.</summary>
    [ObservableProperty]
    private Uri? _previewUri;

    /// <summary>The cursor is on this window when the button is clicked, so wait for it to move to the app.</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeUnderCursorAsync()
    {
        IsBusy = true;
        try
        {
            for (var remaining = 3; remaining > 0; remaining--)
            {
                AnalyzeButtonText = $"Analyzing in {remaining}…";
                Status = $"Put the cursor on the window to analyze… {remaining}";
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            AnalyzeButtonText = "Analyze window under cursor (3 s)";
            var hit = await _service.InspectAtPointAsync(ScreenInfo.GetCursorPosition(), includeAncestors: false);
            if (hit is null)
            {
                Status = "Nothing found under the cursor.";
                return;
            }
            if (hit.Element.ProcessId == OwnProcessId)
            {
                Status = "The cursor was on the inspector. Move it onto the app's window within the 3 seconds.";
                return;
            }
            await AnalyzeAsync(hit.Element);
        }
        catch (Exception ex)
        {
            Status = Failure(ex);
        }
        finally
        {
            AnalyzeButtonText = "Analyze window under cursor (3 s)";
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeSelectedAsync()
    {
        if (_selectedElement() is not { } element)
        {
            Status = "Pick an element in the Inspector tab first (Ctrl-hover or the tree); its window is analyzed.";
            return;
        }
        IsBusy = true;
        try
        {
            await AnalyzeAsync(element);
        }
        catch (Exception ex)
        {
            Status = Failure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAnalyze() => !IsBusy;

    // The analyzer's own messages (desktop picked, window closed) are already worded for the user.
    private static string Failure(Exception ex) =>
        ex is InvalidOperationException ? ex.Message : "Analysis failed: " + FailureMessages.Describe(ex, null).Replace("Inspection failed: ", "");

    private async Task AnalyzeAsync(ElementInfo element)
    {
        Status = "Reading the window, finding what to test and grading its controls…";
        var report = await _analyzer.AnalyzeAsync(element);
        _report = report;
        HasReport = true;
        ScoreText = $"{report.Score} / 100";
        CountsText = $"{report.Count(TestabilityGrade.Good)} good · {report.Count(TestabilityGrade.Fair)} with caveats · {report.Count(TestabilityGrade.Fragile)} fragile";
        // The few changes that help most, then the counts.
        var top = TestabilityRenderer.TopFixes(report).Select((l, i) => $"{i + 1}. {l}");
        SummaryText = string.Join("\n", top.Prepend("In short, the changes that help most:"));
        Status = $"“{Format.Clean(report.Window.Name, 60)}”: {report.Plan?.Features.Count ?? 0} features to test with {report.Plan?.CaseCount ?? 0} test cases; " +
                 $"{report.Findings.Count} controls graded for automation ({report.Duration.TotalSeconds:0.0} s)" +
                 (report.Truncated ? "; the window has more, the rest wasn't graded." : ".");
        FillGroups();
        if (report.Plan is { } plan) FillPlan(plan);
        RenderPreview();
    }

    partial void OnSelectedFilterChanged(string value) => FillGroups();

    private void FillGroups()
    {
        Groups.Clear();
        Members.Clear();
        if (_report is null) return;
        var shown = SelectedFilter switch
        {
            "Fragile only" => _report.Groups.Where(g => g.Grade == TestabilityGrade.Fragile),
            "All controls" => _report.Groups,
            _ => TestabilityRenderer.ToImprove(_report),
        };
        foreach (var g in shown) Groups.Add(Row(g));
    }

    private static GroupRow Row(TestabilityGroup g)
    {
        var f = g.First;
        var lines = new List<string>();
        if (g.Where.Length > 0) lines.Add(char.ToUpperInvariant(g.Where[0]) + g.Where[1..]);
        lines.Add(f.Issues.Count > 0 ? f.Issues[0] : $"Found by: {f.FoundBy}");
        if (f.Fix is { } fix) lines.Add($"Fix{(g.Count > 1 ? " (for each)" : "")}: {fix}");
        var tone = g.Grade switch
        {
            TestabilityGrade.Good => "ok",
            TestabilityGrade.Fair => "warn",
            _ => "bad",
        };
        var title = g.Count > 1 ? g.Title : $"{f.Number}. {g.Title}";
        return new GroupRow(g, title, string.Join("\n", lines), tone);
    }

    partial void OnSelectedGroupChanged(GroupRow? value)
    {
        Members.Clear();
        if (value is null) return;
        foreach (var f in value.Group.Findings)
        {
            var element = StepWording.Element(f.Element with { Name = TestabilityAnalyzer.Visible(f.Element.Name) });
            var title = $"{f.Number}. {char.ToUpperInvariant(element[0])}{element[1..]}" + (f.SimilarNotListed > 0 ? $" (+{f.SimilarNotListed} like it next to it)" : "");
            Members.Add(new FindingRow(f, title, f.Where.Length > 0 ? f.Where : ""));
        }
        MembersHeader = value.Group.Count == 1 ? "The control" : $"Its {value.Group.Count} controls (select one to see it in the app)";
        SelectedMember = Members.FirstOrDefault();
    }

    partial void OnSelectedMemberChanged(FindingRow? value)
    {
        if (value is null) return;
        _ = OutlineAsync(value.Finding.Element, $"{value.Finding.Number}. {Format.Clean(Format.Describe(value.Finding.Element), 50)}");
    }

    private void EndFlash()
    {
        _flashTimer.Stop();
        _highlighter.Hide();
    }

    private void RenderPreview()
    {
        if (_report is null) return;
        try
        {
            Directory.CreateDirectory(_previewFolder);
            var file = Path.Combine(_previewFolder, $"testability-{++_previewNumber}.html");
            File.WriteAllText(file, TestabilityRenderer.Html(_report));
            var previous = PreviewUri;
            PreviewUri = new Uri(file);
            if (previous is { IsFile: true }) TryDelete(previous.LocalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The preview couldn't be updated: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void SaveHtml()
    {
        if (_report is null) return;
        var name = string.Concat((string.IsNullOrWhiteSpace(_report.Window.Name) ? _report.Window.ProcessName : _report.Window.Name).Trim()
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"testability - {(name.Length > 50 ? name[..50] : name)}.html",
            Filter = "Web page (*.html)|*.html",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, TestabilityRenderer.Html(_report));
            Status = $"Saved {dialog.FileName}. It's one file, screenshot included, ready to send to the developers.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't save: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void CopyText()
    {
        if (_report is null) return;
        try
        {
            Clipboard.SetText(TestabilityRenderer.Text(_report));
            Status = "Copied as text (Markdown), for a ticket or a chat. The saved HTML also has the numbered screenshot.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "The clipboard is busy (another app has it open). Try again.";
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes the preview files (they contain a screenshot of the app).</summary>
    public void Dispose()
    {
        _flashTimer.Stop();
        try
        {
            if (Directory.Exists(_previewFolder)) Directory.Delete(_previewFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UiInspector.Core.Recording;
using UiInspector.Core.Reporting;

namespace UiInspector.App;

/// <summary>A recorded step in the screenshot list, with its own on/off switch.</summary>
public sealed partial class StepScreenshotChoice(int stepId, string label) : ObservableObject
{
    public int StepId { get; } = stepId;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _include = true;

    public override string ToString() => Label;
}

public sealed record ScriptLanguage(string Id, string Title)
{
    public override string ToString() => Title;
}

/// <summary>
/// The Bug report tab: what the tester writes (title, expected/actual, notes), a switch for
/// every part of the report, and a live preview of the exact HTML that gets saved.
/// </summary>
public sealed partial class BugReportViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(250);

    private readonly Recorder _recorder;
    private readonly DispatcherTimer _previewTimer;
    private IReadOnlyList<RecordedStep>? _replaySteps; // the steps up to a failed replay step, while the report is about that
    private readonly string _previewFolder = Path.Combine(Path.GetTempPath(), $"UiInspector-preview-{Environment.ProcessId}");
    private int _previewNumber;
    private bool _rebuilding;

    public BugReportViewModel(Recorder recorder, Dispatcher dispatcher)
    {
        _recorder = recorder;
        _previewTimer = new DispatcherTimer(PreviewDelay, DispatcherPriority.Background, (_, _) => RenderPreview(), dispatcher) { IsEnabled = false };
        _recorder.Changed += () => dispatcher.BeginInvoke(() => OnStepsChanged());
        OnStepsChanged();
    }

    public IReadOnlyList<Severity> Severities { get; } = Enum.GetValues<Severity>();

    /// <summary>The languages the recording can be written in: the native four, plus Playwright and Selenium for web pages.</summary>
    public ObservableCollection<ScriptLanguage> ScriptLanguages { get; } = [.. NativeLanguages];

    private static readonly IReadOnlyList<ScriptLanguage> NativeLanguages =
    [
        new("flaui", "FlaUI (C#)"),
        new("uiautomation", "Python · uiautomation"),
        new("pywinauto", "Python · pywinauto"),
        new("appium", "Appium · Python"),
    ];

    public ObservableCollection<StepScreenshotChoice> StepScreenshots { get; } = [];

    // ---------------------------------------------------------------- what the tester writes

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private Severity _severity = Severity.None;
    [ObservableProperty] private string _expected = "";
    [ObservableProperty] private string _actual = "";
    [ObservableProperty] private string _notes = "";

    // ---------------------------------------------------------------- what goes in

    [ObservableProperty] private bool _includeSteps = true;
    [ObservableProperty] private bool _includeScreenshots = true;
    [ObservableProperty] private bool _highlightElement = true;
    [ObservableProperty] private bool _includeExpectedActual = true;
    [ObservableProperty] private bool _includeSeverity = true;
    [ObservableProperty] private bool _includeNotes = true;
    [ObservableProperty] private bool _includeEnvironment = true;
    [ObservableProperty] private bool _includeWindowsVersion = true;
    [ObservableProperty] private bool _includeAppVersion = true;
    [ObservableProperty] private bool _includeAppPath;
    [ObservableProperty] private bool _includeScreens = true;
    [ObservableProperty] private bool _includeLanguage = true;
    [ObservableProperty] private bool _includeDateTime = true;
    [ObservableProperty] private bool _includeScript;
    [ObservableProperty] private ScriptLanguage _selectedScriptLanguage = NativeLanguages[0];
    [ObservableProperty] private bool _includeElementDetails;

    // ---------------------------------------------------------------- output

    /// <summary>The preview file to show; a new file each time, so the browser always reloads.</summary>
    [ObservableProperty] private Uri? _previewUri;

    [ObservableProperty] private string _status = "";

    [ObservableProperty] private bool _hasScreenshots;

    /// <summary>Shown while the report uses the steps of a failed replay instead of the whole recording.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFromReplay))]
    private string _replayNote = "";

    public bool IsFromReplay => ReplayNote.Length > 0;

    /// <summary>
    /// Starts the report from a replay failure: the steps up to the failed one, with the window as
    /// the replay saw it when it failed. Fields the tester already wrote are kept.
    /// </summary>
    public void UseReplayFailure(IReadOnlyList<RecordedStep> steps, int failedIndex, ReplayStepResult failure)
    {
        var failed = steps[failedIndex];
        var list = steps.Take(failedIndex).ToList();
        list.Add(failure.Screenshot is { } screenshot ? failed with { Screenshot = screenshot } : failed);
        _replaySteps = list;

        var wording = StepWording.Describe(failed).TrimEnd('.');
        if (string.IsNullOrWhiteSpace(Title)) Title = $"Step {failedIndex + 1} fails: {wording}";
        if (string.IsNullOrWhiteSpace(Expected))
        {
            var check = wording.Replace("Check that ", "");
            Expected = failed.Action is RecordedAction.CheckText or RecordedAction.CheckExists && check.Length > 0
                ? char.ToUpperInvariant(check[0]) + check[1..] + "."
                : $"{wording} works, as it did when the steps were recorded.";
        }
        if (string.IsNullOrWhiteSpace(Actual)) Actual = failure.Message;
        if (string.IsNullOrWhiteSpace(Notes)) Notes = $"Found by replaying the recorded steps in UiInspector ({DateTime.Now:g}).";
        ReplayNote = $"This report uses steps 1–{failedIndex + 1} of the recording, up to the step that failed in the replay; its screenshot shows the window when it failed.";
        OnStepsChanged(keepReplay: true);
    }

    [RelayCommand]
    private void UseAllSteps()
    {
        _replaySteps = null;
        ReplayNote = "";
        OnStepsChanged();
    }

    private IReadOnlyList<RecordedStep> ReportSteps => _replaySteps ?? _recorder.Steps;

    // Any change to the report's content refreshes the preview (shortly after, so typing stays smooth).
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(PreviewUri) or nameof(Status) or nameof(HasScreenshots) or nameof(ReplayNote) or nameof(IsFromReplay)) return;
        QueuePreview();
    }

    private void QueuePreview()
    {
        if (_rebuilding) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnStepsChanged(bool keepReplay = false)
    {
        // A new or edited recording replaces the replayed steps.
        if (!keepReplay && _replaySteps is not null && !_recorder.Steps.Take(_replaySteps.Count).Select(s => s.Id).SequenceEqual(_replaySteps.Select(s => s.Id)))
        {
            _replaySteps = null;
            ReplayNote = "";
        }
        var steps = ReportSteps;
        var hidden = StepScreenshots.Where(c => !c.Include).Select(c => c.StepId).ToHashSet();

        _rebuilding = true;
        foreach (var choice in StepScreenshots) choice.PropertyChanged -= OnChoiceChanged;
        StepScreenshots.Clear();
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Screenshot is null) continue;
            var choice = new StepScreenshotChoice(steps[i].Id, $"{i + 1}. {StepWording.Describe(steps[i])}") { Include = !hidden.Contains(steps[i].Id) };
            choice.PropertyChanged += OnChoiceChanged;
            StepScreenshots.Add(choice);
        }
        HasScreenshots = StepScreenshots.Count > 0;

        var languages = steps.Count > 0
            ? RecordingScripts.Render(steps).Select(s => new ScriptLanguage(s.Id, s.Title)).ToList()
            : NativeLanguages.ToList();
        if (!languages.Select(l => l.Id).SequenceEqual(ScriptLanguages.Select(l => l.Id)))
        {
            var keep = SelectedScriptLanguage.Id;
            ScriptLanguages.Clear();
            foreach (var language in languages) ScriptLanguages.Add(language);
            SelectedScriptLanguage = ScriptLanguages.FirstOrDefault(l => l.Id == keep) ?? ScriptLanguages[0];
        }
        _rebuilding = false;
        QueuePreview();
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e) => QueuePreview();

    private BugReportOptions Options() => new()
    {
        Steps = IncludeSteps,
        Screenshots = IncludeScreenshots,
        HighlightElement = HighlightElement,
        ExpectedActual = IncludeExpectedActual,
        Severity = IncludeSeverity,
        Notes = IncludeNotes,
        Environment = IncludeEnvironment,
        WindowsVersion = IncludeWindowsVersion,
        AppVersion = IncludeAppVersion,
        AppPath = IncludeAppPath,
        Screens = IncludeScreens,
        Language = IncludeLanguage,
        DateTime = IncludeDateTime,
        Script = IncludeScript,
        ScriptLanguage = SelectedScriptLanguage.Id,
        ElementDetails = IncludeElementDetails,
        HiddenScreenshots = StepScreenshots.Where(c => !c.Include).Select(c => c.StepId).ToHashSet(),
    };

    private BugReport Report()
    {
        var steps = ReportSteps;
        return new BugReport
        {
            Title = Title,
            Severity = Severity,
            Expected = Expected,
            Actual = Actual,
            Notes = Notes,
            Steps = steps,
            Environment = EnvironmentInfo.Collect(steps, _recorder.StartedAt ?? DateTime.Now),
            Scripts = steps.Count > 0 ? RecordingScripts.Render(steps) : [],
        };
    }

    private void RenderPreview()
    {
        _previewTimer.Stop();
        try
        {
            Directory.CreateDirectory(_previewFolder);
            var file = Path.Combine(_previewFolder, $"preview-{++_previewNumber}.html");
            File.WriteAllText(file, BugReportRenderer.Html(Report(), Options()));
            var previous = PreviewUri;
            PreviewUri = new Uri(file);
            if (previous is { IsFile: true }) TryDelete(previous.LocalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The preview couldn't be updated: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- saving

    [RelayCommand]
    private void SaveHtml()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = SafeFileName() + ".html", Filter = "Web page (*.html)|*.html" };
        if (dialog.ShowDialog() != true) return;
        Save(() => File.WriteAllText(dialog.FileName, BugReportRenderer.Html(Report(), Options())),
            $"Saved {dialog.FileName}. It contains everything, including the screenshots, so it can be attached as is.");
    }

    [RelayCommand]
    private void SaveMarkdown()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = SafeFileName() + ".md", Filter = "Markdown (*.md)|*.md" };
        if (dialog.ShowDialog() != true) return;
        var folderName = Path.GetFileNameWithoutExtension(dialog.FileName) + "_files";
        Save(() =>
        {
            var markdown = BugReportRenderer.Markdown(Report(), Options(), folderName);
            var directory = Path.GetDirectoryName(dialog.FileName)!;
            foreach (var (relative, png) in markdown.Files)
            {
                var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, png);
            }
            File.WriteAllText(dialog.FileName, markdown.Text);
        }, $"Saved {dialog.FileName}" + (IncludeScreenshots && HasScreenshots ? $", with the screenshots in {folderName}." : "."));
    }

    [RelayCommand]
    private void CopyText()
    {
        try
        {
            Clipboard.SetText(BugReportRenderer.Text(Report(), Options()));
            Status = "Copied as text (Markdown). Paste it into Jira, Azure DevOps or GitHub, and attach the screenshots from a saved report.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "The clipboard is busy (another app has it open). Try again.";
        }
    }

    private void Save(Action write, string done)
    {
        try
        {
            write();
            Status = done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't save: {ex.Message}";
        }
    }

    private string SafeFileName()
    {
        var name = string.IsNullOrWhiteSpace(Title) ? "bug-report" : Title.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name.Length > 60 ? name[..60] : name;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // still shown; removed with the folder on exit
    }

    /// <summary>Removes the preview files (they contain the screenshots).</summary>
    public void Dispose()
    {
        _previewTimer.Stop();
        try
        {
            if (Directory.Exists(_previewFolder)) Directory.Delete(_previewFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UiInspector.Core;
using UiInspector.Core.Recording;
using UiInspector.Core.Selectors;

namespace UiInspector.App;

/// <summary>One row in the recorded steps list.</summary>
public sealed record StepRow(RecordedStep Step, int Number, string Text, string Detail, string Tone)
{
    public override string ToString() => $"{Number}. {Text}";
}

/// <summary>
/// The Recorder tab: start and stop recording, the steps recorded so far, and the script for the
/// chosen framework. The recorder reports from background threads; everything here runs on the UI thread.
/// </summary>
public sealed partial class RecorderViewModel : ObservableObject
{
    private static readonly TimeSpan FlashTime = TimeSpan.FromMilliseconds(700);

    private readonly Recorder _recorder;
    private readonly IHighlighter _highlighter;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _flashTimer;
    private string _preferredScriptId = "flaui";
    private bool _refreshQueued;

    public RecorderViewModel(Recorder recorder, IHighlighter highlighter, Dispatcher dispatcher)
    {
        _recorder = recorder;
        _highlighter = highlighter;
        _dispatcher = dispatcher;
        _flashTimer = new DispatcherTimer(FlashTime, DispatcherPriority.Normal, (_, _) => EndFlash(), dispatcher) { IsEnabled = false };

        _recorder.Changed += () => _dispatcher.BeginInvoke(QueueRefresh);
        _recorder.Notice += message => _dispatcher.BeginInvoke(() => Status = message);
        _recorder.ElementRecorded += element => _dispatcher.BeginInvoke(() => Flash(element));
        Refresh(); // fills the language list before anything is recorded
    }

    public ObservableCollection<StepRow> Steps { get; } = [];

    public ObservableCollection<CodeSnippet> Scripts { get; } = [];

    /// <summary>Raised when recording starts or stops, so the main window can pause live pick meanwhile.</summary>
    public event Action<bool>? RecordingChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteStepCommand))]
    private StepRow? _selectedStep;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyScriptCommand), nameof(SaveScriptCommand))]
    private CodeSnippet? _selectedScript;

    [ObservableProperty]
    private string _status = "Press Record (or Ctrl+Shift+F8), then use the app you want to test. Clicks, typed text and keys like Enter become steps.";

    [ObservableProperty]
    private string _checkButtonText = "Add check (3 s)";

    [ObservableProperty]
    private bool _isBusy;

    public string RecordButtonText => IsRecording ? "■ Stop" : "● Record";

    public bool HasSteps => Steps.Count > 0;

    [RelayCommand]
    public async Task ToggleRecordingAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (_recorder.IsRecording)
            {
                await _recorder.StopAsync();
                Status = Steps.Count == 0 ? "Stopped. Nothing was recorded." : $"Stopped. {Steps.Count} step{(Steps.Count == 1 ? "" : "s")} recorded; Record again to add more.";
            }
            else
            {
                await _recorder.StartAsync();
                Status = "Recording… Use the target app now. Ctrl+Shift+F7 adds a check for the element under the cursor; Ctrl+Shift+F8 stops.";
            }
        }
        catch (Exception ex)
        {
            Status = $"Couldn't {(_recorder.IsRecording ? "stop" : "start")} recording: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsRecording = _recorder.IsRecording;
            RecordingChanged?.Invoke(IsRecording);
        }
    }

    /// <summary>Adds a check for the element under the cursor (hotkey Ctrl+Shift+F7).</summary>
    public async Task AddCheckAtCursorAsync()
    {
        try
        {
            await _recorder.AddCheckAsync(ScreenInfo.GetCursorPosition());
        }
        catch (Exception ex)
        {
            Status = FailureMessages.Describe(ex, null);
        }
    }

    /// <summary>The button version: the cursor is on this window when it's clicked, so wait for it to move to the target.</summary>
    [RelayCommand]
    private async Task AddCheckDelayedAsync()
    {
        for (var remaining = 3; remaining > 0; remaining--)
        {
            CheckButtonText = $"Checking in {remaining}…";
            Status = $"Put the cursor on the element to check… {remaining}";
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        CheckButtonText = "Add check (3 s)";
        await AddCheckAtCursorAsync();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteStep))]
    private void DeleteStep()
    {
        if (SelectedStep is { } row) _recorder.Remove(row.Step.Id);
    }

    private bool CanDeleteStep() => SelectedStep is not null;

    [RelayCommand]
    private void Clear()
    {
        _recorder.Clear();
        Status = "Cleared.";
    }

    [RelayCommand(CanExecute = nameof(HasScript))]
    private void CopyScript()
    {
        if (SelectedScript is not { } script) return;
        try
        {
            Clipboard.SetText(script.Code);
            Status = $"Copied the {script.Title} script to the clipboard.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "The clipboard is busy (another app has it open). Try again.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasScript))]
    private void SaveScript()
    {
        if (SelectedScript is not { } script) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = script.Id == "flaui" ? "RecordedTest.cs" : "recorded_test.py",
            Filter = script.Id == "flaui" ? "C# file (*.cs)|*.cs|All files (*.*)|*.*" : "Python file (*.py)|*.py|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, script.Code);
            Status = $"Saved the {script.Title} script to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't save: {ex.Message}";
        }
    }

    private bool HasScript() => SelectedScript is not null && Steps.Count > 0;

    partial void OnSelectedScriptChanged(CodeSnippet? value)
    {
        if (value is not null) _preferredScriptId = value.Id;
    }

    partial void OnSelectedStepChanged(StepRow? value)
    {
        if (value is not null && !IsRecording) Flash(value.Step.Target);
    }

    // Several changes often arrive together (a click plus the text typed before it, then the
    // selectors); one refresh per dispatcher pass is enough.
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);
    }

    private void Refresh()
    {
        _refreshQueued = false;
        var selectedId = SelectedStep?.Step.Id;
        var steps = _recorder.Steps;

        Steps.Clear();
        for (var i = 0; i < steps.Count; i++) Steps.Add(Row(steps[i], i + 1));
        SelectedStep = Steps.FirstOrDefault(r => r.Step.Id == selectedId);
        OnPropertyChanged(nameof(HasSteps));

        var keep = _preferredScriptId;
        Scripts.Clear();
        foreach (var script in _recorder.RenderScripts()) Scripts.Add(script);
        SelectedScript = Scripts.FirstOrDefault(s => s.Id == keep) ?? Scripts.FirstOrDefault();
        CopyScriptCommand.NotifyCanExecuteChanged();
        SaveScriptCommand.NotifyCanExecuteChanged();
        IsRecording = _recorder.IsRecording;
    }

    private static StepRow Row(RecordedStep step, int number)
    {
        if (step.IsGenerating) return new StepRow(step, number, step.Describe(), "Generating selector…", "busy");
        if (step.ChosenSelector is not { } selector) return new StepRow(step, number, step.Describe(), $"No selector: {step.Problem}", "bad");

        var detail = selector.Verification.Status switch
        {
            VerificationStatus.Unique => $"✔ {selector.Strategy}",
            VerificationStatus.Unverified => $"⚠ {selector.Strategy}: the element closed before it could be checked",
            _ => $"⚠ {selector.Strategy}: {selector.Verification.Describe()}",
        };
        var fragile = selector.Steps.Any(s => s.Index is not null);
        var tone = selector.Verification.Status == VerificationStatus.Unique && !fragile ? "ok" : "warn";
        if (fragile) detail += " (uses position: fragile)";
        return new StepRow(step, number, step.Describe(), detail, tone);
    }

    private void Flash(ElementInfo element)
    {
        _highlighter.Show(element.BoundingRectangle, Format.Clean(Format.Describe(element), 60), preview: true);
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private void EndFlash()
    {
        _flashTimer.Stop();
        _highlighter.Hide();
    }
}

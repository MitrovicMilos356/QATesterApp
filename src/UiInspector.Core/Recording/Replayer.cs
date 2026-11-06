using System.Diagnostics;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using UiInspector.Core.Reporting;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Recording;

public enum ReplayStatus
{
    Pending,
    Running,
    Passed,
    Failed,

    /// <summary>The step has no selector, so the scripts skip it too.</summary>
    Skipped,
}

/// <summary>How one step went. <see cref="Bounds"/> is where its element was (screen pixels), if it was found.</summary>
public sealed record ReplayStepResult(int StepId, ReplayStatus Status, string Message)
{
    public Rectangle? Bounds { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Passed, but not the way the scripts would do it (see the message).</summary>
    public bool IsWarning { get; init; }

    /// <summary>The app's window when the step failed.</summary>
    public StepScreenshot? Screenshot { get; init; }
}

public sealed class ReplayOptions
{
    /// <summary>How long to wait for each element, as the scripts do.</summary>
    public TimeSpan FindTimeout { get; init; } = TimeSpan.FromSeconds(CodeTemplates.WaitSeconds);

    /// <summary>A pause before each step, so the person watching can follow (and see the highlight).</summary>
    public TimeSpan StepPause { get; init; } = TimeSpan.FromMilliseconds(400);
}

/// <summary>
/// Plays a recording back the way its FlaUI script would: each step's selector is searched from
/// the desktop (waiting up to 10 s), then the same action is performed, a UIA pattern call where
/// there is one. It stops at the first step that fails and says why: which part of the selector
/// found nothing, or what a check got instead.
/// </summary>
public sealed class Replayer(UiaWorker worker, ReplayOptions? options = null)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private readonly ReplayOptions _options = options ?? new ReplayOptions();

    /// <summary>Raised from a background thread before each step (Running) and after it.</summary>
    public event Action<ReplayStepResult>? Progress;

    /// <param name="secrets">The password for each secret step, by step id (passwords are never recorded).</param>
    /// <returns>One result per step; steps after a failure stay Pending.</returns>
    public async Task<IReadOnlyList<ReplayStepResult>> RunAsync(
        IReadOnlyList<RecordedStep> steps, IReadOnlyDictionary<int, string>? secrets = null, CancellationToken cancellationToken = default)
    {
        secrets ??= new Dictionary<int, string>();
        var results = steps.Select(s => new ReplayStepResult(s.Id, ReplayStatus.Pending, "")).ToList();
        for (var i = 0; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[i];
            if (step.ChosenSelector is not { } selector)
            {
                Report(results, i, new ReplayStepResult(step.Id, ReplayStatus.Skipped,
                    $"Skipped: {(step.IsGenerating ? "its selector was still being generated" : step.Problem ?? "no selector could be built")}."));
                continue;
            }

            Report(results, i, new ReplayStepResult(step.Id, ReplayStatus.Running, "Looking for the element…") { Bounds = step.Target.BoundingRectangle });
            await Task.Delay(_options.StepPause, cancellationToken);
            var watch = Stopwatch.StartNew();
            var result = await RunStepAsync(step, selector, secrets.GetValueOrDefault(step.Id), cancellationToken);
            Report(results, i, result with { Duration = watch.Elapsed });
            if (result.Status == ReplayStatus.Failed) break;
        }
        return results;
    }

    private void Report(List<ReplayStepResult> results, int index, ReplayStepResult result)
    {
        results[index] = result;
        Progress?.Invoke(result);
    }

    private async Task<ReplayStepResult> RunStepAsync(RecordedStep step, Selector selector, string? secret, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.FindTimeout;
        try
        {
            while (true)
            {
                // Each attempt is one short worker call, so a slow search never blocks cancelling.
                var found = await worker.RunAsync(a => Resolve(a, selector), cancellationToken);
                if (found.Element is not null)
                {
                    if (step.IsSecret && secret is null)
                    {
                        return await worker.RunAsync(_ => Failed(step, "No password was given for this step.", found), cancellationToken);
                    }
                    return await worker.RunAsync(_ => Act(step, found, secret), cancellationToken);
                }
                if (DateTime.UtcNow >= deadline)
                {
                    var message = $"{found.Problem!.TrimEnd('.')} (waited {_options.FindTimeout.TotalSeconds:0.#} s).";
                    return await worker.RunAsync(_ => Failed(step, message, found), cancellationToken);
                }
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (UiaTimeoutException ex)
        {
            return new ReplayStepResult(step.Id, ReplayStatus.Failed, $"The app stopped answering UI Automation: {ex.Message}");
        }
    }

    /// <summary>The element a selector finds now, like the generated code would: the first match (or the indexed one) at each step.</summary>
    internal sealed record Resolution(AutomationElement? Element, AutomationElement? Window, string? Problem, string? Note);

    internal static Resolution Resolve(AutomationBase automation, Selector selector)
    {
        try
        {
            var windows = Matches(automation, automation.GetDesktop(), selector.Window, TreeScope.Children);
            if (windows.Count == 0) return new(null, null, $"The window ({selector.Window}) isn't open.", null);

            var window = windows[0];
            var scope = window;
            string? note = windows.Count > 1 ? $"{windows.Count} windows match; the first one was used, as in the scripts." : null;
            for (var i = 0; i < selector.Steps.Count; i++)
            {
                var step = selector.Steps[i];
                var matches = Matches(automation, scope, step with { Index = null }, TreeScope.Descendants);
                var where = i == 0 ? "in the window" : $"inside {selector.Steps[i - 1]}";
                if (matches.Count == 0) return new(null, window, $"Nothing matches {step} {where}.", note);
                if (step.Index is { } index)
                {
                    if (index >= matches.Count) return new(null, window, $"Only {matches.Count} elements match {step with { Index = null }} {where}; the step wants #{index + 1}.", note);
                    scope = matches[index];
                }
                else
                {
                    if (matches.Count > 1 && i == selector.Steps.Count - 1)
                    {
                        note = $"{matches.Count} elements match; the first one was used, as in the scripts.";
                    }
                    scope = matches[0];
                }
            }
            return new(scope, window, null, note);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return new(null, null, $"UI Automation failed while searching: {ex.Message}", null);
        }
    }

    private static List<AutomationElement> Matches(AutomationBase automation, AutomationElement scope, SelectorStep step, TreeScope treeScope)
    {
        var condition = LiveTree.Condition(automation.ConditionFactory, step);
        var found = treeScope == TreeScope.Children ? scope.FindAllChildren(condition) : scope.FindAllDescendants(condition);
        var contains = step.Conditions.Where(c => c.Match == MatchKind.Contains).ToList();
        return found
            .Where(e => contains.All(c => (Read(e, c.Property) ?? "").Contains(c.Value, StringComparison.Ordinal)))
            .Where(e => step.Has is null || Matches(automation, e, step.Has, TreeScope.Descendants).Count > 0)
            .ToList();
    }

    private static string? Read(AutomationElement e, SelectorProperty property) => property switch
    {
        SelectorProperty.Name => e.Properties.Name.ValueOrDefault,
        SelectorProperty.AutomationId => e.Properties.AutomationId.ValueOrDefault,
        SelectorProperty.ClassName => e.Properties.ClassName.ValueOrDefault,
        _ => null,
    };

    private static ReplayStepResult Act(RecordedStep step, Resolution found, string? secret)
    {
        var target = found.Element!;
        var bounds = Bounds(target);
        ReplayStepResult Passed(string message) =>
            new(step.Id, ReplayStatus.Passed, found.Note is null ? message : $"{message} Note: {found.Note}") { Bounds = bounds };

        try
        {
            switch (step.Action)
            {
                case RecordedAction.Click:
                    var action = RecordingScripts.ClickAction(step.Target);
                    if (action == ElementAction.Click) Foreground(found);
                    Perform(target, action, "");
                    return Passed(action switch
                    {
                        ElementAction.Click => "Clicked (with the mouse; the element has no pattern action).",
                        ElementAction.Focus => "Focused.",
                        _ => $"Done ({action}).",
                    });
                case RecordedAction.DoubleClick:
                    Foreground(found);
                    target.DoubleClick();
                    return Passed("Double-clicked.");
                case RecordedAction.RightClick:
                    Foreground(found);
                    target.RightClick();
                    return Passed("Right-clicked.");
                case RecordedAction.SetValue or RecordedAction.TypeText:
                    return EnterText(step, found, secret, Passed);
                case RecordedAction.PressKey when step.Key is { } key:
                    Foreground(found);
                    target.Focus();
                    for (var i = 0; i < step.Repeat; i++) Press(key);
                    return Passed($"Pressed {key}{(step.Repeat > 1 ? $" ×{step.Repeat}" : "")}.");
                case RecordedAction.CheckExists:
                    return Passed("The element exists.");
                case RecordedAction.CheckText:
                    var actual = step.CheckSource == TextSource.Value
                        ? target.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault
                        : target.Properties.Name.ValueOrDefault;
                    return actual == step.Text
                        ? Passed($"Shows \"{CodeTemplates.OneLine(actual ?? "")}\".")
                        : Failed(step, $"Expected \"{CodeTemplates.OneLine(step.Text ?? "")}\", but it shows \"{CodeTemplates.OneLine(actual ?? "")}\".", found);
                default:
                    return Passed("Nothing to do for this step.");
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or FlaUI.Core.Exceptions.PatternNotSupportedException or FlaUI.Core.Exceptions.NoClickablePointException)
        {
            return Failed(step, $"The element was found, but the action failed: {ex.Message}", found);
        }
    }

    /// <summary>
    /// Types the step's text and reads the field back, since some fields accept the call without
    /// taking the text. A field that refuses SetValue gets the text through the keyboard instead,
    /// with a warning, because the scripts use SetValue there.
    /// </summary>
    private static ReplayStepResult EnterText(RecordedStep step, Resolution found, string? secret, Func<string, ReplayStepResult> passed)
    {
        var target = found.Element!;
        if (!step.IsSecret && step.Text is null)
        {
            return Failed(step, "The recording has no text for this step: the field's text couldn't be read while recording.", found);
        }
        var text = secret ?? step.Text!;
        var what = step.IsSecret ? "the password" : $"\"{CodeTemplates.OneLine(text)}\"";

        string? refused = null;
        if (step.Action == RecordedAction.SetValue)
        {
            try
            {
                target.Patterns.Value.Pattern.SetValue(text);
                // A password can't be read back; the call succeeding is all there is to check.
                if (step.IsSecret || WaitForText(target, text, exact: true) is null or Unreadable) return passed($"Typed {what}.");
                refused = "it accepted the call, but its text didn't change";
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or FlaUI.Core.Exceptions.PatternNotSupportedException)
            {
                refused = ex.Message.TrimEnd('.');
            }
        }

        Foreground(found);
        target.Focus();
        if (refused is not null) Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); // SetValue would have replaced the text
        Keyboard.Type(text);

        var shows = step.IsSecret ? null : WaitForText(target, text, exact: false);
        if (shows is not null && shows != Unreadable)
        {
            return Failed(step, $"Typed {what}, but the field shows \"{CodeTemplates.OneLine(shows)}\"." +
                                (refused is null ? "" : $" Setting it directly didn't work either ({refused})."), found);
        }
        var result = passed(refused is null
            ? $"Typed {what} with the keyboard." + (shows == Unreadable ? " (The field can't be read back to check it.)" : "")
            : $"Typed {what} with the keyboard: the field didn't take it directly ({refused}). The recorded scripts set it directly here, so they may fail at this step.");
        return refused is null ? result : result with { IsWarning = true };
    }

    private const string Unreadable = "\0";

    /// <summary>Waits briefly for the field to show the text. Null when it does; otherwise what it shows, or <see cref="Unreadable"/>.</summary>
    private static string? WaitForText(AutomationElement target, string text, bool exact)
    {
        string? shows = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            shows = ReadText(target);
            if (shows is null) return Unreadable;
            if (exact ? shows == text : shows.Contains(text, StringComparison.Ordinal)) return null;
            Thread.Sleep(100);
        }
        return shows;
    }

    private static string? ReadText(AutomationElement target)
    {
        try
        {
            if (target.Patterns.Value.PatternOrDefault is { } value) return value.Value.ValueOrDefault;
            return target.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(10_000);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static void Perform(AutomationElement target, ElementAction action, string value)
    {
        switch (action)
        {
            case ElementAction.Invoke: target.Patterns.Invoke.Pattern.Invoke(); break;
            case ElementAction.Toggle: target.Patterns.Toggle.Pattern.Toggle(); break;
            case ElementAction.Select: target.Patterns.SelectionItem.Pattern.Select(); break;
            case ElementAction.Expand: target.Patterns.ExpandCollapse.Pattern.Expand(); break;
            case ElementAction.SetValue: target.Patterns.Value.Pattern.SetValue(value); break;
            case ElementAction.Focus: target.Focus(); break;
            default: target.Click(); break;
        }
    }

    private static void Press(KeyChord key)
    {
        var keys = new List<VirtualKeyShort>();
        if (key.Ctrl) keys.Add(VirtualKeyShort.CONTROL);
        if (key.Alt) keys.Add(VirtualKeyShort.ALT);
        if (key.Shift) keys.Add(VirtualKeyShort.SHIFT);
        if (key.Win) keys.Add(VirtualKeyShort.LWIN);
        keys.Add((VirtualKeyShort)key.VirtualKey);
        if (keys.Count == 1) Keyboard.Type(keys[0]);
        else Keyboard.TypeSimultaneously([.. keys]);
    }

    // Real mouse and keyboard input goes to the foreground window, which is the inspector while it replays.
    private static void Foreground(Resolution found)
    {
        try
        {
            found.Window?.SetForeground();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
    }

    private static ReplayStepResult Failed(RecordedStep step, string message, Resolution found)
    {
        var bounds = found.Element is { } element ? Bounds(element) : null;
        var handle = found.Window?.Properties.NativeWindowHandle.ValueOrDefault ?? (step.Path.Count > 1 ? step.Path[1].NativeWindowHandle : IntPtr.Zero);
        var image = WindowCapture.CaptureWindow(handle);
        return new ReplayStepResult(step.Id, ReplayStatus.Failed, message)
        {
            Bounds = bounds,
            Screenshot = image is null ? null : WindowCapture.Mark(image, bounds),
        };
    }

    private static Rectangle? Bounds(AutomationElement element)
    {
        try
        {
            return element.Properties.BoundingRectangle.TryGetValue(out var r) ? r : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

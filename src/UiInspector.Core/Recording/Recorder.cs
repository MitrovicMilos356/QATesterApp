using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.EventHandlers;
using FlaUI.Core.Exceptions;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Recording;

public sealed class RecorderOptions
{
    public ClickSettings? ClickSettings { get; init; }

    /// <summary>Listen for UIA focus changes. Tests switch this off and drive the recorder directly.</summary>
    public bool TrackFocusEvents { get; init; } = true;

    public Func<IInputSource> InputFactory { get; init; } = () => new InputHook();

    /// <summary>Replaces "the focused element", so tests never read the user's real focused field.</summary>
    internal Func<AutomationBase, AutomationElement?>? FocusOverride { get; init; }
}

/// <summary>
/// Records a workflow as steps: clicks, text typed into fields, key steps (Enter, Ctrl+S…) and
/// checks. Each click is resolved to its element the moment the button goes down, on a UIA worker
/// of its own, before the app reacts and the UI changes. Selectors are generated afterwards, one
/// step at a time, on the inspection service's worker; when an element is gone by then (its
/// dialog closed), the step gets an unverified selector built from the snapshot.
/// </summary>
public sealed class Recorder : IAsyncDisposable
{
    private const int MaxTextLength = 10_000;
    private static readonly int OwnProcessId = Environment.ProcessId;
    private static readonly WebContentWaitOptions WebWait = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    private readonly InspectionService _service;
    private readonly ElementInspector _inspector;
    private readonly RecorderOptions _options;
    private readonly UiaWorker _snapshots = new(new UiaWorkerOptions { OperationTimeout = TimeSpan.FromSeconds(3) });
    private readonly Lock _gate = new();
    private readonly StepBuilder _builder;
    private readonly TextTracker _text = new();
    private readonly Channel<object> _events = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<int> _pending = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _consumer;
    private readonly Task _generator;
    private IInputSource? _input;
    private (AutomationBase Automation, FocusChangedEventHandlerBase Handler)? _focusSubscription;

    public Recorder(InspectionService service, ElementInspector inspector, RecorderOptions? options = null)
    {
        _service = service;
        _inspector = inspector;
        _options = options ?? new RecorderOptions();
        _builder = new StepBuilder(_options.ClickSettings ?? ClickSettings.FromSystem());
        _consumer = Task.Run(ConsumeAsync);
        _generator = Task.Run(GenerateAsync);
    }

    /// <summary>Steps or recording state changed. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Something the user should know (a drag that wasn't recorded, an app that didn't answer). Background thread.</summary>
    public event Action<string>? Notice;

    /// <summary>An action on this element was just recorded, e.g. to flash it on screen. Background thread.</summary>
    public event Action<ElementInfo>? ElementRecorded;

    public bool IsRecording { get; private set; }

    public IReadOnlyList<RecordedStep> Steps
    {
        get
        {
            lock (_gate) return _builder.Steps;
        }
    }

    public async Task StartAsync()
    {
        if (IsRecording) return;
        await EnqueueAsync(new Baseline());

        if (_options.TrackFocusEvents)
        {
            _focusSubscription = await _snapshots.RunAsync(a =>
                ((AutomationBase)a, a.RegisterFocusChangedEvent(element => _events.Writer.TryWrite(new FocusEvent(element, null)))));
        }

        try
        {
            _input = _options.InputFactory();
            _input.Start(e => _events.Writer.TryWrite(e));
        }
        catch
        {
            _input?.Dispose();
            _input = null;
            await UnsubscribeFocusAsync();
            throw;
        }

        IsRecording = true;
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (!IsRecording) return;
        _input?.Dispose();
        _input = null;
        await UnsubscribeFocusAsync();
        IsRecording = false;

        await EnqueueAsync(new Flush()); // text typed right before stopping
        Changed?.Invoke();
    }

    /// <summary>Adds a check for the element at <paramref name="screenPoint"/>: its text if it has one, else that it exists.</summary>
    public Task AddCheckAsync(Point screenPoint) => EnqueueAsync(new CheckRequest(screenPoint, null));

    public void Remove(int stepId)
    {
        lock (_gate) _builder.Remove(stepId);
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate) _builder.Clear();
        Changed?.Invoke();
    }

    public IReadOnlyList<CodeSnippet> RenderScripts() => RecordingScripts.Render(Steps);

    // ---------------------------------------------------------------- test entry points

    /// <summary>Records a click on a known element, as if the mouse went down on it.</summary>
    internal Task ClickOnAsync(AutomationElement element, MouseButton button = MouseButton.Left, long time = 0) =>
        EnqueueAsync(new ElementClick(element, button, time));

    internal Task FocusOnAsync(AutomationElement element) => EnqueueAsync(new FocusEvent(element, new TaskCompletionSource()));

    internal Task CheckOnAsync(AutomationElement element) => EnqueueAsync(new CheckRequest(Point.Empty, element));

    internal void Post(RawInput input) => _events.Writer.TryWrite(input);

    internal Task FlushAsync() => EnqueueAsync(new Flush());

    // ---------------------------------------------------------------- event loop

    private abstract record Request
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Baseline : Request;

    private sealed record Flush : Request;

    /// <summary>Checks the element at <see cref="Point"/>, or <see cref="Element"/> when a test names it.</summary>
    private sealed record CheckRequest(Point Point, AutomationElement? Element) : Request;

    private sealed record ElementClick(AutomationElement Element, MouseButton Button, long Time) : Request;

    /// <summary>From the UIA focus event (Done is null) or a test (Done is set).</summary>
    private sealed record FocusEvent(AutomationElement Element, TaskCompletionSource? Done);

    private async Task EnqueueAsync(Request request)
    {
        _events.Writer.TryWrite(request);
        await request.Done.Task;
    }

    private async Task EnqueueAsync(FocusEvent focus)
    {
        _events.Writer.TryWrite(focus);
        await focus.Done!.Task;
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(_lifetime.Token))
            {
                try
                {
                    await HandleAsync(e);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Notice?.Invoke(FailureMessages.Describe(ex, (e as RawMouse)?.Position));
                }
                finally
                {
                    (e as Request)?.Done.TrySetResult();
                    (e as FocusEvent)?.Done?.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private Task HandleAsync(object e) => e switch
    {
        RawMouse { Down: true } m => OnMouseDownAsync(m),
        RawMouse m => OnMouseUp(m),
        RawTyping => OnTyping(),
        RawKey k => OnKeyAsync(k),
        FocusEvent f => OnFocusAsync(f.Element),
        ElementClick c => OnElementClickAsync(c),
        CheckRequest c => OnCheckAsync(c),
        Flush => OnFlushAsync(),
        Baseline => OnBaselineAsync(),
        _ => Task.CompletedTask,
    };

    private Task OnTyping()
    {
        _text.Typed();
        return Task.CompletedTask;
    }

    private Task OnMouseUp(RawMouse m)
    {
        bool dragged;
        lock (_gate) dragged = _builder.MouseUp(m.Position);
        if (dragged)
        {
            Notice?.Invoke("Drags aren't recorded (only clicks), so that one was left out.");
            Changed?.Invoke();
        }
        return Task.CompletedTask;
    }

    private async Task OnMouseDownAsync(RawMouse m)
    {
        var tracked = _text.Field;
        var (hit, focused, trackedText) = await _snapshots.RunAsync(a =>
            (_inspector.InspectAtPoint(a, m.Position), FocusedField(a), ReadText(tracked)), _lifetime.Token);

        if (hit is { WebContentPending: true })
        {
            // Chromium builds its page tree lazily on the first UIA request; this click woke it.
            hit = await InspectionService.WaitForWebContentAsync(
                ct => _snapshots.RunAsync(a => _inspector.InspectAtPoint(a, m.Position), ct), WebWait, null, _lifetime.Token);
            if (hit is { WebContentPending: true }) Notice?.Invoke("The web page hadn't exposed its content yet, so that click wasn't recorded. Click again.");
        }

        var entries = _text.Update(focused, trackedText);
        var path = hit is { WebContentPending: false } ? ClickPath(hit) : null;
        Apply(b =>
        {
            var ids = entries.Select(b.AddText).ToList();
            if (path is not null) ids.Add(b.AddClick(path, m.Button, m.Position, m.Time));
            return ids;
        }, path?[^1]);
    }

    private async Task OnElementClickAsync(ElementClick c)
    {
        var tracked = _text.Field;
        var (hit, focused, trackedText) = await _snapshots.RunAsync(a =>
            (_inspector.InspectElement(a, c.Element), FocusedField(a), ReadText(tracked)), _lifetime.Token);

        var entries = _text.Update(focused, trackedText);
        var path = hit is null ? null : ClickPath(hit);
        var center = hit is null ? Point.Empty : new Point(
            hit.Element.BoundingRectangle.X + hit.Element.BoundingRectangle.Width / 2,
            hit.Element.BoundingRectangle.Y + hit.Element.BoundingRectangle.Height / 2);
        Apply(b =>
        {
            var ids = entries.Select(b.AddText).ToList();
            if (path is not null) ids.Add(b.AddClick(path, c.Button, center, c.Time));
            return ids;
        }, path?[^1]);
    }

    private async Task OnKeyAsync(RawKey k)
    {
        // Arrows, Delete, Space… edit text inside a field; elsewhere they're actions (move in a list, delete a file).
        if (k.Kind == KeyKind.StepUnlessTextField && _text.Field is not null)
        {
            _text.Typed();
            return;
        }
        // Enter and Tab type into multi-line editors.
        if (k.Chord is { HasCommandModifier: false, VirtualKey: 0x0D } && _text.Field?.Element.ControlType == "Document")
        {
            _text.Typed();
            return;
        }

        var tracked = _text.Field;
        var (focus, field, trackedText) = await _snapshots.RunAsync(a =>
        {
            var element = FocusedElement(a);
            var inspection = element is null ? null : _inspector.InspectElement(a, element);
            return (inspection, inspection is null ? null : Field(inspection), ReadText(tracked));
        }, _lifetime.Token);

        var entries = _text.Update(field, trackedText);
        var path = focus is null || focus.Element.ProcessId == OwnProcessId || focus.AncestorsTruncated ? null : Chain(focus);
        if (path is { Count: < 2 }) path = null;
        Apply(b =>
        {
            var ids = entries.Select(b.AddText).ToList();
            if (path is not null) ids.Add(b.AddKey(path, k.Chord));
            return ids;
        }, path?[^1]);
    }

    private async Task OnFocusAsync(AutomationElement element)
    {
        var tracked = _text.Field;
        var (field, trackedText) = await _snapshots.RunAsync(a =>
        {
            var inspection = _inspector.InspectElement(a, element);
            return (inspection is null ? null : Field(inspection), ReadText(tracked));
        }, _lifetime.Token);
        var entries = _text.Update(field, trackedText);
        Apply(b => entries.Select(b.AddText).ToList(), null);
    }

    private async Task OnCheckAsync(CheckRequest request)
    {
        var tracked = _text.Field;
        var (hit, focused, trackedText, text, source) = await _snapshots.RunAsync(a =>
        {
            var hit = request.Element is { } element ? _inspector.InspectElement(a, element) : _inspector.InspectAtPoint(a, request.Point);
            var (text, source) = hit is null ? (null, TextSource.Value) : CheckText(hit, SmartTarget.FindIndex(hit));
            return (hit, FocusedField(a), ReadText(tracked), text, source);
        }, _lifetime.Token);

        var entries = _text.Update(focused, trackedText);
        var path = hit is null || hit.Element.ProcessId == OwnProcessId || hit.AncestorsTruncated ? null : SmartPath(hit);
        if (path is { Count: < 2 }) path = null;
        if (path is null) Notice?.Invoke("No element of another app under the cursor to check.");
        Apply(b =>
        {
            var ids = entries.Select(b.AddText).ToList();
            if (path is not null) ids.Add(b.AddCheck(path, text, source));
            return ids;
        }, path?[^1]);
    }

    private async Task OnFlushAsync()
    {
        var tracked = _text.Field;
        var (focused, trackedText) = await _snapshots.RunAsync(a => (FocusedField(a), ReadText(tracked)), _lifetime.Token);
        var entries = _text.Update(focused, trackedText);
        Apply(b => entries.Select(b.AddText).ToList(), null);
    }

    private async Task OnBaselineAsync()
    {
        _text.Reset();
        var focused = await _snapshots.RunAsync(FocusedField, _lifetime.Token);
        _text.Update(focused, null);
    }

    private void Apply(Func<StepBuilder, List<int?>> change, ElementInfo? recorded)
    {
        List<int?> ids;
        lock (_gate) ids = change(_builder);
        foreach (var id in ids.OfType<int>()) _pending.Writer.TryWrite(id);
        if (ids.Count == 0 && recorded is null) return;
        if (recorded is not null) ElementRecorded?.Invoke(recorded);
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- selector generation

    private async Task GenerateAsync()
    {
        try
        {
            await foreach (var id in _pending.Reader.ReadAllAsync(_lifetime.Token))
            {
                IReadOnlyList<ElementInfo>? path;
                lock (_gate) path = _builder.Find(id)?.Path;
                if (path is null) continue; // removed meanwhile

                SelectorResult? result = null;
                string? problem;
                try
                {
                    result = await _service.GenerateSelectorAsync(path, _lifetime.Token, includeWeb: false);
                    if (result.Best is null)
                    {
                        // A busy app can drop out of one tree search (e.g. while it handles the click
                        // being recorded); one more try tells that apart from a window that really closed.
                        await Task.Delay(RetryDelay, _lifetime.Token);
                        result = await _service.GenerateSelectorAsync(path, _lifetime.Token, includeWeb: false);
                    }
                    problem = result.Best is null ? result.Problem ?? "No selector could be verified." : null;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    problem = FailureMessages.Describe(ex, null);
                }

                var fallback = result?.Best is null ? SelectorGenerator.BuildUnverified(path) : null;
                lock (_gate) _builder.SetSelector(id, result, fallback, problem);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    // ---------------------------------------------------------------- reading the UI (UIA worker thread)

    private AutomationElement? FocusedElement(AutomationBase automation) =>
        _options.FocusOverride is { } focus ? focus(automation) : automation.FocusedElement();

    private FieldState? FocusedField(AutomationBase automation)
    {
        var element = FocusedElement(automation);
        var inspection = element is null ? null : _inspector.InspectElement(automation, element);
        return inspection is null ? null : Field(inspection);
    }

    /// <summary>The focused element as a text field, or null if it isn't one (or belongs to the inspector).</summary>
    private static FieldState? Field(InspectionResult inspection)
    {
        var info = inspection.Element;
        if (info.ProcessId == OwnProcessId || inspection.AncestorsTruncated || !info.IsEnabled) return null;
        if (info.ControlType is not ("Edit" or "Document" or "ComboBox") || info.Ref is null) return null;

        var live = info.Ref.Element;
        var canSet = info.Patterns.Contains("Value") && !IsReadOnly(live);
        if (!canSet && !info.Patterns.Contains("Text")) return null;

        var path = Chain(inspection);
        if (path.Count < 2) return null;
        return new FieldState(path, info.IsPassword ? null : ReadLiveText(live), info.IsPassword, canSet);
    }

    /// <summary>The tracked field's text now; null for passwords and fields that are gone.</summary>
    private static string? ReadText(FieldState? field) =>
        field is null || field.IsSecret || field.Element.Ref is null ? null : ReadLiveText(field.Element.Ref.Element);

    private static string? ReadLiveText(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Value.PatternOrDefault is { } value) return value.Value.ValueOrDefault;
            return element.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(MaxTextLength);
        }
        catch (Exception ex) when (ex is COMException or FlaUIException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsReadOnly(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.PatternOrDefault?.IsReadOnly.ValueOrDefault ?? true;
        }
        catch (Exception ex) when (ex is COMException or FlaUIException or InvalidOperationException)
        {
            return true;
        }
    }

    private static (string? Text, TextSource Source) CheckText(InspectionResult hit, int smartIndex)
    {
        var chain = Chain(hit);
        var target = chain[chain.Count - 1 - smartIndex];
        if (target.IsPassword || target.Ref is null) return (null, TextSource.Value);
        if (target.Patterns.Contains("Value") && target.ControlType is "Edit" or "ComboBox" or "Document")
        {
            return (ReadLiveText(target.Ref.Element), TextSource.Value);
        }
        return target.ControlType == "Text" && target.Name.Length > 0 ? (target.Name, TextSource.Name) : (null, TextSource.Value);
    }

    // ---------------------------------------------------------------- which element a click is "on"

    private static List<ElementInfo> Chain(InspectionResult result) => result.Ancestors.Reverse().Append(result.Element).ToList();

    /// <summary>Desktop … smart target (the clickable control around a label or icon).</summary>
    private static List<ElementInfo> SmartPath(InspectionResult hit)
    {
        var chain = Chain(hit);
        return chain.Take(chain.Count - SmartTarget.FindIndex(hit)).ToList();
    }

    /// <summary>The path to record for a click, or null for clicks that aren't actions on an app.</summary>
    private static List<ElementInfo>? ClickPath(InspectionResult hit)
    {
        if (hit.Element.ProcessId == OwnProcessId || hit.AncestorsTruncated) return null;
        var path = SmartPath(hit);
        if (path.Count < 2) return null; // the desktop itself

        // The taskbar only switches windows; scrollbars only scroll (scripts find elements without scrolling).
        if (path[1].ClassName is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        if (path.Any(e => e.ControlType == "ScrollBar")) return null;

        // A click on a window's background or title bar just activates it (the title bar's
        // Close/Minimize buttons are separate elements and are recorded).
        var target = path[^1];
        string[] actions = ["Invoke", "Toggle", "SelectionItem", "ExpandCollapse"];
        if (target.ControlType is "Window" or "Pane" or "TitleBar" && !actions.Any(target.Patterns.Contains)) return null;
        return path;
    }

    // ---------------------------------------------------------------- shutdown

    private async Task UnsubscribeFocusAsync()
    {
        if (_focusSubscription is not { } subscription) return;
        _focusSubscription = null;
        try
        {
            await _snapshots.RunAsync(_ => { subscription.Automation.UnregisterFocusChangedEvent(subscription.Handler); return true; });
        }
        catch (Exception ex) when (ex is UiaTimeoutException or COMException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _input?.Dispose();
        _input = null;
        await UnsubscribeFocusAsync();
        IsRecording = false;

        await _lifetime.CancelAsync();
        _events.Writer.TryComplete();
        _pending.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_consumer, _generator).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
        }
        _snapshots.Dispose();
        _lifetime.Dispose();
    }
}

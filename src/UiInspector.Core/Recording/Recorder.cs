using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.EventHandlers;
using FlaUI.Core.Exceptions;
using UiInspector.Core.Reporting;
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
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan HitTestSettleTime = TimeSpan.FromMilliseconds(20);

    private readonly InspectionService _service;
    private readonly ElementInspector _inspector;
    private readonly RecorderOptions _options;
    private readonly UiaWorker _snapshots = new(new UiaWorkerOptions { OperationTimeout = TimeSpan.FromSeconds(3) });

    // Only resolves input at the moment it happens (hit-test, tree snapshot), so it never waits behind other work.
    private readonly UiaWorker _atInput = new(new UiaWorkerOptions { OperationTimeout = TimeSpan.FromSeconds(3) });
    private readonly Lock _gate = new();
    private readonly Dictionary<int, TreeSnapshot> _clickTrees = [];
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

    /// <summary>Take a screenshot of the app's window for each step (used by bug reports).</summary>
    public bool CaptureScreenshots { get; set; } = true;

    /// <summary>When the first recording started (since the last Clear).</summary>
    public DateTime? StartedAt { get; private set; }

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
            _input.Start(e => _events.Writer.TryWrite(AtInputTime(e)));
        }
        catch
        {
            _input?.Dispose();
            _input = null;
            await UnsubscribeFocusAsync();
            throw;
        }

        IsRecording = true;
        StartedAt ??= DateTime.Now;
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
        lock (_gate)
        {
            _builder.Remove(stepId);
            _clickTrees.Remove(stepId);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _builder.Clear();
            _clickTrees.Clear();
        }
        StartedAt = IsRecording ? DateTime.Now : null;
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
        ClickAtInput c => OnMouseDownAsync(c),
        KeyAtInput k => OnKeyAsync(k.Key, k.Resolved),
        RawMouse { Down: true } m => OnMouseDownAsync(new ClickAtInput(m, ResolveAsync(a => ResolveClick(a, m)))),
        RawMouse m => OnMouseUp(m),
        RawTyping => OnTyping(),
        RawKey k => OnKeyAsync(k, null),
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
        lock (_gate) dragged = _builder.MouseUp(m.Position, m.Button);
        if (dragged)
        {
            Notice?.Invoke("Drags aren't recorded (only clicks), so that one was left out.");
            Changed?.Invoke();
        }
        return Task.CompletedTask;
    }

    private async Task OnMouseDownAsync(ClickAtInput click)
    {
        var m = click.Mouse;
        var at = await click.Resolved; // the element, tree and screenshot captured when the button went down
        var hit = at.Hit;
        var tree = at.Tree;
        var path = at.Path;
        var image = at.Image;

        if (hit is { WebContentPending: true })
        {
            // Chromium builds its page tree lazily on the first UIA request; this click woke it.
            hit = await InspectionService.WaitForWebContentAsync(
                ct => _snapshots.RunAsync(a => _inspector.InspectAtPoint(a, m.Position), ct), WebWait, null, _lifetime.Token);
            if (hit is { WebContentPending: true }) Notice?.Invoke("The web page hadn't exposed its content yet, so that click wasn't recorded. Click again.");
            path = hit is { WebContentPending: false } ? ClickPath(hit) : null;
            (tree, image) = (null, null);
        }

        var entries = _text.Update(at.Focused, await TrackedTextAsync(at.Focused));
        var (textShots, shot) = await ScreenshotsAsync(entries, path, image);
        Apply(b =>
        {
            var ids = AddTexts(b, entries, textShots);
            if (path is not null) ids.Add(Remember(b.AddClick(path, m.Button, m.Position, m.Time, shot), tree));
            else b.IgnoreDown();
            return ids;
        }, path?[^1]);
    }

    /// <summary>
    /// The tracked field's text: as read when the button or key went down if that was the focused
    /// field (the app may have moved focus since), otherwise read now.
    /// </summary>
    private async Task<string?> TrackedTextAsync(FieldState? focusedAtInput)
    {
        var tracked = _text.Field;
        if (tracked is null) return null;
        if (focusedAtInput is not null && focusedAtInput.Element.IsSameElement(tracked.Element)) return focusedAtInput.Text;
        return await _snapshots.RunAsync(_ => ReadText(tracked), _lifetime.Token);
    }

    // ---------------------------------------------------------------- captured at the moment of input

    /// <summary>What the UI looked like the moment a button or key went down.</summary>
    private sealed record AtInput(InspectionResult? Hit, IReadOnlyList<ElementInfo>? Path, TreeSnapshot? Tree, FieldState? Focused, WindowImage? Image);

    private sealed record ClickAtInput(RawMouse Mouse, Task<AtInput> Resolved);

    private sealed record KeyAtInput(RawKey Key, Task<AtInput> Resolved);

    /// <summary>
    /// Runs on the hook thread for every event, so it only starts work. A click (or shortcut key) is
    /// resolved right away on a worker of its own, so it never waits behind the previous step's
    /// screenshots and checks; by then the app may have closed the popup that was clicked.
    /// </summary>
    private object AtInputTime(RawInput input) => input switch
    {
        RawMouse { Down: true } m => new ClickAtInput(m, ResolveAsync(a => ResolveClick(a, m))),
        RawKey { Kind: KeyKind.Step } k => new KeyAtInput(k, ResolveAsync(ResolveKey)),
        _ => input,
    };

    private async Task<AtInput> ResolveAsync(Func<AutomationBase, AtInput> resolve)
    {
        var at = await _atInput.RunAsync(resolve, _lifetime.Token);
        if (at.Path is null || !CaptureScreenshots) return at;
        var handle = WindowHandle(at.Path);
        return handle == IntPtr.Zero ? at : at with { Image = await CaptureAsync(handle) };
    }

    private AtInput ResolveClick(AutomationBase automation, RawMouse m)
    {
        var hit = HitTest(automation, m.Position);
        var path = hit is { WebContentPending: false } ? ClickPath(hit) : null;
        // The window's tree as it is now, before the app reacts to the click (it may close a popup).
        var tree = path is null ? null : TryCaptureTree(automation, path);
        return new AtInput(hit, path, tree, FocusedField(automation), null);
    }

    /// <summary>
    /// Chromium answers a hit-test at once from its cache (the last precise result, if it contains
    /// the point: often a container as big as the page) and works out the precise element in the
    /// background, in about 15 ms. The next question gets that answer, which describes the page as
    /// it was at the first question. So: ask, wait 20 ms, ask again, and use the second answer. That
    /// has to happen before the button comes up and the app changes the page (~80 ms on a fast
    /// click), so it's done before anything else.
    /// </summary>
    private InspectionResult? HitTest(AutomationBase automation, Point point)
    {
        var quick = automation.FromPoint(point);
        if (quick?.Properties.FrameworkId.ValueOrDefault != "Chrome") return _inspector.InspectAtPoint(automation, point);

        Thread.Sleep(HitTestSettleTime);
        var hit = _inspector.InspectAtPoint(automation, point);
        if (hit is not null && IsBigContainer(hit))
        {
            // Rarely the renderer is slower; one more short wait, still before a fast click ends.
            Thread.Sleep(HitTestSettleTime);
            hit = _inspector.InspectAtPoint(automation, point) ?? hit;
        }
        return hit;
    }

    // An unnamed element without actions that covers much of its window: what a cache guess looks like.
    private static bool IsBigContainer(InspectionResult hit)
    {
        var e = hit.Element;
        if (e.Name.Trim().Length > 0 || e.Patterns.Any(p => p is "Invoke" or "Toggle" or "SelectionItem" or "Value")) return false;
        var window = hit.Ancestors.Count >= 2 ? hit.Ancestors[^2].BoundingRectangle : e.BoundingRectangle;
        long area = (long)e.BoundingRectangle.Width * e.BoundingRectangle.Height;
        long windowArea = (long)window.Width * window.Height;
        return windowArea > 0 && area * 4 >= windowArea;
    }

    private AtInput ResolveKey(AutomationBase automation)
    {
        var element = FocusedElement(automation);
        var inspection = element is null ? null : _inspector.InspectElement(automation, element);
        var path = inspection is null || inspection.Element.ProcessId == OwnProcessId || inspection.AncestorsTruncated ? null : Chain(inspection);
        if (path is { Count: < 2 }) path = null;
        // Keys like Escape close popups too; keep the tree as it was when the key went down.
        var tree = path is null ? null : TryCaptureTree(automation, path);
        return new AtInput(inspection, path, tree, inspection is null ? null : Field(inspection), null);
    }

    private static IntPtr WindowHandle(IReadOnlyList<ElementInfo> path) =>
        path.Skip(1).Select(e => e.NativeWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);

    private async Task OnElementClickAsync(ElementClick c)
    {
        var tracked = _text.Field;
        var (hit, tree, focused, trackedText) = await _snapshots.RunAsync(a =>
        {
            var hit = _inspector.InspectElement(a, c.Element);
            var tree = hit is not null && ClickPath(hit) is { } clicked ? TryCaptureTree(a, clicked) : null;
            return (hit, tree, FocusedField(a), ReadText(tracked));
        }, _lifetime.Token);

        var entries = _text.Update(focused, trackedText);
        var path = hit is null ? null : ClickPath(hit);
        var center = hit is null ? Point.Empty : new Point(
            hit.Element.BoundingRectangle.X + hit.Element.BoundingRectangle.Width / 2,
            hit.Element.BoundingRectangle.Y + hit.Element.BoundingRectangle.Height / 2);
        var (textShots, shot) = await ScreenshotsAsync(entries, path);
        Apply(b =>
        {
            var ids = AddTexts(b, entries, textShots);
            if (path is not null) ids.Add(Remember(b.AddClick(path, c.Button, center, c.Time, shot), tree));
            else b.IgnoreDown();
            return ids;
        }, path?[^1]);
    }

    private async Task OnKeyAsync(RawKey k, Task<AtInput>? early)
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

        // Shortcut keys were resolved when they went down; other keys are resolved now.
        var at = early is not null ? await early : await _snapshots.RunAsync(ResolveKey, _lifetime.Token);
        var tree = at.Tree;
        var path = at.Path;
        var entries = _text.Update(at.Focused, await TrackedTextAsync(at.Focused));
        var (textShots, shot) = await ScreenshotsAsync(entries, path, at.Image);
        Apply(b =>
        {
            var ids = AddTexts(b, entries, textShots);
            if (path is not null) ids.Add(Remember(b.AddKey(path, k.Chord, shot), tree));
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
        var (textShots, _) = await ScreenshotsAsync(entries, null);
        Apply(b => AddTexts(b, entries, textShots), null);
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
        var (textShots, shot) = await ScreenshotsAsync(entries, path);
        Apply(b =>
        {
            var ids = AddTexts(b, entries, textShots);
            if (path is not null) ids.Add(b.AddCheck(path, text, source, shot));
            return ids;
        }, path?[^1]);
    }

    private async Task OnFlushAsync()
    {
        var tracked = _text.Field;
        var (focused, trackedText) = await _snapshots.RunAsync(a => (FocusedField(a), ReadText(tracked)), _lifetime.Token);
        var entries = _text.Update(focused, trackedText);
        var (textShots, _) = await ScreenshotsAsync(entries, null);
        Apply(b => AddTexts(b, entries, textShots), null);
    }

    /// <summary>Keeps the tree taken at the click for the step's selector check (under the lock).</summary>
    private int? Remember(int? stepId, TreeSnapshot? tree)
    {
        if (stepId is { } id && tree is not null) _clickTrees[id] = tree;
        return stepId;
    }

    private static TreeSnapshot? TryCaptureTree(AutomationBase automation, IReadOnlyList<ElementInfo> path)
    {
        try
        {
            return TreeSnapshot.Capture(automation, path);
        }
        catch (Exception ex) when (ex is COMException or FlaUIException or InvalidOperationException)
        {
            return null; // the selector is then checked live, as before
        }
    }

    private static List<int?> AddTexts(StepBuilder builder, IReadOnlyList<TextEntry> entries, IReadOnlyList<StepScreenshot?> shots) =>
        entries.Select((entry, i) => builder.AddText(entry, shots[i])).ToList();

    // ---------------------------------------------------------------- screenshots

    /// <summary>
    /// Screenshots for the steps one event produces: each window is captured once, then every
    /// step gets its own element outlined. Empty when screenshots are switched off.
    /// </summary>
    private async Task<(IReadOnlyList<StepScreenshot?> Texts, StepScreenshot? Action)> ScreenshotsAsync(
        IReadOnlyList<TextEntry> entries, IReadOnlyList<ElementInfo>? actionPath, WindowImage? capturedAtInput = null)
    {
        if (!CaptureScreenshots || (entries.Count == 0 && actionPath is null)) return (entries.Select(_ => (StepScreenshot?)null).ToList(), null);

        var windows = new Dictionary<IntPtr, WindowImage?>();
        if (actionPath is not null && capturedAtInput is not null) windows[WindowHandle(actionPath)] = capturedAtInput;
        async Task<StepScreenshot?> ShotAsync(IReadOnlyList<ElementInfo> path)
        {
            var handle = path.Skip(1).Select(e => e.NativeWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
            if (handle == IntPtr.Zero) return null;
            if (!windows.TryGetValue(handle, out var image)) windows[handle] = image = await CaptureAsync(handle);
            return image is null ? null : WindowCapture.Mark(image, path[^1].BoundingRectangle);
        }

        var texts = new List<StepScreenshot?>();
        foreach (var entry in entries) texts.Add(await ShotAsync(entry.Field.Path));
        return (texts, actionPath is null ? null : await ShotAsync(actionPath));
    }

    // PrintWindow waits for the app to draw itself; a hung app must not stall recording.
    private static async Task<WindowImage?> CaptureAsync(IntPtr window)
    {
        try
        {
            return await Task.Run(() => WindowCapture.CaptureWindow(window)).WaitAsync(CaptureTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or ExternalException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
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
                TreeSnapshot? tree;
                lock (_gate)
                {
                    path = _builder.Find(id)?.Path;
                    _clickTrees.Remove(id, out tree);
                }
                if (path is null) continue; // removed meanwhile

                SelectorResult? result = null;
                string? problem;
                try
                {
                    // Checked against the UI as it was at the click, so a popup the click closed still counts.
                    result = await _service.GenerateSelectorAsync(path, _lifetime.Token, includeWeb: false, snapshot: tree);
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
                var web = await WebLocatorsAsync(path, result, fallback, tree);
                lock (_gate) _builder.SetSelector(id, result, fallback, problem, web);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Web locators for a step in a Chromium page. Over a debug port the element is found again in
    /// the page by its position; if the page changed since the click, that finds another element,
    /// which shows as a different name. Then the locators built from the accessibility tree are used.
    /// </summary>
    private async Task<Web.WebResult?> WebLocatorsAsync(IReadOnlyList<ElementInfo> path, SelectorResult? result, Selector? fallback, TreeSnapshot? tree)
    {
        if (path[^1].FrameworkId != "Chrome") return null;
        var native = result?.Best is not null ? result : result is not null && fallback is not null ? result with { Best = fallback } : null;
        if (native is null) return null;
        try
        {
            var web = await _service.GenerateWebAsync(path, native, _lifetime.Token, tree);
            if (web is { Source: Web.WebSource.DevTools } && web.Notes.Any(n => n.Contains("differs from the UIA name", StringComparison.Ordinal)))
            {
                var pageUrl = web.PageUrl.Length > 0 ? web.PageUrl : null;
                return Web.AccessibilityLocators.FromSelector(native, path, pageUrl, web.Launch);
            }
            return web;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Web.WebResult.Unavailable(FailureMessages.Describe(ex, null));
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
        _atInput.Dispose();
        _lifetime.Dispose();
    }
}

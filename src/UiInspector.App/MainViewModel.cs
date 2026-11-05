using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UiInspector.Core;
using UiInspector.Core.Selectors;
using DrawingPoint = System.Drawing.Point;

namespace UiInspector.App;

public sealed record PropertyRow(string Name, string Value);

/// <summary>One selector the user can choose in the Selector tab (the best one or an alternative).</summary>
public sealed record SelectorChoice(Selector Selector, string Label);

/// <summary>
/// State and behaviour of the inspector window: picking (live Ctrl-hover and hotkeys), smart
/// target, the element tree, the details panel and the on-screen highlight.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    // Ctrl must be held this long before hover preview starts, and this long to commit a pick,
    // so ordinary shortcuts like Ctrl+C in other apps don't hijack the selection.
    private static readonly TimeSpan HoverPreviewDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CommitHoldTime = TimeSpan.FromMilliseconds(400);
    private static readonly int OwnProcessId = Environment.ProcessId;

    private readonly InspectionService _service;
    private readonly IHighlighter _highlighter;

    private DateTime? _ctrlDownSince;
    private DrawingPoint? _lastHoverPoint;
    private bool _hoverBusy;
    private InspectionResult? _hoverResult;

    public MainViewModel(InspectionService service, IHighlighter highlighter)
    {
        _service = service;
        _highlighter = highlighter;
    }

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = [];

    public ObservableCollection<PropertyRow> Properties { get; } = [];

    [ObservableProperty]
    private TreeNodeViewModel? _selectedNode;

    [ObservableProperty]
    private string _detailTitle = "Nothing selected";

    [ObservableProperty]
    private string _detailSubtitle = "Hold Ctrl and hover over any app, then release to pick an element.";

    /// <summary>True while the details panel shows a live hover preview rather than the selection.</summary>
    [ObservableProperty]
    private bool _isPreview;

    [ObservableProperty]
    private string _status = "Ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBanner))]
    private string? _bannerText;

    [ObservableProperty]
    private bool _bannerIsWarning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectPickedElementCommand))]
    private bool _canSelectPickedElement;

    [ObservableProperty]
    private bool _livePickEnabled = true;

    [ObservableProperty]
    private bool _smartTargetEnabled = true;

    [ObservableProperty]
    private bool _highlightEnabled = true;

    [ObservableProperty]
    private string _delayedCaptureText = "Delayed capture (3 s)";

    public bool HasBanner => !string.IsNullOrEmpty(BannerText);

    // ---------------------------------------------------------------- selector tab state

    private SelectorResult? _selectorResult;
    private CancellationTokenSource? _selectorCts;
    private string _preferredSnippetId = "flaui";

    public ObservableCollection<SelectorChoice> SelectorChoices { get; } = [];

    public ObservableCollection<string> SelectorWarnings { get; } = [];

    public ObservableCollection<CodeSnippet> Snippets { get; } = [];

    [ObservableProperty]
    private SelectorChoice? _selectedChoice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCodeCommand))]
    private CodeSnippet? _selectedSnippet;

    [ObservableProperty]
    private string _selectorHeadline = "Pick an element to generate a selector.";

    /// <summary>"none", "busy", "ok" or "bad": drives the headline colour.</summary>
    [ObservableProperty]
    private string _selectorTone = "none";

    [ObservableProperty]
    private string _selectorSteps = "";

    [ObservableProperty]
    private string _occlusionText = "";

    [ObservableProperty]
    private bool _occlusionIsWarning;

    [ObservableProperty]
    private bool _hasAlternatives;

    // ---------------------------------------------------------------- picking

    /// <summary>Called by the window's timer every ~50 ms with the live keyboard and cursor state.</summary>
    public async Task PickTickAsync(bool ctrlDown, DrawingPoint cursor)
    {
        if (!LivePickEnabled)
        {
            ResetHover();
            return;
        }

        var now = DateTime.UtcNow;
        if (ctrlDown)
        {
            _ctrlDownSince ??= now;
            if (now - _ctrlDownSince < HoverPreviewDelay || _hoverBusy || cursor == _lastHoverPoint) return;

            _lastHoverPoint = cursor;
            _hoverBusy = true;
            try
            {
                var result = await _service.InspectAtPointAsync(cursor, includeAncestors: true, waitForWebContent: false);
                if (result is null || IsOwnWindow(result) || !Native.IsKeyDown(Native.VK_CONTROL)) return;
                _hoverResult = result;
                ShowPreview(result);
            }
            catch (Exception ex)
            {
                Status = FailureMessages.Describe(ex, cursor);
            }
            finally
            {
                _hoverBusy = false;
            }
            return;
        }

        if (_ctrlDownSince is not { } since) return;

        var held = now - since;
        var picked = _hoverResult;
        ResetHover();

        if (picked is not null && held >= CommitHoldTime)
        {
            // Hover never waits for Chromium's lazy tree; the committed pick does.
            if (picked.WebContentPending && picked.ScreenPoint is { } point)
            {
                await CaptureAtPointAsync(point);
            }
            else
            {
                Commit(picked);
            }
        }
        else if (picked is not null)
        {
            RestoreSelectionView(); // Ctrl was just a shortcut, not a pick
        }
    }

    public async Task HandleHotkeyAsync(HotkeyCommand command)
    {
        switch (command)
        {
            case HotkeyCommand.InspectAtCursor:
                await CaptureAtPointAsync(ScreenInfo.GetCursorPosition());
                break;
            case HotkeyCommand.InspectFocused:
                await InspectFocusedAsync();
                break;
            case HotkeyCommand.InspectDelayed:
                await DelayedCaptureAsync();
                break;
            case HotkeyCommand.ToggleHover:
                LivePickEnabled = !LivePickEnabled;
                Status = LivePickEnabled ? "Live pick on: hold Ctrl and hover." : "Live pick off.";
                break;
        }
    }

    [RelayCommand]
    private async Task InspectFocusedAsync()
    {
        try
        {
            var result = await _service.InspectFocusedAsync(onWaiting: OnWaitingForWebContent);
            if (result is null) Status = "No focused element found.";
            else if (IsOwnWindow(result)) Status = "The inspector itself has focus. Use Ctrl+Shift+F10 while the target app is active.";
            else Commit(result);
        }
        catch (Exception ex)
        {
            Status = FailureMessages.Describe(ex, null);
        }
    }

    [RelayCommand]
    private async Task DelayedCaptureAsync()
    {
        for (var remaining = 3; remaining > 0; remaining--)
        {
            DelayedCaptureText = $"Capturing in {remaining}…";
            Status = $"Delayed capture: put the cursor on the target (open the menu or tooltip now)… {remaining}";
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        DelayedCaptureText = "Delayed capture (3 s)";
        await CaptureAtPointAsync(ScreenInfo.GetCursorPosition());
    }

    private async Task CaptureAtPointAsync(DrawingPoint point)
    {
        try
        {
            var result = await _service.InspectAtPointAsync(point, onWaiting: OnWaitingForWebContent);
            if (result is null) Status = "No element found under the cursor.";
            else if (IsOwnWindow(result)) Status = "That's the inspector's own window. Point at another app.";
            else Commit(result);
        }
        catch (Exception ex)
        {
            Status = FailureMessages.Describe(ex, point);
        }
    }

    private void OnWaitingForWebContent() => Status = "Web page is still building its accessibility tree, waiting…";

    private static bool IsOwnWindow(InspectionResult result) => result.Element.ProcessId == OwnProcessId;

    private void ResetHover()
    {
        _ctrlDownSince = null;
        _lastHoverPoint = null;
        _hoverResult = null;
    }

    // ---------------------------------------------------------------- committing a pick

    private void Commit(InspectionResult result)
    {
        // Root first: desktop … window … hit element.
        var chain = result.Ancestors.Reverse().Append(result.Element).ToList();
        var smartIndex = SmartTargetEnabled ? SmartTarget.FindIndex(result) : 0;
        var targetIndex = chain.Count - 1 - smartIndex;

        var pathNodes = new List<TreeNodeViewModel>(chain.Count);
        TreeNodeViewModel? parent = null;
        foreach (var info in chain)
        {
            var node = new TreeNodeViewModel(info, LoadChildrenAsync, parent) { IsOnPath = true };
            parent?.SetPathChild(node);
            pathNodes.Add(node);
            parent = node;
        }
        pathNodes[^1].IsHitElement = true;

        RootNodes.Clear();
        RootNodes.Add(pathNodes[0]);

        // Expanding a path node loads its real children (siblings of the next path node) while
        // keeping that path node in place. The loads queue on the UIA worker top-down.
        foreach (var node in pathNodes.Take(pathNodes.Count - 1)) node.IsExpanded = true;

        Select(pathNodes[targetIndex]);

        if (smartIndex > 0)
        {
            SetBanner($"You pointed at {Format.Describe(result.Element)}. Selected its clickable ancestor " +
                      $"{Format.Describe(chain[targetIndex])} instead, since that's what a test would interact with.",
                      warning: false);
            CanSelectPickedElement = true;
        }
        else if (result.WebContentPending)
        {
            var app = Format.Or(result.Element.ProcessName, "the app");
            SetBanner($"This is an empty web page root: {app} hasn't exposed its page content to UI Automation. " +
                      $"Restart {app} with the --force-renderer-accessibility switch, then pick again.",
                      warning: true);
        }
        else
        {
            SetBanner(null, warning: false);
        }

        var dpi = result.ScreenPoint is { } p ? $" · {ScreenInfo.GetDpiAt(p)} DPI" : "";
        Status = $"Picked {Format.Describe(chain[targetIndex])} in {result.Duration.TotalMilliseconds:0} ms{dpi}";
    }

    private Task<ChildrenResult> LoadChildrenAsync(ElementInfo parent) => _service.GetChildrenAsync(parent);

    /// <summary>Tree pick: show every top-level window, for elements the mouse can't reach (hidden, off-screen, covered).</summary>
    [RelayCommand]
    private async Task BrowseWindowsAsync()
    {
        try
        {
            var desktop = await _service.GetDesktopAsync();
            var root = new TreeNodeViewModel(desktop, LoadChildrenAsync, null);
            RootNodes.Clear();
            RootNodes.Add(root);
            SetBanner(null, warning: false);
            root.IsExpanded = true;
            Select(root);
            Status = "Browsing all windows. Expand a window to find elements, including hidden or off-screen ones.";
        }
        catch (Exception ex)
        {
            Status = FailureMessages.Describe(ex, null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectPickedElement))]
    private void SelectPickedElement()
    {
        var node = RootNodes.FirstOrDefault();
        while (node is not null && !node.IsHitElement)
        {
            node = node.Children.FirstOrDefault(c => c.IsOnPath);
        }
        if (node is null) return;

        Select(node);
        SetBanner(null, warning: false);
        CanSelectPickedElement = false;
    }

    // ---------------------------------------------------------------- selection & details

    private void Select(TreeNodeViewModel node)
    {
        if (SelectedNode is { } previous && !ReferenceEquals(previous, node)) previous.IsSelected = false;
        node.IsSelected = true;
        SelectedNode = node;
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value is null || value.IsPlaceholder) return;
        ShowDetails(value.Info, preview: false);
        _ = GenerateSelectorAsync(value);
    }

    // ---------------------------------------------------------------- selector generation

    private async Task GenerateSelectorAsync(TreeNodeViewModel node)
    {
        _selectorCts?.Cancel();
        var cts = _selectorCts = new CancellationTokenSource();

        _selectorResult = null;
        SelectorChoices.Clear();
        SelectorWarnings.Clear();
        Snippets.Clear();
        SelectorSteps = "";
        OcclusionText = "";
        HasAlternatives = false;
        SelectorTone = "busy";
        SelectorHeadline = "Generating a selector and verifying it against the live app…";

        try
        {
            var result = await _service.GenerateSelectorAsync(PathFromRoot(node), cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowSelectorResult(result);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            SelectorTone = "bad";
            SelectorHeadline = $"Selector generation failed: {FailureMessages.Describe(ex, null)}";
        }
    }

    private void ShowSelectorResult(SelectorResult result)
    {
        _selectorResult = result;
        if (result.Occlusion is { } occlusion)
        {
            OcclusionText = $"Click check: {occlusion.Message}";
            OcclusionIsWarning = occlusion.IsCovered;
        }

        if (result.Best is not { } best)
        {
            SelectorTone = "bad";
            SelectorHeadline = result.Problem ?? "No selector could be verified for this element.";
            return;
        }

        foreach (var selector in result.Alternatives.Prepend(best))
        {
            SelectorChoices.Add(new SelectorChoice(selector, $"{selector.Strategy}  ·  {selector.Verification.Status}"));
        }
        HasAlternatives = SelectorChoices.Count > 1;
        SelectedChoice = SelectorChoices[0];
    }

    partial void OnSelectedChoiceChanged(SelectorChoice? value)
    {
        if (value is null || _selectorResult is null) return;
        var selector = value.Selector;

        var unique = selector.Verification.Status == VerificationStatus.Unique;
        SelectorTone = unique ? "ok" : "bad";
        SelectorHeadline = $"{(unique ? "✔" : "✖")} {selector.Verification.Describe()}  Strategy: {selector.Strategy}";

        var lines = new List<string> { $"Window   {selector.Window}" };
        for (var i = 0; i < selector.Steps.Count; i++)
        {
            lines.Add($"{(i == selector.Steps.Count - 1 ? "Target" : "Inside"),-8} {selector.Steps[i]}");
        }
        SelectorSteps = string.Join(Environment.NewLine, lines);

        SelectorWarnings.Clear();
        foreach (var warning in selector.Warnings) SelectorWarnings.Add(warning);

        var keep = _preferredSnippetId;
        Snippets.Clear();
        foreach (var snippet in CodeTemplates.Render(selector, _selectorResult)) Snippets.Add(snippet);
        SelectedSnippet = Snippets.FirstOrDefault(s => s.Id == keep) ?? Snippets.FirstOrDefault();
    }

    partial void OnSelectedSnippetChanged(CodeSnippet? value)
    {
        if (value is not null) _preferredSnippetId = value.Id;
    }

    private bool CanCopyCode() => SelectedSnippet is not null;

    [RelayCommand(CanExecute = nameof(CanCopyCode))]
    private void CopyCode()
    {
        if (SelectedSnippet is not { } snippet) return;
        try
        {
            Clipboard.SetText(snippet.Code);
            Status = $"Copied the {snippet.Title} code to the clipboard.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "The clipboard is busy (another app has it open). Try again.";
        }
    }

    private static List<ElementInfo> PathFromRoot(TreeNodeViewModel node)
    {
        var path = new List<ElementInfo>();
        for (var n = node; n is not null; n = n.Parent) path.Insert(0, n.Info);
        return path;
    }

    partial void OnHighlightEnabledChanged(bool value)
    {
        if (!value) _highlighter.Hide();
        else if (SelectedNode is { IsPlaceholder: false } node) Highlight(node.Info, preview: false);
    }

    private void ShowPreview(InspectionResult result)
    {
        var target = SmartTargetEnabled ? SmartTarget.Find(result) : result.Element;
        ShowDetails(target, preview: true);
        Status = ReferenceEquals(target, result.Element)
            ? $"Release Ctrl to pick {Format.Describe(target)}"
            : $"Release Ctrl to pick {Format.Describe(target)} (smart target; you're over {Format.Describe(result.Element)})";
    }

    private void RestoreSelectionView()
    {
        if (SelectedNode is { IsPlaceholder: false } node) ShowDetails(node.Info, preview: false);
        else
        {
            _highlighter.Hide();
            IsPreview = false;
        }
    }

    private void ShowDetails(ElementInfo info, bool preview)
    {
        IsPreview = preview;
        DetailTitle = Format.Describe(info);
        DetailSubtitle = $"{Format.Or(info.FrameworkId, "?")} · {Format.Or(info.ProcessName, "?")} (pid {info.ProcessId})" +
                         (preview ? " · preview, release Ctrl to pick" : "");
        Properties.Clear();
        foreach (var (name, value) in Format.Properties(info)) Properties.Add(new PropertyRow(name, value));
        Highlight(info, preview);
    }

    private void Highlight(ElementInfo info, bool preview)
    {
        if (!HighlightEnabled) return;
        _highlighter.Show(info.BoundingRectangle, Format.Clean(Format.Describe(info), 60), preview);
    }

    private void SetBanner(string? text, bool warning)
    {
        BannerText = text;
        BannerIsWarning = warning;
        if (text is null) CanSelectPickedElement = false;
    }

    // ---------------------------------------------------------------- clipboard

    [RelayCommand]
    private void CopyDetails()
    {
        if (SelectedNode is not { IsPlaceholder: false } node)
        {
            Status = "Nothing selected to copy.";
            return;
        }

        try
        {
            Clipboard.SetText(Format.Report(node.Info, PathFromRoot(node)));
            Status = $"Copied details of {Format.Describe(node.Info)} to the clipboard.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "The clipboard is busy (another app has it open). Try again.";
        }
    }
}

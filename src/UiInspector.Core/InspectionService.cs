using System.Diagnostics;
using System.Drawing;
using UiInspector.Core.Selectors;
using UiInspector.Core.Web;

namespace UiInspector.Core;

public sealed class WebContentWaitOptions
{
    /// <summary>How long to keep retrying while a Chromium page reports no content.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>
/// High-level inspection API for front ends (CLI now, GUI later). Runs inspections on the
/// <see cref="UiaWorker"/> and hides Chromium's lazy accessibility start-up: if the first
/// hit is an empty page root, it retries until real content appears or the wait times out.
/// </summary>
public sealed class InspectionService(UiaWorker worker, ElementInspector inspector, WebContentWaitOptions? waitOptions = null)
{
    // Selector generation runs several tree searches; on large apps (Discord, VS Code) that can
    // take a few seconds, well beyond a single inspection.
    private static readonly TimeSpan SelectorTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan WebTimeout = TimeSpan.FromSeconds(15);

    private readonly WebContentWaitOptions _waitOptions = waitOptions ?? new WebContentWaitOptions();
    private readonly SelectorGenerator _selectorGenerator = new(inspector);
    private readonly WebBridge _webBridge = new();

    /// <summary>Chromium remote-debugging port to use; null detects it from the app's command line.</summary>
    public int? CdpPort { get; set; }

    /// <param name="onWaiting">Called once if the service starts waiting for web content.</param>
    public Task<InspectionResult?> InspectAtPointAsync(
        Point screenPoint,
        bool includeAncestors = true,
        bool waitForWebContent = true,
        Action? onWaiting = null,
        CancellationToken cancellationToken = default)
    {
        Task<InspectionResult?> Attempt(CancellationToken ct) =>
            worker.RunAsync(a => inspector.InspectAtPoint(a, screenPoint, includeAncestors), ct);

        return waitForWebContent
            ? WaitForWebContentAsync(Attempt, _waitOptions, onWaiting, cancellationToken)
            : Attempt(cancellationToken);
    }

    /// <param name="onWaiting">Called once if the service starts waiting for web content.</param>
    public Task<InspectionResult?> InspectFocusedAsync(
        bool includeAncestors = true,
        Action? onWaiting = null,
        CancellationToken cancellationToken = default)
    {
        return WaitForWebContentAsync(
            ct => worker.RunAsync(a => inspector.InspectFocused(a, includeAncestors), ct),
            _waitOptions, onWaiting, cancellationToken);
    }

    /// <param name="pathFromRoot">Desktop first, then the top-level window, …, the element last.</param>
    /// <param name="includeWeb">Also read Chromium pages over CDP for Playwright/Selenium locators.</param>
    /// <param name="snapshot">Verify against this snapshot (taken when the user clicked) instead of the live UI.</param>
    public async Task<SelectorResult> GenerateSelectorAsync(
        IReadOnlyList<ElementInfo> pathFromRoot, CancellationToken cancellationToken = default, bool includeWeb = true, IElementTree? snapshot = null)
    {
        var result = await worker.RunAsync(a => _selectorGenerator.Generate(a, pathFromRoot, snapshot), cancellationToken, SelectorTimeout);
        if (!includeWeb || !WebApps.IsWebContent(pathFromRoot)) return result;
        return result with { Web = await GenerateWebAsync(pathFromRoot, result, cancellationToken, snapshot) };
    }

    /// <summary>
    /// Playwright/Selenium locators for an element in a Chromium page: read from the page itself
    /// when the app has a debug port, otherwise (or if that fails) built from the accessibility
    /// tree that UI Automation already sees.
    /// </summary>
    public async Task<WebResult> GenerateWebAsync(IReadOnlyList<ElementInfo> pathFromRoot, SelectorResult native, CancellationToken cancellationToken = default, IElementTree? snapshot = null)
    {
        var launch = WebApps.Describe(pathFromRoot);
        string? devToolsProblem = null;

        // Firefox has no Chromium debug port; its locators always come from the accessibility tree.
        var port = pathFromRoot[^1].FrameworkId != "Chrome" ? null : CdpPort ?? TryFindPort(pathFromRoot[^1].ProcessId);
        if (port is not null)
        {
            // The web side talks to the page over CDP, not UIA, so it runs off the UIA worker.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(WebTimeout);
            try
            {
                var web = await _webBridge.InspectAsync(pathFromRoot, port, timeout.Token);
                if (web.Connected) return web with { Launch = launch };
                devToolsProblem = web.Problem;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                devToolsProblem = $"The page didn't answer on port {port} within {WebTimeout.TotalSeconds:0} s.";
            }
            catch (Exception ex) when (ex is CdpException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException
                                           or KeyNotFoundException or InvalidOperationException)
            {
                devToolsProblem = $"Reading the page over port {port} failed: {ex.Message}";
            }
        }

        var pageUrl = await worker.RunAsync(_ => WebApps.ReadPageUrl(pathFromRoot), cancellationToken);
        var fromTree = AccessibilityLocators.FromSelector(native, pathFromRoot, pageUrl, launch);
        fromTree = await worker.RunAsync(a => AccessibilityLocators.WithPrecedingLabel(fromTree, pathFromRoot, snapshot ?? new LiveTree(a)), cancellationToken);
        fromTree = await worker.RunAsync(a => AccessibilityLocators.WithInnerText(fromTree, pathFromRoot, pageUrl, launch, snapshot ?? new LiveTree(a)), cancellationToken);
        return devToolsProblem is null || !fromTree.Connected ? fromTree : fromTree with { Notes = [.. fromTree.Notes, devToolsProblem] };
    }

    private static int? TryFindPort(int processId)
    {
        try
        {
            return DebugPort.TryFind(processId);
        }
        catch (System.Management.ManagementException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the element is now, scrolled into view first if it's out of sight (a field further down
    /// a page): for outlining it. Null if it's gone (the page changed or the window closed).
    /// </summary>
    public Task<System.Drawing.Rectangle?> RevealAsync(ElementInfo element, CancellationToken cancellationToken = default) =>
        worker.RunAsync<System.Drawing.Rectangle?>(_ =>
        {
            if (element.Ref is null) return null;
            try
            {
                var live = element.Ref.Element;
                if (live.Properties.IsOffscreen.ValueOrDefault) live.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
                return live.Properties.BoundingRectangle.TryGetValue(out var bounds) && bounds is { Width: > 0, Height: > 0 } ? bounds : null;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.FlaUIException)
            {
                return null;
            }
        }, cancellationToken);

    public Task<ElementInfo> GetDesktopAsync(CancellationToken cancellationToken = default) =>
        worker.RunAsync(inspector.ReadDesktop, cancellationToken);

    public Task<ChildrenResult> GetChildrenAsync(ElementInfo parent, CancellationToken cancellationToken = default)
    {
        var handle = parent.Ref ?? throw new ArgumentException("The element has no live UIA handle.", nameof(parent));
        return worker.RunAsync(a => inspector.GetChildren(a, handle), cancellationToken);
    }

    /// <summary>
    /// Repeats <paramref name="attempt"/> while it returns a result with
    /// <see cref="InspectionResult.WebContentPending"/>, up to the configured timeout.
    /// Returns the last result; if it is still pending, the caller should explain why.
    /// </summary>
    public static async Task<InspectionResult?> WaitForWebContentAsync(
        Func<CancellationToken, Task<InspectionResult?>> attempt,
        WebContentWaitOptions options,
        Action? onWaiting,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await attempt(cancellationToken);
        var notified = false;

        while (result is { WebContentPending: true } && stopwatch.Elapsed < options.Timeout)
        {
            if (!notified)
            {
                onWaiting?.Invoke();
                notified = true;
            }
            await Task.Delay(options.PollInterval, cancellationToken);
            result = await attempt(cancellationToken);
        }

        // Report the total time the user waited, not just the final attempt.
        return notified && result is not null ? result with { Duration = stopwatch.Elapsed } : result;
    }
}

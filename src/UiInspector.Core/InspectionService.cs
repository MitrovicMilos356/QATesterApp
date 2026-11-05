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
    public async Task<SelectorResult> GenerateSelectorAsync(IReadOnlyList<ElementInfo> pathFromRoot, CancellationToken cancellationToken = default)
    {
        var result = await worker.RunAsync(a => _selectorGenerator.Generate(a, pathFromRoot), cancellationToken, SelectorTimeout);
        if (pathFromRoot[^1].FrameworkId != "Chrome") return result;

        // The web side talks to the page over CDP, not UIA, so it runs off the UIA worker.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WebTimeout);
        try
        {
            return result with { Web = await _webBridge.InspectAsync(pathFromRoot, CdpPort, timeout.Token) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return result with { Web = WebResult.Unavailable($"The page didn't answer within {WebTimeout.TotalSeconds:0} s.") };
        }
        catch (Exception ex) when (ex is CdpException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException
                                       or System.Management.ManagementException or KeyNotFoundException or InvalidOperationException)
        {
            return result with { Web = WebResult.Unavailable($"Reading the page failed: {ex.Message}") };
        }
    }

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

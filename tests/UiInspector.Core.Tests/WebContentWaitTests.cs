using UiInspector.Core;

namespace UiInspector.Core.Tests;

public class WebContentWaitTests
{
    private static readonly WebContentWaitOptions FastOptions = new()
    {
        Timeout = TimeSpan.FromMilliseconds(500),
        PollInterval = TimeSpan.FromMilliseconds(10),
    };

    private static InspectionResult Result(bool pending, string name = "") => new()
    {
        Element = new ElementInfo { Name = name },
        Source = InspectionSource.Point,
        WebContentPending = pending,
    };

    [Fact]
    public async Task ReadyOnFirstAttempt_ReturnsImmediatelyWithoutWaiting()
    {
        var attempts = 0;
        var waited = false;

        var result = await InspectionService.WaitForWebContentAsync(
            _ => { attempts++; return Task.FromResult<InspectionResult?>(Result(pending: false, "Seven")); },
            FastOptions, () => waited = true, CancellationToken.None);

        Assert.Equal("Seven", result?.Element.Name);
        Assert.Equal(1, attempts);
        Assert.False(waited);
    }

    [Fact]
    public async Task PendingThenReady_RetriesAndReturnsRealContent()
    {
        var attempts = 0;
        var waitedCount = 0;

        // A generous timeout: with the desktop tests running in parallel, two 10 ms delays can take long.
        var options = new WebContentWaitOptions { Timeout = TimeSpan.FromSeconds(10), PollInterval = TimeSpan.FromMilliseconds(10) };
        var result = await InspectionService.WaitForWebContentAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult<InspectionResult?>(attempts < 3 ? Result(pending: true) : Result(pending: false, "Dyno"));
            },
            options, () => waitedCount++, CancellationToken.None);

        Assert.Equal("Dyno", result?.Element.Name);
        Assert.False(result?.WebContentPending);
        Assert.Equal(3, attempts);
        Assert.Equal(1, waitedCount); // notified once, not per retry
    }

    [Fact]
    public async Task StaysPending_GivesUpAfterTimeoutAndReturnsPendingResult()
    {
        var started = DateTime.UtcNow;

        var result = await InspectionService.WaitForWebContentAsync(
            _ => Task.FromResult<InspectionResult?>(Result(pending: true)),
            FastOptions, null, CancellationToken.None);

        Assert.True(result?.WebContentPending);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        Assert.True(result!.Duration >= FastOptions.Timeout, "Duration should cover the whole wait");
    }

    [Fact]
    public async Task NoElement_ReturnsNullWithoutRetrying()
    {
        var attempts = 0;

        var result = await InspectionService.WaitForWebContentAsync(
            _ => { attempts++; return Task.FromResult<InspectionResult?>(null); },
            FastOptions, null, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Cancellation_StopsWaiting()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var slowOptions = new WebContentWaitOptions { Timeout = TimeSpan.FromSeconds(30), PollInterval = TimeSpan.FromMilliseconds(10) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InspectionService.WaitForWebContentAsync(
            _ => Task.FromResult<InspectionResult?>(Result(pending: true)),
            slowOptions, null, cts.Token));
    }
}

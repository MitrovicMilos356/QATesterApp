using System.Diagnostics;
using UiInspector.Core;

namespace UiInspector.Core.Tests;

public class UiaWorkerTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task RunAsync_ReturnsResultFromWorkerThread()
    {
        using var worker = new UiaWorker();

        var threadName = await worker.RunAsync(_ => Thread.CurrentThread.Name);

        Assert.Equal("UIA worker #1", threadName);
    }

    [Fact]
    public async Task RunAsync_RunsOnMtaThread()
    {
        using var worker = new UiaWorker();

        var apartment = await worker.RunAsync(_ => Thread.CurrentThread.GetApartmentState());

        Assert.Equal(ApartmentState.MTA, apartment);
    }

    [Fact]
    public async Task RunAsync_PropagatesExceptionsFromWork()
    {
        using var worker = new UiaWorker();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.RunAsync<int>(_ => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task RunAsync_HungWork_TimesOutAndRecoversOnFreshThread()
    {
        using var worker = new UiaWorker(new UiaWorkerOptions { OperationTimeout = ShortTimeout });
        using var release = new ManualResetEventSlim();

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<UiaTimeoutException>(() => worker.RunAsync(_ => release.Wait(Timeout.Infinite)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Timeout took {stopwatch.Elapsed}");

        // The hung thread is still blocked, yet new work runs immediately on a replacement.
        var threadName = await worker.RunAsync(_ => Thread.CurrentThread.Name);
        Assert.Equal("UIA worker #2", threadName);
        Assert.Equal(1, worker.RestartCount);

        release.Set();
    }

    [Fact]
    public async Task RunAsync_WorkQueuedBehindHungCall_FailsInsteadOfHanging()
    {
        using var worker = new UiaWorker(new UiaWorkerOptions { OperationTimeout = ShortTimeout });
        using var release = new ManualResetEventSlim();

        var hung = worker.RunAsync(_ => release.Wait(Timeout.Infinite));
        var queued = worker.RunAsync(_ => 42);

        await Assert.ThrowsAsync<UiaTimeoutException>(() => hung);
        var queuedError = await Record.ExceptionAsync(() => queued);
        Assert.True(queuedError is UiaTimeoutException or OperationCanceledException, $"Unexpected: {queuedError}");

        Assert.Equal(7, await worker.RunAsync(_ => 7));
        release.Set();
    }

    [Fact]
    public async Task RunAsync_AfterDispose_Throws()
    {
        var worker = new UiaWorker();
        worker.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.RunAsync(_ => 1));
    }
}

using System.Collections.Concurrent;
using FlaUI.UIA3;

namespace UiInspector.Core;

public sealed class UiaWorkerOptions
{
    /// <summary>Timeout UIA applies to each single cross-process call (connect and transaction).</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Total time one operation (e.g. a full inspection) may take before it is abandoned.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class UiaTimeoutException(TimeSpan timeout)
    : TimeoutException($"The target application did not respond within {timeout.TotalSeconds:0.#} s (it may be busy or hung).");

/// <summary>
/// Runs all UI Automation work on a dedicated MTA thread. Cross-process UIA calls can block
/// for a long time when the target app is hung; if an operation exceeds its timeout, the
/// worker thread is abandoned (left to finish on its own) and a fresh one takes its place,
/// so callers never freeze.
/// </summary>
public sealed class UiaWorker : IDisposable
{
    private readonly UiaWorkerOptions _options;
    private readonly Lock _gate = new();
    private WorkerThread _current;
    private int _generation;
    private bool _disposed;

    public UiaWorker(UiaWorkerOptions? options = null)
    {
        _options = options ?? new UiaWorkerOptions();
        _current = new WorkerThread(_options.CallTimeout, ++_generation);
    }

    /// <summary>How many times a hung worker thread has been replaced.</summary>
    public int RestartCount => _generation - 1;

    /// <param name="timeout">Overrides <see cref="UiaWorkerOptions.OperationTimeout"/> for long operations.</param>
    public async Task<T> RunAsync<T>(Func<UIA3Automation, T> work, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var operationTimeout = timeout ?? _options.OperationTimeout;
        WorkerThread worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            worker = _current;
        }

        try
        {
            return await worker.Enqueue(work).WaitAsync(operationTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (!_disposed && ReferenceEquals(_current, worker))
                {
                    _current = new WorkerThread(_options.CallTimeout, ++_generation);
                }
            }
            worker.Abandon();
            throw new UiaTimeoutException(operationTimeout);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _current.Abandon();
    }

    private sealed class WorkerThread
    {
        private readonly BlockingCollection<WorkItem> _queue = new();
        private readonly TimeSpan _callTimeout;
        private volatile bool _abandoned;

        public WorkerThread(TimeSpan callTimeout, int generation)
        {
            _callTimeout = callTimeout;
            var thread = new Thread(Run) { IsBackground = true, Name = $"UIA worker #{generation}" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public Task<T> Enqueue<T>(Func<UIA3Automation, T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new WorkItem(
                automation =>
                {
                    try { tcs.TrySetResult(work(automation)); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                },
                ex => tcs.TrySetException(ex));

            try { _queue.Add(item); }
            catch (InvalidOperationException) { item.Fail(new ObjectDisposedException(nameof(UiaWorker))); }
            return tcs.Task;
        }

        public void Abandon()
        {
            _abandoned = true;
            _queue.CompleteAdding();
            while (_queue.TryTake(out var item))
            {
                item.Fail(new OperationCanceledException("The UIA worker was restarted; try again."));
            }
        }

        private void Run()
        {
            UIA3Automation automation;
            try
            {
                automation = new UIA3Automation
                {
                    ConnectionTimeout = _callTimeout,
                    TransactionTimeout = _callTimeout,
                };
            }
            catch (Exception ex)
            {
                foreach (var item in _queue.GetConsumingEnumerable()) item.Fail(ex);
                return;
            }

            using (automation)
            {
                foreach (var item in _queue.GetConsumingEnumerable())
                {
                    if (_abandoned)
                    {
                        item.Fail(new OperationCanceledException("The UIA worker was restarted; try again."));
                        continue;
                    }
                    item.Execute(automation);
                }
            }
        }

        private sealed record WorkItem(Action<UIA3Automation> Execute, Action<Exception> Fail);
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;

namespace SharpLabNext.Operations;

public sealed class OperationExecutionOptions
{
    public const string SectionName = "OperationExecution";

    public int QueueCapacity { get; set; } = 256;

    public int WorkerConcurrency { get; set; } = 8;

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public string ExecutorId { get; set; } = "operation-executor";

    public void Validate()
    {
        if (QueueCapacity is < 1 or > 100_000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(QueueCapacity)} is outside the supported range.");
        }

        if (WorkerConcurrency is < 1 or > 256)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(WorkerConcurrency)} is outside the supported range.");
        }

        if (ShutdownTimeout <= TimeSpan.Zero || ShutdownTimeout > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(ShutdownTimeout)} is outside the supported range.");
        }

        if (string.IsNullOrWhiteSpace(ExecutorId) || ExecutorId.Length > 128)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(ExecutorId)} is invalid.");
        }
    }
}

public sealed class BoundedOperationScheduler : IAsyncDisposable
{
    private readonly OperationStore _operations;
    private readonly OperationExecutionOptions _options;
    private readonly Channel<WorkItem> _queue;
    private readonly ConcurrentDictionary<long, WorkItem> _active = new();
    private readonly Task[] _workers;
    private readonly Lock _gate = new();
    private long _nextWorkItemId;
    private long _queuedCount;
    private int _state;

    public BoundedOperationScheduler(
        OperationStore operations,
        OperationExecutionOptions? options = null)
    {
        _operations = operations;
        _options = options ?? new OperationExecutionOptions();
        _options.Validate();
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _options.WorkerConcurrency == 1,
            SingleWriter = false
        });
        _workers = Enumerable.Range(0, _options.WorkerConcurrency)
            .Select(_ => Task.Run(RunWorkerAsync, CancellationToken.None))
            .ToArray();
    }

    public bool TryQueue(OperationStart operation, Func<Task> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(executeAsync);

        var rejection = QueueRejection.Full;
        lock (_gate)
        {
            if (_state != 0)
            {
                rejection = QueueRejection.Stopping;
            }
            else
            {
                var item = new WorkItem(
                    Interlocked.Increment(ref _nextWorkItemId),
                    operation,
                    executeAsync,
                    DateTimeOffset.UtcNow);
                if (!_active.TryAdd(item.Id, item))
                {
                    throw new InvalidOperationException("Failed to allocate a scheduler work item.");
                }

                var queued = Interlocked.Increment(ref _queuedCount);
                RecordQueueDepth(queued);
                if (_queue.Writer.TryWrite(item))
                {
                    return true;
                }

                queued = Interlocked.Decrement(ref _queuedCount);
                RecordQueueDepth(queued);
                _active.TryRemove(item.Id, out _);
            }
        }

        Reject(operation, rejection);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        WorkItem[] active;
        lock (_gate)
        {
            if (_state != 0)
            {
                return;
            }

            _state = 1;
            _queue.Writer.TryComplete();
            active = _active.Values.ToArray();
        }

        foreach (var item in active)
        {
            Cancel(item.Operation);
        }

        try
        {
            await Task.WhenAll(_workers).WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            foreach (var item in _active.Values)
            {
                CompleteCancelled(item.Operation);
            }
        }
        finally
        {
            Volatile.Write(ref _state, 2);
        }
    }

    private async Task RunWorkerAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var queued = Interlocked.Decrement(ref _queuedCount);
            RecordQueueDepth(queued);
            SharpLabNextTelemetry.Metrics.RecordQueueWait(
                _options.ExecutorId,
                DateTimeOffset.UtcNow - item.EnqueuedAtUtc,
                SharpLabNextTelemetryOutcome.Succeeded);
            try
            {
                if (Volatile.Read(ref _state) != 0)
                {
                    CompleteCancelled(item.Operation);
                    continue;
                }

                if (item.Operation.CancellationToken.IsCancellationRequested)
                {
                    CompleteCancelled(item.Operation);
                    continue;
                }

                await item.ExecuteAsync().ConfigureAwait(false);
                EnsureTerminal(item.Operation);
            }
            catch (OperationCanceledException) when (item.Operation.CancellationToken.IsCancellationRequested)
            {
                CompleteCancelled(item.Operation);
            }
            catch (Exception)
            {
                FailIfActive(
                    item.Operation,
                    "operation-executor-failed",
                    WorkerErrorCategory.Internal,
                    "The operation executor failed.",
                    retryable: true,
                    safeToRetry: false);
            }
            finally
            {
                _active.TryRemove(item.Id, out _);
            }
        }
    }

    private void EnsureTerminal(OperationStart operation)
    {
        var state = _operations.Get(operation.Handle.OperationId);
        if (state?.Status == OperationStatus.Cancelling)
        {
            CompleteCancelled(operation);
        }
        else if (state is not null && !IsTerminal(state.Status))
        {
            FailIfActive(
                operation,
                "operation-executor-incomplete",
                WorkerErrorCategory.Internal,
                "The operation executor stopped without a terminal result.",
                retryable: true,
                safeToRetry: false);
        }
    }

    private void Reject(OperationStart operation, QueueRejection rejection)
    {
        SharpLabNextTelemetry.Metrics.RecordQueueRejection(_options.ExecutorId);
        var stopping = rejection == QueueRejection.Stopping;
        FailIfActive(
            operation,
            stopping ? "operation-executor-stopping" : "operation-queue-full",
            stopping ? WorkerErrorCategory.Unavailable : WorkerErrorCategory.ResourceExhausted,
            stopping
                ? "The operation executor is shutting down."
                : "The operation queue is full. Retry after capacity becomes available.",
            retryable: true,
            safeToRetry: true);
    }

    private void Cancel(OperationStart operation) =>
        _operations.Cancel(
            operation.Handle.OperationId,
            "service-shutdown",
            DateTimeOffset.UtcNow);

    private void CompleteCancelled(OperationStart operation)
    {
        Cancel(operation);
        TryAppend(
            operation,
            new CompletedOperationEventPayload(
                OperationCompletionStatus.Cancelled,
                DateTimeOffset.UtcNow - operation.Handle.CreatedAtUtc));
    }

    private void FailIfActive(
        OperationStart operation,
        string code,
        WorkerErrorCategory category,
        string publicMessage,
        bool retryable,
        bool safeToRetry)
    {
        var state = _operations.Get(operation.Handle.OperationId);
        if (state is null || IsTerminal(state.Status))
        {
            return;
        }

        TryAppend(operation, new FailedOperationEventPayload(new WorkerError(
            code,
            category,
            publicMessage,
            retryable,
            safeToRetry,
            state.TraceId,
            _options.ExecutorId,
            "unknown")));
    }

    private void TryAppend(OperationStart operation, OperationEventPayload payload)
    {
        try
        {
            _operations.Append(operation.Handle.OperationId, payload, DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException)
        {
            // A competing executor or shutdown path already made the operation terminal.
        }
        catch (KeyNotFoundException)
        {
            // A terminal operation can expire while a late shutdown callback is completing.
        }
    }

    private static bool IsTerminal(OperationStatus status) =>
        status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;

    private void RecordQueueDepth(long depth) =>
        SharpLabNextTelemetry.Metrics.RecordQueueDepth(_options.ExecutorId, depth);

    private sealed record WorkItem(
        long Id,
        OperationStart Operation,
        Func<Task> ExecuteAsync,
        DateTimeOffset EnqueuedAtUtc);

    private enum QueueRejection
    {
        Full,
        Stopping
    }
}

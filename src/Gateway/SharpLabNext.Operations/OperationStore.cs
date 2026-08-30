using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SharpLabNext.Contracts;

namespace SharpLabNext.Operations;

public sealed class OperationStoreOptions
{
    public const string SectionName = "OperationStore";

    public int MaximumEventsPerOperation { get; set; } = 10_000;

    public int MaximumOperations { get; set; } = 4_096;

    public TimeSpan TerminalOperationTimeToLive { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OverloadOperationTimeToLive { get; set; } = TimeSpan.FromMinutes(1);

    public void Validate()
    {
        if (MaximumEventsPerOperation is < 2 or > 1_000_000)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaximumEventsPerOperation)} is outside the supported range.");
        }

        if (MaximumOperations is < 1 or > 1_000_000)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaximumOperations)} is outside the supported range.");
        }

        if (TerminalOperationTimeToLive <= TimeSpan.Zero || TerminalOperationTimeToLive > TimeSpan.FromDays(30))
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(TerminalOperationTimeToLive)} is outside the supported range.");
        }

        if (OverloadOperationTimeToLive <= TimeSpan.Zero || OverloadOperationTimeToLive > TerminalOperationTimeToLive)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(OverloadOperationTimeToLive)} must be positive and no greater than the terminal operation TTL.");
        }
    }
}

public sealed class OperationCapacityExceededException(int maximumOperations) : InvalidOperationException($"The operation store limit of {maximumOperations} operations was reached.")
{
    public int MaximumOperations { get; } = maximumOperations;
}

public sealed record OperationStart(OperationHandle Handle, CancellationToken CancellationToken);

public sealed class OperationStore
{
    private readonly ConcurrentDictionary<string, OperationEntry> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idempotency = new(StringComparer.Ordinal);
    private readonly Lock _startGate = new();
    private readonly OperationStoreOptions _options;

    public OperationStore(OperationStoreOptions? options = null)
    {
        _options = options ?? new OperationStoreOptions();
        _options.Validate();
    }

    public OperationStart Start(string requestId, string idempotencyKey, OperationKind kind, string traceId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);

        lock (_startGate)
        {
            RemoveExpiredTerminalOperationsCore(now);
            if (_idempotency.TryGetValue(idempotencyKey, out var existingId) && _operations.TryGetValue(existingId, out var existing))
            {
                return new OperationStart(new OperationHandle(existingId, existing.RequestId, existing.CreatedAtUtc, true), existing.Cancellation.Token);
            }

            _idempotency.Remove(idempotencyKey);
            if (_operations.Count >= _options.MaximumOperations)
            {
                throw new OperationCapacityExceededException(_options.MaximumOperations);
            }

            var operationId = $"op_{Guid.NewGuid():N}";
            var entry = new OperationEntry(operationId, requestId, idempotencyKey, kind, traceId, now);
            if (!_operations.TryAdd(operationId, entry))
            {
                throw new InvalidOperationException("Failed to allocate a unique operation ID.");
            }

            _idempotency.Add(idempotencyKey, operationId);
            AppendCore(entry, new AcceptedOperationEventPayload(requestId, kind), now);
            return new OperationStart(new OperationHandle(operationId, requestId, now, false), entry.Cancellation.Token);
        }
    }

    public int RemoveExpiredTerminalOperations(DateTimeOffset now)
    {
        lock (_startGate)
        {
            return RemoveExpiredTerminalOperationsCore(now);
        }
    }

    public OperationEvent Append(string operationId, OperationEventPayload payload, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(payload);
        var entry = GetRequired(operationId);
        lock (entry.Gate)
        {
            return AppendCore(entry, payload, now);
        }
    }

    public OperationState? Get(string operationId)
    {
        if (!_operations.TryGetValue(operationId, out var entry))
        {
            return null;
        }

        lock (entry.Gate)
        {
            return entry.ToState();
        }
    }

    public IReadOnlyList<OperationEvent>? GetEvents(string operationId, long fromSequence = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromSequence);
        if (!_operations.TryGetValue(operationId, out var entry))
            return null;
        lock (entry.Gate)
            return entry.Events.Where(item => item.Sequence > fromSequence).ToArray();
    }

    public CancelResult Cancel(string operationId, string? reason, DateTimeOffset now)
    {
        if (!_operations.TryGetValue(operationId, out var entry))
        {
            return new CancelResult(operationId, CancelDisposition.NotFound, 0);
        }

        lock (entry.Gate)
        {
            if (IsTerminal(entry.Status))
            {
                return new CancelResult(operationId, CancelDisposition.AlreadyTerminal, entry.LastSequence);
            }

            if (entry.Status == OperationStatus.Cancelling)
            {
                return new CancelResult(operationId, CancelDisposition.AlreadyCancelling, entry.LastSequence);
            }

            entry.Status = OperationStatus.Cancelling;
            entry.UpdatedAtUtc = now;
            entry.Cancellation.Cancel();
            entry.Pulse();
            _ = reason;
            return new CancelResult(operationId, CancelDisposition.Accepted, entry.LastSequence);
        }
    }

    public async IAsyncEnumerable<OperationEvent> WatchAsync(string operationId, long fromSequence, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromSequence);

        var entry = GetRequired(operationId);
        var cursor = fromSequence;
        while (true)
        {
            OperationEvent[] batch;
            Task signal;
            bool terminal;
            lock (entry.Gate)
            {
                batch = entry.Events.Where(item => item.Sequence > cursor).ToArray();
                terminal = IsTerminal(entry.Status);
                signal = entry.Signal.Task;
            }

            foreach (var operationEvent in batch)
            {
                cursor = operationEvent.Sequence;
                yield return operationEvent;
            }

            if (terminal)
            {
                yield break;
            }

            await signal.WaitAsync(cancellationToken);
        }
    }

    private OperationEntry GetRequired(string operationId)
    {
        return _operations.TryGetValue(operationId, out var entry)
            ? entry : throw new KeyNotFoundException($"Operation '{operationId}' was not found.");
    }

    private int RemoveExpiredTerminalOperationsCore(DateTimeOffset now)
    {
        var removedCount = 0;
        foreach (var pair in _operations)
        {
            var entry = pair.Value;
            lock (entry.Gate)
            {
                if (!IsTerminal(entry.Status) || entry.CompletedAtUtc is null)
                {
                    continue;
                }

                var timeToLive = entry.Error?.Code == "operation-queue-full"
                    ? _options.OverloadOperationTimeToLive : _options.TerminalOperationTimeToLive;
                if (entry.CompletedAtUtc > now - timeToLive)
                {
                    continue;
                }

                if (!_operations.TryRemove(new KeyValuePair<string, OperationEntry>(pair.Key, entry)))
                {
                    continue;
                }

                if (_idempotency.TryGetValue(entry.IdempotencyKey, out var mappedOperationId) && StringComparer.Ordinal.Equals(mappedOperationId, entry.OperationId))
                {
                    _idempotency.Remove(entry.IdempotencyKey);
                }

                entry.Cancellation.Dispose();
                removedCount++;
            }
        }

        return removedCount;
    }

    private OperationEvent AppendCore(OperationEntry entry, OperationEventPayload payload, DateTimeOffset now)
    {
        if (IsTerminal(entry.Status))
        {
            throw new InvalidOperationException("An operation cannot emit events after reaching a terminal state.");
        }

        if (entry.Events.Count >= _options.MaximumEventsPerOperation)
        {
            throw new InvalidOperationException("The operation event limit was exceeded.");
        }

        if (!payload.IsTerminal && entry.Events.Count == _options.MaximumEventsPerOperation - 1)
        {
            payload = new FailedOperationEventPayload(new WorkerError("operation-event-limit-exceeded", WorkerErrorCategory.ResourceExhausted, "The operation emitted too many events.", false, false, entry.TraceId, "operation-store", "unknown"));
        }

        var operationEvent = new OperationEvent(entry.OperationId, checked(entry.LastSequence + 1), now, entry.TraceId, payload);
        entry.Events.Add(operationEvent);
        entry.LastSequence = operationEvent.Sequence;
        entry.UpdatedAtUtc = now;
        switch (payload)
        {
            case AcceptedOperationEventPayload:
                entry.Status = OperationStatus.Accepted;
                break;
            case CompletedOperationEventPayload completed:
                entry.Status = completed.Status == OperationCompletionStatus.Cancelled
                    ? OperationStatus.Cancelled : OperationStatus.Completed;
                entry.CompletedAtUtc = now;
                break;
            case FailedOperationEventPayload failed:
                entry.Status = OperationStatus.Failed;
                entry.CompletedAtUtc = now;
                entry.Error = failed.Error;
                break;
            default:
                if (entry.Status != OperationStatus.Cancelling)
                {
                    entry.Status = OperationStatus.Running;
                }

                break;
        }

        entry.Pulse();
        return operationEvent;
    }

    private static bool IsTerminal(OperationStatus status) =>
        status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;

    private sealed class OperationEntry(string operationId, string requestId, string idempotencyKey, OperationKind kind, string traceId, DateTimeOffset createdAtUtc)
    {
        public Lock Gate { get; } = new();
        public string OperationId { get; } = operationId;
        public string RequestId { get; } = requestId;
        public string IdempotencyKey { get; } = idempotencyKey;
        public OperationKind Kind { get; } = kind;
        public string TraceId { get; } = traceId;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
        public DateTimeOffset UpdatedAtUtc { get; set; } = createdAtUtc;
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public OperationStatus Status { get; set; } = OperationStatus.Accepted;
        public long LastSequence { get; set; }
        public WorkerError? Error { get; set; }
        public List<OperationEvent> Events { get; } = [];
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Signal { get; private set; } = NewSignal();

        public OperationState ToState() => new(OperationId, RequestId, Kind, Status, LastSequence, CreatedAtUtc, UpdatedAtUtc, CompletedAtUtc, TraceId, Error);

        public void Pulse()
        {
            var previous = Signal;
            Signal = NewSignal();
            previous.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

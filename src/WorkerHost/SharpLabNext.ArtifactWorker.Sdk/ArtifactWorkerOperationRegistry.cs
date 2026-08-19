using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker.Sdk;

public sealed partial class ArtifactWorkerOperationRegistry : IDisposable
{
    private readonly ArtifactWorkerCapabilityManifest _manifest;
    private readonly ArtifactWorkerHostIdentity _hostIdentity;
    private readonly ILogger<ArtifactWorkerOperationRegistry> _logger;
    private readonly ConcurrentDictionary<string, ArtifactOperation> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotency = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _concurrency;

    public ArtifactWorkerOperationRegistry(
        ArtifactWorkerCapabilityManifest manifest,
        ArtifactWorkerHostIdentity hostIdentity,
        ILogger<ArtifactWorkerOperationRegistry> logger)
    {
        _manifest = manifest;
        _hostIdentity = hostIdentity;
        _logger = logger;
        _concurrency = new SemaphoreSlim(
            manifest.Limits.MaximumConcurrentOperations,
            manifest.Limits.MaximumConcurrentOperations);
    }

    public OperationHandle Start(
        string requestId,
        string idempotencyKey,
        OperationKind kind,
        Func<string, CancellationToken, Task<ArtifactWorkerJobExecution>> execute)
    {
        ValidateRequestIdentity(requestId, idempotencyKey);
        ArgumentNullException.ThrowIfNull(execute);
        var idempotencyId = $"{kind}:{idempotencyKey}";
        if (TryGetExisting(idempotencyId, out var existing))
            return Handle(existing!, isExisting: true);

        TrimCompleted();
        if (_operations.Count >= _manifest.Limits.MaximumRetainedOperations)
        {
            throw new ArtifactWorkerRequestException(
                "worker-capacity-exceeded",
                "The artifact worker operation registry is at capacity.",
                StatusCodes.Status429TooManyRequests,
                WorkerErrorCategory.ResourceExhausted);
        }

        var operationId = $"op_{Guid.NewGuid():N}";
        var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var operation = new ArtifactOperation(
            operationId,
            requestId,
            kind,
            traceId,
            _manifest.Limits.MaximumEventsPerOperation);
        if (!_operations.TryAdd(operationId, operation))
            throw new InvalidOperationException("Could not allocate an artifact operation.");
        if (!_idempotency.TryAdd(idempotencyId, operationId))
        {
            _operations.TryRemove(operationId, out _);
            operation.Dispose();
            if (TryGetExisting(idempotencyId, out existing))
                return Handle(existing!, isExisting: true);
            throw new InvalidOperationException("Could not resolve an artifact operation idempotency race.");
        }

        operation.Append(new AcceptedOperationEventPayload(requestId, kind));
        _ = Task.Run(() => ExecuteAsync(operation, execute));
        return Handle(operation, isExisting: false);
    }

    public OperationState? Get(string operationId) =>
        _operations.TryGetValue(operationId, out var operation) ? operation.Snapshot() : null;

    public IReadOnlyList<OperationEvent>? GetEvents(string operationId, long fromSequence)
    {
        if (fromSequence < 0)
        {
            throw new ArtifactWorkerRequestException(
                "invalid-sequence",
                "fromSequence cannot be negative.");
        }
        return _operations.TryGetValue(operationId, out var operation)
            ? operation.Events(fromSequence)
            : null;
    }

    public CancelResult Cancel(string operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
            return new CancelResult(operationId, CancelDisposition.NotFound, 0);
        return operation.Cancel();
    }

    public void Dispose()
    {
        foreach (var operation in _operations.Values)
            operation.Dispose();
        _concurrency.Dispose();
    }

    private async Task ExecuteAsync(
        ArtifactOperation operation,
        Func<string, CancellationToken, Task<ArtifactWorkerJobExecution>> execute)
    {
        var started = DateTimeOffset.UtcNow;
        var entered = false;
        try
        {
            await _concurrency.WaitAsync(operation.CancellationToken).ConfigureAwait(false);
            entered = true;
            operation.MarkRunning();
            operation.Append(new ProgressOperationEventPayload("artifact-processing", null, 0));
            var execution = await execute(operation.OperationId, operation.CancellationToken).ConfigureAwait(false);
            if (execution.Content is not null)
            {
                operation.Append(new ContentProducedOperationEventPayload(
                    execution.Content.ContentRef,
                    execution.Content.MediaType,
                    execution.Content.Size));
            }
            if (execution.Artifact is not null)
            {
                operation.Append(new ArtifactProducedOperationEventPayload(
                    execution.Artifact.ArtifactRef,
                    execution.Artifact.ArtifactFormat,
                    execution.Artifact.Role));
            }
            operation.Append(new TypedResultOperationEventPayload(execution.Result));
            operation.Complete(OperationCompletionStatus.Completed, DateTimeOffset.UtcNow - started);
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            operation.Complete(OperationCompletionStatus.Cancelled, DateTimeOffset.UtcNow - started);
        }
        catch (Exception exception)
        {
            var error = ArtifactWorkerErrorMapper.Map(
                exception,
                operation.TraceId,
                _manifest.WorkerId,
                _hostIdentity.WorkerImageId);
            OperationFailed(
                _logger,
                operation.OperationId,
                error.Code,
                operation.TraceId);
            operation.Fail(error);
        }
        finally
        {
            if (entered)
                _concurrency.Release();
        }
    }

    private bool TryGetExisting(string idempotencyId, out ArtifactOperation? operation)
    {
        operation = null;
        return _idempotency.TryGetValue(idempotencyId, out var operationId) &&
            _operations.TryGetValue(operationId, out operation);
    }

    private void TrimCompleted()
    {
        if (_operations.Count < _manifest.Limits.MaximumRetainedOperations)
            return;
        var removable = _operations.Values
            .Where(static operation => operation.IsTerminal)
            .OrderBy(static operation => operation.UpdatedAtUtc)
            .Take(Math.Max(1, _manifest.Limits.MaximumRetainedOperations / 4))
            .ToArray();
        foreach (var operation in removable)
        {
            if (_operations.TryRemove(operation.OperationId, out var removed))
                removed.Dispose();
        }
        foreach (var pair in _idempotency)
        {
            if (!_operations.ContainsKey(pair.Value))
                _idempotency.TryRemove(pair.Key, out _);
        }
    }

    private static void ValidateRequestIdentity(string requestId, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
            throw new ArtifactWorkerRequestException("invalid-request", "RequestId is required and must not exceed 128 characters.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
            throw new ArtifactWorkerRequestException("invalid-request", "IdempotencyKey is required and must not exceed 256 characters.");
    }

    private static OperationHandle Handle(ArtifactOperation operation, bool isExisting) => new(
        operation.OperationId,
        operation.RequestId,
        operation.CreatedAtUtc,
        isExisting);

    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Error,
        Message = "Artifact operation {OperationId} failed with {ErrorCode}. TraceId {TraceId}.")]
    private static partial void OperationFailed(
        ILogger logger,
        string operationId,
        string errorCode,
        string traceId);

    private sealed class ArtifactOperation : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<OperationEvent> _events = [];
        private readonly CancellationTokenSource _cancellation = new();
        private readonly int _maximumEvents;
        private OperationStatus _status = OperationStatus.Accepted;
        private WorkerError? _error;
        private DateTimeOffset? _completedAtUtc;
        private long _sequence;

        public ArtifactOperation(
            string operationId,
            string requestId,
            OperationKind kind,
            string traceId,
            int maximumEvents)
        {
            OperationId = operationId;
            RequestId = requestId;
            Kind = kind;
            TraceId = traceId;
            _maximumEvents = maximumEvents;
            CreatedAtUtc = DateTimeOffset.UtcNow;
            UpdatedAtUtc = CreatedAtUtc;
        }

        public string OperationId { get; }

        public string RequestId { get; }

        public OperationKind Kind { get; }

        public string TraceId { get; }

        public DateTimeOffset CreatedAtUtc { get; }

        public DateTimeOffset UpdatedAtUtc { get; private set; }

        public CancellationToken CancellationToken => _cancellation.Token;

        public bool IsTerminal
        {
            get
            {
                lock (_gate)
                    return _status is OperationStatus.Completed or OperationStatus.Cancelled or OperationStatus.Failed;
            }
        }

        public void MarkRunning()
        {
            lock (_gate)
            {
                if (_status == OperationStatus.Accepted)
                    _status = OperationStatus.Running;
                UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        public void Append(OperationEventPayload payload)
        {
            lock (_gate)
            {
                if (_completedAtUtc is not null)
                    throw new InvalidOperationException("Cannot append to a terminal artifact operation.");
                AppendCore(payload, DateTimeOffset.UtcNow);
            }
        }

        public void Complete(OperationCompletionStatus status, TimeSpan elapsed)
        {
            lock (_gate)
            {
                if (_completedAtUtc is not null)
                    return;
                var now = DateTimeOffset.UtcNow;
                AppendCore(new CompletedOperationEventPayload(status, elapsed), now);
                _status = status == OperationCompletionStatus.Cancelled
                    ? OperationStatus.Cancelled
                    : OperationStatus.Completed;
                _completedAtUtc = now;
                UpdatedAtUtc = now;
            }
        }

        public void Fail(WorkerError error)
        {
            lock (_gate)
            {
                if (_completedAtUtc is not null)
                    return;
                var now = DateTimeOffset.UtcNow;
                AppendCore(new FailedOperationEventPayload(error), now);
                _error = error;
                _status = OperationStatus.Failed;
                _completedAtUtc = now;
                UpdatedAtUtc = now;
            }
        }

        public OperationState Snapshot()
        {
            lock (_gate)
            {
                return new OperationState(
                    OperationId,
                    RequestId,
                    Kind,
                    _status,
                    _sequence,
                    CreatedAtUtc,
                    UpdatedAtUtc,
                    _completedAtUtc,
                    TraceId,
                    _error);
            }
        }

        public OperationEvent[] Events(long fromSequence)
        {
            lock (_gate)
                return _events.Where(item => item.Sequence > fromSequence).ToArray();
        }

        public CancelResult Cancel()
        {
            CancelResult result;
            lock (_gate)
            {
                if (_status is OperationStatus.Completed or OperationStatus.Cancelled or OperationStatus.Failed)
                    return new CancelResult(OperationId, CancelDisposition.AlreadyTerminal, _sequence);
                if (_status == OperationStatus.Cancelling)
                    return new CancelResult(OperationId, CancelDisposition.AlreadyCancelling, _sequence);
                _status = OperationStatus.Cancelling;
                UpdatedAtUtc = DateTimeOffset.UtcNow;
                result = new CancelResult(OperationId, CancelDisposition.Accepted, _sequence);
            }
            _cancellation.Cancel();
            return result;
        }

        public void Dispose() => _cancellation.Dispose();

        private void AppendCore(OperationEventPayload payload, DateTimeOffset timestamp)
        {
            if (_events.Count >= _maximumEvents)
                throw new InvalidOperationException("The artifact operation event limit was exceeded.");
            _events.Add(new OperationEvent(OperationId, ++_sequence, timestamp, TraceId, payload));
            UpdatedAtUtc = timestamp;
        }
    }
}

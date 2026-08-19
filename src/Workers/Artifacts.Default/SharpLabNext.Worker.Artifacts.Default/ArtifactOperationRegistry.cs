using System.Collections.Concurrent;
using System.Diagnostics;
using SharpLabNext.ArtifactProcessing.Protocol;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker;

internal sealed class ArtifactOperationRegistry(
    ArtifactWorkerSettings settings,
    ILogger<ArtifactOperationRegistry> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, ArtifactOperation> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotency = new(StringComparer.Ordinal);

    public OperationHandle Start(
        string requestId,
        string idempotencyKey,
        OperationKind kind,
        Func<string, CancellationToken, Task<ArtifactJobExecution>> execute)
    {
        var idempotencyId = $"{kind}:{idempotencyKey}";
        if (_idempotency.TryGetValue(idempotencyId, out var existingId) &&
            _operations.TryGetValue(existingId, out var existing))
        {
            return new OperationHandle(existing.OperationId, existing.RequestId, existing.CreatedAtUtc, true);
        }

        TrimCompleted();
        var operationId = $"op_{Guid.NewGuid():N}";
        var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var operation = new ArtifactOperation(operationId, requestId, kind, traceId);
        if (!_operations.TryAdd(operationId, operation))
            throw new InvalidOperationException("Could not allocate an artifact operation.");
        if (!_idempotency.TryAdd(idempotencyId, operationId))
        {
            _operations.TryRemove(operationId, out _);
            var racedId = _idempotency[idempotencyId];
            var raced = _operations[racedId];
            return new OperationHandle(raced.OperationId, raced.RequestId, raced.CreatedAtUtc, true);
        }

        operation.Append(new AcceptedOperationEventPayload(requestId, kind));
        _ = Task.Run(() => ExecuteAsync(operation, execute));
        return new OperationHandle(operationId, requestId, operation.CreatedAtUtc, false);
    }

    public OperationState? Get(string operationId) =>
        _operations.TryGetValue(operationId, out var operation) ? operation.Snapshot() : null;

    public IReadOnlyList<OperationEvent>? GetEvents(string operationId, long fromSequence)
    {
        if (fromSequence < 0)
            throw new ArtifactRequestValidationException("fromSequence cannot be negative.");
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
    }

    private async Task ExecuteAsync(
        ArtifactOperation operation,
        Func<string, CancellationToken, Task<ArtifactJobExecution>> execute)
    {
        var started = DateTimeOffset.UtcNow;
        operation.MarkRunning();
        operation.Append(new ProgressOperationEventPayload("artifact-processing", null, 0));
        try
        {
            var execution = await execute(operation.OperationId, operation.CancellationToken);
            execution = execution with { Result = AttachIdentity(execution.Result) };
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
            ArtifactWorkerLog.OperationFailed(
                logger,
                exception,
                operation.OperationId,
                operation.TraceId);
            operation.Fail(ToWorkerError(exception, operation.TraceId));
        }
    }

    private OperationResult AttachIdentity(OperationResult result)
    {
        var version = result switch
        {
            TransformArtifactResult => ProcessorProtocol.RuntimeInstrumentationVersion,
            RenderArtifactResult => settings.Identity.IlSpyVersion,
            VerifyArtifactResult verification => verification.VerifierVersion,
            _ => null
        };
        if (version is null)
            return result;
        var identity = new ArtifactProcessorIdentity(
            settings.Identity.ReleaseId,
            settings.Identity.ProcessorId,
            version,
            settings.Identity.WorkerImageId);
        return result switch
        {
            TransformArtifactResult transform => transform with { Identity = identity },
            RenderArtifactResult render => render with { Identity = identity },
            VerifyArtifactResult verification => verification with { Identity = identity },
            _ => result
        };
    }

    private WorkerError ToWorkerError(Exception exception, string traceId)
    {
        var (code, category, message, retryable, safeToRetry) = exception switch
        {
            ArtifactRequestValidationException => (
                "invalid-argument",
                WorkerErrorCategory.InvalidArgument,
                "The artifact request is invalid.",
                false,
                false),
            ArtifactNotFoundException => (
                "artifact-not-found",
                WorkerErrorCategory.NotFound,
                "The requested artifact was not found.",
                false,
                false),
            ArtifactStoreUnavailableException => (
                "artifact-store-unavailable",
                WorkerErrorCategory.Unavailable,
                "The Artifact Store is unavailable.",
                true,
                true),
            ArtifactProcessorCrashedException => (
                "artifact-processor-failed",
                WorkerErrorCategory.Internal,
                "The isolated artifact processor failed.",
                true,
                true),
            _ => (
                "artifact-worker-internal",
                WorkerErrorCategory.Internal,
                "The artifact worker failed.",
                true,
                true)
        };
        return new WorkerError(
            code,
            category,
            message,
            retryable,
            safeToRetry,
            traceId,
            settings.Identity.ProcessorId,
            settings.Identity.WorkerImageId);
    }

    private void TrimCompleted()
    {
        if (_operations.Count < settings.Limits.MaxRetainedOperations)
            return;
        var removable = _operations.Values
            .Where(static operation => operation.IsTerminal)
            .OrderBy(static operation => operation.UpdatedAtUtc)
            .Take(Math.Max(1, settings.Limits.MaxRetainedOperations / 4))
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

    private sealed class ArtifactOperation : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<OperationEvent> _events = [];
        private readonly CancellationTokenSource _cancellation = new();
        private OperationStatus _status = OperationStatus.Accepted;
        private WorkerError? _error;
        private DateTimeOffset? _completedAtUtc;
        private long _sequence;

        public ArtifactOperation(string operationId, string requestId, OperationKind kind, string traceId)
        {
            OperationId = operationId;
            RequestId = requestId;
            Kind = kind;
            TraceId = traceId;
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
                var now = DateTimeOffset.UtcNow;
                _events.Add(new OperationEvent(OperationId, ++_sequence, now, TraceId, payload));
                UpdatedAtUtc = now;
            }
        }

        public void Complete(OperationCompletionStatus status, TimeSpan elapsed)
        {
            lock (_gate)
            {
                if (_completedAtUtc is not null)
                    return;
                var now = DateTimeOffset.UtcNow;
                _events.Add(new OperationEvent(
                    OperationId,
                    ++_sequence,
                    now,
                    TraceId,
                    new CompletedOperationEventPayload(status, elapsed)));
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
                _events.Add(new OperationEvent(
                    OperationId,
                    ++_sequence,
                    now,
                    TraceId,
                    new FailedOperationEventPayload(error)));
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
    }
}

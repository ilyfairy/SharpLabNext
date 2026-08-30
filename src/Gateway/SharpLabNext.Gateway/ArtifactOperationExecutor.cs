using SharpLabNext.Contracts;
using SharpLabNext.Operations;

namespace SharpLabNext.Gateway;

public sealed class ArtifactOperationExecutor(OperationStore operations, BoundedOperationScheduler scheduler, IArtifactWorkerClientFactory workerFactory, ArtifactPipelineOptions options, ILogger<ArtifactOperationExecutor> logger)
{
    private static readonly Action<ILogger, string, Exception?> LogFailure = LoggerMessage.Define<string>(LogLevel.Error, new EventId(30, nameof(LogFailure)), "Artifact operation {OperationId} failed.");

    public void QueueTransform(OperationStart operation, TransformArtifactRequest request) =>
        Queue(operation, request.RequestId, request.DeadlineUtc, request.ProcessorId, OperationKind.TransformArtifact, (worker, cancellationToken) => worker.StartTransformAsync(request, cancellationToken), static result => result is TransformArtifactResult);

    public void QueueRender(OperationStart operation, RenderArtifactRequest request) =>
        Queue(operation, request.RequestId, request.DeadlineUtc, request.ProcessorId, OperationKind.RenderArtifact, (worker, cancellationToken) => worker.StartRenderAsync(request, cancellationToken), static result => result is RenderArtifactResult);

    public void QueueVerify(OperationStart operation, VerifyArtifactRequest request) =>
        Queue(operation, request.RequestId, request.DeadlineUtc, request.ProcessorId, OperationKind.VerifyArtifact, (worker, cancellationToken) => worker.StartVerifyAsync(request, cancellationToken), static result => result is VerifyArtifactResult);

    private void Queue(OperationStart operation, string requestId, DateTimeOffset deadlineUtc, string processorId, OperationKind expectedKind, Func<IArtifactWorkerClient, CancellationToken, Task<OperationHandle>> start, Func<OperationResult, bool> resultMatches) =>
        scheduler.TryQueue(operation, () => ExecuteAsync(operation, requestId, deadlineUtc, processorId, expectedKind, start, resultMatches));

    private async Task ExecuteAsync(OperationStart operation, string requestId, DateTimeOffset deadlineUtc, string processorId, OperationKind expectedKind, Func<IArtifactWorkerClient, CancellationToken, Task<OperationHandle>> start, Func<OperationResult, bool> resultMatches)
    {
        var started = DateTimeOffset.UtcNow;
        using var deadlineCancellation = CreateDeadlineCancellation(deadlineUtc, started);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationToken, deadlineCancellation.Token);
        string? remoteOperationId = null;
        IArtifactWorkerClient? worker = null;
        try
        {
            linkedCancellation.Token.ThrowIfCancellationRequested();
            worker = workerFactory.Create(processorId);
            Append(operation, new ProgressOperationEventPayload("artifact-worker", "Starting the isolated artifact processor.", 0.05));
            var remote = await start(worker, linkedCancellation.Token).ConfigureAwait(false);
            remoteOperationId = remote.OperationId;
            await RelayAsync(worker, operation, requestId, expectedKind, resultMatches, remote, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            await CancelRemoteBoundedAsync(worker, remoteOperationId, "gateway-client-cancelled").ConfigureAwait(false);
            Append(operation, new CompletedOperationEventPayload(OperationCompletionStatus.Cancelled, DateTimeOffset.UtcNow - started));
        }
        catch (OperationCanceledException exception) when (deadlineCancellation.IsCancellationRequested)
        {
            LogFailure(logger, operation.Handle.OperationId, exception);
            await CancelRemoteBoundedAsync(worker, remoteOperationId, "gateway-deadline-exceeded").ConfigureAwait(false);
            AppendFailure(operation, new WorkerError("artifact-pipeline-deadline-exceeded", WorkerErrorCategory.DeadlineExceeded, "The artifact operation deadline elapsed.", true, true, operation.Handle.OperationId, processorId, "unknown"));
        }
        catch (ArtifactWorkerEndpointUnavailableException exception)
        {
            LogFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, new WorkerError("artifact-worker-unavailable", WorkerErrorCategory.Unavailable, "The selected artifact worker is not installed.", false, false, operation.Handle.OperationId, processorId, "unknown"));
        }
        catch (ArtifactWorkerClientException exception)
        {
            LogFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, exception.Error);
        }
        catch (Exception exception)
        {
            LogFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, new WorkerError("artifact-pipeline-internal", WorkerErrorCategory.Internal, "The artifact pipeline failed.", true, true, operation.Handle.OperationId, processorId, "unknown"));
        }
    }

    private async Task RelayAsync(IArtifactWorkerClient worker, OperationStart local, string requestId, OperationKind expectedKind, Func<OperationResult, bool> resultMatches, OperationHandle remote, CancellationToken cancellationToken)
    {
        var previousSequence = 0L;
        var acceptedSeen = false;
        var resultSeen = false;
        var terminalSeen = false;

        while (!terminalSeen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var events = await worker.GetEventsAsync(remote.OperationId, previousSequence, cancellationToken).ConfigureAwait(false);
            foreach (var operationEvent in events)
            {
                ValidateRemoteEvent(operationEvent, remote.OperationId, requestId, expectedKind, resultMatches, ref previousSequence, ref acceptedSeen, ref resultSeen, ref terminalSeen);
                if (operationEvent.Payload is not AcceptedOperationEventPayload)
                    Append(local, operationEvent.Payload);
            }

            if (terminalSeen)
                break;

            if (events.Count == 0)
            {
                var state = await worker.GetOperationAsync(remote.OperationId, cancellationToken).ConfigureAwait(false) ?? throw ProtocolFailure("The artifact worker lost an active operation.");
                if (IsTerminal(state.Status))
                {
                    var finalEvents = await worker.GetEventsAsync(remote.OperationId, previousSequence, cancellationToken).ConfigureAwait(false);
                    foreach (var operationEvent in finalEvents)
                    {
                        ValidateRemoteEvent(operationEvent, remote.OperationId, requestId, expectedKind, resultMatches, ref previousSequence, ref acceptedSeen, ref resultSeen, ref terminalSeen);
                        if (operationEvent.Payload is not AcceptedOperationEventPayload)
                            Append(local, operationEvent.Payload);
                    }
                    if (!terminalSeen)
                    {
                        throw ProtocolFailure("The artifact worker became terminal without exposing a terminal event.");
                    }
                }
            }

            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateRemoteEvent(OperationEvent operationEvent, string operationId, string requestId, OperationKind expectedKind, Func<OperationResult, bool> resultMatches, ref long previousSequence, ref bool acceptedSeen, ref bool resultSeen, ref bool terminalSeen)
    {
        if (terminalSeen || !StringComparer.Ordinal.Equals(operationEvent.OperationId, operationId) || operationEvent.Sequence <= previousSequence)
        {
            throw ProtocolFailure("The artifact worker event stream was invalid.");
        }

        if (operationEvent.Payload is AcceptedOperationEventPayload accepted)
        {
            if (acceptedSeen || previousSequence != 0 || !StringComparer.Ordinal.Equals(accepted.RequestId, requestId) || accepted.OperationKind != expectedKind)
            {
                throw ProtocolFailure("The artifact worker accepted a different operation.");
            }
            acceptedSeen = true;
        }
        else if (!acceptedSeen)
        {
            throw ProtocolFailure("The artifact worker stream did not begin with an accepted event.");
        }

        if (operationEvent.Payload is TypedResultOperationEventPayload typed)
        {
            if (resultSeen || !resultMatches(typed.Result))
                throw ProtocolFailure("The artifact worker returned an unexpected result.");
            resultSeen = true;
        }

        if (operationEvent.Payload is CompletedOperationEventPayload)
        {
            if (!resultSeen)
                throw ProtocolFailure("The artifact worker completed without a typed result.");
            terminalSeen = true;
        }
        else if (operationEvent.Payload is FailedOperationEventPayload)
        {
            terminalSeen = true;
        }

        previousSequence = operationEvent.Sequence;
    }

    private async Task CancelRemoteBoundedAsync(IArtifactWorkerClient? worker, string? operationId, string reason)
    {
        if (worker is null || operationId is null)
            return;
        using var timeout = new CancellationTokenSource(options.CancellationGracePeriod);
        try
        {
            await worker.CancelAsync(operationId, reason, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArtifactWorkerClientException or OperationCanceledException)
        {
            LogFailure(logger, operationId, exception);
        }
    }

    private CancellationTokenSource CreateDeadlineCancellation(DateTimeOffset deadlineUtc, DateTimeOffset now)
    {
        var remaining = deadlineUtc - now;
        if (remaining <= TimeSpan.Zero)
        {
            var elapsed = new CancellationTokenSource();
            elapsed.Cancel();
            return elapsed;
        }
        return new CancellationTokenSource(remaining < options.MaximumDuration ? remaining : options.MaximumDuration);
    }

    private void Append(OperationStart operation, OperationEventPayload payload) =>
        operations.Append(operation.Handle.OperationId, payload, DateTimeOffset.UtcNow);

    private void AppendFailure(OperationStart operation, WorkerError error) =>
        Append(operation, new FailedOperationEventPayload(error));

    private static ArtifactWorkerClientException ProtocolFailure(string message) => new(new WorkerError("artifact-worker-protocol-invalid", WorkerErrorCategory.Internal, message, false, false, "artifact-pipeline", "artifact-worker", "unknown"));

    private static bool IsTerminal(OperationStatus status) =>
        status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;
}

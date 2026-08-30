using SharpLabNext.Contracts;
using SharpLabNext.Operations;
using SharpLabNext.RuntimeSupervisor.Client;

namespace SharpLabNext.Gateway;

public sealed class RuntimeOperationExecutor(OperationStore operations, BoundedOperationScheduler scheduler, IRuntimeSupervisorClient supervisor, RuntimePipelineOptions options, ILogger<RuntimeOperationExecutor> logger)
{
    private static readonly Action<ILogger, string, Exception?> LogRuntimeFailure = LoggerMessage.Define<string>(LogLevel.Error, new EventId(30, nameof(LogRuntimeFailure)), "Runtime operation {OperationId} failed.");
    private static readonly Action<ILogger, string, string, Exception?> LogCancellationPropagationFailure =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(31, nameof(LogCancellationPropagationFailure)), "Could not propagate cancellation for Gateway operation {OperationId} to runtime operation {RemoteOperationId}.");

    public void QueueRun(OperationStart operation, RunRequest request, string? runtimeSessionId = null) =>
        scheduler.TryQueue(operation, () => ExecuteAsync(operation, OperationKind.Run, request.DeadlineUtc, cancellationToken => supervisor.StartRunAsync(request, runtimeSessionId, cancellationToken)));

    public void QueueJit(OperationStart operation, JitRequest request, string? runtimeSessionId = null) =>
        scheduler.TryQueue(operation, () => ExecuteAsync(operation, OperationKind.Jit, request.DeadlineUtc, cancellationToken => supervisor.StartJitAsync(request, runtimeSessionId, cancellationToken)));

    public Task ReleaseSessionAsync(string runtimeSessionId, CancellationToken cancellationToken = default) =>
        supervisor.ReleaseSessionAsync(runtimeSessionId, cancellationToken);

    private async Task ExecuteAsync(OperationStart operation, OperationKind expectedKind, DateTimeOffset requestedDeadline, Func<CancellationToken, Task<OperationHandle>> startRemoteAsync)
    {
        var started = DateTimeOffset.UtcNow;
        string? remoteOperationId = null;
        using var deadlineCancellation = CreateDeadlineCancellation(requestedDeadline, started);
        try
        {
            if (operation.CancellationToken.IsCancellationRequested)
            {
                CompleteCancelled(operation, started);
                return;
            }

            operations.Append(operation.Handle.OperationId, new ProgressOperationEventPayload("runtime-supervisor-dispatch", "Dispatching the runtime operation to Runtime Supervisor.", 0.02), DateTimeOffset.UtcNow);

            using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadlineCancellation.Token, operation.CancellationToken);
            var remoteStart = startRemoteAsync(startCancellation.Token);
            OperationHandle remoteHandle;
            try
            {
                remoteHandle = await remoteStart.WaitAsync(startCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = ObserveCompletionAsync(remoteStart);
                throw;
            }
            remoteOperationId = remoteHandle.OperationId;
            using var forwardingCancellation = new CancellationTokenSource();
            var forwarding = ForwardEventsAsync(operation, remoteOperationId, remoteHandle.RequestId, expectedKind, forwardingCancellation.Token);
            var localCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = operation.CancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), localCancellation);
            var deadlineElapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadlineRegistration = deadlineCancellation.Token.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), deadlineElapsed);
            var first = await Task.WhenAny(forwarding, localCancellation.Task, deadlineElapsed.Task).ConfigureAwait(false);
            if (first == forwarding)
            {
                await forwarding.ConfigureAwait(false);
                return;
            }

            if (first == deadlineElapsed.Task)
            {
                var deadlineGrace = Task.Delay(options.CancellationGracePeriod);
                first = await Task.WhenAny(forwarding, localCancellation.Task, deadlineGrace).ConfigureAwait(false);
                if (first == forwarding)
                {
                    await forwarding.ConfigureAwait(false);
                    return;
                }

                if (first == deadlineGrace)
                {
                    forwardingCancellation.Cancel();
                    _ = ObserveCompletionAsync(forwarding);
                    deadlineCancellation.Token.ThrowIfCancellationRequested();
                }
            }

            await PropagateCancellationAsync(operation, remoteOperationId, "gateway-operation-cancelled").ConfigureAwait(false);
            try
            {
                await forwarding.WaitAsync(options.CancellationGracePeriod).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                LogCancellationPropagationFailure(logger, operation.Handle.OperationId, remoteOperationId, exception);
                forwardingCancellation.Cancel();
                _ = ObserveCompletionAsync(forwarding);
                CompleteCancelledIfRequired(operation, started);
            }
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            if (remoteOperationId is not null)
            {
                await PropagateCancellationAsync(operation, remoteOperationId, "gateway-operation-cancelled").ConfigureAwait(false);
            }

            CompleteCancelledIfRequired(operation, started);
        }
        catch (OperationCanceledException exception) when (deadlineCancellation.IsCancellationRequested)
        {
            if (remoteOperationId is not null)
            {
                await PropagateCancellationAsync(operation, remoteOperationId, "gateway-deadline-exceeded").ConfigureAwait(false);
            }

            LogRuntimeFailure(logger, operation.Handle.OperationId, exception);
            AppendFailureIfRequired(operation, new WorkerError("runtime-pipeline-deadline-exceeded", WorkerErrorCategory.DeadlineExceeded, "The runtime operation deadline elapsed.", true, false, operation.Handle.OperationId, "runtime-supervisor", "unknown"));
        }
        catch (RuntimeEventForwardingException exception)
        {
            LogRuntimeFailure(logger, operation.Handle.OperationId, exception);
            if (operation.CancellationToken.IsCancellationRequested)
            {
                CompleteCancelledIfRequired(operation, started);
            }
            else
            {
                AppendFailureIfRequired(operation, new WorkerError("runtime-supervisor-protocol-invalid", WorkerErrorCategory.Internal, "The runtime supervisor returned an invalid event stream.", false, false, operation.Handle.OperationId, "runtime-supervisor", "unknown"));
            }
        }
        catch (RuntimeSupervisorClientException exception)
        {
            LogRuntimeFailure(logger, operation.Handle.OperationId, exception);
            if (operation.CancellationToken.IsCancellationRequested)
            {
                CompleteCancelledIfRequired(operation, started);
            }
            else
            {
                AppendFailureIfRequired(operation, exception.Error with { SafeToRetry = false });
            }
        }
        catch (Exception exception)
        {
            LogRuntimeFailure(logger, operation.Handle.OperationId, exception);
            if (operation.CancellationToken.IsCancellationRequested)
            {
                CompleteCancelledIfRequired(operation, started);
            }
            else
            {
                AppendFailureIfRequired(operation, new WorkerError("runtime-pipeline-internal", WorkerErrorCategory.Internal, "The runtime pipeline failed.", true, false, operation.Handle.OperationId, "runtime-supervisor", "unknown"));
            }
        }
    }

    private async Task ForwardEventsAsync(OperationStart operation, string remoteOperationId, string remoteRequestId, OperationKind expectedKind, CancellationToken cancellationToken)
    {
        var acceptedSeen = false;
        var terminalSeen = false;
        OperationResult? result = null;
        long previousRemoteSequence = 0;
        await foreach (var remoteEvent in supervisor.WatchEventsAsync(remoteOperationId, fromSequence: 0, cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(remoteEvent.OperationId, remoteOperationId, StringComparison.Ordinal) || remoteEvent.Sequence <= previousRemoteSequence)
            {
                throw new RuntimeEventForwardingException("The runtime supervisor event identity or sequence was invalid.");
            }

            if (terminalSeen)
            {
                throw new RuntimeEventForwardingException("The runtime supervisor emitted an event after a terminal event.");
            }

            previousRemoteSequence = remoteEvent.Sequence;
            if (remoteEvent.Payload is AcceptedOperationEventPayload accepted)
            {
                if (acceptedSeen || result is not null || accepted.OperationKind != expectedKind || !string.Equals(accepted.RequestId, remoteRequestId, StringComparison.Ordinal))
                {
                    throw new RuntimeEventForwardingException("The runtime supervisor accepted event did not match the Gateway operation.");
                }

                acceptedSeen = true;
                continue;
            }

            if (!acceptedSeen)
            {
                throw new RuntimeEventForwardingException("The runtime supervisor event stream did not begin with an accepted event.");
            }

            if (remoteEvent.Payload is TypedResultOperationEventPayload typed)
            {
                if (result is not null || !ResultMatchesKind(typed.Result, expectedKind))
                {
                    throw new RuntimeEventForwardingException("The runtime supervisor result type or cardinality was invalid.");
                }

                result = typed.Result;
            }

            if (remoteEvent.Payload is CompletedOperationEventPayload completed)
            {
                if (result is null || !CompletionMatchesResult(completed, result))
                {
                    throw new RuntimeEventForwardingException("The runtime supervisor completion did not match its typed result.");
                }

                terminalSeen = true;
            }
            else if (remoteEvent.Payload is FailedOperationEventPayload)
            {
                if (result is not null)
                {
                    throw new RuntimeEventForwardingException("The runtime supervisor failed after emitting a typed result.");
                }

                terminalSeen = true;
            }

            // OutputChunk.Data is an opaque base64 payload on the wire and must not be decoded here.
            var payload = remoteEvent.Payload is FailedOperationEventPayload failed
                ? failed with { Error = failed.Error with { SafeToRetry = false } }
                : remoteEvent.Payload;
            operations.Append(operation.Handle.OperationId, payload, DateTimeOffset.UtcNow);
        }

        if (!acceptedSeen || !terminalSeen)
        {
            throw new RuntimeEventForwardingException("The runtime supervisor event stream ended before reaching a terminal event.");
        }
    }

    private async Task PropagateCancellationAsync(OperationStart operation, string remoteOperationId, string reason)
    {
        try
        {
            var remoteCancellation = supervisor.CancelAsync(remoteOperationId, reason, CancellationToken.None);
            try
            {
                _ = await remoteCancellation.WaitAsync(options.ControlRequestTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = ObserveCompletionAsync(remoteCancellation);
                throw;
            }
        }
        catch (Exception exception)
        {
            LogCancellationPropagationFailure(logger, operation.Handle.OperationId, remoteOperationId, exception);
        }
    }

    private CancellationTokenSource CreateDeadlineCancellation(DateTimeOffset requestedDeadline, DateTimeOffset now)
    {
        var remaining = requestedDeadline - now;
        if (remaining <= TimeSpan.Zero)
        {
            var expired = new CancellationTokenSource();
            expired.Cancel();
            return expired;
        }

        return new CancellationTokenSource(remaining < options.MaximumDuration ? remaining : options.MaximumDuration);
    }

    private void CompleteCancelled(OperationStart operation, DateTimeOffset started) =>
        operations.Append(operation.Handle.OperationId, new CompletedOperationEventPayload(OperationCompletionStatus.Cancelled, DateTimeOffset.UtcNow - started), DateTimeOffset.UtcNow);

    private void CompleteCancelledIfRequired(OperationStart operation, DateTimeOffset started)
    {
        if (!IsTerminal(operation.Handle.OperationId))
        {
            CompleteCancelled(operation, started);
        }
    }

    private void AppendFailureIfRequired(OperationStart operation, WorkerError error)
    {
        if (!IsTerminal(operation.Handle.OperationId))
        {
            operations.Append(operation.Handle.OperationId, new FailedOperationEventPayload(error), DateTimeOffset.UtcNow);
        }
    }

    private bool IsTerminal(string operationId) =>
        operations.Get(operationId)?.Status is OperationStatus.Completed
            or OperationStatus.Failed
            or OperationStatus.Cancelled;

    private static bool ResultMatchesKind(OperationResult result, OperationKind expectedKind) =>
        (expectedKind, result) is
            (OperationKind.Run, RunResult) or
            (OperationKind.Jit, JitResult);

    private static bool CompletionMatchesResult(CompletedOperationEventPayload completion, OperationResult result)
    {
        var resultWasCancelled = result is RunResult { Status: RunTerminalStatus.Cancelled }
            or JitResult { Status: JitTerminalStatus.Cancelled };
        return completion.Status == (resultWasCancelled ? OperationCompletionStatus.Cancelled : OperationCompletionStatus.Completed);
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    private sealed class RuntimeEventForwardingException(string message) : Exception(message);
}

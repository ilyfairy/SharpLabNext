using SharpLabNext.Contracts;
using SharpLabNext.Operations;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.Gateway;

public sealed class ExplainOperationExecutor(OperationStore operations, BoundedOperationScheduler scheduler, IToolchainWorkerClientFactory workers, BuildPipelineOptions options, ILogger<ExplainOperationExecutor> logger)
{
    private static readonly Action<ILogger, string, Exception?> LogExplainFailure = LoggerMessage.Define<string>(LogLevel.Error, new EventId(24, nameof(LogExplainFailure)), "Explain operation {OperationId} failed.");

    public void QueueExplain(OperationStart operation, ExplainRequest request, string workerId) => scheduler.TryQueue(operation, () => ExecuteExplainAsync(operation, request, workerId));

    private async Task ExecuteExplainAsync(OperationStart operation, ExplainRequest request, string workerId)
    {
        var started = DateTimeOffset.UtcNow;
        using var deadlineCancellation = CreateDeadlineCancellation(request.DeadlineUtc, started);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationToken, deadlineCancellation.Token);
        try
        {
            linkedCancellation.Token.ThrowIfCancellationRequested();
            Append(operation, new ProgressOperationEventPayload("explain", "Explaining the immutable C# syntax snapshot.", 0.1));
            var worker = workers.Create(workerId);
            var response = await worker.ExplainAsync(request, linkedCancellation.Token).ConfigureAwait(false);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            Append(operation, new TypedResultOperationEventPayload(response.Result));
            Append(operation, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, DateTimeOffset.UtcNow - started));
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            Append(operation, new CompletedOperationEventPayload(OperationCompletionStatus.Cancelled, DateTimeOffset.UtcNow - started));
        }
        catch (OperationCanceledException exception) when (deadlineCancellation.IsCancellationRequested)
        {
            LogExplainFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, new WorkerError("explain-deadline-exceeded", WorkerErrorCategory.DeadlineExceeded, "The explain deadline elapsed.", true, true, operation.Handle.OperationId, workerId, "unknown"));
        }
        catch (ToolchainWorkerException exception)
        {
            LogExplainFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, exception.Error);
        }
        catch (ToolchainWorkerEndpointUnavailableException exception)
        {
            LogExplainFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, new WorkerError("worker-unavailable", WorkerErrorCategory.Unavailable, "The selected explain provider is not installed.", false, false, operation.Handle.OperationId, exception.WorkerId, "unknown"));
        }
        catch (Exception exception)
        {
            LogExplainFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, new WorkerError("explain-pipeline-internal", WorkerErrorCategory.Internal, "The explain pipeline failed.", true, true, operation.Handle.OperationId, workerId, "unknown"));
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

    private void Append(OperationStart operation, OperationEventPayload payload) => operations.Append(operation.Handle.OperationId, payload, DateTimeOffset.UtcNow);

    private void AppendFailure(OperationStart operation, WorkerError error) => Append(operation, new FailedOperationEventPayload(error));
}

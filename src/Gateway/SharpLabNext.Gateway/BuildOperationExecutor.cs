using System.Net;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.Operations;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.Gateway;

public sealed class BuildOperationExecutor(
    OperationStore operations,
    BoundedOperationScheduler scheduler,
    IToolchainWorkerClientFactory workers,
    IBuildArtifactPublisher artifactPublisher,
    BuildPipelineOptions options,
    ILogger<BuildOperationExecutor> logger)
{
    private static readonly Action<ILogger, string, Exception?> LogBuildFailure = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(20, nameof(LogBuildFailure)),
        "Build operation {OperationId} failed.");

    public void QueueBuild(OperationStart operation, BuildRequest request, string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        scheduler.TryQueue(operation, () => ExecuteBuildAsync(operation, request, workerId));
    }

    private async Task ExecuteBuildAsync(OperationStart operation, BuildRequest request, string workerId)
    {
        var started = DateTimeOffset.UtcNow;
        using var deadlineCancellation = CreateDeadlineCancellation(request, started);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operation.CancellationToken,
            deadlineCancellation.Token);
        try
        {
            linkedCancellation.Token.ThrowIfCancellationRequested();
            Append(operation, new ProgressOperationEventPayload(
                $"{workerId}-build",
                "Compiling the immutable workspace snapshot.",
                0.1));
            var worker = workers.Create(workerId);
            var response = await worker.BuildAsync(request, linkedCancellation.Token).ConfigureAwait(false);
            linkedCancellation.Token.ThrowIfCancellationRequested();

            var result = response.Result;
            AppendDiagnostics(operation, result);
            if (result is BuildResult { Outcome: BuildOutcome.Succeeded } buildResult)
            {
                var artifactRef = buildResult.ArtifactRef
                    ?? throw new BuildArtifactPublishingException(
                        "The worker omitted the artifact reference for a successful build.");
                Append(operation, new ProgressOperationEventPayload(
                    "artifact-store",
                    response.DevelopmentArtifact is null
                        ? "Verifying the compiler-published artifact."
                        : "Verifying and publishing the development artifact envelope.",
                    0.8));
                PublishedBuildArtifact published;
                if (response.DevelopmentArtifact is { } envelope)
                {
                    if (envelope.ArtifactRef != artifactRef)
                    {
                        throw new BuildArtifactPublishingException(
                            "The development artifact envelope identity does not match the build result.");
                    }
                    published = await artifactPublisher.PublishAsync(envelope, linkedCancellation.Token)
                        .ConfigureAwait(false);
                    if (published.ArtifactRef != artifactRef)
                    {
                        throw new BuildArtifactPublishingException(
                            "The development artifact publication changed its content address.");
                    }
                }
                else
                {
                    published = await artifactPublisher.AcceptPublishedAsync(
                        artifactRef,
                        buildResult.Identity,
                        linkedCancellation.Token).ConfigureAwait(false);
                }
                result = buildResult;
                Append(operation, new ArtifactProducedOperationEventPayload(
                    published.ArtifactRef,
                    published.ArtifactFormat,
                    "build-output"));
            }

            linkedCancellation.Token.ThrowIfCancellationRequested();
            Append(operation, new TypedResultOperationEventPayload(result));
            Append(operation, new CompletedOperationEventPayload(
                OperationCompletionStatus.Completed,
                DateTimeOffset.UtcNow - started));
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            Append(operation, new CompletedOperationEventPayload(
                OperationCompletionStatus.Cancelled,
                DateTimeOffset.UtcNow - started));
        }
        catch (OperationCanceledException exception) when (deadlineCancellation.IsCancellationRequested)
        {
            LogBuildFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, new WorkerError(
                "build-deadline-exceeded",
                WorkerErrorCategory.DeadlineExceeded,
                "The build deadline elapsed.",
                true,
                true,
                operation.Handle.OperationId,
                workerId,
                "unknown"));
        }
        catch (ToolchainWorkerEndpointUnavailableException exception)
        {
            LogBuildFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, new WorkerError(
                "worker-unavailable",
                WorkerErrorCategory.Unavailable,
                "The selected compiler worker is not installed.",
                true,
                true,
                operation.Handle.OperationId,
                workerId,
                "unknown"));
        }
        catch (ToolchainWorkerException exception)
        {
            LogBuildFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, exception.Error);
        }
        catch (ArtifactStoreHttpException exception)
        {
            LogBuildFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, ArtifactStoreFailure(operation, exception.StatusCodeValue));
        }
        catch (HttpRequestException exception)
        {
            LogBuildFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, ArtifactStoreFailure(operation, exception.StatusCode));
        }
        catch (BuildArtifactPublishingException exception)
        {
            LogBuildFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, new WorkerError(
                "worker-artifact-invalid",
                WorkerErrorCategory.Internal,
                "The toolchain worker returned an invalid artifact.",
                false,
                false,
                operation.Handle.OperationId,
                workerId,
                "unknown"));
        }
        catch (Exception exception)
        {
            LogBuildFailure(logger, operation.Handle.OperationId, exception);
            AppendFailure(operation, new WorkerError(
                "build-pipeline-internal",
                WorkerErrorCategory.Internal,
                "The build pipeline failed.",
                true,
                true,
                operation.Handle.OperationId,
                workerId,
                "unknown"));
        }
    }

    private CancellationTokenSource CreateDeadlineCancellation(BuildRequest request, DateTimeOffset now)
    {
        var remaining = request.DeadlineUtc - now;
        if (remaining <= TimeSpan.Zero)
        {
            var elapsed = new CancellationTokenSource();
            elapsed.Cancel();
            return elapsed;
        }

        var effectiveDuration = remaining < options.MaximumDuration ? remaining : options.MaximumDuration;
        return new CancellationTokenSource(effectiveDuration);
    }

    private void AppendDiagnostics(OperationStart operation, OperationResult result)
    {
        var diagnostics = result switch
        {
            BuildResult build => build.Diagnostics,
            CompilationCheckResult check => check.Diagnostics,
            GeneratedSourceResult => [],
            _ => []
        };
        foreach (var diagnostic in diagnostics)
        {
            Append(operation, new DiagnosticOperationEventPayload(diagnostic));
        }
    }

    private void Append(OperationStart operation, OperationEventPayload payload) =>
        operations.Append(operation.Handle.OperationId, payload, DateTimeOffset.UtcNow);

    private void AppendFailure(OperationStart operation, WorkerError error) =>
        Append(operation, new FailedOperationEventPayload(error));

    private static WorkerError ArtifactStoreFailure(OperationStart operation, HttpStatusCode? statusCode)
    {
        var resourceExhausted = statusCode == HttpStatusCode.RequestEntityTooLarge;
        var unavailable = statusCode is null
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
        return new WorkerError(
            resourceExhausted
                ? "artifact-store-limit-exceeded"
                : unavailable ? "artifact-store-unavailable" : "artifact-store-rejected-artifact",
            resourceExhausted
                ? WorkerErrorCategory.ResourceExhausted
                : unavailable ? WorkerErrorCategory.Unavailable : WorkerErrorCategory.Internal,
            resourceExhausted
                ? "The compiled artifact exceeds the Artifact Store limit."
                : unavailable
                    ? "The Artifact Store is unavailable."
                    : "The Artifact Store rejected the compiled artifact.",
            unavailable,
            unavailable,
            operation.Handle.OperationId,
            "artifact-store",
            "unknown");
    }
}

using SharpLabNext.Contracts;

namespace SharpLabNext.Contracts.Grpc;

public interface IWorkerControlService
{
    ValueTask<WorkerDescriptor> NegotiateAsync(
        NegotiateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<WorkerDescriptor> DescribeAsync(
        DescribeRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<HealthResponse> HealthAsync(
        HealthRequest request,
        CancellationToken cancellationToken = default);
}

public interface ILanguageSessionService
{
    ValueTask<LanguageSession> OpenLanguageSessionAsync(
        OpenLanguageSessionRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<LanguageFrame> LanguageChannelAsync(
        IAsyncEnumerable<LanguageFrame> frames,
        CancellationToken cancellationToken = default);

    ValueTask CloseLanguageSessionAsync(
        CloseLanguageSessionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IBuildService
{
    ValueTask<OperationHandle> StartBuildAsync(
        BuildRequest request,
        CancellationToken cancellationToken = default);
}

public interface IArtifactService
{
    ValueTask<OperationHandle> StartTransformArtifactAsync(
        TransformArtifactRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<OperationHandle> StartRenderArtifactAsync(
        RenderArtifactRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<OperationHandle> StartVerifyArtifactAsync(
        VerifyArtifactRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeJobService
{
    ValueTask<OperationHandle> StartRunAsync(
        RunRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<OperationHandle> StartJitAsync(
        JitRequest request,
        CancellationToken cancellationToken = default);
}

public interface IOperationsService
{
    IAsyncEnumerable<OperationEvent> WatchOperationAsync(
        WatchOperationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<OperationState> GetOperationAsync(
        GetOperationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<CancelResult> CancelOperationAsync(
        CancelOperationRequest request,
        CancellationToken cancellationToken = default);
}

using System.Text.Json;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.Operations;

namespace SharpLabNext.Gateway;

internal sealed record OperationControlResponse(int StatusCode, object? Payload, string? Location = null)
{
    public IResult ToHttpResult() => StatusCode == StatusCodes.Status202Accepted
        ? Results.Accepted(Location, Payload) : StatusCode == StatusCodes.Status404NotFound && Payload is null
            ? Results.NotFound() : Results.Json(Payload, ContractJson.CreateSerializerOptions(), statusCode: StatusCode);
}

internal sealed class OperationControlService(CatalogDocument catalog, PipelineResolutionRegistry registry, OperationStore operations, BuildOperationExecutor buildExecutor, ArtifactOperationExecutor artifactExecutor, RuntimeOperationExecutor runtimeExecutor, ExplainOperationExecutor explainExecutor, LanguageWorkerEndpointRegistry workerEndpoints, GatewayDependencyHealthService dependencyHealth)
{
    private static readonly JsonSerializerOptions SerializerOptions = ContractJson.CreateSerializerOptions();

    public Task<OperationControlResponse> StartAsync(string operation, JsonElement request, string traceId, CancellationToken cancellationToken) =>
        StartAsync(operation, request, traceId, runtimeSessionId: null, cancellationToken);

    public Task<OperationControlResponse> StartAsync(string operation, JsonElement request, string traceId, string? runtimeSessionId, CancellationToken cancellationToken) => operation switch
        {
            "build" => StartBuildAsync(Deserialize<BuildRequest>(request), traceId, cancellationToken),
            "explain" => StartExplainAsync(Deserialize<ExplainRequest>(request), traceId, cancellationToken),
            "artifact-transform" => StartTransformAsync(Deserialize<TransformArtifactRequest>(request), traceId, cancellationToken),
            "artifact-render" => StartRenderAsync(Deserialize<RenderArtifactRequest>(request), traceId, cancellationToken),
            "verification" => StartVerificationAsync(Deserialize<VerifyArtifactRequest>(request), traceId, cancellationToken),
            "run" => StartRunAsync(Deserialize<RunRequest>(request), traceId, runtimeSessionId, cancellationToken),
            "jit" => StartJitAsync(Deserialize<JitRequest>(request), traceId, runtimeSessionId, cancellationToken),
            _ => Task.FromResult(Problem(StatusCodes.Status400BadRequest, "unsupported-operation", "The requested operation kind is not supported."))
        };

    public async Task<OperationControlResponse> StartBuildAsync(BuildRequest request, string traceId, CancellationToken cancellationToken)
    {
        var resolution = registry.Get(request.PipelineResolutionId, DateTimeOffset.UtcNow);
        if (resolution is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid-pipeline-resolution", "Resolve the selection again before building.");
        }

        if (!string.Equals(resolution.EffectiveSelection.ToolchainId, request.ToolchainId, StringComparison.Ordinal) || !string.Equals(resolution.EffectiveSelection.ReferenceSetId, request.ReferenceSetId, StringComparison.Ordinal))
        {
            return Problem(StatusCodes.Status400BadRequest, "pipeline-mismatch", "Build request does not match the resolved pipeline.");
        }

        var toolchain = catalog.Toolchains.FirstOrDefault(candidate => string.Equals(candidate.Id, resolution.EffectiveSelection.ToolchainId, StringComparison.Ordinal));
        var compilerWorkerId = resolution.PipelinePlan.CompilerWorkerId;
        if (toolchain is null || string.IsNullOrWhiteSpace(compilerWorkerId) || !string.Equals(toolchain.WorkerId, compilerWorkerId, StringComparison.Ordinal))
        {
            return Problem(StatusCodes.Status400BadRequest, "pipeline-mismatch", "The resolved compiler worker does not match the active catalog.");
        }

        if (!workerEndpoints.TryGet(compilerWorkerId, out _))
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "worker-unavailable", "The selected compiler worker is not installed.");
        }

        var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var unavailableReason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, request.Target == BuildTarget.Artifact);
        if (unavailableReason is not null)
            return DependencyUnavailable(unavailableReason);

        var operation = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.Build, traceId, DateTimeOffset.UtcNow);
        if (!operation.Handle.IsExisting)
            buildExecutor.QueueBuild(operation, request, compilerWorkerId);

        return Accepted(operation.Handle);
    }

    public async Task<OperationControlResponse> StartExplainAsync(ExplainRequest request, string traceId, CancellationToken cancellationToken)
    {
        if (!HasOperationIdentity(request.RequestId, request.IdempotencyKey))
            return InvalidOperationIdentity();

        var resolution = registry.Get(request.PipelineResolutionId, DateTimeOffset.UtcNow);
        if (resolution is null ||
            request.Workspace is null ||
            !StringComparer.Ordinal.Equals(resolution.EffectiveSelection.OutputId, "explain") ||
            !StringComparer.Ordinal.Equals(resolution.EffectiveSelection.LanguageId, "csharp") ||
            !StringComparer.Ordinal.Equals(request.Workspace.LanguageId, resolution.EffectiveSelection.LanguageId) ||
            !StringComparer.Ordinal.Equals(request.Workspace.ReferenceSetId, resolution.EffectiveSelection.ReferenceSetId) ||
            resolution.PipelinePlan.Stages.Count != 1 ||
            resolution.PipelinePlan.Stages[0] is not { Kind: PipelineStageKind.Explain } stage ||
            !StringComparer.Ordinal.Equals(stage.ProviderId, resolution.PipelinePlan.CompilerWorkerId))
        {
            return Problem(StatusCodes.Status400BadRequest, "pipeline-mismatch", "Explain request does not match an active resolved C# pipeline.");
        }

        if (!workerEndpoints.TryGet(stage.ProviderId, out _))
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "worker-unavailable", "The selected explain provider is not installed.");
        }

        var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var unavailableReason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: false);
        if (unavailableReason is not null)
            return DependencyUnavailable(unavailableReason);

        var operation = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.Explain, traceId, DateTimeOffset.UtcNow);
        if (!operation.Handle.IsExisting)
            explainExecutor.QueueExplain(operation, request, stage.ProviderId);
        return Accepted(operation.Handle);
    }

    public async Task<OperationControlResponse> StartTransformAsync(TransformArtifactRequest request, string traceId, CancellationToken cancellationToken)
    {
        if (!HasOperationIdentity(request.RequestId, request.IdempotencyKey))
            return InvalidOperationIdentity();
        var resolution = registry.Get(request.PipelineResolutionId, DateTimeOffset.UtcNow);
        if (resolution is null || !MatchesArtifactPipeline(resolution, request.ProcessorId, request.TransformId, PipelineStageKind.Transform) || !MatchesTransformOptions(request))
        {
            return Problem(StatusCodes.Status400BadRequest, "pipeline-mismatch", "Transform request does not match an active resolved pipeline.");
        }

        var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var unavailableReason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: true);
        if (unavailableReason is not null)
            return DependencyUnavailable(unavailableReason);
        var operation = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.TransformArtifact, traceId, DateTimeOffset.UtcNow);
        if (!operation.Handle.IsExisting)
            artifactExecutor.QueueTransform(operation, request);
        return Accepted(operation.Handle);
    }

    public async Task<OperationControlResponse> StartRenderAsync(RenderArtifactRequest request, string traceId, CancellationToken cancellationToken)
    {
        if (!HasOperationIdentity(request.RequestId, request.IdempotencyKey))
            return InvalidOperationIdentity();
        var resolution = registry.Get(request.PipelineResolutionId, DateTimeOffset.UtcNow);
        if (resolution is null || !MatchesArtifactPipeline(resolution, request.ProcessorId, request.OutputId, PipelineStageKind.Render))
        {
            return Problem(StatusCodes.Status400BadRequest, "pipeline-mismatch", "Render request does not match an active resolved pipeline.");
        }

        var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var unavailableReason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: true);
        if (unavailableReason is not null)
            return DependencyUnavailable(unavailableReason);
        var operation = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.RenderArtifact, traceId, DateTimeOffset.UtcNow);
        if (!operation.Handle.IsExisting)
            artifactExecutor.QueueRender(operation, request);
        return Accepted(operation.Handle);
    }

    public async Task<OperationControlResponse> StartVerificationAsync(VerifyArtifactRequest request, string traceId, CancellationToken cancellationToken)
    {
        if (!HasOperationIdentity(request.RequestId, request.IdempotencyKey))
            return InvalidOperationIdentity();
        var resolution = registry.Get(request.PipelineResolutionId, DateTimeOffset.UtcNow);
        if (resolution is null || !MatchesArtifactPipeline(resolution, request.ProcessorId, "il-verify", PipelineStageKind.Verify))
        {
            return Problem(StatusCodes.Status400BadRequest, "pipeline-mismatch", "Verification request does not match an active resolved pipeline.");
        }

        var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var unavailableReason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: true);
        if (unavailableReason is not null)
            return DependencyUnavailable(unavailableReason);
        var operation = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.VerifyArtifact, traceId, DateTimeOffset.UtcNow);
        if (!operation.Handle.IsExisting)
            artifactExecutor.QueueVerify(operation, request);
        return Accepted(operation.Handle);
    }

    public async Task<OperationControlResponse> StartRunAsync(RunRequest request, string traceId, CancellationToken cancellationToken) =>
        await StartRunAsync(request, traceId, runtimeSessionId: null, cancellationToken).ConfigureAwait(false);

    private async Task<OperationControlResponse> StartRunAsync(RunRequest request, string traceId, string? runtimeSessionId, CancellationToken cancellationToken)
    {
        if (!HasOperationIdentity(request.RequestId, request.IdempotencyKey))
            return InvalidOperationIdentity();

        var resolution = registry.Get(request.PipelineResolutionId, DateTimeOffset.UtcNow);
        var expectedOutputId = request.Options?.Instrumentation == RunInstrumentation.ExecutionFlow
            ? "execution-flow" : "run";
        if (resolution is null || request.Options is null || !MatchesRuntimePipeline(resolution, request.RuntimeProfileId, request.Options.SecurityPolicyId, expectedOutputId, PipelineStageKind.Run))
        {
            return Problem(StatusCodes.Status400BadRequest, "pipeline-mismatch", "Run request does not match an active resolved pipeline.");
        }

        var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var unavailableReason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: true);
        if (unavailableReason is not null)
            return DependencyUnavailable(unavailableReason);

        var operation = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.Run, traceId, DateTimeOffset.UtcNow);
        if (!operation.Handle.IsExisting)
            runtimeExecutor.QueueRun(operation, request, runtimeSessionId);
        return Accepted(operation.Handle);
    }

    public async Task<OperationControlResponse> StartJitAsync(JitRequest request, string traceId, CancellationToken cancellationToken) =>
        await StartJitAsync(request, traceId, runtimeSessionId: null, cancellationToken).ConfigureAwait(false);

    private async Task<OperationControlResponse> StartJitAsync(JitRequest request, string traceId, string? runtimeSessionId, CancellationToken cancellationToken)
    {
        if (!HasOperationIdentity(request.RequestId, request.IdempotencyKey))
            return InvalidOperationIdentity();

        var resolution = registry.Get(request.PipelineResolutionId, DateTimeOffset.UtcNow);
        if (resolution is null || request.Options is null || !MatchesRuntimePipeline(resolution, request.RuntimeProfileId, request.Options.SecurityPolicyId, "jit-asm", PipelineStageKind.Jit))
        {
            return Problem(StatusCodes.Status400BadRequest, "pipeline-mismatch", "JIT request does not match an active resolved pipeline.");
        }

        var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var unavailableReason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: true);
        if (unavailableReason is not null)
            return DependencyUnavailable(unavailableReason);

        var operation = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.Jit, traceId, DateTimeOffset.UtcNow);
        if (!operation.Handle.IsExisting)
            runtimeExecutor.QueueJit(operation, request, runtimeSessionId);
        return Accepted(operation.Handle);
    }

    public Task ReleaseRuntimeSessionAsync(string runtimeSessionId, CancellationToken cancellationToken = default) =>
        runtimeExecutor.ReleaseSessionAsync(runtimeSessionId, cancellationToken);

    public OperationControlResponse GetState(string operationId)
    {
        var state = operations.Get(operationId);
        return state is null
            ? new OperationControlResponse(StatusCodes.Status404NotFound, null) : new OperationControlResponse(StatusCodes.Status200OK, state);
    }

    public OperationControlResponse Cancel(string operationId, string? requestOperationId, string? reason)
    {
        if (requestOperationId is not null && !string.Equals(operationId, requestOperationId, StringComparison.Ordinal))
            return Problem(StatusCodes.Status400BadRequest, "operation-id-mismatch", null);

        var result = operations.Cancel(operationId, reason, DateTimeOffset.UtcNow);
        return result.Disposition == CancelDisposition.NotFound
            ? new OperationControlResponse(StatusCodes.Status404NotFound, null) : new OperationControlResponse(StatusCodes.Status200OK, result);
    }

    private static T Deserialize<T>(JsonElement request) =>
        request.Deserialize<T>(SerializerOptions) ?? throw new JsonException($"The {typeof(T).Name} command request was empty.");

    private static OperationControlResponse Accepted(OperationHandle handle) => new(StatusCodes.Status202Accepted, handle, $"/api/v1/operations/{handle.OperationId}");

    private static OperationControlResponse InvalidOperationIdentity() => Problem(StatusCodes.Status400BadRequest, "invalid-request-identity", "RequestId and IdempotencyKey are required.");

    private static OperationControlResponse DependencyUnavailable(string message) => Problem(StatusCodes.Status503ServiceUnavailable, "profile-unavailable", message);

    private static OperationControlResponse Problem(int statusCode, string error, string? message) =>
        new(statusCode, new OperationControlProblem(error, message));

    private static bool HasOperationIdentity(string? requestId, string? idempotencyKey) =>
        !string.IsNullOrWhiteSpace(requestId) && !string.IsNullOrWhiteSpace(idempotencyKey);

    private static bool MatchesRuntimePipeline(ResolveSelectionResponse resolution, string runtimeProfileId, string securityPolicyId, string outputId, PipelineStageKind stageKind)
    {
        if (!string.Equals(resolution.EffectiveSelection.RuntimeId, runtimeProfileId, StringComparison.Ordinal) || !string.Equals(resolution.EffectiveSelection.OutputId, outputId, StringComparison.Ordinal) || !string.Equals(resolution.PipelinePlan.RuntimeId, runtimeProfileId, StringComparison.Ordinal) || !string.Equals(resolution.PipelinePlan.SecurityPolicyId, securityPolicyId, StringComparison.Ordinal) || resolution.PipelinePlan.Stages.Count == 0)
        {
            return false;
        }

        var stage = resolution.PipelinePlan.Stages[^1];
        return stage.Kind == stageKind &&
               string.Equals(stage.Id, outputId, StringComparison.Ordinal) &&
               string.Equals(stage.ProviderId, runtimeProfileId, StringComparison.Ordinal);
    }

    private static bool MatchesArtifactPipeline(ResolveSelectionResponse resolution, string processorId, string outputId, PipelineStageKind stageKind)
    {
        if (resolution.PipelinePlan.Stages.Count < 2)
            return false;
        var stage = stageKind == PipelineStageKind.Transform
            ? resolution.PipelinePlan.Stages.FirstOrDefault(candidate => candidate.Kind == stageKind && StringComparer.Ordinal.Equals(candidate.Id, outputId)) : StringComparer.Ordinal.Equals(resolution.EffectiveSelection.OutputId, outputId)
                ? resolution.PipelinePlan.Stages[^1] : null;
        if (stage is null)
            return false;
        return stage.Kind == stageKind && StringComparer.Ordinal.Equals(stage.Id, outputId) && StringComparer.Ordinal.Equals(stage.ProviderId, processorId);
    }

    private static bool MatchesTransformOptions(TransformArtifactRequest request) =>
        request.TransformId != "runtime-instrumentation-v1" ||
        (request.Options.PreservePortablePdb && request.Options.PreserveSequencePoints && StringComparer.Ordinal.Equals(request.Options.RewriterProfileId, "execution-flow-v1"));

    private sealed record OperationControlProblem(string Error, string? Message);
}

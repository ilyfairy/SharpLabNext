using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.Http;
using SharpLabNext.InternalServices;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.ArtifactWorker.Sdk;

public static class ArtifactWorkerEndpointExtensions
{
    public static IServiceCollection AddSharpLabNextArtifactWorker(this IServiceCollection services, ServiceIdentity identity, string workerImageId, ArtifactWorkerCapabilityManifest capabilityManifest)
    {
        ArtifactWorkerCapabilityManifestSerializer.Validate(capabilityManifest, identity);
        if (string.IsNullOrWhiteSpace(workerImageId) || workerImageId.Length > 256 || workerImageId.Contains('\0'))
            throw new ArgumentException("Worker image identity is invalid.", nameof(workerImageId));

        services.AddSharpLabNextWorker(identity);
        services.AddSingleton(capabilityManifest);
        services.AddSingleton(new ArtifactWorkerHostIdentity(workerImageId));
        services.AddSingleton(new ArtifactWorkerRuntimeState(capabilityManifest.WorkerId));
        services.AddSingleton<ArtifactWorkerHandlerRegistry>();
        services.AddSingleton<ArtifactWorkerOperationRegistry>();
        return services;
    }

    public static WebApplication MapSharpLabNextArtifactWorker(this WebApplication app)
    {
        app.UseSharpLabNextInternalServiceAuthentication(InternalServiceAuthenticationOptions.FromConfiguration(app.Configuration, app.Environment));
        app.MapGet("/health/live", () => Results.Ok(new { Status = "live" }));
        app.MapGet("/health/ready", HandleReadinessAsync);
        app.MapGet("/api/v1/worker/describe", HandleDescribeAsync);
        app.MapGet("/api/v1/worker/capabilities", (ArtifactWorkerCapabilityManifest manifest) => manifest);
        app.MapPost("/api/v1/artifact-transforms", StartTransform);
        app.MapPost("/api/v1/artifact-renders", StartRender);
        app.MapPost("/api/v1/verifications", StartVerification);
        app.MapGet("/api/v1/operations/{operationId}", GetOperation);
        app.MapGet("/api/v1/operations/{operationId}/events", GetEvents);
        app.MapPost("/api/v1/operations/{operationId}/cancel", CancelOperation);
        return app;
    }

    private static async Task<IResult> HandleReadinessAsync(IServiceProvider services, ServiceIdentity identity, ArtifactWorkerCapabilityManifest manifest, ArtifactWorkerRuntimeState runtime, CancellationToken cancellationToken)
    {
        var checks = await CheckAsync(services, cancellationToken).ConfigureAwait(false);
        var status = checks.Any(static check => check.Status == HealthStatus.Unhealthy)
            ? HealthStatus.Unhealthy : checks.Any(static check => check.Status == HealthStatus.Degraded)
                ? HealthStatus.Degraded : HealthStatus.Healthy;
        var response = new HealthResponse(status, manifest.WorkerId, runtime.InstanceId, identity.Protocol, DateTimeOffset.UtcNow, checks);
        return status == HealthStatus.Unhealthy
            ? Results.Json(response, ContractJson.CreateSerializerOptions(), statusCode: StatusCodes.Status503ServiceUnavailable) : Results.Ok(response);
    }

    private static async Task<IResult> HandleDescribeAsync(IServiceProvider services, ServiceIdentity identity, ArtifactWorkerCapabilityManifest manifest, ArtifactWorkerHostIdentity hostIdentity, ArtifactWorkerRuntimeState runtime, CancellationToken cancellationToken)
    {
        var checks = await CheckAsync(services, cancellationToken).ConfigureAwait(false);
        var available = checks.All(static check => check.Status != HealthStatus.Unhealthy);
        var profiles = new[] { manifest.WorkerId };
        var service = identity with { Status = available ? "ready" : "unhealthy" };
        return Results.Ok(new WorkerDescriptor(service, runtime.InstanceId, WorkerKind.ArtifactProcessor, hostIdentity.WorkerImageId, identity.Protocol, [identity.Protocol], manifest.Capabilities.Select(capability => new WorkerCapabilityDescriptor(capability, 1, available, profiles, available ? null : "The artifact worker is unhealthy.")).ToArray(), profiles, runtime.StartedAtUtc));
    }

    private static IResult StartTransform(TransformArtifactRequest request, ArtifactWorkerHandlerRegistry handlers, ArtifactWorkerOperationRegistry operations, ArtifactWorkerCapabilityManifest manifest, ServiceIdentity identity, HttpContext context)
    {
        try
        {
            ValidateCommon(request.RequestId, request.IdempotencyKey, request.PipelineResolutionId, request.ProcessorId, request.ArtifactRef, request.DeadlineUtc, manifest);
            var handler = handlers.GetTransform(request.TransformId);
            var handle = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.TransformArtifact, (operationId, cancellationToken) => ExecuteWithDeadlineAsync(request.DeadlineUtc, manifest.Limits.MaximumOperationMilliseconds, token => handler.TransformAsync(request, operationId, token), cancellationToken));
            return Results.Accepted($"/api/v1/operations/{handle.OperationId}", handle);
        }
        catch (ArtifactWorkerRequestException exception)
        {
            return Problem(context, identity, exception);
        }
        catch (ArgumentException exception)
        {
            return Problem(context, identity, new ArtifactWorkerRequestException("invalid-request", "The artifact request contains an invalid reference.", innerException: exception));
        }
    }

    private static IResult StartRender(RenderArtifactRequest request, ArtifactWorkerHandlerRegistry handlers, ArtifactWorkerOperationRegistry operations, ArtifactWorkerCapabilityManifest manifest, ServiceIdentity identity, HttpContext context)
    {
        try
        {
            ValidateCommon(request.RequestId, request.IdempotencyKey, request.PipelineResolutionId, request.ProcessorId, request.ArtifactRef, request.DeadlineUtc, manifest);
            if (request.Options.MaxCharacters <= 0)
                throw new ArtifactWorkerRequestException("invalid-request", "MaxCharacters must be positive.");
            var handler = handlers.GetRender(request.OutputId);
            var handle = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.RenderArtifact, (operationId, cancellationToken) => ExecuteWithDeadlineAsync(request.DeadlineUtc, manifest.Limits.MaximumOperationMilliseconds, token => handler.RenderAsync(request, operationId, token), cancellationToken));
            return Results.Accepted($"/api/v1/operations/{handle.OperationId}", handle);
        }
        catch (ArtifactWorkerRequestException exception)
        {
            return Problem(context, identity, exception);
        }
        catch (ArgumentException exception)
        {
            return Problem(context, identity, new ArtifactWorkerRequestException("invalid-request", "The artifact request contains an invalid reference.", innerException: exception));
        }
    }

    private static IResult StartVerification(VerifyArtifactRequest request, ArtifactWorkerHandlerRegistry handlers, ArtifactWorkerOperationRegistry operations, ArtifactWorkerCapabilityManifest manifest, ServiceIdentity identity, HttpContext context)
    {
        try
        {
            ValidateCommon(request.RequestId, request.IdempotencyKey, request.PipelineResolutionId, request.ProcessorId, request.ArtifactRef, request.DeadlineUtc, manifest);
            if (request.Options.MaxFindings <= 0)
                throw new ArtifactWorkerRequestException("invalid-request", "MaxFindings must be positive.");
            var handler = handlers.GetVerification(request.Options.VerificationProfileId);
            var handle = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.VerifyArtifact, (operationId, cancellationToken) => ExecuteWithDeadlineAsync(request.DeadlineUtc, manifest.Limits.MaximumOperationMilliseconds, token => handler.VerifyAsync(request, operationId, token), cancellationToken));
            return Results.Accepted($"/api/v1/operations/{handle.OperationId}", handle);
        }
        catch (ArtifactWorkerRequestException exception)
        {
            return Problem(context, identity, exception);
        }
        catch (ArgumentException exception)
        {
            return Problem(context, identity, new ArtifactWorkerRequestException("invalid-request", "The artifact request contains an invalid reference.", innerException: exception));
        }
    }

    private static IResult GetOperation(string operationId, ArtifactWorkerOperationRegistry operations) =>
        operations.Get(operationId) is { } state ? Results.Ok(state) : Results.NotFound();

    private static IResult GetEvents(string operationId, ArtifactWorkerOperationRegistry operations, ServiceIdentity identity, HttpContext context)
    {
        if (!PascalCaseQuery.TryGetOptionalInt64(context.Request, "FromSequence", out var fromSequence))
        {
            return Problem(context, identity, new ArtifactWorkerRequestException("invalid-request", "FromSequence must use its exact PascalCase spelling and be a valid integer."));
        }

        try
        {
            return operations.GetEvents(operationId, fromSequence ?? 0) is { } events
                ? Results.Ok(events) : Results.NotFound();
        }
        catch (ArtifactWorkerRequestException exception)
        {
            return Problem(context, identity, exception);
        }
    }

    private static IResult CancelOperation(string operationId, CancelOperationRequest request, ArtifactWorkerOperationRegistry operations, ServiceIdentity identity, HttpContext context)
    {
        if (!string.Equals(operationId, request.OperationId, StringComparison.Ordinal))
        {
            return Problem(context, identity, new ArtifactWorkerRequestException("invalid-request", "The cancellation path and request operation IDs differ."));
        }
        var result = operations.Cancel(operationId);
        return result.Disposition == CancelDisposition.NotFound ? Results.NotFound(result) : Results.Ok(result);
    }

    private static async Task<ArtifactWorkerJobExecution> ExecuteWithDeadlineAsync(DateTimeOffset deadlineUtc, int maximumMilliseconds, Func<CancellationToken, Task<ArtifactWorkerJobExecution>> execute, CancellationToken operationCancellation)
    {
        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new ArtifactWorkerDeadlineExceededException("The artifact operation deadline elapsed.");
        var maximum = TimeSpan.FromMilliseconds(maximumMilliseconds);
        using var deadline = new CancellationTokenSource(remaining < maximum ? remaining : maximum);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationCancellation, deadline.Token);
        try
        {
            return await execute(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !operationCancellation.IsCancellationRequested)
        {
            throw new ArtifactWorkerDeadlineExceededException("The artifact operation deadline elapsed.", exception);
        }
    }

    private static void ValidateCommon(string requestId, string idempotencyKey, string pipelineResolutionId, string processorId, ArtifactRef artifactRef, DateTimeOffset deadlineUtc, ArtifactWorkerCapabilityManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
            throw new ArtifactWorkerRequestException("invalid-request", "RequestId is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
            throw new ArtifactWorkerRequestException("invalid-request", "IdempotencyKey is required.");
        if (string.IsNullOrWhiteSpace(pipelineResolutionId) || pipelineResolutionId.Length > 256)
            throw new ArtifactWorkerRequestException("invalid-request", "PipelineResolutionId is required.");
        if (!string.Equals(processorId, manifest.WorkerId, StringComparison.Ordinal))
            throw new ArtifactWorkerRequestException("wrong-processor", "The request targets another artifact processor.");
        _ = ArtifactStoreProtocol.GetDigest(artifactRef);
        if (deadlineUtc <= DateTimeOffset.UtcNow)
        {
            throw new ArtifactWorkerRequestException("deadline-exceeded", "The artifact operation deadline elapsed.", StatusCodes.Status408RequestTimeout, WorkerErrorCategory.DeadlineExceeded);
        }
    }

    private static async Task<IReadOnlyList<HealthCheckResult>> CheckAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var checks = new List<HealthCheckResult>();
        var started = Stopwatch.GetTimestamp();
        try
        {
            _ = services.GetRequiredService<ArtifactWorkerHandlerRegistry>();
            checks.Add(new HealthCheckResult("handler-registry", HealthStatus.Healthy, null, Stopwatch.GetElapsedTime(started)));
        }
        catch (Exception)
        {
            checks.Add(new HealthCheckResult("handler-registry", HealthStatus.Unhealthy, "The registered artifact handlers do not match the capability manifest.", Stopwatch.GetElapsedTime(started)));
            return checks;
        }

        foreach (var check in services.GetServices<IArtifactWorkerReadinessCheck>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                checks.Add(await check.CheckAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                checks.Add(new HealthCheckResult(check.Name, HealthStatus.Unhealthy, "An artifact worker dependency is unavailable.", null));
            }
        }
        return checks;
    }

    private static IResult Problem(HttpContext context, ServiceIdentity identity, ArtifactWorkerRequestException exception) => Results.Problem(
            statusCode: exception.StatusCode,
            title: exception.Code,
            detail: exception.PublicMessage,
            extensions: new Dictionary<string, object?>
            {
                ["Code"] = exception.Code,
                ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                ["WorkerId"] = identity.Id
            });

    private sealed class ArtifactWorkerRuntimeState(string workerId)
    {
        public string InstanceId { get; } = $"{workerId}-{Guid.NewGuid():N}";

        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    }
}

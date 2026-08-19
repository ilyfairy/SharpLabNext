using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.Http;
using SharpLabNext.InternalServices;
using SharpLabNext.Observability;
using SharpLabNext.Operations;
using SharpLabNext.Operations.Http;
using SharpLabNext.RuntimeSupervisor;

var builder = WebApplication.CreateBuilder(args);
var internalServiceAuthentication = InternalServiceAuthenticationOptions.FromConfiguration(
    builder.Configuration,
    builder.Environment);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    ContractJson.ApplySerializerOptions(options.SerializerOptions);
});
builder.Services.AddSharpLabNextProblemDetails();
var operationStoreOptions = new OperationStoreOptions();
builder.Configuration.GetSection(OperationStoreOptions.SectionName).Bind(operationStoreOptions);
operationStoreOptions.Validate();
var operationExecutionOptions = new OperationExecutionOptions();
builder.Configuration.GetSection(OperationExecutionOptions.SectionName).Bind(operationExecutionOptions);
operationExecutionOptions.Validate();
var descriptor = new ServiceIdentity(
    "runtime-supervisor",
    ServiceKind.RuntimeSupervisor,
    builder.Configuration["ReleaseId"] ?? "development",
    ProtocolVersion.WorkerV1,
    RuntimeSupervisorServiceCapabilities.All,
    "ready");
builder.AddSharpLabNextObservability(descriptor.Id, descriptor.ReleaseId);
builder.Services.AddSingleton(descriptor);
var runtimeProfileOverlay = new RuntimeSupervisorProfileOverlayOptions();
builder.Configuration.GetSection(RuntimeSupervisorProfileOverlayOptions.SectionName).Bind(runtimeProfileOverlay);
var runtimePromotionPreflight = RuntimePromotionPreflightOptions.Load(builder.Configuration);
builder.Services.AddOptions<RuntimeSupervisorOptions>()
    .Bind(builder.Configuration.GetSection(RuntimeSupervisorOptions.SectionName))
    .PostConfigure(options =>
    {
        runtimeProfileOverlay.ApplyTo(options);
        runtimePromotionPreflight.ApplyTo(options);
    })
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RuntimeSupervisorOptions>, RuntimeSupervisorOptionsValidator>();
builder.Services.AddSingleton(operationStoreOptions);
builder.Services.AddSingleton(operationExecutionOptions);
builder.Services.AddSingleton<OperationStore>();
builder.Services.AddSingleton<BoundedOperationScheduler>();
builder.Services.AddSingleton<RuntimeSandboxPolicy>();
builder.Services.AddSingleton<IDockerEngineClient, DockerEngineClient>();
builder.Services.AddSingleton<RuntimeSessionRegistry>();
builder.Services.AddSingleton<RuntimeCapabilityPreflightCoordinator>();
builder.Services.AddSingleton<RuntimePerformancePreflightCoordinator>();
builder.Services.AddHttpClient<IArtifactStoreClient, ArtifactStoreClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<RuntimeSupervisorOptions>>().Value;
    client.BaseAddress = new Uri(options.ArtifactStoreBaseAddress, UriKind.Absolute);
    client.Timeout = Timeout.InfiniteTimeSpan;
    internalServiceAuthentication.ConfigureClient(client);
});
builder.Services.AddSingleton<RuntimeJobExecutor>();
builder.Services.AddHostedService<RuntimeContainerReaper>();

var app = builder.Build();
_ = app.Services.GetRequiredService<RuntimeSandboxPolicy>();
app.UseSharpLabNextOperationCapacityHandling();
app.UseSharpLabNextInternalServiceAuthentication(internalServiceAuthentication);
app.MapGet("/health/live", () => Results.Ok(new { Status = "live" }));
app.MapGet("/health/ready", async (
    ServiceIdentity service,
    IDockerEngineClient docker,
    IOptions<RuntimeSupervisorOptions> configuredOptions,
    CancellationToken cancellationToken) =>
{
    var dockerReady = await docker.PingAsync(cancellationToken);
    var status = dockerReady ? "ready" : "unavailable";
    return dockerReady
        ? Results.Ok(new
        {
            Status = status,
            service.Id,
            service.ReleaseId,
            DockerControl = true,
            RuntimeSessionReuse = configuredOptions.Value.SessionReuseEnabled,
            RuntimeProfiles = configuredOptions.Value.Profiles.Select(static profile => profile.Id)
        })
        : Results.Json(
            new
            {
                Status = status,
                service.Id,
                service.ReleaseId,
                DockerControl = false
            },
            ContractJson.CreateSerializerOptions(),
            statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/v1/runtime/status", (
    ServiceIdentity service,
    IOptions<RuntimeSupervisorOptions> options) => new
{
    service,
    Profiles = options.Value.Profiles.Select(profile => new
    {
        profile.Id,
        profile.Family,
        profile.RuntimeVersion,
        profile.RuntimeCommit,
        profile.JitVersion,
        profile.JitCommit,
        profile.RuntimeImageId,
        profile.Image,
        profile.Rid,
        profile.Architecture,
        profile.AcceptedRuntimeFamilies,
        profile.AcceptedFrameworks,
        profile.AcceptedArtifactFormats,
        profile.Capabilities,
        profile.ProvidedRuntimeFeatureTags,
        profile.ProvidedMetadataFeatureTags,
        profile.Container,
        profile.Operations
    })
});

app.MapPost("/internal/v1/jobs/run", (
    RunRequest request,
    HttpContext context,
    OperationStore operations,
    RuntimeJobExecutor executor) =>
{
    var validation = RuntimeJobRequestValidator.Validate(request);
    if (validation is not null)
    {
        return Results.BadRequest(validation);
    }
    if (!TryReadRuntimeSessionId(context, out var runtimeSessionId))
        return Results.BadRequest(new { Error = "invalid-runtime-session-id" });

    var operation = operations.Start(
        request.RequestId,
        request.IdempotencyKey,
        OperationKind.Run,
        request.RequestId,
        DateTimeOffset.UtcNow);
    if (!operation.Handle.IsExisting)
    {
        executor.QueueRun(operation, request, runtimeSessionId);
    }

    return Results.Accepted($"/internal/v1/operations/{operation.Handle.OperationId}", operation.Handle);
});

app.MapPost("/internal/v1/jobs/jit", (
    JitRequest request,
    HttpContext context,
    OperationStore operations,
    RuntimeJobExecutor executor) =>
{
    var validation = RuntimeJobRequestValidator.Validate(request);
    if (validation is not null)
    {
        return Results.BadRequest(validation);
    }
    if (!TryReadRuntimeSessionId(context, out var runtimeSessionId))
        return Results.BadRequest(new { Error = "invalid-runtime-session-id" });

    var operation = operations.Start(
        request.RequestId,
        request.IdempotencyKey,
        OperationKind.Jit,
        request.RequestId,
        DateTimeOffset.UtcNow);
    if (!operation.Handle.IsExisting)
    {
        executor.QueueJit(operation, request, runtimeSessionId);
    }

    return Results.Accepted($"/internal/v1/operations/{operation.Handle.OperationId}", operation.Handle);
});

app.MapPost("/internal/v1/performance/samples", async (
    RuntimePerformanceSampleRequest request,
    RuntimePerformancePreflightCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    try
    {
        var sample = await coordinator.MeasureAsync(request, cancellationToken).ConfigureAwait(false);
        return Results.Ok(sample);
    }
    catch (RuntimePerformancePreflightException exception)
    {
        return Results.Json(
            new { Error = exception.Code, Message = exception.PublicMessage },
            ContractJson.CreateSerializerOptions(),
            statusCode: exception.StatusCode);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { Error = "invalid-performance-request", Message = exception.Message });
    }
});

app.MapPost(
    "/internal/v1/capabilities/preflight",
    RuntimeCapabilityPreflightEndpoint.HandleAsync);

app.MapPost("/internal/v1/sessions/{sessionId}/release", async (
    string sessionId,
    RuntimeSessionRegistry sessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        RuntimeSessionRegistry.ValidateSessionId(sessionId);
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { Error = "invalid-runtime-session-id" });
    }

    await sessions.ReleaseAsync(sessionId, cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
});

app.MapGet("/internal/v1/operations/{operationId}", (string operationId, OperationStore operations) =>
{
    var state = operations.Get(operationId);
    return state is null ? Results.NotFound() : Results.Ok(state);
});

app.MapGet("/internal/v1/operations/{operationId}/events", async (
    string operationId,
    HttpContext context,
    OperationStore operations) =>
{
    if (!PascalCaseQuery.TryGetOptionalInt64(context.Request, "FromSequence", out var fromSequence))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (operations.Get(operationId) is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-store";
    var serializerOptions = ContractJson.CreateSerializerOptions();
    await foreach (var operationEvent in operations.WatchAsync(
                       operationId,
                       fromSequence ?? 0,
                       context.RequestAborted))
    {
        var json = JsonSerializer.Serialize(operationEvent, serializerOptions);
        await context.Response.WriteAsync(
            $"id: {operationEvent.Sequence}\nevent: operation\ndata: {json}\n\n",
            context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
});

app.MapPost("/internal/v1/operations/{operationId}/cancel", (
    string operationId,
    CancelOperationRequest request,
    OperationStore operations) =>
{
    if (!string.Equals(operationId, request.OperationId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { Error = "operation-id-mismatch" });
    }

    var result = operations.Cancel(operationId, request.Reason, DateTimeOffset.UtcNow);
    return result.Disposition == CancelDisposition.NotFound ? Results.NotFound() : Results.Ok(result);
});

app.Run();

static bool TryReadRuntimeSessionId(HttpContext context, out string? runtimeSessionId)
{
    runtimeSessionId = context.Request.Headers["X-SharpLabNext-Runtime-Session-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(runtimeSessionId))
    {
        runtimeSessionId = null;
        return true;
    }

    try
    {
        RuntimeSessionRegistry.ValidateSessionId(runtimeSessionId);
        return true;
    }
    catch (ArgumentException)
    {
        runtimeSessionId = null;
        return false;
    }
}

public partial class Program;

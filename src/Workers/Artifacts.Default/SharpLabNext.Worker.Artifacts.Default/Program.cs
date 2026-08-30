using System.Diagnostics;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker;
using SharpLabNext.Contracts;
using SharpLabNext.Http;
using SharpLabNext.InternalServices;
using SharpLabNext.Observability;
using SharpLabNext.WorkerHost;

var builder = WebApplication.CreateBuilder(args);
var internalServiceAuthentication = InternalServiceAuthenticationOptions.FromConfiguration(builder.Configuration, builder.Environment);
var settings = ArtifactWorkerSettings.FromConfiguration(builder.Configuration);
builder.AddSharpLabNextObservability(settings.Identity.ProcessorId, settings.Identity.ReleaseId);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(settings.Identity);
builder.Services.AddSingleton(settings.Limits);
builder.Services.AddHttpClient<IArtifactStoreClient, ArtifactStoreClient>(client =>
{
    client.BaseAddress = new Uri(settings.ArtifactStoreBaseUrl, UriKind.Absolute);
    client.Timeout = Timeout.InfiniteTimeSpan;
    internalServiceAuthentication.ConfigureClient(client);
});
builder.Services.AddSingleton<ArtifactBundleMaterializer>();
builder.Services.AddSingleton<IArtifactProcessorRunner, ArtifactProcessorProcessRunner>();
builder.Services.AddSingleton<IArtifactJobExecutor, ArtifactJobExecutor>();
builder.Services.AddSingleton<ArtifactOperationRegistry>();
builder.Services.AddSingleton<ArtifactWorkerHealthService>();
builder.Services.AddSharpLabNextWorker(new ServiceIdentity(settings.Identity.ProcessorId, ServiceKind.ArtifactWorker, settings.Identity.ReleaseId, ProtocolVersion.WorkerV1, ["il", "decompiled-csharp", "il-verify"], "starting"));

var app = builder.Build();
app.UseSharpLabNextInternalServiceAuthentication(internalServiceAuthentication);
app.MapGet("/health/live", () => Results.Ok(new { Status = "live" }));
app.MapGet("/health/ready", (ArtifactWorkerHealthService healthService) =>
{
    var health = healthService.Check();
    return health.Status == HealthStatus.Healthy
        ? Results.Ok(health) : Results.Json(health, ContractJson.CreateSerializerOptions(), statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/v1/worker/describe", (ArtifactWorkerHealthService healthService) => healthService.Describe());
app.MapPost("/api/v1/artifact-transforms", StartTransform);
app.MapPost("/api/v1/artifact-renders", StartRender);
app.MapPost("/api/v1/verifications", StartVerify);
app.MapGet("/api/v1/operations/{operationId}", GetOperation);
app.MapGet("/api/v1/operations/{operationId}/events", GetEvents);
app.MapPost("/api/v1/operations/{operationId}/cancel", CancelOperation);
app.Run();

static IResult StartTransform(TransformArtifactRequest request, ArtifactOperationRegistry operations, IArtifactJobExecutor executor)
{
    var handle = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.TransformArtifact, (operationId, cancellationToken) => executor.TransformAsync(request, operationId, cancellationToken));
    return Results.Accepted($"/api/v1/operations/{handle.OperationId}", handle);
}

static IResult StartRender(RenderArtifactRequest request, ArtifactOperationRegistry operations, IArtifactJobExecutor executor)
{
    var handle = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.RenderArtifact, (operationId, cancellationToken) => executor.RenderAsync(request, operationId, cancellationToken));
    return Results.Accepted($"/api/v1/operations/{handle.OperationId}", handle);
}

static IResult StartVerify(VerifyArtifactRequest request, ArtifactOperationRegistry operations, IArtifactJobExecutor executor)
{
    var handle = operations.Start(request.RequestId, request.IdempotencyKey, OperationKind.VerifyArtifact, (operationId, cancellationToken) => executor.VerifyAsync(request, operationId, cancellationToken));
    return Results.Accepted($"/api/v1/operations/{handle.OperationId}", handle);
}

static IResult GetOperation(string operationId, ArtifactOperationRegistry operations) =>
    operations.Get(operationId) is { } state ? Results.Ok(state) : Results.NotFound();

static IResult GetEvents(string operationId, ArtifactOperationRegistry operations, HttpContext context)
{
    if (!PascalCaseQuery.TryGetOptionalInt64(context.Request, "FromSequence", out var fromSequence))
        return Problem(context, "invalid-argument", "FromSequence must use its exact PascalCase spelling and be a valid integer.", StatusCodes.Status400BadRequest);

    try
    {
        return operations.GetEvents(operationId, fromSequence ?? 0) is { } events
            ? Results.Ok(events) : Results.NotFound();
    }
    catch (ArtifactRequestValidationException exception)
    {
        return Problem(context, "invalid-argument", exception.Message, StatusCodes.Status400BadRequest);
    }
}

static IResult CancelOperation(string operationId, CancelOperationRequest request, ArtifactOperationRegistry operations, HttpContext context)
{
    if (!StringComparer.Ordinal.Equals(operationId, request.OperationId))
        return Problem(context, "invalid-argument", "The operation ID does not match the route.", StatusCodes.Status400BadRequest);
    var result = operations.Cancel(operationId);
    return result.Disposition == CancelDisposition.NotFound ? Results.NotFound(result) : Results.Ok(result);
}

static IResult Problem(HttpContext context, string code, string message, int statusCode) =>
    Results.Problem(
        statusCode: statusCode,
        title: code,
        detail: message,
        extensions: new Dictionary<string, object?>
        {
            ["Code"] = code,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            ["WorkerId"] = "artifacts-default"
        });

public partial class Program;

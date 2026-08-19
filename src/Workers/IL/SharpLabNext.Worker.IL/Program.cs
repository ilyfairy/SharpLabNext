using System.Diagnostics;
using System.Net.WebSockets;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.InternalServices;
using SharpLabNext.Observability;
using SharpLabNext.Worker.IL;
using SharpLabNext.WorkerHost;

var builder = WebApplication.CreateBuilder(args);
var internalServiceAuthentication = InternalServiceAuthenticationOptions.FromConfiguration(
    builder.Configuration,
    builder.Environment);
var settings = IlWorkerSettings.FromConfiguration(builder.Configuration);
builder.AddSharpLabNextObservability(
    settings.Identity.ToolchainId,
    settings.Identity.ReleaseId);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(settings.Identity);
builder.Services.AddSingleton(settings.CompilationLimits);
builder.Services.AddSingleton(settings.LspLimits);
builder.Services.AddSingleton(settings.DevelopmentArtifactEnvelope);
builder.Services.AddSingleton(settings.ArtifactPublishing);
builder.Services.AddHttpClient<IArtifactStoreClient, ArtifactStoreClient>(client =>
{
    client.BaseAddress = settings.ArtifactPublishing.BaseAddress;
    client.Timeout = Timeout.InfiniteTimeSpan;
    internalServiceAuthentication.ConfigureClient(client);
});
builder.Services.AddSingleton(new IlReferenceSetProvider(
    settings.ReferenceSets,
    builder.Environment.IsProduction() ||
    builder.Configuration.GetValue("ReferenceSetAttestation:Required", false)));
builder.Services.AddSingleton<IlAssemblerProcess>();
builder.Services.AddSingleton<IlBuildService>();
builder.Services.AddSingleton<IlArtifactPublisher>();
builder.Services.AddSingleton<IlLanguageService>();
builder.Services.AddSingleton<IlLanguageSessionManager>();
builder.Services.AddSingleton(IlWorkerProcessIdentity.Create());
builder.Services.AddSingleton<IlWorkerHealthService>();
builder.Services.AddSharpLabNextWorker(new ServiceIdentity(
    settings.Identity.ToolchainId,
    ServiceKind.ToolchainWorker,
    settings.Identity.ReleaseId,
    ProtocolVersion.WorkerV1,
    ["compile-check", "managed-pe", "multi-file", "lsp"],
    "starting"));

var app = builder.Build();
app.UseSharpLabNextInternalServiceAuthentication(internalServiceAuthentication);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.MapGet("/health/live", () => Results.Ok(new { Status = "live" }));
app.MapGet("/health/ready", async (IlWorkerHealthService health, CancellationToken cancellationToken) =>
{
    var result = await health.CheckAsync(cancellationToken);
    return result.Status == HealthStatus.Healthy
        ? Results.Ok(result)
        : Results.Json(
            result,
            ContractJson.CreateSerializerOptions(),
            statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/v1/worker/describe", async (
    IlWorkerHealthService health,
    CancellationToken cancellationToken) => await health.DescribeAsync(cancellationToken));
app.MapPost("/api/v1/build", HandleBuildAsync);
app.MapPost("/api/v1/language-sessions", HandleOpenLanguageSessionAsync);
app.MapDelete("/api/v1/language-sessions/{sessionId}", HandleCloseLanguageSession);
app.MapGet("/api/v1/language-sessions/{sessionId}/lsp", HandleLanguageWebSocketAsync);
app.Run();

static async Task<IResult> HandleOpenLanguageSessionAsync(
    OpenLanguageSessionRequest request,
    IlLanguageSessionManager sessions,
    HttpContext context)
{
    try
    {
        return Results.Ok(await sessions.OpenAsync(request, context.RequestAborted));
    }
    catch (Exception exception) when (exception is IlBuildRequestValidationException or IlLspInvalidParamsException)
    {
        return Problem(context, "invalid-argument", exception.Message, StatusCodes.Status400BadRequest);
    }
    catch (IlLspLimitExceededException exception)
    {
        return Problem(context, "resource-exhausted", exception.Message, StatusCodes.Status429TooManyRequests);
    }
    catch (IlReferenceSetUnavailableException exception)
    {
        return Problem(context, "unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
    }
}

static IResult HandleCloseLanguageSession(
    string sessionId,
    IlLanguageSessionManager sessions,
    HttpContext context) =>
    sessions.Close(sessionId)
        ? Results.NoContent()
        : Problem(context, "not-found", "The IL language session does not exist.", StatusCodes.Status404NotFound);

static async Task HandleLanguageWebSocketAsync(
    string sessionId,
    IlLanguageSessionManager sessions,
    IlLspLimits limits,
    HttpContext context)
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        return;
    }
    if (!sessions.TryGet(sessionId, out var session) || session is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await using var connection = new IlLspJsonRpcConnection(socket, session, limits, context.RequestAborted);
    try
    {
        await connection.RunAsync();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
    }
    catch (IlLspSessionUnavailableException)
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.PolicyViolation,
                "IL language session unavailable.",
                CancellationToken.None);
        }
    }
}

static async Task<IResult> HandleBuildAsync(
    BuildRequest request,
    IlBuildService buildService,
    IlArtifactPublisher artifactPublisher,
    IlDevelopmentArtifactEnvelopeOptions envelopeOptions,
    HttpContext context,
    ILogger<Program> logger)
{
    try
    {
        var execution = await buildService.ExecuteAsync(request, context.RequestAborted);
        IlDevelopmentArtifactEnvelope? envelope = null;
        if (execution.Artifact is not null)
        {
            if (envelopeOptions.Enabled)
            {
                envelope = IlDevelopmentArtifactEnvelope.FromArtifact(
                    execution.Artifact,
                    envelopeOptions);
            }
            else
            {
                var publishedRef = await PublishArtifactAsync(
                    request,
                    execution.Artifact,
                    artifactPublisher,
                    context.RequestAborted);
                if (execution.Result is not BuildResult { ArtifactRef: { } resultRef } ||
                    resultRef != publishedRef)
                {
                    throw new InvalidOperationException(
                        "The published IL artifact identity does not match the build result.");
                }
            }
        }
        return Results.Ok(new IlWorkerBuildHttpResponse(request.RequestId, execution.Result, envelope));
    }
    catch (IlBuildRequestValidationException exception)
    {
        return Problem(context, "invalid-argument", exception.Message, StatusCodes.Status400BadRequest);
    }
    catch (IlReferenceSetUnavailableException exception)
    {
        return Problem(context, "unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
    }
    catch (IlAssemblerUnavailableException exception)
    {
        IlWorkerLog.AssemblerUnavailable(logger, exception, request.RequestId);
        return Problem(context, "unavailable", "The isolated IL assembler is unavailable.", StatusCodes.Status503ServiceUnavailable);
    }
    catch (IlBuildOutputLimitExceededException exception)
    {
        return Problem(context, "resource-exhausted", exception.Message, StatusCodes.Status413PayloadTooLarge);
    }
    catch (IlDevelopmentArtifactEnvelopeException exception)
    {
        return Problem(context, "artifact-store-unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
    }
    catch (ArtifactBundlePublicationException exception)
    {
        return exception.Failure switch
        {
            ArtifactBundlePublicationFailure.ResourceExhausted => Problem(
                context,
                "resource-exhausted",
                exception.Message,
                StatusCodes.Status413PayloadTooLarge),
            ArtifactBundlePublicationFailure.Unavailable => Problem(
                context,
                "artifact-store-unavailable",
                exception.Message,
                StatusCodes.Status503ServiceUnavailable),
            _ => Problem(
                context,
                "artifact-store-rejected-artifact",
                exception.Message,
                StatusCodes.Status502BadGateway)
        };
    }
    catch (IlBuildDeadlineExceededException exception)
    {
        return Problem(context, "deadline-exceeded", exception.Message, StatusCodes.Status408RequestTimeout);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Problem(context, "cancelled", "The IL build was cancelled.", 499);
    }
    catch (Exception exception)
    {
        IlWorkerLog.BuildFailed(logger, exception, request.RequestId);
        return Problem(context, "internal", "The IL worker failed to process the build.", StatusCodes.Status500InternalServerError);
    }
}

static async Task<ArtifactRef> PublishArtifactAsync(
    BuildRequest request,
    IlCompiledArtifact artifact,
    IlArtifactPublisher publisher,
    CancellationToken cancellationToken)
{
    var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
    if (remaining <= TimeSpan.Zero)
        throw new IlBuildDeadlineExceededException(
            "The build deadline elapsed before artifact publication.",
            cancellationToken);

    using var deadline = new CancellationTokenSource(remaining);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
    try
    {
        return await publisher.PublishAsync(artifact, linked.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (
        deadline.IsCancellationRequested &&
        !cancellationToken.IsCancellationRequested)
    {
        throw new IlBuildDeadlineExceededException(
            "The build deadline elapsed while publishing the artifact.",
            deadline.Token);
    }
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
            ["WorkerId"] = "mobius-ilasm-stable"
        });

public partial class Program;

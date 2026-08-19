using System.Diagnostics;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.InternalServices;
using SharpLabNext.Observability;
using SharpLabNext.Worker.FSharp;
using SharpLabNext.Worker.FSharp.Compiler;
using SharpLabNext.WorkerHost;

if (FSharpBuildChild.IsInvocation(args))
{
    await FSharpBuildChild.RunAsync(WebApplication.CreateBuilder([]));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var internalServiceAuthentication = InternalServiceAuthenticationOptions.FromConfiguration(
    builder.Configuration,
    builder.Environment);
var settings = FSharpWorkerSettings.FromConfiguration(builder.Configuration);
if (!settings.BuildProcess.Enabled && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "FSharpWorker:BuildProcess:Enabled can only be false in Development.");
}
builder.AddSharpLabNextObservability(
    settings.Identity.ToolchainId,
    settings.Identity.ReleaseId);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(settings.Identity);
builder.Services.AddSingleton(settings.CompilationLimits);
builder.Services.AddSingleton(settings.AstLimits);
builder.Services.AddSingleton(settings.LspLimits);
builder.Services.AddSingleton(settings.BuildProcess);
builder.Services.AddSingleton(settings.DevelopmentArtifactEnvelope);
builder.Services.AddSingleton(settings.ArtifactPublishing);
builder.Services.AddHttpClient<IArtifactStoreClient, ArtifactStoreClient>(client =>
{
    client.BaseAddress = settings.ArtifactPublishing.BaseAddress;
    client.Timeout = Timeout.InfiniteTimeSpan;
    internalServiceAuthentication.ConfigureClient(client);
});
builder.Services.AddSingleton(new FSharpReferenceSetProvider(
    settings.ReferenceSets,
    builder.Environment.IsProduction() ||
    builder.Configuration.GetValue("ReferenceSetAttestation:Required", false)));
builder.Services.AddSingleton<FSharpCompilerFacade>();
builder.Services.AddSingleton<FSharpBuildService>();
builder.Services.AddSingleton<ICompilerProcessRunner>(
    new CompilerProcessRunner(settings.BuildProcess));
builder.Services.AddSingleton<IFSharpBuildExecutor, FSharpBuildProcessExecutor>();
builder.Services.AddSingleton<FSharpArtifactPublisher>();
builder.Services.AddSingleton<FSharpLanguageSessionManager>();
builder.Services.AddSingleton(FSharpWorkerProcessIdentity.Create());
builder.Services.AddSingleton<FSharpWorkerHealthService>();
builder.Services.AddHostedService<FSharpReferenceSetWarmupService>();
builder.Services.AddSharpLabNextWorker(new ServiceIdentity(
    settings.Identity.ToolchainId,
    ServiceKind.ToolchainWorker,
    settings.Identity.ReleaseId,
    ProtocolVersion.WorkerV1,
    [
        "compile-check", "managed-pe", "portable-pdb", "ast", "multi-file", "lsp",
        "diagnostics", "completion", "hover", "signature-help", "semantic-tokens",
        "document-symbols", "code-actions"
    ],
    "starting"));

var app = builder.Build();
app.UseSharpLabNextInternalServiceAuthentication(internalServiceAuthentication);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.MapGet("/health/live", () => Results.Ok(new { Status = "live" }));
app.MapGet("/health/ready", async (FSharpWorkerHealthService health, CancellationToken cancellationToken) =>
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
    FSharpWorkerHealthService health,
    CancellationToken cancellationToken) => await health.DescribeAsync(cancellationToken));
app.MapPost("/api/v1/build", HandleBuildAsync);
app.MapPost("/api/v1/language-sessions", HandleOpenLanguageSessionAsync);
app.MapDelete("/api/v1/language-sessions/{sessionId}", HandleCloseLanguageSessionAsync);
app.MapGet("/api/v1/language-sessions/{sessionId}/lsp", HandleLanguageWebSocketAsync);
app.Run();

static async Task<IResult> HandleOpenLanguageSessionAsync(
    OpenLanguageSessionRequest request,
    FSharpLanguageSessionManager sessions,
    HttpContext context)
{
    try
    {
        return Results.Ok(await sessions.OpenAsync(request, context.RequestAborted));
    }
    catch (Exception exception) when (exception is FSharpBuildRequestValidationException or FSharpLspInvalidParamsException)
    {
        return Problem(context, "invalid-argument", exception.Message, StatusCodes.Status400BadRequest);
    }
    catch (FSharpLspLimitExceededException exception)
    {
        return Problem(context, "resource-exhausted", exception.Message, StatusCodes.Status429TooManyRequests);
    }
    catch (FSharpReferenceSetUnavailableException exception)
    {
        return Problem(context, "unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
    }
}

static async Task<IResult> HandleCloseLanguageSessionAsync(
    string sessionId,
    FSharpLanguageSessionManager sessions,
    HttpContext context) =>
    await sessions.CloseAsync(sessionId)
        ? Results.NoContent()
        : Problem(context, "not-found", "The F# language session does not exist.", StatusCodes.Status404NotFound);

static async Task HandleLanguageWebSocketAsync(
    string sessionId,
    FSharpLanguageSessionManager sessions,
    FSharpLspLimits limits,
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
    await using var connection = new FSharpLspJsonRpcConnection(socket, session, limits, context.RequestAborted);
    try
    {
        await connection.RunAsync();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
    }
    catch (FSharpLspSessionUnavailableException)
    {
        if (socket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            await socket.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                "F# language session unavailable.",
                CancellationToken.None);
        }
    }
}

static async Task<IResult> HandleBuildAsync(
    BuildRequest request,
    IFSharpBuildExecutor buildService,
    FSharpArtifactPublisher artifactPublisher,
    FSharpDevelopmentArtifactEnvelopeOptions envelopeOptions,
    HttpContext context,
    ILogger<Program> logger)
{
    try
    {
        var execution = await buildService.ExecuteAsync(request, context.RequestAborted);
        FSharpDevelopmentArtifactEnvelope? envelope = null;
        if (execution.Artifact is not null)
        {
            if (envelopeOptions.Enabled)
            {
                envelope = FSharpDevelopmentArtifactEnvelope.FromArtifact(
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
                        "The published F# artifact identity does not match the build result.");
                }
            }
        }
        return Results.Ok(new FSharpWorkerBuildHttpResponse(request.RequestId, execution.Result, envelope));
    }
    catch (FSharpBuildRequestValidationException exception)
    {
        return Problem(context, "invalid-argument", exception.Message, StatusCodes.Status400BadRequest);
    }
    catch (FSharpReferenceSetUnavailableException exception)
    {
        return Problem(context, "unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
    }
    catch (FSharpBuildOutputLimitExceededException exception)
    {
        return Problem(context, "resource-exhausted", exception.Message, StatusCodes.Status413PayloadTooLarge);
    }
    catch (FSharpDevelopmentArtifactEnvelopeException exception)
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
    catch (FSharpBuildDeadlineExceededException exception)
    {
        return Problem(context, "deadline-exceeded", exception.Message, StatusCodes.Status408RequestTimeout);
    }
    catch (CompilerProcessCapacityExceededException exception)
    {
        return Problem(context, "resource-exhausted", exception.Message, StatusCodes.Status429TooManyRequests);
    }
    catch (CompilerProcessMemoryLimitExceededException exception)
    {
        return Problem(context, "resource-exhausted", exception.Message, StatusCodes.Status429TooManyRequests);
    }
    catch (CompilerProcessException exception)
    {
        FSharpWorkerLog.BuildFailed(logger, exception, request.RequestId);
        return Problem(
            context,
            "compiler-process-unavailable",
            "The isolated F# compiler process failed.",
            StatusCodes.Status503ServiceUnavailable);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Problem(context, "cancelled", "The F# build was cancelled.", 499);
    }
    catch (Exception exception)
    {
        FSharpWorkerLog.BuildFailed(logger, exception, request.RequestId);
        return Problem(context, "internal", "The F# worker failed to process the build.", StatusCodes.Status500InternalServerError);
    }
}

static async Task<ArtifactRef> PublishArtifactAsync(
    BuildRequest request,
    FSharpCompiledArtifact artifact,
    FSharpArtifactPublisher publisher,
    CancellationToken cancellationToken)
{
    var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
    if (remaining <= TimeSpan.Zero)
        throw new FSharpBuildDeadlineExceededException(
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
        throw new FSharpBuildDeadlineExceededException(
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
            ["WorkerId"] = "fsharp-stable"
        });

public partial class Program;

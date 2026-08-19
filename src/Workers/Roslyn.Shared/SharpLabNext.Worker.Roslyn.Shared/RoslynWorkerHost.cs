using System.Diagnostics;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.InternalServices;
using SharpLabNext.Observability;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.Roslyn;

public static class RoslynWorkerHost
{
    private static readonly string[] Capabilities =
    [
        "compile-check", "managed-pe", "portable-pdb", "ast", "multi-file", "lsp",
        "diagnostics", "completion", "hover", "signature-help", "semantic-tokens",
        "document-symbols", "code-actions", "explain"
    ];

    public static WebApplication Build(
        WebApplicationBuilder builder,
        bool configureObservability = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var internalServiceAuthentication = InternalServiceAuthenticationOptions.FromConfiguration(
            builder.Configuration,
            builder.Environment);
        var settings = RoslynWorkerSettings.FromConfiguration(builder.Configuration);
        if (!settings.BuildProcess.Enabled && !builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "RoslynWorker:BuildProcess:Enabled can only be false in Development.");
        }
        if (configureObservability)
        {
            builder.AddSharpLabNextObservability(
                settings.Identity.ToolchainId,
                settings.Identity.ReleaseId);
        }
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
        builder.Services.AddSingleton(new ReferenceSetProvider(
            settings.ReferenceSets,
            builder.Environment.IsProduction() ||
            builder.Configuration.GetValue("ReferenceSetAttestation:Required", false)));
        builder.Services.AddSingleton(WorkerProcessIdentity.Create(settings.Identity.ToolchainId));
        builder.Services.AddSingleton<CSharpBuildService>();
        builder.Services.AddSingleton<VisualBasicBuildService>();
        builder.Services.AddSingleton<RoslynBuildService>();
        builder.Services.AddSingleton<ICompilerProcessRunner>(
            new CompilerProcessRunner(settings.BuildProcess));
        builder.Services.AddSingleton<IRoslynBuildExecutor, RoslynBuildProcessExecutor>();
        builder.Services.AddSingleton<RoslynArtifactPublisher>();
        builder.Services.AddSingleton<CSharpExplainService>();
        builder.Services.AddSingleton<RoslynLanguageSessionManager>();
        builder.Services.AddSingleton<RoslynWorkerHealthService>();
        builder.Services.AddHostedService<ReferenceSetWarmupService>();

        builder.Services.AddSharpLabNextWorker(new ServiceIdentity(
            settings.Identity.ToolchainId,
            ServiceKind.ToolchainWorker,
            settings.Identity.ReleaseId,
            ProtocolVersion.WorkerV1,
            Capabilities,
            "starting"));

        var app = builder.Build();
        app.UseSharpLabNextInternalServiceAuthentication(internalServiceAuthentication);
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });
        app.MapGet("/health/live", () => Results.Ok(new { Status = "live" }));
        app.MapGet("/health/ready", async (
            RoslynWorkerHealthService healthService,
            CancellationToken cancellationToken) =>
        {
            var health = await healthService.CheckAsync(cancellationToken);
            return health.Status == HealthStatus.Healthy
                ? Results.Ok(health)
                : Results.Json(
                    health,
                    ContractJson.CreateSerializerOptions(),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        app.MapGet("/api/v1/worker/describe", async (
            RoslynWorkerHealthService healthService,
            CancellationToken cancellationToken) =>
            await healthService.DescribeAsync(cancellationToken));
        app.MapPost("/api/v1/build", HandleBuildAsync);
        app.MapPost("/api/v1/explain", HandleExplainAsync);
        app.MapPost("/api/v1/language-sessions", HandleOpenLanguageSessionAsync);
        app.MapDelete("/api/v1/language-sessions/{sessionId}", HandleCloseLanguageSessionAsync);
        app.MapGet("/api/v1/language-sessions/{sessionId}/lsp", HandleLanguageWebSocketAsync);
        return app;
    }

    private static async Task<IResult> HandleOpenLanguageSessionAsync(
        OpenLanguageSessionRequest request,
        RoslynLanguageSessionManager sessions,
        HttpContext httpContext)
    {
        try
        {
            var session = await sessions.OpenAsync(request, httpContext.RequestAborted);
            return Results.Ok(session);
        }
        catch (BuildRequestValidationException exception)
        {
            return Problem(httpContext, "invalid-argument", exception.Message, StatusCodes.Status400BadRequest);
        }
        catch (LspInvalidParamsException exception)
        {
            return Problem(httpContext, "invalid-argument", exception.Message, StatusCodes.Status400BadRequest);
        }
        catch (LspLimitExceededException exception)
        {
            return Problem(httpContext, "resource-exhausted", exception.Message, StatusCodes.Status429TooManyRequests);
        }
        catch (ReferenceSetUnavailableException exception)
        {
            return Problem(httpContext, "unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
        }
        catch (CompilerIdentityMismatchException exception)
        {
            return Problem(httpContext, "unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleCloseLanguageSessionAsync(
        string sessionId,
        RoslynLanguageSessionManager sessions,
        HttpContext httpContext)
    {
        var removed = await sessions.CloseAsync(sessionId);
        return removed
            ? Results.NoContent()
            : Problem(httpContext, "not-found", "The language session does not exist.", StatusCodes.Status404NotFound);
    }

    private static async Task HandleLanguageWebSocketAsync(
        string sessionId,
        RoslynLanguageSessionManager sessions,
        LspLimits limits,
        HttpContext httpContext)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        if (!sessions.TryGet(sessionId, out var session) || session is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var socket = await httpContext.WebSockets.AcceptWebSocketAsync();
        await using var connection = new LspJsonRpcWebSocketConnection(
            socket,
            session,
            limits,
            httpContext.RequestAborted);
        try
        {
            await connection.RunAsync();
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
        }
        catch (LspSessionUnavailableException)
        {
            if (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                await socket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                    "Language session unavailable.",
                    CancellationToken.None);
            }
        }
    }

    private static async Task<IResult> HandleBuildAsync(
        BuildRequest request,
        IRoslynBuildExecutor buildService,
        RoslynArtifactPublisher artifactPublisher,
        DevelopmentArtifactEnvelopeOptions envelopeOptions,
        HttpContext httpContext,
        ILoggerFactory loggerFactory)
    {
        try
        {
            var execution = await buildService.ExecuteAsync(request, httpContext.RequestAborted);
            DevelopmentArtifactEnvelope? artifactEnvelope = null;
            if (execution.Artifact is not null)
            {
                if (envelopeOptions.Enabled)
                {
                    artifactEnvelope = DevelopmentArtifactEnvelope.FromArtifact(
                        execution.Artifact,
                        envelopeOptions);
                }
                else
                {
                    var publishedRef = await PublishArtifactAsync(
                        request,
                        execution.Artifact,
                        artifactPublisher,
                        httpContext.RequestAborted);
                    if (execution.Result is not BuildResult { ArtifactRef: { } resultRef } ||
                        resultRef != publishedRef)
                    {
                        throw new InvalidOperationException(
                            "The published artifact identity does not match the build result.");
                    }
                }
            }

            return Results.Ok(new WorkerBuildHttpResponse(
                request.RequestId,
                execution.Result,
                artifactEnvelope));
        }
        catch (BuildRequestValidationException exception)
        {
            return Problem(httpContext, "invalid-argument", exception.Message, StatusCodes.Status400BadRequest);
        }
        catch (ReferenceSetUnavailableException exception)
        {
            return Problem(httpContext, "unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
        }
        catch (CompilerIdentityMismatchException exception)
        {
            return Problem(httpContext, "unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
        }
        catch (BuildOutputLimitExceededException exception)
        {
            return Problem(httpContext, "resource-exhausted", exception.Message, StatusCodes.Status413PayloadTooLarge);
        }
        catch (DevelopmentArtifactEnvelopeException exception)
        {
            return Problem(httpContext, "artifact-store-unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArtifactBundlePublicationException exception)
        {
            return exception.Failure switch
            {
                ArtifactBundlePublicationFailure.ResourceExhausted => Problem(
                    httpContext,
                    "resource-exhausted",
                    exception.Message,
                    StatusCodes.Status413PayloadTooLarge),
                ArtifactBundlePublicationFailure.Unavailable => Problem(
                    httpContext,
                    "artifact-store-unavailable",
                    exception.Message,
                    StatusCodes.Status503ServiceUnavailable),
                _ => Problem(
                    httpContext,
                    "artifact-store-rejected-artifact",
                    exception.Message,
                    StatusCodes.Status502BadGateway)
            };
        }
        catch (BuildDeadlineExceededException exception)
        {
            return Problem(httpContext, "deadline-exceeded", exception.Message, StatusCodes.Status408RequestTimeout);
        }
        catch (CompilerProcessCapacityExceededException exception)
        {
            return Problem(httpContext, "resource-exhausted", exception.Message, StatusCodes.Status429TooManyRequests);
        }
        catch (CompilerProcessMemoryLimitExceededException exception)
        {
            return Problem(httpContext, "resource-exhausted", exception.Message, StatusCodes.Status429TooManyRequests);
        }
        catch (CompilerProcessException exception)
        {
            WorkerLog.InternalBuildFailure(
                loggerFactory.CreateLogger("SharpLabNext.Worker.Roslyn"),
                exception,
                request.RequestId,
                Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier);
            return Problem(
                httpContext,
                "compiler-process-unavailable",
                "The isolated Roslyn compiler process failed.",
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            return Problem(httpContext, "cancelled", "The build request was cancelled.", 499);
        }
        catch (Exception exception)
        {
            WorkerLog.InternalBuildFailure(
                loggerFactory.CreateLogger("SharpLabNext.Worker.Roslyn"),
                exception,
                request.RequestId,
                Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier);
            return Problem(
                httpContext,
                "internal",
                "The Roslyn worker failed to process the build.",
                StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<ArtifactRef> PublishArtifactAsync(
        BuildRequest request,
        CompiledArtifact artifact,
        RoslynArtifactPublisher publisher,
        CancellationToken cancellationToken)
    {
        var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new BuildDeadlineExceededException("The build deadline elapsed before artifact publication.", cancellationToken);

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
            throw new BuildDeadlineExceededException(
                "The build deadline elapsed while publishing the artifact.",
                deadline.Token);
        }
    }

    private static async Task<IResult> HandleExplainAsync(
        ExplainRequest request,
        CSharpExplainService explainService,
        HttpContext httpContext,
        ILoggerFactory loggerFactory)
    {
        try
        {
            var result = await explainService.ExecuteAsync(request, httpContext.RequestAborted);
            return Results.Ok(new WorkerExplainHttpResponse(request.RequestId, result));
        }
        catch (BuildRequestValidationException exception)
        {
            return Problem(httpContext, "invalid-argument", exception.Message, StatusCodes.Status400BadRequest);
        }
        catch (CompilerIdentityMismatchException exception)
        {
            return Problem(httpContext, "unavailable", exception.Message, StatusCodes.Status503ServiceUnavailable);
        }
        catch (BuildDeadlineExceededException exception)
        {
            return Problem(httpContext, "deadline-exceeded", exception.Message, StatusCodes.Status408RequestTimeout);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            return Problem(httpContext, "cancelled", "The explain request was cancelled.", 499);
        }
        catch (Exception exception)
        {
            WorkerLog.InternalBuildFailure(
                loggerFactory.CreateLogger("SharpLabNext.Worker.Roslyn"),
                exception,
                request.RequestId,
                Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier);
            return Problem(
                httpContext,
                "internal",
                "The Roslyn worker failed to explain the workspace.",
                StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult Problem(HttpContext context, string code, string message, int statusCode)
    {
        var workerId = context.RequestServices
            .GetRequiredService<RoslynWorkerIdentity>()
            .ToolchainId;
        return Results.Problem(
            statusCode: statusCode,
            title: code,
            detail: message,
            extensions: new Dictionary<string, object?>
            {
                ["Code"] = code,
                ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                ["WorkerId"] = workerId
            });
    }
}

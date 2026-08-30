using System.Net.WebSockets;
using System.Text.Json;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;
using SharpLabNext.Http;
using SharpLabNext.InternalServices;
using SharpLabNext.Observability;
using SharpLabNext.Operations;
using SharpLabNext.Operations.Http;
using SharpLabNext.PipelineResolver;
using SharpLabNext.RuntimeSupervisor.Client;
using SharpLabNext.Worker.Client;
using Resolver = SharpLabNext.PipelineResolver.PipelineResolver;

var builder = WebApplication.CreateBuilder(args);
var internalServiceAuthentication = InternalServiceAuthenticationOptions.FromConfiguration(builder.Configuration, builder.Environment);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    ContractJson.ApplySerializerOptions(options.SerializerOptions);
});
builder.Services.AddSharpLabNextProblemDetails();

var catalogPath = ResolveConfigurationFile(builder.Environment.ContentRootPath, builder.Configuration["Catalog:Path"], Path.Combine("catalog", "catalog.json"));
var lockPath = ResolveConfigurationFile(builder.Environment.ContentRootPath, builder.Configuration["Catalog:LockPath"], "lock.json");
var profileUpdateStatusPath = ResolveProfileUpdateStatusFile(builder.Environment.ContentRootPath, builder.Configuration[$"{ProfileUpdateStatusOptions.SectionName}:StatusPath"]);
var catalog = await CatalogLoader.LoadCatalogAsync(catalogPath);
var releaseLock = await CatalogLoader.LoadReleaseLockAsync(lockPath);
if (!string.Equals(catalog.ReleaseId, releaseLock.ReleaseId, StringComparison.Ordinal))
{
    throw new InvalidOperationException("Catalog and release lock identities do not match.");
}
var expectedReferenceSetDigests = ReferenceSetIdentityResolver.ResolveExpectedDigests(catalog, releaseLock);

var buildPipelineOptions = new BuildPipelineOptions();
builder.Configuration.GetSection(BuildPipelineOptions.SectionName).Bind(buildPipelineOptions);
buildPipelineOptions.Validate();
var runtimePipelineOptions = new RuntimePipelineOptions();
builder.Configuration.GetSection(RuntimePipelineOptions.SectionName).Bind(runtimePipelineOptions);
runtimePipelineOptions.Validate();
var artifactPipelineOptions = new ArtifactPipelineOptions();
builder.Configuration.GetSection(ArtifactPipelineOptions.SectionName).Bind(artifactPipelineOptions);
artifactPipelineOptions.Validate();
var languageSessionOptions = new LanguageSessionGatewayOptions();
builder.Configuration.GetSection(LanguageSessionGatewayOptions.SectionName).Bind(languageSessionOptions);
languageSessionOptions.Validate();
var operationStoreOptions = new OperationStoreOptions();
builder.Configuration.GetSection(OperationStoreOptions.SectionName).Bind(operationStoreOptions);
operationStoreOptions.Validate();
var operationExecutionOptions = new OperationExecutionOptions();
builder.Configuration.GetSection(OperationExecutionOptions.SectionName).Bind(operationExecutionOptions);
operationExecutionOptions.Validate();
var gatewayTrafficOptions = new GatewayTrafficOptions();
builder.Configuration.GetSection(GatewayTrafficOptions.SectionName).Bind(gatewayTrafficOptions);
gatewayTrafficOptions.Validate();
var githubOAuthOptions = GitHubOAuthOptions.FromConfiguration(builder.Configuration, builder.Environment);
var githubApiBaseAddress = GitHubExternalEndpoint.Parse(
    builder.Configuration["GitHub:ApiBaseAddress"] ?? "https://api.github.com/",
    "GitHub:ApiBaseAddress",
    builder.Environment);
var artifactStoreBaseAddress = RequiredServiceUri(builder.Configuration["Services:ArtifactStore:BaseAddress"], "Services:ArtifactStore:BaseAddress");
var runtimeSupervisorBaseAddress = RequiredServiceUri(builder.Configuration["Services:RuntimeSupervisor:BaseAddress"], "Services:RuntimeSupervisor:BaseAddress");
var dependencyHealthOptions = new GatewayDependencyHealthOptions(builder.Configuration.GetValue("DependencyHealth:Enabled", true), artifactStoreBaseAddress, runtimeSupervisorBaseAddress, builder.Configuration.GetValue("DependencyHealth:CacheDuration", TimeSpan.FromSeconds(2)), builder.Configuration.GetValue("DependencyHealth:ProbeTimeout", TimeSpan.FromSeconds(2)), internalServiceAuthentication.Token);
dependencyHealthOptions.Validate();
var languageWorkerEndpoints = LanguageWorkerEndpointRegistry.FromConfiguration(builder.Configuration, catalog.ReleaseId, catalog.Toolchains, expectedReferenceSetDigests, internalServiceAuthentication.Token);
var artifactWorkerEndpoints = ArtifactWorkerEndpointRegistry.FromConfiguration(builder.Configuration, catalog.ReleaseId, catalog.ArtifactProcessors.Select(static processor => processor.WorkerId), internalServiceAuthentication.Token);
var runtimeSupervisorSettings = new RuntimeSupervisorClientSettings(runtimePipelineOptions.ControlRequestTimeout, runtimePipelineOptions.MaximumEventCharacters);

var descriptor = new ServiceIdentity("gateway", ServiceKind.Gateway, catalog.ReleaseId, ProtocolVersion.WorkerV1, ["health", "static-web", "catalog", "profile-updates", "selection-resolver", "language-sessions", "operations", "artifact-operations", "runtime-operations", "explain", "github-gists"], "ready");
builder.AddSharpLabNextObservability(descriptor.Id, descriptor.ReleaseId);
builder.Services.AddSingleton(descriptor);
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton(releaseLock);
builder.Services.AddSingleton<PipelineResolutionRegistry>();
builder.Services.AddSingleton(operationStoreOptions);
builder.Services.AddSingleton(operationExecutionOptions);
builder.Services.AddSingleton<OperationStore>();
builder.Services.AddSingleton<BoundedOperationScheduler>();
builder.Services.AddSingleton(buildPipelineOptions);
builder.Services.AddSingleton(runtimePipelineOptions);
builder.Services.AddSingleton(artifactPipelineOptions);
builder.Services.AddSingleton(languageSessionOptions);
builder.Services.AddSingleton(languageWorkerEndpoints);
builder.Services.AddSingleton(artifactWorkerEndpoints);
builder.Services.AddSingleton(runtimeSupervisorSettings);
builder.Services.AddSingleton(dependencyHealthOptions);
builder.Services.AddSingleton(new ProfileUpdateStatusOptions(profileUpdateStatusPath));
builder.Services.AddSingleton<ProfileUpdateStatusReader>();
builder.Services.AddSingleton(githubOAuthOptions);
builder.Services.AddSingleton<GitHubOAuthSessionStore>();
builder.Services.AddHttpClient(nameof(ToolchainWorkerClientFactory), client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<IToolchainWorkerClientFactory, ToolchainWorkerClientFactory>();
builder.Services.AddHttpClient<GitHubOAuthClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<IGitHubGistClient, GitHubGistClient>(client =>
{
    client.BaseAddress = githubApiBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(20);
    client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<IArtifactStoreClient, ArtifactStoreClient>(client =>
{
    client.BaseAddress = artifactStoreBaseAddress;
    client.Timeout = Timeout.InfiniteTimeSpan;
    internalServiceAuthentication.ConfigureClient(client);
});
builder.Services.AddHttpClient(nameof(ArtifactWorkerClientFactory), client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<IArtifactWorkerClientFactory, ArtifactWorkerClientFactory>();
builder.Services.AddHttpClient<IRuntimeSupervisorClient, RuntimeSupervisorClient>(client =>
{
    client.BaseAddress = runtimeSupervisorBaseAddress;
    client.Timeout = Timeout.InfiniteTimeSpan;
    internalServiceAuthentication.ConfigureClient(client);
});
builder.Services.AddHttpClient(nameof(HttpGatewayDependencyProbe), client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.MaxResponseContentBufferSize = 1024 * 1024;
});
builder.Services.AddSingleton<IGatewayDependencyProbe, HttpGatewayDependencyProbe>();
builder.Services.AddSingleton<GatewayDependencyHealthService>();
builder.Services.AddSingleton<IBuildArtifactPublisher, BuildArtifactPublisher>();
builder.Services.AddSingleton<BuildOperationExecutor>();
builder.Services.AddSingleton<ArtifactOperationExecutor>();
builder.Services.AddSingleton<RuntimeOperationExecutor>();
builder.Services.AddSingleton<ExplainOperationExecutor>();
builder.Services.AddSingleton<OperationControlService>();
builder.Services.AddSingleton<OperationCommandWebSocket>();
builder.Services.AddSingleton<GistShareService>();
builder.Services.AddSingleton<LanguageSessionGatewayService>();
builder.Services.AddHostedService(static services => services.GetRequiredService<LanguageSessionGatewayService>());
builder.Services.AddSingleton<LanguageWebSocketProxy>();
builder.Services.AddSharpLabNextGatewayTrafficProtection(gatewayTrafficOptions);

var app = builder.Build();

app.UseSharpLabNextOperationCapacityHandling();
app.UseSharpLabNextGatewayTrafficProtection();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = languageSessionOptions.KeepAliveInterval });
var developmentFrontend = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "..", "frontend", "dist"));
var packagedFrontend = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var frontendAssets = new PrecompressedStaticAssetServer([packagedFrontend, developmentFrontend]);
app.Use(async (context, next) =>
{
    if (!await frontendAssets.TryServeRequestAsync(context))
        await next(context);
});

app.MapGet("/health/live", () => Results.Ok(new { Status = "live" }));
app.MapGet("/health/ready", async (ServiceIdentity service, GatewayDependencyHealthService dependencyHealth, CancellationToken cancellationToken) =>
{
    var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken);
    var response = new { Status = snapshot.Ready ? "ready" : "unavailable", service.Id, service.ReleaseId, Protocol = service.Protocol.ToString(), CatalogRevision = snapshot.Catalog.Revision, ObservedAtUtc = snapshot.ObservedAtUtc, Dependencies = snapshot.Dependencies.Values.OrderBy(static dependency => dependency.Id, StringComparer.Ordinal).Select(static dependency => new { dependency.Id, Kind = dependency.Kind.ToString(), Status = dependency.Ready ? "ready" : "unavailable", dependency.Reason }) };
    return snapshot.Ready
        ? Results.Ok(response) : Results.Json(response, ContractJson.CreateSerializerOptions(), statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/v1/system", (ServiceIdentity service) => service);
app.MapGet("/api/v1/profile-updates", (ServiceIdentity service, ProfileUpdateStatusReader status, CancellationToken cancellationToken) => status.ReadAsync(service.ReleaseId, cancellationToken));
app.MapGet("/api/v1/catalog", async (GatewayDependencyHealthService dependencyHealth, CancellationToken cancellationToken) => (await dependencyHealth.GetSnapshotAsync(cancellationToken)).Catalog);

app.MapPost("/api/v1/selections/resolve", async (ResolveSelectionRequest request, PipelineResolutionRegistry registry, GatewayDependencyHealthService dependencyHealth, CancellationToken cancellationToken) =>
{
    try
    {
        var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken);
        var resolution = Resolver.Resolve(snapshot.Catalog, request, DateTimeOffset.UtcNow);
        registry.Store(resolution);
        return Results.Ok(resolution);
    }
    catch (SelectionResolutionException exception)
    {
        return Results.BadRequest(new { Error = exception.Code, Field = exception.Field.ToString(), Value = exception.Value, Message = exception.Message });
    }
});

app.MapPost("/api/v1/language-sessions", async (OpenLanguageSessionRequest request, HttpContext context, LanguageSessionGatewayService sessions) =>
{
    try
    {
        var session = await sessions.OpenAsync(request, context.RequestAborted);
        return Results.Ok(session);
    }
    catch (GatewayLanguageSessionException exception)
    {
        return Results.Json(
            new { Error = exception.Code, Message = exception.Message, TraceId = context.TraceIdentifier },
            ContractJson.CreateSerializerOptions(),
            statusCode: exception.StatusCode);
    }
});

app.MapDelete("/api/v1/language-sessions/{sessionId}", async (string sessionId, HttpContext context, LanguageSessionGatewayService sessions) =>
{
    // Session teardown is intentionally idempotent. A WebSocket close can
    // remove the session before the client-side cleanup request arrives.
    await sessions.CloseAsync(sessionId, context.RequestAborted);
    return Results.NoContent();
});

app.MapGet("/api/v1/language-sessions/{sessionId}/lsp", async (string sessionId, HttpContext context, LanguageWebSocketProxy proxy) =>
{
    await proxy.RunAsync(sessionId, context);
});

app.MapPost("/api/v1/builds", async (BuildRequest request, HttpContext context, OperationControlService control) => (await control.StartBuildAsync(request, context.TraceIdentifier, context.RequestAborted)).ToHttpResult());
app.MapPost("/api/v1/explanations", async (ExplainRequest request, HttpContext context, OperationControlService control) => (await control.StartExplainAsync(request, context.TraceIdentifier, context.RequestAborted)).ToHttpResult());
app.MapPost("/api/v1/artifact-transforms", async (TransformArtifactRequest request, HttpContext context, OperationControlService control) => (await control.StartTransformAsync(request, context.TraceIdentifier, context.RequestAborted)).ToHttpResult());
app.MapPost("/api/v1/artifact-renders", async (RenderArtifactRequest request, HttpContext context, OperationControlService control) => (await control.StartRenderAsync(request, context.TraceIdentifier, context.RequestAborted)).ToHttpResult());
app.MapPost("/api/v1/verifications", async (VerifyArtifactRequest request, HttpContext context, OperationControlService control) => (await control.StartVerificationAsync(request, context.TraceIdentifier, context.RequestAborted)).ToHttpResult());
app.MapPost("/api/v1/runs", async (RunRequest request, HttpContext context, OperationControlService control) => (await control.StartRunAsync(request, context.TraceIdentifier, context.RequestAborted)).ToHttpResult());
app.MapPost("/api/v1/jit", StartJit);
app.MapPost("/api/v1/jits", StartJit);
app.MapGet("/api/v1/operations/ws", (HttpContext context, OperationCommandWebSocket commands) => commands.RunAsync(context));

app.MapGet("/api/v1/operations/{operationId}", (string operationId, OperationControlService control) => control.GetState(operationId).ToHttpResult());

app.MapGet("/api/v1/operations/{operationId}/contents/sha256/{digest}", async (string operationId, string digest, HttpContext context, OperationStore operations, IArtifactStoreClient artifactStore, ArtifactPipelineOptions contentOptions) =>
{
    ContentRef contentRef;
    try
    {
        contentRef = ArtifactStoreProtocol.ContentRefFromDigest(digest);
    }
    catch (ArgumentException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(
            new { Error = "invalid-content-reference", Message = "The content digest is malformed." },
            ContractJson.CreateSerializerOptions(),
            context.RequestAborted);
        return;
    }

    var events = operations.GetEvents(operationId);
    var produced = events?.Select(static item => item.Payload).OfType<ContentProducedOperationEventPayload>().FirstOrDefault(item => item.ContentRef == contentRef);
    if (produced is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    try
    {
        await using var content = await artifactStore.OpenContentReadAsync(contentRef, context.RequestAborted);
        if (content.Length is null || content.Length < 0 || content.Length > contentOptions.MaximumPublicContentBytes || produced.Size != content.Length)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(
                new { Error = "content-metadata-invalid", Message = "The stored result content metadata is invalid." },
                ContractJson.CreateSerializerOptions(),
                context.RequestAborted);
            return;
        }

        context.Response.ContentType = produced.MediaType;
        context.Response.ContentLength = content.Length;
        context.Response.Headers.CacheControl = "private, max-age=3600, immutable";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (content.ETag is not null)
            context.Response.Headers.ETag = content.ETag;
        await content.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (ArtifactStoreHttpException exception) when (exception.StatusCodeValue == System.Net.HttpStatusCode.NotFound)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
    catch (Exception exception) when (exception is ArtifactStoreHttpException or HttpRequestException)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(
            new { Error = "artifact-store-unavailable", Message = "The result content is temporarily unavailable." },
            ContractJson.CreateSerializerOptions(),
            context.RequestAborted);
    }
});

app.MapGet("/api/v1/operations/{operationId}/events", async (string operationId, HttpContext context, OperationStore operations) =>
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

    if (fromSequence is < 0)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var serializerOptions = ContractJson.CreateSerializerOptions();
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        try
        {
            await foreach (var operationEvent in operations.WatchAsync(operationId, fromSequence ?? 0, context.RequestAborted))
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(operationEvent, serializerOptions);
                await socket.SendAsync(json.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted).ConfigureAwait(false);
            }

            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Operation stream completed.", context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            // Browser disconnects and request cancellation end only this subscription.
        }
        return;
    }

    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-store";
    await foreach (var operationEvent in operations.WatchAsync(operationId, fromSequence ?? 0, context.RequestAborted))
    {
        var json = JsonSerializer.Serialize(operationEvent, serializerOptions);
        await context.Response.WriteAsync($"id: {operationEvent.Sequence}\nevent: operation\ndata: {json}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
});

app.MapPost("/api/v1/operations/{operationId}/cancel", (string operationId, CancelOperationRequest request, OperationControlService control) => control.Cancel(operationId, request.OperationId, request.Reason).ToHttpResult());

app.MapGet("/api/v1/auth/github/status", (HttpContext context, GitHubOAuthOptions options, GitHubOAuthSessionStore sessions) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var session = GetGitHubSession(context, sessions);
    return Results.Ok(new GitHubAuthStatus(options.Available, session is not null, session?.Login, session?.CsrfToken));
});

app.MapGet("/api/v1/auth/github/start", (HttpContext context, GitHubOAuthOptions options, GitHubOAuthSessionStore sessions, GitHubOAuthClient oauth) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!PascalCaseQuery.TryGetOptionalSingle(context.Request, "ReturnPath", out var returnPath))
    {
        return Results.BadRequest(new { Error = "invalid-query", Message = "ReturnPath must use its exact PascalCase spelling and appear at most once." });
    }

    if (!options.Available)
    {
        return Results.Json(
            new { Error = "oauth-unavailable", Message = "GitHub OAuth is not configured." },
            ContractJson.CreateSerializerOptions(),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    try
    {
        var pending = sessions.CreatePending(returnPath, DateTimeOffset.UtcNow);
        context.Response.Cookies.Append("SharpLabNext.GitHubOAuthState", pending.State, GitHubCookie(context, options, httpOnly: true, sameSite: SameSiteMode.Lax, path: "/api/v1/auth/github/callback"));
        var callback = GitHubCallbackUri(context, options);
        return Results.Ok(new GitHubOAuthStartResponse(oauth.CreateAuthorizationUri(pending.State, callback).AbsoluteUri));
    }
    catch (GitHubOAuthException exception)
    {
        return Results.BadRequest(new { Error = "oauth-invalid-request", Message = exception.Message });
    }
});

app.MapGet("/api/v1/auth/github/callback", async (string? code, string? state, string? error, HttpContext context, GitHubOAuthOptions options, GitHubOAuthSessionStore sessions, GitHubOAuthClient oauth, IGitHubGistClient github) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
    {
        return Results.BadRequest(new { Error = "oauth-denied", Message = "GitHub authorization was denied or incomplete." });
    }
    var stateCookie = context.Request.Cookies["SharpLabNext.GitHubOAuthState"];
    context.Response.Cookies.Delete("SharpLabNext.GitHubOAuthState", GitHubCookie(context, options, httpOnly: true, sameSite: SameSiteMode.Lax, path: "/api/v1/auth/github/callback"));
    if (stateCookie is null || !sessions.TryTakePending(state, stateCookie, DateTimeOffset.UtcNow, out var pending))
    {
        return Results.BadRequest(new { Error = "oauth-state-invalid", Message = "The GitHub OAuth state is invalid or expired." });
    }
    try
    {
        var callback = GitHubCallbackUri(context, options);
        var token = await oauth.ExchangeCodeAsync(code, callback, context.RequestAborted);
        var login = await github.GetLoginAsync(token, context.RequestAborted);
        var session = sessions.CreateSession(token, login, DateTimeOffset.UtcNow);
        context.Response.Cookies.Append("SharpLabNext.GitHubSession", session.SessionId, GitHubCookie(context, options, httpOnly: true, sameSite: SameSiteMode.Lax, path: "/"));
        return Results.Redirect(pending!.ReturnPath);
    }
    catch (GitHubOAuthException exception)
    {
        return Results.BadRequest(new { Error = "oauth-exchange-failed", Message = exception.Message });
    }
    catch (GitHubApiException exception)
    {
        return GitHubProblem(exception);
    }
});

app.MapPost("/api/v1/auth/github/logout", (HttpContext context, GitHubOAuthOptions options, GitHubOAuthSessionStore sessions) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var session = GetGitHubSession(context, sessions);
    if (session is null)
    {
        context.Response.Cookies.Delete("SharpLabNext.GitHubSession", GitHubCookie(context, options, httpOnly: true, sameSite: SameSiteMode.Lax, path: "/"));
        return Results.NoContent();
    }
    if (!sessions.ValidateCsrf(session, context.Request.Headers["X-SharpLabNext-CSRF"].FirstOrDefault()))
    {
        return Results.Json(
            new { Error = "csrf-invalid", Message = "The GitHub session CSRF token is missing or invalid." },
            ContractJson.CreateSerializerOptions(),
            statusCode: StatusCodes.Status403Forbidden);
    }
    sessions.RemoveSession(session.SessionId);
    context.Response.Cookies.Delete("SharpLabNext.GitHubSession", GitHubCookie(context, options, httpOnly: true, sameSite: SameSiteMode.Lax, path: "/"));
    return Results.NoContent();
});

app.MapGet("/api/v1/shares/gists/{id}", async (string id, HttpContext context, GitHubOAuthSessionStore sessions, GistShareService gists) =>
{
    context.Response.Headers.CacheControl = "private, no-store";
    if (!PascalCaseQuery.TryGetOptionalSingle(context.Request, "Target", out var target) || !PascalCaseQuery.TryGetOptionalSingle(context.Request, "Branch", out var branch) || !PascalCaseQuery.TryGetOptionalSingle(context.Request, "Mode", out var mode))
    {
        return Results.BadRequest(new { Error = "invalid-query", Message = "Target, Branch, and Mode must use their exact PascalCase spellings and appear at most once." });
    }

    BuildConfiguration? buildMode = mode?.ToLowerInvariant() switch
    {
        null or "" => null,
        "debug" => BuildConfiguration.Debug,
        "release" => BuildConfiguration.Release,
        _ => (BuildConfiguration?)-1
    };
    if (buildMode is not null && !Enum.IsDefined(buildMode.Value))
        return Results.BadRequest(new { Error = "invalid-mode", Message = "Gist mode must be debug or release." });
    try
    {
        var document = await gists.GetAsync(id, new GistLoadOverrides(target, branch, buildMode), GetGitHubSession(context, sessions), context.RequestAborted);
        return Results.Ok(document);
    }
    catch (GistValidationException exception)
    {
        return Results.BadRequest(new { Error = "invalid-gist", Message = exception.Message });
    }
    catch (GitHubApiException exception)
    {
        return GitHubProblem(exception);
    }
});

app.MapPost("/api/v1/shares/gists", async (CreateGistRequest request, HttpContext context, GitHubOAuthSessionStore sessions, GistShareService gists) =>
{
    context.Response.Headers.CacheControl = "private, no-store";
    var session = GetGitHubSession(context, sessions);
    var unauthorized = RequireGitHubMutationSession(context, sessions, session);
    if (unauthorized is not null)
        return unauthorized;
    try
    {
        var document = await gists.CreateAsync(request, session!, context.RequestAborted);
        return Results.Created($"/api/v1/shares/gists/{document.Id}", document);
    }
    catch (GistValidationException exception)
    {
        return Results.BadRequest(new { Error = "invalid-gist", Message = exception.Message });
    }
    catch (GitHubApiException exception)
    {
        return GitHubProblem(exception);
    }
});

app.MapPatch("/api/v1/shares/gists/{id}", async (string id, UpdateGistRequest request, HttpContext context, GitHubOAuthSessionStore sessions, GistShareService gists) =>
{
    context.Response.Headers.CacheControl = "private, no-store";
    var session = GetGitHubSession(context, sessions);
    var unauthorized = RequireGitHubMutationSession(context, sessions, session);
    if (unauthorized is not null)
        return unauthorized;
    try
    {
        return Results.Ok(await gists.UpdateAsync(id, request, session!, context.RequestAborted));
    }
    catch (GistAuthorizationException exception)
    {
        return Results.Json(
            new { Error = "gist-forbidden", Message = exception.Message },
            ContractJson.CreateSerializerOptions(),
            statusCode: StatusCodes.Status403Forbidden);
    }
    catch (GistValidationException exception)
    {
        return Results.BadRequest(new { Error = "invalid-gist", Message = exception.Message });
    }
    catch (GitHubApiException exception)
    {
        return GitHubProblem(exception);
    }
});

app.MapFallback(async context =>
{
    if (await frontendAssets.TryServeIndexAsync(context))
        return;

    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    await context.Response.WriteAsJsonAsync(
        new { Error = "frontend-not-built", Message = "Run the frontend production build first." },
        ContractJson.CreateSerializerOptions(),
        context.RequestAborted);
});

app.Run();

static string ResolveConfigurationFile(string contentRoot, string? configuredPath, string fileName)
{
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        return Path.GetFullPath(configuredPath, contentRoot);
    }

    var packaged = Path.Combine(contentRoot, "profiles", fileName);
    if (File.Exists(packaged))
    {
        return packaged;
    }

    return Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "..", "profiles", fileName));
}

static string ResolveProfileUpdateStatusFile(string contentRoot, string? configuredPath)
{
    if (!string.IsNullOrWhiteSpace(configuredPath))
        return Path.GetFullPath(configuredPath, contentRoot);

    var packaged = Path.Combine(contentRoot, "config", "profile-update-status.json");
    if (File.Exists(packaged))
        return packaged;

    return Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "..", "artifacts", "profile-updater", "status.public.json"));
}

static Uri RequiredServiceUri(string? value, string configurationKey)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException($"{configurationKey} must be an absolute HTTP(S) URI.");
    }

    return uri;
}

static GitHubOAuthSession? GetGitHubSession(HttpContext context, GitHubOAuthSessionStore sessions) =>
    sessions.TryGetSession(context.Request.Cookies["SharpLabNext.GitHubSession"], DateTimeOffset.UtcNow, out var session)
        ? session : null;

static IResult? RequireGitHubMutationSession(HttpContext context, GitHubOAuthSessionStore sessions, GitHubOAuthSession? session)
{
    if (session is null)
    {
        return Results.Json(
            new { Error = "github-auth-required", Message = "Sign in with GitHub before saving a Gist." },
            ContractJson.CreateSerializerOptions(),
            statusCode: StatusCodes.Status401Unauthorized);
    }
    if (!sessions.ValidateCsrf(session, context.Request.Headers["X-SharpLabNext-CSRF"].FirstOrDefault()))
    {
        return Results.Json(
            new { Error = "csrf-invalid", Message = "The GitHub session CSRF token is missing or invalid." },
            ContractJson.CreateSerializerOptions(),
            statusCode: StatusCodes.Status403Forbidden);
    }
    return null;
}

static Uri GitHubCallbackUri(HttpContext context, GitHubOAuthOptions options)
{
    if (options.CallbackUri is not null)
        return options.CallbackUri;
    return new UriBuilder(context.Request.Scheme, context.Request.Host.Host, context.Request.Host.Port ?? -1, $"{context.Request.PathBase}/api/v1/auth/github/callback").Uri;
}

static CookieOptions GitHubCookie(HttpContext context, GitHubOAuthOptions options, bool httpOnly, SameSiteMode sameSite, string path) => new()
{
    HttpOnly = httpOnly,
    Secure = context.Request.IsHttps || options.CallbackUri?.Scheme == Uri.UriSchemeHttps,
    SameSite = sameSite,
    IsEssential = true,
    Path = path
};

static IResult GitHubProblem(GitHubApiException exception)
{
    var statusCode = (int)exception.StatusCode;
    if (statusCode < 400 || statusCode > 599)
        statusCode = StatusCodes.Status502BadGateway;
    return Results.Json(
        new { Error = "github-api-error", Message = exception.PublicMessage },
        ContractJson.CreateSerializerOptions(),
        statusCode: statusCode);
}

static async Task<IResult> StartJit(JitRequest request, HttpContext context, OperationControlService control) =>
    (await control.StartJitAsync(request, context.TraceIdentifier, context.RequestAborted)).ToHttpResult();

public partial class Program;

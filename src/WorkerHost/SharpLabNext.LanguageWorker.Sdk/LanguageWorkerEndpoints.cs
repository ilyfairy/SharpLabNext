using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SharpLabNext.Contracts;
using SharpLabNext.InternalServices;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.LanguageWorker.Sdk;

public interface ILanguageWorkerBuildService
{
    Task<LanguageWorkerBuildExecution> BuildAsync(
        BuildRequest request,
        CancellationToken cancellationToken);
}

public interface ILanguageWorkerSessionService
{
    Task<LanguageSession> OpenAsync(
        OpenLanguageSessionRequest request,
        CancellationToken cancellationToken);

    Task<bool> CloseAsync(string sessionId, CancellationToken cancellationToken);

    Task RunAsync(string sessionId, WebSocket socket, CancellationToken cancellationToken);
}

public static class LanguageWorkerEndpointExtensions
{
    public static IServiceCollection AddSharpLabNextLanguageWorker<TBuildService>(
        this IServiceCollection services,
        ServiceIdentity descriptor,
        LanguageWorkerCapabilityManifest capabilityManifest,
        LanguageWorkerHostMetadata hostMetadata)
        where TBuildService : class, ILanguageWorkerBuildService
    {
        LanguageWorkerCapabilityManifestSerializer.Validate(capabilityManifest, descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostMetadata.WorkerImageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostMetadata.InstanceId);
        ValidateReferenceSetAttestations(capabilityManifest, hostMetadata.ReferenceSets);
        services.AddSharpLabNextWorker(descriptor);
        services.AddSingleton(capabilityManifest);
        services.AddSingleton(hostMetadata);
        services.AddSingleton<ILanguageWorkerBuildService, TBuildService>();
        return services;
    }

    public static IServiceCollection AddSharpLabNextLanguageWorker<TBuildService, TSessionService>(
        this IServiceCollection services,
        ServiceIdentity descriptor,
        LanguageWorkerCapabilityManifest capabilityManifest,
        LanguageWorkerHostMetadata hostMetadata)
        where TBuildService : class, ILanguageWorkerBuildService
        where TSessionService : class, ILanguageWorkerSessionService
    {
        services.AddSharpLabNextLanguageWorker<TBuildService>(descriptor, capabilityManifest, hostMetadata);
        services.AddSingleton<ILanguageWorkerSessionService, TSessionService>();
        return services;
    }

    public static WebApplication MapSharpLabNextLanguageWorker(
        this WebApplication app,
        bool mapLanguageSessions = true)
    {
        app.UseSharpLabNextInternalServiceAuthentication(
            InternalServiceAuthenticationOptions.FromConfiguration(app.Configuration, app.Environment));
        app.MapGet("/health/live", () => Results.Ok(new { Status = "live" }));
        app.MapGet("/health/ready", (
            ServiceIdentity identity,
            LanguageWorkerHostMetadata hostMetadata) => Results.Ok(new HealthResponse(
                HealthStatus.Healthy,
                identity.Id,
                hostMetadata.InstanceId,
                identity.Protocol,
                DateTimeOffset.UtcNow,
                [new HealthCheckResult("language-worker", HealthStatus.Healthy, "Language worker is ready.", TimeSpan.Zero)])));
        app.MapGet("/api/v1/worker/describe", (
            ServiceIdentity identity,
            LanguageWorkerCapabilityManifest manifest,
            LanguageWorkerHostMetadata hostMetadata) => CreateWorkerDescriptor(identity, manifest, hostMetadata));
        app.MapGet("/api/v1/worker/capabilities", (LanguageWorkerCapabilityManifest manifest) => manifest);
        app.MapPost("/api/v1/build", HandleBuildAsync);
        if (mapLanguageSessions)
        {
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
            app.MapPost("/api/v1/language-sessions", HandleOpenSessionAsync);
            app.MapDelete("/api/v1/language-sessions/{sessionId}", HandleCloseSessionAsync);
            app.MapGet("/api/v1/language-sessions/{sessionId}/lsp", HandleLanguageChannelAsync);
        }
        return app;
    }

    private static WorkerDescriptor CreateWorkerDescriptor(
        ServiceIdentity identity,
        LanguageWorkerCapabilityManifest manifest,
        LanguageWorkerHostMetadata hostMetadata)
    {
        var profiles = manifest.ToolchainIds.ToArray();
        return new WorkerDescriptor(
            identity,
            hostMetadata.InstanceId,
            WorkerKind.Toolchain,
            hostMetadata.WorkerImageId,
            identity.Protocol,
            [identity.Protocol],
            manifest.Capabilities.Select(capability => new WorkerCapabilityDescriptor(
                capability,
                1,
                Available: true,
                profiles)).ToArray(),
            profiles,
            hostMetadata.StartedAtUtc,
            ReferenceSets: hostMetadata.ReferenceSets);
    }

    private static void ValidateReferenceSetAttestations(
        LanguageWorkerCapabilityManifest manifest,
        IReadOnlyList<ReferenceSetAttestation>? attestations)
    {
        if (attestations is null)
            return;
        var byId = new Dictionary<string, ReferenceSetAttestation>(StringComparer.Ordinal);
        foreach (var attestation in attestations)
        {
            if (string.IsNullOrWhiteSpace(attestation.Id) ||
                !byId.TryAdd(attestation.Id, attestation) ||
                string.IsNullOrWhiteSpace(attestation.TargetFramework) ||
                string.IsNullOrWhiteSpace(attestation.Digest) ||
                string.IsNullOrWhiteSpace(attestation.ContentDigest) ||
                attestation.Provenance is null ||
                string.IsNullOrWhiteSpace(attestation.Provenance.Kind) ||
                string.IsNullOrWhiteSpace(attestation.Provenance.ResolvedVersion))
            {
                throw new ArgumentException(
                    "Language worker reference-set attestations are invalid.",
                    nameof(attestations));
            }
        }
        if (manifest.SupportedReferenceSetIds.Any(id => !byId.ContainsKey(id)))
        {
            throw new ArgumentException(
                "Language worker host metadata must attest every supported reference set.",
                nameof(attestations));
        }
    }

    private static async Task<IResult> HandleBuildAsync(
        BuildRequest request,
        ILanguageWorkerBuildService buildService,
        ServiceIdentity identity,
        LanguageWorkerCapabilityManifest manifest,
        HttpContext context)
    {
        try
        {
            ValidateBuildRequest(request, identity, manifest);
            var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return Problem(context, identity, "deadline-exceeded", "The build deadline elapsed.", StatusCodes.Status408RequestTimeout);
            var allowed = TimeSpan.FromMilliseconds(manifest.Limits.MaximumBuildMilliseconds);
            using var deadline = new CancellationTokenSource(remaining < allowed ? remaining : allowed);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
            var execution = await buildService.BuildAsync(request, linked.Token).ConfigureAwait(false);
            return Results.Ok(new LanguageWorkerBuildHttpResponse(request.RequestId, execution.Result, execution.Artifact));
        }
        catch (LanguageWorkerRequestException exception)
        {
            return Problem(context, identity, exception.Code, exception.PublicMessage, exception.StatusCode);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Problem(context, identity, "cancelled", "The build was cancelled.", 499);
        }
        catch (OperationCanceledException)
        {
            return Problem(context, identity, "deadline-exceeded", "The build deadline elapsed.", StatusCodes.Status408RequestTimeout);
        }
    }

    private static async Task<IResult> HandleOpenSessionAsync(
        OpenLanguageSessionRequest request,
        ILanguageWorkerSessionService sessions,
        ServiceIdentity identity,
        LanguageWorkerCapabilityManifest manifest,
        HttpContext context)
    {
        try
        {
            ValidateLanguageSessionRequest(request, identity, manifest);
            return Results.Ok(await sessions.OpenAsync(request, context.RequestAborted).ConfigureAwait(false));
        }
        catch (LanguageWorkerRequestException exception)
        {
            return Results.Problem(statusCode: exception.StatusCode, title: exception.Code, detail: exception.PublicMessage);
        }
    }

    private static async Task<IResult> HandleCloseSessionAsync(
        string sessionId,
        ILanguageWorkerSessionService sessions,
        HttpContext context) =>
        await sessions.CloseAsync(sessionId, context.RequestAborted).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task HandleLanguageChannelAsync(
        string sessionId,
        ILanguageWorkerSessionService sessions,
        HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        try
        {
            await sessions.RunAsync(sessionId, socket, context.RequestAborted).ConfigureAwait(false);
        }
        catch (LanguageWorkerRequestException exception)
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    exception.PublicMessage,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateBuildRequest(
        BuildRequest request,
        ServiceIdentity identity,
        LanguageWorkerCapabilityManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new LanguageWorkerRequestException("invalid-request", "Request identity is required.");
        if (!manifest.ToolchainIds.Contains(request.ToolchainId, StringComparer.Ordinal))
            throw new LanguageWorkerRequestException("wrong-toolchain", "The request targets another toolchain.");
        if (!string.Equals(request.Workspace.LanguageId, manifest.LanguageId, StringComparison.Ordinal))
            throw new LanguageWorkerRequestException("wrong-language", "The workspace targets another language.");
        if (request.Workspace.SchemaVersion != ContractSchemaVersions.WorkspaceSnapshot)
            throw new LanguageWorkerRequestException("unsupported-workspace", "The workspace schema version is unsupported.");
        if (!string.Equals(request.ReferenceSetId, request.Workspace.ReferenceSetId, StringComparison.Ordinal))
            throw new LanguageWorkerRequestException("reference-set-mismatch", "The request and workspace reference sets differ.");
        ValidateWorkspace(request.Workspace, manifest);
        if (!manifest.SupportedReferenceSetIds.Contains(request.ReferenceSetId, StringComparer.Ordinal))
            throw new LanguageWorkerRequestException("unsupported-reference-set", "The selected reference set is not supported by this worker.");
        var requiredCapability = request.Target switch
        {
            BuildTarget.Artifact => "artifact",
            BuildTarget.CompileCheck => "compile-check",
            BuildTarget.Ast => "ast",
            BuildTarget.GeneratedSource => "generated-source",
            _ => throw new LanguageWorkerRequestException("unsupported-target", "The selected build target is unsupported.")
        };
        if (!manifest.Capabilities.Contains(requiredCapability, StringComparer.Ordinal))
            throw new LanguageWorkerRequestException("unsupported-target", "The selected build target is not declared by this worker.");
    }

    private static void ValidateLanguageSessionRequest(
        OpenLanguageSessionRequest request,
        ServiceIdentity identity,
        LanguageWorkerCapabilityManifest manifest)
    {
        if (!manifest.Capabilities.Contains("lsp", StringComparer.Ordinal))
            throw new LanguageWorkerRequestException("unsupported-capability", "This worker does not provide language sessions.");
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.PipelineResolutionId))
            throw new LanguageWorkerRequestException("invalid-request", "Request identity is required.");
        if (!manifest.ToolchainIds.Contains(request.ToolchainId, StringComparer.Ordinal) ||
            !string.Equals(request.LanguageId, manifest.LanguageId, StringComparison.Ordinal) ||
            !string.Equals(request.Workspace.LanguageId, manifest.LanguageId, StringComparison.Ordinal))
        {
            throw new LanguageWorkerRequestException("wrong-toolchain", "The language session targets another worker.");
        }
        if (!string.Equals(request.LspVersion, ContractSchemaVersions.Lsp, StringComparison.Ordinal))
            throw new LanguageWorkerRequestException("unsupported-lsp-version", "The requested LSP version is unsupported.");
        if (!string.Equals(request.ReferenceSetId, request.Workspace.ReferenceSetId, StringComparison.Ordinal))
            throw new LanguageWorkerRequestException("reference-set-mismatch", "The request and workspace reference sets differ.");
        if (!manifest.SupportedReferenceSetIds.Contains(request.ReferenceSetId, StringComparer.Ordinal))
            throw new LanguageWorkerRequestException("unsupported-reference-set", "The selected reference set is not supported by this worker.");
        ValidateWorkspace(request.Workspace, manifest);
    }

    private static void ValidateWorkspace(
        WorkspaceSnapshot workspace,
        LanguageWorkerCapabilityManifest manifest)
    {
        if (workspace.SchemaVersion != ContractSchemaVersions.WorkspaceSnapshot)
            throw new LanguageWorkerRequestException("unsupported-workspace", "The workspace schema version is unsupported.");
        if (workspace.Files.Count == 0)
            throw new LanguageWorkerRequestException("invalid-workspace", "The workspace must contain at least one source file.");
        if (workspace.Files.Count > manifest.Limits.MaximumFiles)
            throw new LanguageWorkerRequestException("workspace-too-large", "The workspace contains too many source files.", StatusCodes.Status413PayloadTooLarge);
        long sourceBytes = 0;
        foreach (var file in workspace.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                throw new LanguageWorkerRequestException("invalid-workspace", "Every source file must have a path.");
            sourceBytes += Encoding.UTF8.GetByteCount(file.Text);
            if (sourceBytes > manifest.Limits.MaximumSourceUtf8Bytes)
                throw new LanguageWorkerRequestException("workspace-too-large", "The workspace source exceeds the UTF-8 byte limit.", StatusCodes.Status413PayloadTooLarge);
        }
        if (!workspace.Files.Any(file => string.Equals(file.Path, workspace.ActiveFile, StringComparison.Ordinal)))
            throw new LanguageWorkerRequestException("invalid-workspace", "The active file must exist in the workspace.");
    }

    private static IResult Problem(
        HttpContext context,
        ServiceIdentity identity,
        string code,
        string message,
        int statusCode) => Results.Problem(
            statusCode: statusCode,
            title: code,
            detail: message,
            extensions: new Dictionary<string, object?>
            {
                ["Code"] = code,
                ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                ["WorkerId"] = identity.Id
            });
}

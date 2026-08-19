using System.Diagnostics;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.FSharp.Compiler;

namespace SharpLabNext.Worker.FSharp;

public sealed record FSharpWorkerProcessIdentity(string InstanceId, DateTimeOffset StartedAtUtc)
{
    public static FSharpWorkerProcessIdentity Create() => new($"fsharp-stable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
}

public sealed class FSharpWorkerHealthService(
    FSharpReferenceSetProvider referenceSets,
    FSharpWorkerSettings settings,
    FSharpWorkerProcessIdentity processIdentity)
{
    private static readonly string[] Capabilities =
    [
        "compile-check", "managed-pe", "portable-pdb", "ast", "multi-file", "lsp",
        "diagnostics", "completion", "hover", "signature-help", "semantic-tokens",
        "document-symbols", "code-actions"
    ];

    public async Task<HealthResponse> CheckAsync(CancellationToken cancellationToken)
    {
        var compilerTimer = Stopwatch.StartNew();
        var compilerHealthy = settings.Identity.CompilerVersion == FSharpCompilerFacade.CompilerVersion &&
            FSharpCompilerFacade.LoadedCompilerVersion == FSharpCompilerFacade.CompilerVersion &&
            settings.Identity.FSharpCorePackageVersion == FSharpCompilerFacade.FSharpCorePackageVersion;
        var checks = new List<HealthCheckResult>
        {
            new(
                "fsharp-compiler-identity",
                compilerHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                compilerHealthy
                    ? $"FSharp.Compiler.Service {FSharpCompilerFacade.CompilerVersion} and FSharp.Core {FSharpCompilerFacade.FSharpCorePackageVersion} are pinned."
                    : "The loaded F# compiler identity is not approved.",
                compilerTimer.Elapsed)
        };
        var referenceTimer = Stopwatch.StartNew();
        var referenceHealth = await referenceSets.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(new HealthCheckResult(
            "reference-sets",
            referenceHealth.IsHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
            referenceHealth.Message,
            referenceTimer.Elapsed));
        var status = checks.All(static item => item.Status == HealthStatus.Healthy)
            ? HealthStatus.Healthy
            : HealthStatus.Unhealthy;
        return new HealthResponse(
            status,
            settings.Identity.ToolchainId,
            processIdentity.InstanceId,
            ProtocolVersion.WorkerV1,
            DateTimeOffset.UtcNow,
            checks);
    }

    public async Task<WorkerDescriptor> DescribeAsync(CancellationToken cancellationToken)
    {
        var health = await CheckAsync(cancellationToken).ConfigureAwait(false);
        var available = health.Status == HealthStatus.Healthy;
        string[] profiles = [settings.Identity.ToolchainId];
        return new WorkerDescriptor(
            new ServiceIdentity(
                settings.Identity.ToolchainId,
                ServiceKind.ToolchainWorker,
                settings.Identity.ReleaseId,
                ProtocolVersion.WorkerV1,
                Capabilities,
                available ? "ready" : "unhealthy"),
            processIdentity.InstanceId,
            WorkerKind.Toolchain,
            settings.Identity.WorkerImageId,
            ProtocolVersion.WorkerV1,
            [ProtocolVersion.WorkerV1],
            Capabilities.Select(capability => new WorkerCapabilityDescriptor(
                capability,
                1,
                available,
                profiles,
                available ? null : "Worker preflight failed.")).ToArray(),
            profiles,
            processIdentity.StartedAtUtc,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["compilerVersion"] = FSharpCompilerFacade.LoadedCompilerVersion,
                ["fsharpCoreVersion"] = FSharpCompilerFacade.FSharpCorePackageVersion
            },
            referenceSets.Attestations);
    }
}

public sealed class FSharpReferenceSetWarmupService(
    FSharpReferenceSetProvider referenceSets,
    ILogger<FSharpReferenceSetWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var health = await referenceSets.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (!health.IsHealthy)
            FSharpWorkerLog.ReferenceSetPreflightFailed(logger, health.Message);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

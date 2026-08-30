using System.Diagnostics;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.IL;

public sealed record IlWorkerProcessIdentity(string InstanceId, DateTimeOffset StartedAtUtc)
{
    public static IlWorkerProcessIdentity Create() => new($"mobius-ilasm-stable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
}

public sealed class IlWorkerHealthService(IlReferenceSetProvider referenceSets, IlAssemblerProcess assembler, IlWorkerSettings settings, IlWorkerProcessIdentity processIdentity)
{
    private static readonly string[] Capabilities =
    [
        "compile-check", "managed-pe", "multi-file", "lsp", "diagnostics", "completion", "hover",
        "signature-help", "code-actions", "semantic-tokens", "document-symbols", "folding-ranges"
    ];

    public async Task<HealthResponse> CheckAsync(CancellationToken cancellationToken)
    {
        var checks = new List<HealthCheckResult>();
        var assemblerTimer = Stopwatch.StartNew();
        var assemblerHealth = await assembler.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(new HealthCheckResult("mobius-ilasm", assemblerHealth.IsHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, assemblerHealth.Message, assemblerTimer.Elapsed));

        var referenceTimer = Stopwatch.StartNew();
        var referenceHealth = referenceSets.CheckHealth();
        checks.Add(new HealthCheckResult("reference-sets", referenceHealth.IsHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, referenceHealth.Message, referenceTimer.Elapsed));

        var storageTimer = Stopwatch.StartNew();
        var storage = CheckStorage();
        checks.Add(new HealthCheckResult("temporary-storage", storage.IsHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, storage.Message, storageTimer.Elapsed));
        var status = checks.All(static check => check.Status == HealthStatus.Healthy)
            ? HealthStatus.Healthy : HealthStatus.Unhealthy;
        return new HealthResponse(status, settings.Identity.ToolchainId, processIdentity.InstanceId, ProtocolVersion.WorkerV1, DateTimeOffset.UtcNow, checks);
    }

    public async Task<WorkerDescriptor> DescribeAsync(CancellationToken cancellationToken)
    {
        var health = await CheckAsync(cancellationToken).ConfigureAwait(false);
        var available = health.Status == HealthStatus.Healthy;
        string[] profiles = [settings.Identity.ToolchainId];
        return new WorkerDescriptor(
            new ServiceIdentity(settings.Identity.ToolchainId, ServiceKind.ToolchainWorker, settings.Identity.ReleaseId, ProtocolVersion.WorkerV1, Capabilities, available ? "ready" : "unhealthy"),
            processIdentity.InstanceId,
            WorkerKind.Toolchain,
            settings.Identity.WorkerImageId,
            ProtocolVersion.WorkerV1,
            [ProtocolVersion.WorkerV1],
            Capabilities.Select(capability => new WorkerCapabilityDescriptor(capability, 1, available, profiles, available ? null : "Worker preflight failed.")).ToArray(),
            profiles,
            processIdentity.StartedAtUtc,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["compilerVersion"] = Compiler.IlCompilerProtocol.PackageVersion
            },
            referenceSets.Attestations);
    }

    private IlAssemblerHealth CheckStorage()
    {
        try
        {
            Directory.CreateDirectory(settings.WorkRoot);
            var path = Path.Combine(settings.WorkRoot, $"health-{Guid.NewGuid():N}");
            File.WriteAllText(path, "ready");
            File.Delete(path);
            return new IlAssemblerHealth(true, "The isolated compiler temporary directory is writable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new IlAssemblerHealth(false, "The isolated compiler temporary directory is unavailable.");
        }
    }
}

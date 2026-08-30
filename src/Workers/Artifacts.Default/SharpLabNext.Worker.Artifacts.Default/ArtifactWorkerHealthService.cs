using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker;

internal sealed class ArtifactWorkerHealthService(ArtifactWorkerSettings settings)
{
    private readonly string _instanceId = $"artifacts-default-{Guid.NewGuid():N}";
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    public HealthResponse Check()
    {
        var checks = new List<HealthCheckResult>();
        checks.Add(File.Exists(settings.ProcessorAssemblyPath) ? new HealthCheckResult("processor", HealthStatus.Healthy, null, null) : new HealthCheckResult("processor", HealthStatus.Unhealthy, "The isolated artifact processor is unavailable.", null));
        try
        {
            Directory.CreateDirectory(settings.WorkRoot);
            checks.Add(new HealthCheckResult("work-root", HealthStatus.Healthy, null, null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            checks.Add(new HealthCheckResult("work-root", HealthStatus.Unhealthy, "The artifact worker temporary storage is unavailable.", null));
        }

        foreach (var referenceSetId in ArtifactReferenceSetConfigurationContract.RequiredSystemModules.Keys.Order(StringComparer.Ordinal))
        {
            var available = settings.ReferenceSets.TryGetValue(referenceSetId, out var referenceSet) &&
                referenceSet.Paths.Count > 0 &&
                referenceSet.Paths.All(Directory.Exists);
            checks.Add(new HealthCheckResult($"reference-set:{referenceSetId}", available ? HealthStatus.Healthy : HealthStatus.Unhealthy, available ? null : "A configured artifact reference set is unavailable.", null));
        }

        var status = checks.Any(static check => check.Status == HealthStatus.Unhealthy)
            ? HealthStatus.Unhealthy : HealthStatus.Healthy;
        return new HealthResponse(status, settings.Identity.ProcessorId, _instanceId, ProtocolVersion.WorkerV1, DateTimeOffset.UtcNow, checks);
    }

    public WorkerDescriptor Describe()
    {
        var health = Check();
        var available = health.Status == HealthStatus.Healthy;
        var profiles = new[] { settings.Identity.ProcessorId };
        return new WorkerDescriptor(
            new ServiceIdentity(settings.Identity.ProcessorId, ServiceKind.ArtifactWorker, settings.Identity.ReleaseId, ProtocolVersion.WorkerV1, ["il", "decompiled-csharp", "il-verify"], available ? "ready" : "unhealthy"),
            _instanceId,
            WorkerKind.ArtifactProcessor,
            settings.Identity.WorkerImageId,
            ProtocolVersion.WorkerV1,
            [ProtocolVersion.WorkerV1],
            [
                new WorkerCapabilityDescriptor("il", 1, available, profiles),
                new WorkerCapabilityDescriptor("decompiled-csharp", 1, available, profiles),
                new WorkerCapabilityDescriptor("il-verify", 1, available, profiles)
            ],
            profiles,
            _startedAtUtc,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ilspyVersion"] = settings.Identity.IlSpyVersion,
                ["ilVerificationVersion"] = settings.Identity.IlVerificationVersion
            });
    }
}

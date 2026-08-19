using System.Diagnostics;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics;

internal sealed class ConstGenericsArtifactWorkerReadinessCheck(
    ConstGenericsProcessorRunner processorRunner,
    ConstGenericsArtifactWorkerSettings settings) : IArtifactWorkerReadinessCheck
{
    public string Name => "const-generics-processor";

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!Directory.Exists(settings.ReferenceRoot) ||
            !File.Exists(Path.Combine(settings.ReferenceRoot, "System.Runtime.dll")))
        {
            return new HealthCheckResult(
                Name,
                HealthStatus.Unhealthy,
                "The matching ConstGenerics reference set is unavailable.",
                Stopwatch.GetElapsedTime(started));
        }
        if (!Directory.Exists(settings.RuntimeReferenceRoot) ||
            !File.Exists(Path.Combine(settings.RuntimeReferenceRoot, "System.Private.CoreLib.dll")))
        {
            return new HealthCheckResult(
                Name,
                HealthStatus.Unhealthy,
                "The matching ConstGenerics runtime implementation set is unavailable.",
                Stopwatch.GetElapsedTime(started));
        }

        var health = await processorRunner.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return new HealthCheckResult(
            Name,
            health.IsHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
            health.Message,
            Stopwatch.GetElapsedTime(started));
    }
}

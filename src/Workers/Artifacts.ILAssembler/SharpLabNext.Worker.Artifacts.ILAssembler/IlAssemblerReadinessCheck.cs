using System.Diagnostics;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Artifacts.ILAssembler;

internal sealed class IlAssemblerReadinessCheck(IlCompilerProcessRunner compiler) : IArtifactWorkerReadinessCheck
{
    public string Name => "isolated-il-compiler";

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var health = await compiler.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return new HealthCheckResult(Name, health.IsHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, health.Message, Stopwatch.GetElapsedTime(started));
    }
}

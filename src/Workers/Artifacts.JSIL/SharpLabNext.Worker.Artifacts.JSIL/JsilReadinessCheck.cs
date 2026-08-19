using System.Diagnostics;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Artifacts.JSIL;

internal sealed class JsilReadinessCheck(JsilWorkerSettings settings) : IArtifactWorkerReadinessCheck
{
    public string Name => "jsil-mono";

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!File.Exists(settings.MonoPath) || !File.Exists(settings.CompilerPath) ||
            settings.ReferenceSets.Values.Any(reference => !Directory.Exists(reference.Path)))
        {
            return new HealthCheckResult(
                Name,
                HealthStatus.Unhealthy,
                "The pinned JSIL compiler, Mono runtime, or reference sets are unavailable.",
                Stopwatch.GetElapsedTime(started));
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = settings.MonoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--version" }
        });
        if (process is null)
            return new HealthCheckResult(Name, HealthStatus.Unhealthy, "Mono could not be started.", null);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var healthy = process.ExitCode == 0;
        return new HealthCheckResult(
            Name,
            healthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
            healthy ? null : "Mono failed its readiness probe.",
            Stopwatch.GetElapsedTime(started));
    }
}

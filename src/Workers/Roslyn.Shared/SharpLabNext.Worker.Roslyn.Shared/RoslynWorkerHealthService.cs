using System.Diagnostics;
using System.Reflection;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Roslyn;

public sealed record WorkerProcessIdentity(string InstanceId, DateTimeOffset StartedAtUtc)
{
    public static WorkerProcessIdentity Create(string toolchainId) => new($"{toolchainId}-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
}

internal static class RoslynCompilerIdentity
{
    private const string CommitHashAttributeName = "Microsoft.CodeAnalysis.CommitHashAttribute";

    public static string? GetCommit(Assembly assembly) => assembly.GetCustomAttributesData().Where(static attribute => attribute.AttributeType.FullName == CommitHashAttributeName).SelectMany(static attribute => attribute.ConstructorArguments).Select(static argument => argument.Value as string).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    public static bool Matches(RoslynWorkerIdentity expected, string loadedVersion, string? loadedCommit) =>
        StringComparer.Ordinal.Equals(loadedVersion, expected.CompilerVersion) &&
        (string.IsNullOrWhiteSpace(expected.CompilerCommit) || StringComparer.OrdinalIgnoreCase.Equals(loadedCommit, expected.CompilerCommit));

    public static void Ensure(RoslynWorkerIdentity expected, string compilerName, string loadedVersion, string? loadedCommit)
    {
        if (Matches(expected, loadedVersion, loadedCommit))
            return;

        throw new CompilerIdentityMismatchException($"Configured Roslyn {expected.CompilerVersion} ({expected.CompilerCommit ?? "any commit"}) does not match loaded {compilerName} {loadedVersion} ({loadedCommit ?? "commit unavailable"}).");
    }
}

public sealed class RoslynWorkerHealthService(ReferenceSetProvider referenceSets, RoslynWorkerIdentity identity, WorkerProcessIdentity processIdentity)
{
    public async Task<HealthResponse> CheckAsync(CancellationToken cancellationToken)
    {
        var checks = new List<HealthCheckResult>(2);

        var compilerTimer = Stopwatch.StartNew();
        var compilerIdentities = new List<(string Language, string Version, string? Commit)>();
        if (identity.SupportsLanguage("csharp"))
        {
            compilerIdentities.Add(("C#", CSharpBuildService.GetLoadedCompilerVersion(), CSharpBuildService.GetLoadedCompilerCommit()));
        }
        if (identity.SupportsLanguage("visual-basic"))
        {
            compilerIdentities.Add(("Visual Basic", VisualBasicBuildService.GetLoadedCompilerVersion(), VisualBasicBuildService.GetLoadedCompilerCommit()));
        }

        var compilerHealthy = compilerIdentities.Count > 0 && compilerIdentities.All(loaded => RoslynCompilerIdentity.Matches(identity, loaded.Version, loaded.Commit));
        var loadedDescription = string.Join(", ", compilerIdentities.Select(static loaded => $"{loaded.Language} {loaded.Version} ({loaded.Commit ?? "commit unavailable"})"));
        checks.Add(new HealthCheckResult("roslyn-compiler-identity", compilerHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, compilerHealthy ? $"Roslyn compiler identity is loaded: {loadedDescription}." : $"Configured Roslyn {identity.CompilerVersion} ({identity.CompilerCommit ?? "any commit"}) does not match loaded compiler identities: {loadedDescription}.", compilerTimer.Elapsed));

        var referencesTimer = Stopwatch.StartNew();
        var referenceHealth = await referenceSets.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(new HealthCheckResult("reference-sets", referenceHealth.IsHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, referenceHealth.Message, referencesTimer.Elapsed));

        var status = checks.All(static check => check.Status == HealthStatus.Healthy)
            ? HealthStatus.Healthy : HealthStatus.Unhealthy;
        return new HealthResponse(status, identity.ToolchainId, processIdentity.InstanceId, ProtocolVersion.WorkerV1, DateTimeOffset.UtcNow, checks);
    }

    public async Task<WorkerDescriptor> DescribeAsync(CancellationToken cancellationToken)
    {
        var health = await CheckAsync(cancellationToken).ConfigureAwait(false);
        var available = health.Status == HealthStatus.Healthy;
        string[] profileIds = [identity.ToolchainId];
        var capabilities = new[]
        {
            "compile-check",
            "managed-pe",
            "portable-pdb",
            "ast",
            "multi-file",
            "lsp",
            "diagnostics",
            "completion",
            "hover",
            "signature-help",
            "semantic-tokens",
            "document-symbols",
            "code-actions",
            "explain"
        };

        var compilerIdentity = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["compilerVersion"] = CSharpBuildService.GetLoadedCompilerVersion()
        };
        if (CSharpBuildService.GetLoadedCompilerCommit() is { Length: > 0 } compilerCommit)
            compilerIdentity["compilerCommit"] = compilerCommit;
        return new WorkerDescriptor(
            new ServiceIdentity(identity.ToolchainId, ServiceKind.ToolchainWorker, identity.ReleaseId, ProtocolVersion.WorkerV1, capabilities, available ? "ready" : "unhealthy"),
            processIdentity.InstanceId,
            WorkerKind.Toolchain,
            identity.WorkerImageId,
            ProtocolVersion.WorkerV1,
            [ProtocolVersion.WorkerV1],
            capabilities.Select(capability => new WorkerCapabilityDescriptor(capability, ContractVersion: 1, Available: available, profileIds, available ? null : "Worker preflight failed.")).ToArray(),
            profileIds,
            processIdentity.StartedAtUtc,
            compilerIdentity,
            referenceSets.Attestations);
    }
}

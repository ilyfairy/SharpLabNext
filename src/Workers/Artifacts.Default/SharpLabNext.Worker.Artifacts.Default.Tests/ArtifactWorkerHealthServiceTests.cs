using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class ArtifactWorkerHealthServiceTests
{
    [Fact]
    public void MissingConfiguredReferenceRootIsUnhealthy()
    {
        var root = CreateRoot();
        try
        {
            var settings = CreateSettings(root, Path.Combine(root, "missing-reference-set"));

            var health = new ArtifactWorkerHealthService(settings).Check();

            Assert.Equal(HealthStatus.Unhealthy, health.Status);
            var check = Assert.Single(health.Checks, static item => item.Name == "reference-set:jsharp20-ref");
            Assert.Equal(HealthStatus.Unhealthy, check.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExistingConfiguredReferenceRootIsHealthy()
    {
        var root = CreateRoot();
        try
        {
            var referenceRoot = Directory.CreateDirectory(Path.Combine(root, "jsharp20-ref")).FullName;
            var settings = CreateSettings(root, referenceRoot);

            var health = new ArtifactWorkerHealthService(settings).Check();

            Assert.Equal(HealthStatus.Healthy, health.Status);
            Assert.Contains(health.Checks, static item => item is { Name: "reference-set:jsharp20-ref", Status: HealthStatus.Healthy });
            foreach (var referenceSetId in NetFxManagedReferenceSets.ById.Keys)
            {
                Assert.Contains(health.Checks, item => item is { Status: HealthStatus.Healthy } && item.Name == $"reference-set:{referenceSetId}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ArtifactWorkerSettings CreateSettings(string root, string referenceRoot)
    {
        var processorPath = Path.Combine(root, "processor.dll");
        File.WriteAllBytes(processorPath, []);
        var referenceSets = ArtifactReferenceSetConfigurationContract.RequiredSystemModules.ToDictionary(static pair => pair.Key, pair => new ArtifactReferenceSet(pair.Key, [pair.Key == ArtifactFormatContract.JSharpReferenceSet ? referenceRoot : root], pair.Value), StringComparer.Ordinal);
        return new ArtifactWorkerSettings(
            new ArtifactWorkerIdentity("release", $"sha256:{new string('1', 64)}", "artifacts-default", "10.1.0.8386", "10.0.9"),
            ArtifactProcessorLimits.Default,
            "http://artifact-store:8080",
            processorPath,
            "dotnet",
            Path.Combine(root, "work"),
            referenceSets,
            new HashSet<string>(["default"], StringComparer.Ordinal));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-artifact-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}

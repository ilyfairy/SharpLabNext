using Microsoft.Extensions.Configuration;
using SharpLabNext.ArtifactWorker.Sdk;

namespace SharpLabNext.Worker.Artifacts.JSIL.Tests;

public sealed class JsilWorkerContractTests
{
    [Fact]
    public void CapabilityManifestExposesOnlyOrdinaryManagedPeToJavaScript()
    {
        var manifest = ArtifactWorkerCapabilityManifestSerializer.Load(
            Path.Combine(AppContext.BaseDirectory, "artifact-worker.json"));

        Assert.Equal("artifacts-jsil", manifest.WorkerId);
        Assert.Equal(["javascript"], manifest.Capabilities);
        Assert.Equal(["dotnet-managed-pe-v1"], manifest.AcceptedArtifactFormats);
        Assert.Equal(["javascript-v1"], manifest.ProducedArtifactFormats);
        Assert.Equal(["javascript"], manifest.RenderOutputIds);
        Assert.Empty(manifest.TransformIds);
        Assert.Equal(2, manifest.Limits.MaximumConcurrentOperations);
    }

    [Fact]
    public void ReferenceSetSelectionRequiresTheExactTargetFramework()
    {
        var manifest = ArtifactWorkerCapabilityManifestSerializer.Load(
            Path.Combine(AppContext.BaseDirectory, "artifact-worker.json"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jsil:ReleaseId"] = "test",
                ["Jsil:WorkerImageId"] = "test-image",
                ["Jsil:Version"] = "0.8.2",
                ["Jsil:Commit"] = "1d57d5427c87ab92ffa3ca4b82429cd7509796ba",
                ["Jsil:WorkRoot"] = Path.GetTempPath(),
                ["Jsil:MonoPath"] = Path.Combine(Path.GetTempPath(), "mono"),
                ["Jsil:CompilerPath"] = Path.Combine(Path.GetTempPath(), "JSILc.AnyCPU.exe"),
                ["ArtifactStore:BaseUrl"] = "http://artifact-store/",
                ["ReferenceSets:net10-ref:TargetFramework"] = "net10.0",
                ["ReferenceSets:net10-ref:Path"] = Path.GetTempPath()
            })
            .Build();
        var settings = JsilWorkerSettings.FromConfiguration(configuration, manifest);

        Assert.Equal("net10-ref", settings.GetReferenceSet("net10-ref", "net10.0").Id);
        Assert.Throws<ArtifactWorkerIncompatibleArtifactException>(() =>
            settings.GetReferenceSet("net10-ref", "net11.0"));
        Assert.Throws<ArtifactWorkerIncompatibleArtifactException>(() =>
            settings.GetReferenceSet("const-generics-ref", "net10.0"));
    }

    [Fact]
    public void TemporaryArtifactPathsCannotEscapeTheWorkRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jsil-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.StartsWith(
                Path.GetFullPath(root),
                JsilTemporaryDirectory.ResolvePath(root, "bin/app.dll"),
                StringComparison.Ordinal);
            Assert.ThrowsAny<ArgumentException>(() =>
                JsilTemporaryDirectory.ResolvePath(root, "../outside.dll"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

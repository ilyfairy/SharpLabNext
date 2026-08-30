using Microsoft.Extensions.Configuration;

namespace SharpLabNext.Worker.CppCli.Tests;

public sealed class CppCliWorkerConfigurationTests
{
    [Fact]
    public void ConfigurationAllowsCompilerIdentityWithoutCommit()
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            var settings = CppCliWorkerSettings.FromConfiguration(Configuration(root));

            Assert.Equal(CppCliTestSettings.CompilerVersion, settings.Identity.CompilerVersion);
            Assert.Null(settings.Identity.CompilerCommit);
            Assert.Equal(Path.Combine(root, "cl"), settings.CompilerPath);
            Assert.Equal(100, settings.ProcessLimits.MaximumDiagnostics);
            Assert.Equal($"sha256:{new string('b', 64)}", settings.ReferenceSet.Digest);
            Assert.Equal($"sha256:{new string('c', 64)}", settings.ReferenceSet.ContentDigest);
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void RelativeCompilerPathIsRejected()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CppCli:CompilerVersion"] = CppCliTestSettings.CompilerVersion,
                ["CppCli:CompilerPath"] = "cl"
            }).Build();

        var exception = Assert.Throws<InvalidOperationException>(() => CppCliWorkerSettings.FromConfiguration(configuration));

        Assert.Contains("absolute path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestAdvertisesOnlyRealInitialCapabilities()
    {
        var manifest = CppCliTestSettings.LoadManifest();

        Assert.Equal(CppCliToolchain.ToolchainId, manifest.WorkerId);
        Assert.Equal(CppCliToolchain.LanguageId, manifest.LanguageId);
        Assert.Equal(["artifact", "compile-check"], manifest.Capabilities);
        Assert.Equal([CppCliToolchain.ArtifactFormat], manifest.ProducedArtifactFormats);
        Assert.Equal([CppCliToolchain.ReferenceSetId], manifest.SupportedReferenceSetIds);
        Assert.Equal(1, manifest.Limits.MaximumConcurrentBuilds);
    }

    private static IConfiguration Configuration(string root) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CppCli:ReleaseId"] = "test-release",
                ["CppCli:CompilerVersion"] = CppCliTestSettings.CompilerVersion,
                ["CppCli:WorkerImageId"] = $"sha256:{new string('a', 64)}",
                ["CppCli:ReferenceSetDigest"] = $"sha256:{new string('b', 64)}",
                ["CppCli:ReferenceSetContentDigest"] = $"sha256:{new string('c', 64)}",
                ["CppCli:ReferenceSetSourceUri"] = $"docker://codex/msvc-wine@sha256:{new string('d', 64)}",
                ["CppCli:CompilerPath"] = Path.Combine(root, "cl"),
                ["CppCli:WorkRoot"] = Path.Combine(root, "work")
            }).Build();
}

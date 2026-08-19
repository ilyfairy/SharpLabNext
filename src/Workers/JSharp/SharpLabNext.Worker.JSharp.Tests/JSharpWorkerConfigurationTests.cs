using Microsoft.Extensions.Configuration;

namespace SharpLabNext.Worker.JSharp.Tests;

public sealed class JSharpWorkerConfigurationTests
{
    [Fact]
    public void ProductionSettingsMatchOperatorImageExportPaths()
    {
        var settingsPath = Path.Combine(
            JSharpTestSettings.RepositoryRoot,
            "src",
            "Workers",
            "JSharp",
            "SharpLabNext.Worker.JSharp",
            "appsettings.json");
        using var stream = File.OpenRead(settingsPath);
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        Assert.Equal("/usr/lib/wine/wine64", configuration["JSharp:CompilerHostPath"]);
        Assert.Equal("/opt/sharplabnext/jsharp20/vjc.exe", configuration["JSharp:CompilerPath"]);
    }

    [Fact]
    public void ConfigurationRequiresOperatorIdentitiesAndAbsoluteCompilerPaths()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var settings = JSharpWorkerSettings.FromConfiguration(Configuration(root));

            Assert.Equal(JSharpTestSettings.CompilerVersion, settings.Identity.CompilerVersion);
            Assert.Null(settings.Identity.CompilerCommit);
            Assert.Equal(JSharpTestSettings.CreateSettings(root).CompilerHostPath, settings.CompilerHostPath);
            Assert.Equal(JSharpTestSettings.CreateSettings(root).CompilerPath, settings.CompilerPath);
            Assert.Equal(100, settings.ProcessLimits.MaximumDiagnostics);
            Assert.Equal(25, settings.ProcessLimits.MemoryPollIntervalMilliseconds);
            Assert.Equal($"sha256:{new string('b', 64)}", settings.ReferenceSet.Digest);
            var attestation = settings.ReferenceSet.CreateAttestation();
            Assert.Equal("operator-image", attestation.Provenance.Kind);
            Assert.Equal(settings.ReferenceSet.Digest, attestation.Provenance.SourceArchiveDigest);
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("JSharp:CompilerHostPath")]
    [InlineData("JSharp:CompilerPath")]
    public void RelativeCompilerPathsAreRejected(string key)
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var values = Values(root);
            values[key] = "relative/path";
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                JSharpWorkerSettings.FromConfiguration(configuration));

            Assert.Contains("absolute host path", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("JSharp:MaximumProcessOutputBytes", "16777217")]
    [InlineData("JSharp:MaximumProcessWorkingSetBytes", "8589934593")]
    [InlineData("JSharp:MaximumDiagnostics", "1001")]
    [InlineData("JSharp:MemoryPollIntervalMilliseconds", "9")]
    public void ProcessLimitsRejectUnboundedValues(string key, string value)
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var values = Values(root);
            values[key] = value;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

            Assert.Throws<InvalidOperationException>(() =>
                JSharpWorkerSettings.FromConfiguration(configuration));
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void ManifestAdvertisesOnlyUnregisteredBuildCapabilities()
    {
        var manifest = JSharpTestSettings.LoadManifest();

        Assert.Equal(JSharpToolchain.ToolchainId, manifest.WorkerId);
        Assert.Equal(JSharpToolchain.LanguageId, manifest.LanguageId);
        Assert.Equal(["artifact", "compile-check"], manifest.Capabilities);
        Assert.Equal([JSharpToolchain.ArtifactFormat], manifest.ProducedArtifactFormats);
        Assert.Equal([JSharpToolchain.ReferenceSetId], manifest.SupportedReferenceSetIds);
        Assert.Equal(1, manifest.Limits.MaximumConcurrentBuilds);
    }

    private static IConfiguration Configuration(string root) =>
        new ConfigurationBuilder().AddInMemoryCollection(Values(root)).Build();

    private static Dictionary<string, string?> Values(string root)
    {
        var settings = JSharpTestSettings.CreateSettings(root);
        return new Dictionary<string, string?>
        {
            ["JSharp:ReleaseId"] = "test-release",
            ["JSharp:CompilerVersion"] = JSharpTestSettings.CompilerVersion,
            ["JSharp:WorkerImageId"] = $"sha256:{new string('a', 64)}",
            ["JSharp:ReferenceSetDigest"] = $"sha256:{new string('b', 64)}",
            ["JSharp:ReferenceSetContentDigest"] = $"sha256:{new string('c', 64)}",
            ["JSharp:ReferenceSetSourceUri"] = "operator://test/jsharp20-ref",
            ["JSharp:CompilerHostPath"] = settings.CompilerHostPath,
            ["JSharp:CompilerPath"] = settings.CompilerPath,
            ["JSharp:WorkRoot"] = Path.Combine(root, "configured-work")
        };
    }
}

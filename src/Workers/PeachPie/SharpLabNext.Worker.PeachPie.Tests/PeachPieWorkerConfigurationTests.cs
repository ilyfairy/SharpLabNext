using Microsoft.Extensions.Configuration;

namespace SharpLabNext.Worker.PeachPie.Tests;

public sealed class PeachPieWorkerConfigurationTests
{
    [Fact]
    public void ConfigurationUsesPinnedCompilerIdentityAndSharedReferenceSetShape()
    {
        var root = PeachPieTestSettings.CreateRoot();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(PeachPieTestSettings.WebHostConfiguration(root)).Build();

            var settings = PeachPieWorkerSettings.FromConfiguration(configuration);

            Assert.Equal(PeachPieToolchain.CompilerVersion, settings.Identity.CompilerVersion);
            Assert.Equal(PeachPieToolchain.CompilerCommit, settings.Identity.CompilerCommit);
            Assert.Equal("net10-ref", Assert.Single(settings.ReferenceSets).Id);
            Assert.True(settings.BuildProcess.Enabled);
            Assert.EndsWith(PeachPieToolchain.MonoUnixNativePackagePath.Replace('/', Path.DirectorySeparatorChar), settings.MonoUnixNativeLibraryPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("1.1.12", "608bf30cf3f43f97e32825076a2cfdaa25043e50")]
    [InlineData("1.1.13", "0000000000000000000000000000000000000000")]
    public void ConfigurationRejectsCompilerIdentityDrift(string version, string commit)
    {
        var root = PeachPieTestSettings.CreateRoot();
        try
        {
            var values = PeachPieTestSettings.WebHostConfiguration(root).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            values["PeachPie:CompilerVersion"] = version;
            values["PeachPie:CompilerCommit"] = commit;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

            Assert.Throws<InvalidOperationException>(() => PeachPieWorkerSettings.FromConfiguration(configuration));
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void ManifestDoesNotClaimUnimplementedLanguageServices()
    {
        var manifest = PeachPieTestSettings.LoadManifest();

        Assert.Equal(PeachPieToolchain.ToolchainId, manifest.WorkerId);
        Assert.Equal([PeachPieToolchain.ToolchainId], manifest.ToolchainIds);
        Assert.Equal(PeachPieToolchain.LanguageId, manifest.LanguageId);
        Assert.Contains("diagnostics", manifest.Capabilities);
        Assert.DoesNotContain("lsp", manifest.Capabilities);
        Assert.DoesNotContain("completion", manifest.Capabilities);
        Assert.DoesNotContain("hover", manifest.Capabilities);
        Assert.DoesNotContain("semantic-tokens", manifest.Capabilities);
        Assert.Equal("--sharplabnext-peachpie-compiler-child", PeachPieCompilerChild.ChildArgument);
    }

    [Fact]
    public void DependencyGraphExcludesUnlicensedCompilerDiagnosticsPackage()
    {
        var projectDirectory = Path.Combine(PeachPieTestSettings.RepositoryRoot, "src", "Workers", "PeachPie", "SharpLabNext.Worker.PeachPie");
        var project = File.ReadAllText(Path.Combine(projectDirectory, "SharpLabNext.Worker.PeachPie.csproj"));
        var lockFile = File.ReadAllText(Path.Combine(projectDirectory, "packages.lock.json"));

        Assert.Contains("<PackageDownload Include=\"Peachpie.CodeAnalysis\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Peachpie.Compiler.Diagnostics", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Peachpie.Compiler.Diagnostics", lockFile, StringComparison.OrdinalIgnoreCase);
    }
}

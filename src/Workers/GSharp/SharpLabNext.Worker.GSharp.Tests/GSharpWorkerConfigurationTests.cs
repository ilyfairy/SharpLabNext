using Microsoft.Extensions.Configuration;

namespace SharpLabNext.Worker.GSharp.Tests;

public sealed class GSharpWorkerConfigurationTests
{
    [Fact]
    public void ConfigurationLoadsIndependentToolchainProfiles()
    {
        var values = new Dictionary<string, string?>
        {
            [$"GSharp:Toolchains:{GSharpToolchain.ToolchainId}:CompilerVersion"] = "0.3.33",
            [$"GSharp:Toolchains:{GSharpToolchain.ToolchainId}:CompilerCommit"] = new('a', 40),
            [$"GSharp:Toolchains:{GSharpToolchain.ToolchainId}:CompilerAssemblyPath"] = "stable/gsc.dll",
            [$"GSharp:Toolchains:{GSharpToolchain.ToolchainId}:LanguageServerAssemblyPath"] = "stable/GSharp.LanguageServer.dll",
            [$"GSharp:Toolchains:{GSharpToolchain.LegacyToolchainId}:CompilerVersion"] = "0.3.8",
            [$"GSharp:Toolchains:{GSharpToolchain.LegacyToolchainId}:CompilerCommit"] = new('b', 40),
            [$"GSharp:Toolchains:{GSharpToolchain.LegacyToolchainId}:CompilerAssemblyPath"] = "legacy/gsc.dll",
            [$"GSharp:Toolchains:{GSharpToolchain.LegacyToolchainId}:LanguageServerAssemblyPath"] = "legacy/GSharp.LanguageServer.dll",
            ["ReferenceSets:net10-ref:Path"] = ".",
            ["ReferenceSets:net10-ref:TargetFramework"] = "net10.0",
            ["ReferenceSets:net10-ref:FrameworkVersion"] = "10.0.9"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var settings = GSharpWorkerSettings.FromConfiguration(configuration);

        Assert.Equal(2, settings.Toolchains.Count);
        Assert.Equal("0.3.33", settings.GetToolchain(GSharpToolchain.ToolchainId).CompilerVersion);
        Assert.Equal("0.3.8", settings.GetToolchain(GSharpToolchain.LegacyToolchainId).CompilerVersion);
        Assert.NotEqual(settings.GetToolchain(GSharpToolchain.ToolchainId).CompilerAssemblyPath, settings.GetToolchain(GSharpToolchain.LegacyToolchainId).CompilerAssemblyPath);
    }

    [Theory]
    [InlineData("0.3.8", "0.3")]
    [InlineData("0.4.0-preview.1", "0.4")]
    public void FeatureVersionUsesSemanticVersionMajorAndMinor(string compilerVersion, string expected)
    {
        Assert.Equal(expected, GSharpCompilerIdentity.GetFeatureVersion(compilerVersion));
    }

    [Theory]
    [InlineData("0.3")]
    [InlineData("v0.3.8")]
    [InlineData("0.03.8")]
    public void InvalidCompilerVersionFailsDuringConfiguration(string compilerVersion)
    {
        var configuration = CreateConfiguration(compilerVersion, GSharpTestSettings.CompilerCommit);

        var exception = Assert.Throws<InvalidOperationException>(() => GSharpWorkerSettings.FromConfiguration(configuration));

        Assert.Contains($"GSharp:Toolchains:{GSharpToolchain.ToolchainId}:CompilerVersion", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("723cbdae")]
    [InlineData("723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf0z")]
    public void InvalidCompilerCommitFailsDuringConfiguration(string compilerCommit)
    {
        var configuration = CreateConfiguration(GSharpTestSettings.CompilerVersion, compilerCommit);

        var exception = Assert.Throws<InvalidOperationException>(() => GSharpWorkerSettings.FromConfiguration(configuration));

        Assert.Contains($"GSharp:Toolchains:{GSharpToolchain.ToolchainId}:CompilerCommit", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration CreateConfiguration(string compilerVersion, string compilerCommit) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"GSharp:Toolchains:{GSharpToolchain.ToolchainId}:CompilerVersion"] = compilerVersion,
                [$"GSharp:Toolchains:{GSharpToolchain.ToolchainId}:CompilerCommit"] = compilerCommit,
                [$"GSharp:Toolchains:{GSharpToolchain.ToolchainId}:CompilerAssemblyPath"] = "gsc.dll",
                [$"GSharp:Toolchains:{GSharpToolchain.ToolchainId}:LanguageServerAssemblyPath"] = "GSharp.LanguageServer.dll",
                ["ReferenceSets:net10-ref:Path"] = ".",
                ["ReferenceSets:net10-ref:TargetFramework"] = "net10.0",
                ["ReferenceSets:net10-ref:FrameworkVersion"] = "10.0.9"
            })
.Build();
}

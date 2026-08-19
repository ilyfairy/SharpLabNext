using Microsoft.Extensions.Configuration;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class ArtifactWorkerSettingsTests
{
    [Fact]
    public void ProductionSettingsIncludeNetFxReferenceClosure()
    {
        var settingsPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Workers",
            "Artifacts.Default",
            "SharpLabNext.Worker.Artifacts.Default",
            "appsettings.json");
        using var stream = File.OpenRead(settingsPath);
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        var settings = ArtifactWorkerSettings.FromConfiguration(configuration);
        Assert.Equal(
            ArtifactReferenceSetConfigurationContract.RequiredSystemModules.Keys.Order(StringComparer.Ordinal),
            settings.ReferenceSets.Keys.Order(StringComparer.Ordinal));
        foreach (var contract in NetFxManagedReferenceSets.ById.Values)
        {
            var referenceSet = settings.ReferenceSets[contract.ReferenceSetId];
            Assert.Equal("mscorlib", referenceSet.SystemModuleName);
            Assert.Equal(
                [Path.GetFullPath($"/reference-sets/{contract.ReferenceSetId}")],
                referenceSet.Paths);
        }

        Assert.Equal(
            "/reference-sets/jsharp20-ref",
            configuration["ArtifactWorker:ReferenceSets:jsharp20-ref:Paths:0"]);
        var jsharpReferenceSet = settings.ReferenceSets["jsharp20-ref"];
        Assert.Equal("mscorlib", jsharpReferenceSet.SystemModuleName);
        Assert.Equal(
            [Path.GetFullPath("/reference-sets/jsharp20-ref")],
            jsharpReferenceSet.Paths);
    }

    [Fact]
    public void MissingOrUnknownReferenceSetIsRejected()
    {
        var values = ArtifactReferenceSetConfigurationContract.RequiredSystemModules
            .SelectMany(pair => new Dictionary<string, string?>
            {
                [$"ArtifactWorker:ReferenceSets:{pair.Key}:Paths:0"] = $"/reference-sets/{pair.Key}",
                [$"ArtifactWorker:ReferenceSets:{pair.Key}:SystemModuleName"] = pair.Value
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        values.Remove("ArtifactWorker:ReferenceSets:netfx30-managed-ref:Paths:0");
        values.Remove("ArtifactWorker:ReferenceSets:netfx30-managed-ref:SystemModuleName");
        values["ArtifactWorker:ReferenceSets:unknown-ref:Paths:0"] = "/reference-sets/unknown-ref";
        values["ArtifactWorker:ReferenceSets:unknown-ref:SystemModuleName"] = "mscorlib";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ArtifactWorkerSettings.FromConfiguration(configuration));

        Assert.Contains("netfx30-managed-ref", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unknown-ref", exception.Message, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the SharpLabNext repository root.");
    }
}

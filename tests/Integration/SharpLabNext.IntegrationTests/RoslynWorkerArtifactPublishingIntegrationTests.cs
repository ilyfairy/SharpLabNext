using System.Net;
using System.Net.Http.Json;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.IntegrationTests;

[Collection<ArtifactStoreProcessTestGroup>]
public sealed class RoslynWorkerArtifactPublishingIntegrationTests
{
    [Fact]
    public async Task ProductionBuildPublishesDirectlyAndReturnsOnlyArtifactRef()
    {
        const string internalServiceToken = "shared-internal-service-token-for-process-tests";
        await using var artifactStore = await ArtifactStoreProcess.StartAsync(TestContext.Current.CancellationToken, internalServiceToken: internalServiceToken);
        using var referenceSets = await AttestedReferenceSetTestData.CreateAsync(TestContext.Current.CancellationToken);
        var workerEnvironment = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ArtifactStore__BaseUrl"] = artifactStore.HttpClient.BaseAddress!.AbsoluteUri,
            ["RoslynWorker__DevelopmentArtifactEnvelope__Enabled"] = "false"
        };
        referenceSets.AddToEnvironment(workerEnvironment, "net10-ref", "net11-preview-ref");
        await using var worker = await DotNetWebServiceProcess.StartAsync("src/Workers/Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable.csproj", "/health/ready", workerEnvironment, TestContext.Current.CancellationToken, internalServiceToken: internalServiceToken);
        var request = CreateRequest();

        using var response = await worker.HttpClient.PostAsJsonAsync("/api/v1/build", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        var responseText = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, responseText);
        Assert.DoesNotContain("peImageBase64", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("portablePdbBase64", responseText, StringComparison.Ordinal);
        var build = System.Text.Json.JsonSerializer.Deserialize<ToolchainBuildResponse>(responseText, ContractJson.CreateSerializerOptions());
        Assert.NotNull(build);
        Assert.Null(build.DevelopmentArtifact);
        var result = Assert.IsType<BuildResult>(build.Result);
        var artifactRef = Assert.IsType<ArtifactRef>(result.ArtifactRef);

        var descriptor = await artifactStore.Client.GetArtifactAsync(artifactRef, TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        Assert.Equal(artifactRef, descriptor.Manifest.ArtifactId);
        Assert.Equal("roslyn-stable", descriptor.Manifest.Producer.ToolchainId);
        Assert.Equal(2, descriptor.Entries.Count);
        var primaryAssembly = Assert.Single(descriptor.Entries, static entry => entry.Role == "primary-assembly");
        await using var content = await artifactStore.Client.OpenContentReadAsync(primaryAssembly.ContentRef, TestContext.Current.CancellationToken);
        var header = new byte[2];
        await content.Content.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
        Assert.Equal([0x4d, 0x5a], header);
    }

    private static BuildRequest CreateRequest()
    {
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, BuildOutputKind.Console, AllowUnsafe: false, EmitPortablePdb: true, NullableContextMode.Enable, LanguageVersion: "14.0");
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, Revision: 81, SelectionRevision: 12, LanguageId: "csharp", Files: [new WorkspaceFile("Program.cs", 1, "System.Console.WriteLine(42);")], ActiveFile: "Program.cs", SourceOrder: ["Program.cs"], ReferenceSetId: "net10-ref", BuildOptions: options);
        return new BuildRequest("worker-direct-publish", "worker-direct-publish-key", "worker-direct-publish-pipeline", "roslyn-stable", "net10-ref", workspace, DateTimeOffset.UtcNow.AddSeconds(30), options, BuildTarget.Artifact);
    }

}

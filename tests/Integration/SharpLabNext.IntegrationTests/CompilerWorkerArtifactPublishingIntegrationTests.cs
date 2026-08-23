using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.IntegrationTests;

[Collection<ArtifactStoreProcessTestGroup>]
public sealed class CompilerWorkerArtifactPublishingIntegrationTests
{
    private const string InternalServiceToken = "shared-internal-service-token-for-process-tests";

    [Fact]
    public async Task FSharpWorkerPublishesEntrySymbolsAndFSharpCoreDirectly()
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "SharpLabNext-FSharpDirect", Guid.NewGuid().ToString("N"));
        await using var artifactStore = await ArtifactStoreProcess.StartAsync(
            TestContext.Current.CancellationToken,
            internalServiceToken: InternalServiceToken);
        using var referenceSets = await AttestedReferenceSetTestData.CreateAsync(
            TestContext.Current.CancellationToken);
        try
        {
            var workerEnvironment = new Dictionary<string, string?>
            {
                ["FSharpWorker__WorkRoot"] = workRoot,
                ["FSharpWorker__DevelopmentArtifactEnvelope__Enabled"] = "false",
                ["ArtifactStore__BaseUrl"] = artifactStore.HttpClient.BaseAddress!.AbsoluteUri
            };
            referenceSets.AddToEnvironment(workerEnvironment, "net10-ref", "net11-preview-ref");
            await using var worker = await StartWorkerAsync(
                "src/Workers/FSharp/SharpLabNext.Worker.FSharp/SharpLabNext.Worker.FSharp.csproj",
                workerEnvironment);
            var request = CreateRequest(
                "fsharp",
                "fsharp-stable",
                "Program.fs",
                "module Program\nopen System\n[<EntryPoint>]\nlet main _ = Console.WriteLine(42); 0\n",
                NullableContextMode.Disable,
                "9.0");

            var build = await BuildAsync(worker.HttpClient, request);

            var result = Assert.IsType<BuildResult>(build.Result);
            Assert.Null(build.DevelopmentArtifact);
            var artifactRef = Assert.IsType<ArtifactRef>(result.ArtifactRef);
            var descriptor = await artifactStore.Client.GetArtifactAsync(
                artifactRef,
                TestContext.Current.CancellationToken);
            Assert.NotNull(descriptor);
            Assert.Equal(
                ["portable-pdb", "primary-assembly", "support-assembly"],
                descriptor.Entries.Select(static entry => entry.Role).Order(StringComparer.Ordinal));
            Assert.Contains(
                descriptor.Entries,
                static entry => entry.Path == "FSharp.Core.dll" && entry.Role == "support-assembly");
        }
        finally
        {
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GatewayPublisherAcceptsPeachPieManagedPeAndSupportClosure()
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "SharpLabNext-PeachPieDirect", Guid.NewGuid().ToString("N"));
        await using var artifactStore = await ArtifactStoreProcess.StartAsync(
            TestContext.Current.CancellationToken,
            internalServiceToken: InternalServiceToken);
        using var referenceSets = await AttestedReferenceSetTestData.CreateAsync(
            TestContext.Current.CancellationToken);
        try
        {
            var workerEnvironment = new Dictionary<string, string?>
            {
                ["PeachPie__WorkRoot"] = workRoot,
                ["ArtifactStore__BaseUrl"] = artifactStore.HttpClient.BaseAddress!.AbsoluteUri
            };
            referenceSets.AddToEnvironment(workerEnvironment, "net10-ref");
            await using var worker = await StartWorkerAsync(
                "src/Workers/PeachPie/SharpLabNext.Worker.PeachPie/SharpLabNext.Worker.PeachPie.csproj",
                workerEnvironment);
            var request = CreateRequest(
                "php",
                "peachpie-stable",
                "index.php",
                "<?php function square($value) { return $value * $value; } echo square(7);",
                NullableContextMode.Disable,
                "8.5");

            var build = await BuildAsync(worker.HttpClient, request, expectDevelopmentArtifact: true);

            var result = Assert.IsType<BuildResult>(build.Result);
            var envelope = Assert.IsType<WorkerArtifactEnvelope>(build.DevelopmentArtifact);
            var artifactRef = Assert.IsType<ArtifactRef>(result.ArtifactRef);
            var published = await new BuildArtifactPublisher(
                    artifactStore.Client,
                    new BuildPipelineOptions())
                .PublishAsync(envelope, TestContext.Current.CancellationToken);
            Assert.Equal(artifactRef, published.ArtifactRef);
            var descriptor = await artifactStore.Client.GetArtifactAsync(
                artifactRef,
                TestContext.Current.CancellationToken);
            Assert.NotNull(descriptor);
            Assert.DoesNotContain(descriptor.Entries, static entry => entry.Role == "portable-pdb");
            Assert.Contains(
                descriptor.Entries,
                static entry => entry.Path == "Peachpie.Runtime.dll" && entry.Role == "support-assembly");
            Assert.Contains(
                descriptor.Entries,
                static entry => entry.Path == "Peachpie.Library.dll" && entry.Role == "support-assembly");
            var primary = Assert.Single(descriptor.Entries, static entry => entry.Role == "primary-assembly");
            await using var content = await artifactStore.Client.OpenContentReadAsync(
                primary.ContentRef,
                TestContext.Current.CancellationToken);
            var header = new byte[2];
            await content.Content.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
            Assert.Equal([0x4d, 0x5a], header);
        }
        finally
        {
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, recursive: true);
        }
    }

    [Fact]
    public async Task IlWorkerPublishesManagedPeDirectly()
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "SharpLabNext-IlDirect", Guid.NewGuid().ToString("N"));
        await using var artifactStore = await ArtifactStoreProcess.StartAsync(
            TestContext.Current.CancellationToken,
            internalServiceToken: InternalServiceToken);
        using var referenceSets = await AttestedReferenceSetTestData.CreateAsync(
            TestContext.Current.CancellationToken);
        try
        {
            var workerEnvironment = new Dictionary<string, string?>
            {
                ["IlWorker__WorkRoot"] = workRoot,
                ["IlWorker__DevelopmentArtifactEnvelope__Enabled"] = "false",
                ["ArtifactStore__BaseUrl"] = artifactStore.HttpClient.BaseAddress!.AbsoluteUri
            };
            referenceSets.AddToEnvironment(workerEnvironment, "net10-ref", "net11-preview-ref");
            await using var worker = await StartWorkerAsync(
                "src/Workers/IL/SharpLabNext.Worker.IL/SharpLabNext.Worker.IL.csproj",
                workerEnvironment);
            var request = CreateRequest(
                "il",
                "mobius-ilasm-stable",
                "Program.il",
                """
                .assembly DirectPublish {}
                .module DirectPublish.dll
                .class public auto ansi Program extends [System.Runtime]System.Object
                {
                  .method public hidebysig static void Main() cil managed
                  {
                    .entrypoint
                    .maxstack 1
                    ldc.i4.s 42
                    call void [System.Console]System.Console::WriteLine(int32)
                    ret
                  }
                }
                """,
                NullableContextMode.Disable,
                "ecma-335");

            var build = await BuildAsync(worker.HttpClient, request);

            var result = Assert.IsType<BuildResult>(build.Result);
            Assert.Null(build.DevelopmentArtifact);
            var artifactRef = Assert.IsType<ArtifactRef>(result.ArtifactRef);
            var descriptor = await artifactStore.Client.GetArtifactAsync(
                artifactRef,
                TestContext.Current.CancellationToken);
            Assert.NotNull(descriptor);
            var entry = Assert.Single(descriptor.Entries);
            Assert.Equal("primary-assembly", entry.Role);
            await using var content = await artifactStore.Client.OpenContentReadAsync(
                entry.ContentRef,
                TestContext.Current.CancellationToken);
            var header = new byte[2];
            await content.Content.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
            Assert.Equal([0x4d, 0x5a], header);
        }
        finally
        {
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, recursive: true);
        }
    }

    private static Task<DotNetWebServiceProcess> StartWorkerAsync(
        string project,
        IReadOnlyDictionary<string, string?> environment)
    {
        var allEnvironment = new Dictionary<string, string?>(environment, StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production"
        };
        return DotNetWebServiceProcess.StartAsync(
            project,
            "/health/ready",
            allEnvironment,
            TestContext.Current.CancellationToken,
            noBuild: false,
            internalServiceToken: InternalServiceToken);
    }

    private static async Task<ToolchainBuildResponse> BuildAsync(
        HttpClient client,
        BuildRequest request,
        bool expectDevelopmentArtifact = false)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/build",
            request,
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        if (!expectDevelopmentArtifact)
        {
            Assert.DoesNotContain("fileContentsBase64", body, StringComparison.Ordinal);
            Assert.DoesNotContain("peImageBase64", body, StringComparison.Ordinal);
        }
        return JsonSerializer.Deserialize<ToolchainBuildResponse>(
            body,
            ContractJson.CreateSerializerOptions())
            ?? throw new InvalidOperationException("Worker build response was empty.");
    }

    private static BuildRequest CreateRequest(
        string languageId,
        string toolchainId,
        string fileName,
        string source,
        NullableContextMode nullableContext,
        string languageVersion)
    {
        var options = new BuildOptions(
            BuildConfiguration.Release,
            Optimize: true,
            BuildOutputKind.Console,
            AllowUnsafe: false,
            EmitPortablePdb: true,
            nullableContext,
            languageVersion);
        var workspace = new WorkspaceSnapshot(
            ContractSchemaVersions.WorkspaceSnapshot,
            Revision: 94,
            SelectionRevision: 13,
            languageId,
            [new WorkspaceFile(fileName, 1, source)],
            fileName,
            [fileName],
            "net10-ref",
            options);
        return new BuildRequest(
            $"direct-{languageId}",
            $"direct-{languageId}-key",
            $"direct-{languageId}-pipeline",
            toolchainId,
            "net10-ref",
            workspace,
            DateTimeOffset.UtcNow.AddMinutes(1),
            options,
            BuildTarget.Artifact);
    }

}

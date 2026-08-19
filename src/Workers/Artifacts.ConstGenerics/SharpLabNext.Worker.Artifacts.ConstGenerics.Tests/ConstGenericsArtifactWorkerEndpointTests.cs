using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics.Tests;

public sealed class ConstGenericsArtifactWorkerEndpointTests
{
    [Fact]
    public async Task CapabilitiesExposeOnlyTheApprovedConstGenericsOperations()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Assert.Same(
            SharpLabNextTelemetry.Metrics,
            factory.Services.GetRequiredService<SharpLabNextMetrics>());

        var manifest = await client.GetFromJsonAsync<ArtifactWorkerCapabilityManifest>(
            "/api/v1/worker/capabilities",
            TestContext.Current.CancellationToken);

        Assert.NotNull(manifest);
        Assert.Equal("artifacts-const-generics", manifest.WorkerId);
        Assert.Equal(["il", "decompiled-csharp", "il-verify"], manifest.Capabilities);
        Assert.Equal(["il", "decompiled-csharp"], manifest.RenderOutputIds);
        Assert.Equal(["il-verify"], manifest.VerificationProfileIds);
        Assert.Empty(manifest.TransformIds);
    }

    [Fact]
    public async Task WrongProcessorIdentityIsRejectedSynchronously()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var request = ConstGenericsTestInfrastructure.RenderRequest(
            new ArtifactRef($"sha256:{new string('c', 64)}"),
            "il") with
        {
            ProcessorId = "artifacts-default"
        };

        using var response = await client.PostAsJsonAsync(
            "/api/v1/artifact-renders",
            request,
            ConstGenericsTestInfrastructure.JsonOptions,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(
            ConstGenericsTestInfrastructure.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Contains("wrong-processor", problem["Title"].ToString());
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?
            .Name ?? "Debug";
        var buildOutput = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "SharpLabNext.Worker.Artifacts.ConstGenerics.Processor",
            "bin",
            configuration,
            "net8.0",
            "SharpLabNext.Worker.Artifacts.ConstGenerics.Processor.dll"));
        var processorPath = File.Exists(buildOutput)
            ? buildOutput
            : throw new FileNotFoundException("The ConstGenerics processor test build output is unavailable.", buildOutput);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConstGenericsArtifactWorker:ReleaseId"] = "test-release",
                    ["ConstGenericsArtifactWorker:WorkerImageId"] = $"sha256:{new string('a', 64)}",
                    ["ConstGenericsArtifactWorker:ProcessorDotNetHostPath"] =
                        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                    ["ConstGenericsArtifactWorker:ProcessorAssemblyPath"] = processorPath,
                    ["ConstGenericsArtifactWorker:ReferenceRoot"] =
                        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                    ["ConstGenericsArtifactWorker:RuntimeReferenceRoot"] =
                        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                    ["ConstGenericsArtifactWorker:SystemModuleName"] = "System.Private.CoreLib",
                    ["ArtifactStore:BaseUrl"] = "http://artifact-store.test"
                }));
        });
    }
}

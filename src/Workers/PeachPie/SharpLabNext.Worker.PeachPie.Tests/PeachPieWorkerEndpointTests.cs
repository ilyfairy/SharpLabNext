using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.PeachPie.Tests;

[Collection(PeachPieTestGroup.Name)]
public sealed class PeachPieWorkerEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    [Fact]
    public async Task DescribeBuildAndCapabilityEndpointsExposeThePinnedIsolatedWorker()
    {
        var root = PeachPieTestSettings.CreateRoot();
        try
        {
            await using var factory = new PeachPieWebApplicationFactory(root);
            using var client = factory.CreateClient();

            var descriptor = await client.GetFromJsonAsync<WorkerDescriptor>("/api/v1/worker/describe", JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(descriptor);
            Assert.Equal(PeachPieToolchain.ToolchainId, descriptor.Service.Id);
            Assert.NotNull(descriptor.Identity);
            Assert.Equal(PeachPieToolchain.CompilerVersion, descriptor.Identity["compilerVersion"]);
            Assert.Equal(PeachPieToolchain.CompilerCommit, descriptor.Identity["compilerCommit"]);

            var capabilities = await client.GetFromJsonAsync<LanguageWorkerCapabilityManifest>("/api/v1/worker/capabilities", JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(capabilities);
            Assert.Contains("artifact", capabilities.Capabilities);
            Assert.DoesNotContain("lsp", capabilities.Capabilities);

            using var invalidResponse = await client.PostAsJsonAsync("/api/v1/build", PeachPieTestSettings.CreateRequest(BuildTarget.CompileCheck, "<?php\nfunction broken( {\n"), JsonOptions, TestContext.Current.CancellationToken);
            var invalidBody = await invalidResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(invalidResponse.IsSuccessStatusCode, invalidBody);
            var invalid = JsonSerializer.Deserialize<LanguageWorkerBuildHttpResponse>(invalidBody, JsonOptions);
            Assert.NotNull(invalid);
            var check = Assert.IsType<CompilationCheckResult>(invalid.Result);
            Assert.False(check.CompilationSucceeded);
            Assert.All(check.Diagnostics, static diagnostic => Assert.Equal("Program.php", diagnostic.FilePath));

            using var artifactResponse = await client.PostAsJsonAsync("/api/v1/build", PeachPieTestSettings.CreateRequest(BuildTarget.Artifact, "<?php echo 'endpoint';"), JsonOptions, TestContext.Current.CancellationToken);
            var artifactBody = await artifactResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(artifactResponse.IsSuccessStatusCode, artifactBody);
            var artifact = JsonSerializer.Deserialize<LanguageWorkerBuildHttpResponse>(artifactBody, JsonOptions);
            Assert.NotNull(artifact);
            Assert.Equal(BuildOutcome.Succeeded, Assert.IsType<BuildResult>(artifact.Result).Outcome);
            var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(artifact.DevelopmentArtifact);
            Assert.NotNull(envelope.FileContentsBase64);
            Assert.Contains(PeachPieToolchain.RuntimeAssemblyName, envelope.FileContentsBase64.Keys);
            Assert.Contains(PeachPieToolchain.LibraryAssemblyName, envelope.FileContentsBase64.Keys);

            using var sessions = await client.PostAsJsonAsync(
                "/api/v1/language-sessions",
                new { },
                JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, sessions.StatusCode);
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }
}

internal sealed class PeachPieWebApplicationFactory : WebApplicationFactory<global::Program>
{
    private readonly string _root;
    private readonly PeachPieProcessEnvironment _environment;

    public PeachPieWebApplicationFactory(string root)
    {
        _root = root;
        _environment = PeachPieProcessEnvironment.Apply(root);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            var settings = PeachPieTestSettings.CreateSettings(_root, isolated: true);
            services.RemoveAll<ICompilerProcessRunner>();
            services.AddSingleton<ICompilerProcessRunner>(PeachPieTestSettings.CreateCompilerProcessRunner(settings));
        });
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            _environment.Dispose();
        }
    }
}

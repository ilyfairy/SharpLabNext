using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.CppCli.Tests;

public sealed class CppCliWorkerEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    [Fact]
    public async Task EndpointExposesBuildOnlyWorkerAndMixedModeArtifact()
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            await using var factory = new CppCliWebApplicationFactory(root);
            using var client = factory.CreateClient();
            var manifest = await client.GetFromJsonAsync<LanguageWorkerCapabilityManifest>("/api/v1/worker/capabilities", JsonOptions, TestContext.Current.CancellationToken);

            Assert.NotNull(manifest);
            Assert.Equal(["artifact", "compile-check"], manifest.Capabilities);
            var descriptor = await client.GetFromJsonAsync<WorkerDescriptor>("/api/v1/worker/describe", JsonOptions, TestContext.Current.CancellationToken);
            var referenceSet = Assert.Single(descriptor!.ReferenceSets!);
            Assert.Equal(CppCliToolchain.ReferenceSetId, referenceSet.Id);
            Assert.Equal(CppCliToolchain.TargetFramework, referenceSet.TargetFramework);
            Assert.Equal($"sha256:{new string('b', 64)}", referenceSet.Digest);
            Assert.Equal("operator-image", referenceSet.Provenance.Kind);
            using var response = await client.PostAsJsonAsync("/api/v1/build", CppCliTestSettings.CreateRequest(BuildTarget.Artifact), JsonOptions, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, body);
            var build = JsonSerializer.Deserialize<LanguageWorkerBuildHttpResponse>(body, JsonOptions);
            Assert.NotNull(build);
            Assert.Equal(BuildOutcome.Succeeded, Assert.IsType<BuildResult>(build.Result).Outcome);
            Assert.Equal(CppCliToolchain.ArtifactFormat, build.DevelopmentArtifact!.ArtifactFormat);
            Assert.Equal(CppCliToolchain.RuntimeFamily, build.DevelopmentArtifact.Manifest.RuntimeRequirement.Family);

            using var noLsp = await client.PostAsJsonAsync(
                "/api/v1/language-sessions",
                new { },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, noLsp.StatusCode);
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }
}

internal sealed class CppCliWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _previousEnvironment;

    public CppCliWebApplicationFactory(string root)
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CppCli:ReleaseId"] = "development",
            ["CppCli:CompilerVersion"] = CppCliTestSettings.CompilerVersion,
            ["CppCli:WorkerImageId"] = $"sha256:{new string('0', 64)}",
            ["CppCli:ReferenceSetDigest"] = $"sha256:{new string('b', 64)}",
            ["CppCli:ReferenceSetContentDigest"] = $"sha256:{new string('c', 64)}",
            ["CppCli:ReferenceSetSourceUri"] = $"docker://codex/msvc-wine@sha256:{new string('d', 64)}",
            ["CppCli:CompilerPath"] = Path.Combine(root, "cl"),
            ["CppCli:WorkRoot"] = Path.Combine(root, "web-work")
        };
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in configuration)
        {
            var environmentKey = key.Replace(":", "__", StringComparison.Ordinal);
            previous[environmentKey] = Environment.GetEnvironmentVariable(environmentKey);
            Environment.SetEnvironmentVariable(environmentKey, value);
        }
        _previousEnvironment = previous;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILanguageWorkerBuildService>();
            services.AddSingleton<ILanguageWorkerBuildService>(provider => new CppCliBuildService(new FakeCppCliCompilerProcess(new CppCliCompilerInvocation(true, CppCliTestSettings.CreateMixedModePe(), [])), provider.GetRequiredService<CppCliWorkerSettings>(), provider.GetRequiredService<LanguageWorkerCapabilityManifest>()));
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
            foreach (var (key, value) in _previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}

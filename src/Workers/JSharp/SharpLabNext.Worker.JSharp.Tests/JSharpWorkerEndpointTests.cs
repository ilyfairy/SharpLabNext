using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.JSharp.Tests;

public sealed class JSharpWorkerEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    [Fact]
    public async Task EndpointExposesBuildOnlyWorkerAndX64Clr2Artifact()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            await using var factory = new JSharpWebApplicationFactory(root);
            using var client = factory.CreateClient();
            var manifest = await client.GetFromJsonAsync<LanguageWorkerCapabilityManifest>("/api/v1/worker/capabilities", JsonOptions, TestContext.Current.CancellationToken);

            Assert.NotNull(manifest);
            Assert.Equal(["artifact", "compile-check"], manifest.Capabilities);
            var descriptor = await client.GetFromJsonAsync<WorkerDescriptor>("/api/v1/worker/describe", JsonOptions, TestContext.Current.CancellationToken);
            var referenceSet = Assert.Single(descriptor!.ReferenceSets!);
            Assert.Equal(JSharpToolchain.ReferenceSetId, referenceSet.Id);
            Assert.Equal(JSharpToolchain.TargetFramework, referenceSet.TargetFramework);
            Assert.Equal("operator-image", referenceSet.Provenance.Kind);

            using var response = await client.PostAsJsonAsync("/api/v1/build", JSharpTestSettings.CreateRequest(BuildTarget.Artifact), JsonOptions, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, body);
            var build = JsonSerializer.Deserialize<LanguageWorkerBuildHttpResponse>(body, JsonOptions);
            Assert.NotNull(build);
            var artifact = build.DevelopmentArtifact!;
            Assert.Equal(JSharpToolchain.ArtifactFormat, artifact.ArtifactFormat);
            Assert.Equal("x64", artifact.Manifest.RuntimeRequirement.Architecture);
            Assert.Equal([JSharpToolchain.RuntimeFeatureTag], artifact.Manifest.RuntimeRequirement.RequiredRuntimeFeatureTags);

            using var noLsp = await client.PostAsJsonAsync(
                "/api/v1/language-sessions",
                new { },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, noLsp.StatusCode);
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }
}

internal sealed class JSharpWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _previousEnvironment;

    public JSharpWebApplicationFactory(string root)
    {
        var settings = JSharpTestSettings.CreateSettings(root);
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["JSharp:ReleaseId"] = "development",
            ["JSharp:CompilerVersion"] = JSharpTestSettings.CompilerVersion,
            ["JSharp:WorkerImageId"] = $"sha256:{new string('0', 64)}",
            ["JSharp:ReferenceSetDigest"] = $"sha256:{new string('b', 64)}",
            ["JSharp:ReferenceSetContentDigest"] = $"sha256:{new string('c', 64)}",
            ["JSharp:ReferenceSetSourceUri"] = "operator://test/jsharp20-ref",
            ["JSharp:CompilerHostPath"] = settings.CompilerHostPath,
            ["JSharp:CompilerPath"] = settings.CompilerPath,
            ["JSharp:WorkRoot"] = Path.Combine(root, "web-work")
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
            services.AddSingleton<ILanguageWorkerBuildService>(provider => new JSharpBuildService(new FakeJSharpCompilerProcess(new JSharpCompilerInvocation(true, JSharpTestSettings.CreateClr2ManagedPe(), [])), provider.GetRequiredService<JSharpWorkerSettings>(), provider.GetRequiredService<LanguageWorkerCapabilityManifest>()));
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

extern alias ArtifactAssemblerHost;
extern alias ArtifactStoreHost;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;
using SharpLabNext.Worker.IL.Compiler;

namespace SharpLabNext.Worker.Artifacts.ILAssembler.Tests;

public sealed class IlAssemblerArtifactWorkerTests
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        ContractJson.CreateSerializerOptions();

    [Fact]
    public async Task RealArtifactStoreTransformAndRenderUseAsyncEndpointsAndIsolatedCompiler()
    {
        var root = CreateRoot();
        var storeRoot = Path.Combine(root, "store");
        var workRoot = Path.Combine(root, "work");
        try
        {
            await using var storeFactory = CreateStoreFactory(storeRoot);
            using var storeHttp = storeFactory.CreateClient();
            var store = new ArtifactStoreClient(storeHttp);
            var source = Encoding.UTF8.GetBytes(ValidCil);
            var sourceManifest = CreateSourceManifest(source);
            await using (var upload = new MemoryStream(source, writable: false))
            {
                var stored = await store.PutArtifactAsync(
                    sourceManifest,
                    [new ArtifactFileUpload("Program.il", upload, source.LongLength)],
                    TimeSpan.FromHours(1),
                    TestContext.Current.CancellationToken);
                Assert.Equal(sourceManifest.ArtifactId, stored.ArtifactRef);
            }

            await using var workerFactory = CreateWorkerFactory(workRoot, store);
            using var worker = workerFactory.CreateClient();
            Assert.Same(
                SharpLabNextTelemetry.Metrics,
                workerFactory.Services.GetRequiredService<SharpLabNextMetrics>());
            using var ready = await worker.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            var readyBody = await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(ready.IsSuccessStatusCode, readyBody);

            var transform = new TransformArtifactRequest(
                "transform-request",
                "transform-key",
                "pipeline-test",
                sourceManifest.ArtifactId,
                "il-assembler",
                "assemble-il",
                new TransformArtifactOptions(),
                DateTimeOffset.UtcNow.AddSeconds(30));
            var firstHandle = await StartAsync(
                worker,
                "/api/v1/artifact-transforms",
                transform);
            var secondHandle = await StartAsync(
                worker,
                "/api/v1/artifact-transforms",
                transform with { RequestId = "transform-request-duplicate" });
            Assert.False(firstHandle.IsExisting);
            Assert.True(secondHandle.IsExisting);
            Assert.Equal(firstHandle.OperationId, secondHandle.OperationId);

            var transformEvents = await WaitForEventsAsync(worker, firstHandle.OperationId);
            OperationEventStreamContract.Validate(transformEvents);
            var transformResult = Assert.IsType<TransformArtifactResult>(
                Assert.Single(transformEvents
                    .Select(static item => item.Payload)
                    .OfType<TypedResultOperationEventPayload>()).Result);
            Assert.Equal(ArtifactJobOutcome.Succeeded, transformResult.Outcome);
            Assert.Equal("dotnet-managed-pe-v1", transformResult.ArtifactFormat);
            Assert.True(transformResult.ArtifactRef.HasValue);
            var outputArtifactRef = transformResult.ArtifactRef.Value;
            Assert.Contains(transformEvents, static item => item.Payload is ArtifactProducedOperationEventPayload);

            var outputBundle = await store.GetArtifactAsync(
                outputArtifactRef,
                TestContext.Current.CancellationToken);
            Assert.NotNull(outputBundle);
            Assert.Equal("dotnet-managed-pe-v1", outputBundle.Manifest.ArtifactFormat);
            Assert.Equal(sourceManifest.ArtifactId, outputBundle.Manifest.Derivation?.ParentArtifactId);
            Assert.Equal("il-assembler", outputBundle.Manifest.Derivation?.ProcessorId);
            Assert.Equal("coreclr", outputBundle.Manifest.RuntimeRequirement.Family);
            Assert.Equal("minilang", outputBundle.Manifest.Producer.LanguageId);
            Assert.Empty(outputBundle.Manifest.MetadataFeatureTags);
            await using (var pe = await store.OpenArtifactFileReadAsync(
                outputBundle.Manifest.ArtifactId,
                outputBundle.Manifest.EntryAssembly,
                TestContext.Current.CancellationToken))
            {
                var bytes = await ReadAllAsync(pe.Content, TestContext.Current.CancellationToken);
                Assert.True(bytes.Length > 2);
                Assert.Equal((byte)'M', bytes[0]);
                Assert.Equal((byte)'Z', bytes[1]);
            }

            var render = new RenderArtifactRequest(
                "render-request",
                "render-key",
                "pipeline-test",
                sourceManifest.ArtifactId,
                "il-assembler",
                "generated-il",
                new RenderArtifactOptions(MaxCharacters: 100_000),
                DateTimeOffset.UtcNow.AddSeconds(30));
            var renderHandle = await StartAsync(worker, "/api/v1/artifact-renders", render);
            var renderEvents = await WaitForEventsAsync(worker, renderHandle.OperationId);
            OperationEventStreamContract.Validate(renderEvents);
            var renderResult = Assert.IsType<RenderArtifactResult>(
                Assert.Single(renderEvents
                    .Select(static item => item.Payload)
                    .OfType<TypedResultOperationEventPayload>()).Result);
            Assert.Equal(ArtifactJobOutcome.Succeeded, renderResult.Outcome);
            Assert.Equal(ContentIdentity.Compute(source), renderResult.ContentRef);
            Assert.Equal("il-assembler", renderResult.Identity?.ProcessorId);
            Assert.Equal(IlCompilerProtocol.PackageVersion, renderResult.Identity?.ProcessorVersion);
            Assert.Contains(renderEvents, static item => item.Payload is ContentProducedOperationEventPayload);
            Assert.True(renderResult.ContentRef.HasValue);
            var renderedContentRef = renderResult.ContentRef.Value;
            await using (var generated = await store.OpenContentReadAsync(
                renderedContentRef,
                TestContext.Current.CancellationToken))
            {
                Assert.Equal(source, await ReadAllAsync(
                    generated.Content,
                    TestContext.Current.CancellationToken));
            }

            using var unsupported = await worker.PostAsJsonAsync(
                "/api/v1/artifact-renders",
                render with { RequestId = "unsupported", IdempotencyKey = "unsupported", OutputId = "il" },
                JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
            Assert.True(workerFactory.Services.GetRequiredService<IlCompilerProcessRunner>().StartedProcessCount >= 2);
            Assert.DoesNotContain(
                AppDomain.CurrentDomain.GetAssemblies(),
                static assembly => assembly.GetName().Name?.StartsWith("Mobius", StringComparison.Ordinal) == true);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ReaderRejectsCorruptedContentAndReleasesLease()
    {
        var expected = Encoding.UTF8.GetBytes(ValidCil);
        var corrupted = expected.ToArray();
        corrupted[^1] ^= 0x01;
        var manifest = CreateSourceManifest(expected);
        var handler = new CorruptArtifactStoreHandler(manifest, corrupted);
        var store = new ArtifactStoreClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://artifact-store.test")
        });
        var capability = ArtifactWorkerCapabilityManifestSerializer.Load(
            Path.Combine(AppContext.BaseDirectory, "artifact-worker.json"));
        var settings = CreateSettings(CreateRoot());
        var reader = new CilArtifactReader(store, settings, capability);

        await Assert.ThrowsAsync<ArtifactWorkerIncompatibleArtifactException>(() => reader.ReadAsync(
            manifest.ArtifactId,
            "op_corrupt",
            TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.DownloadCount);
        Assert.Equal(1, handler.LeaseReleaseCount);
        DeleteRoot(settings.WorkRoot);
    }

    private static WebApplicationFactory<ArtifactStoreHost::Program> CreateStoreFactory(string root) =>
        new WebApplicationFactory<ArtifactStoreHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ArtifactStore:RootPath"] = root,
                    ["ArtifactStore:CleanupInterval"] = "01:00:00"
                }));
        });

    private static WebApplicationFactory<ArtifactAssemblerHost::Program> CreateWorkerFactory(
        string workRoot,
        IArtifactStoreClient store) =>
        new WebApplicationFactory<ArtifactAssemblerHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                WorkerConfiguration(workRoot)));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IArtifactStoreClient>();
                services.AddSingleton(store);
            });
        });

    private static IlAssemblerWorkerSettings CreateSettings(string workRoot)
    {
        var manifest = ArtifactWorkerCapabilityManifestSerializer.Load(
            Path.Combine(AppContext.BaseDirectory, "artifact-worker.json"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(WorkerConfiguration(workRoot))
            .Build();
        return IlAssemblerWorkerSettings.FromConfiguration(configuration, manifest);
    }

    private static Dictionary<string, string?> WorkerConfiguration(string workRoot) => new()
    {
        ["ArtifactAssembler:ReleaseId"] = "test-release",
        ["ArtifactAssembler:WorkerImageId"] = $"sha256:{new string('a', 64)}",
        ["ArtifactAssembler:CompilerVersion"] = "0.1.0",
        ["ArtifactAssembler:WorkRoot"] = workRoot,
        ["ArtifactAssembler:CompilerAssemblyPath"] = Path.Combine(
            Path.GetDirectoryName(typeof(ArtifactAssemblerHost::Program).Assembly.Location)!,
            "SharpLabNext.Worker.IL.Compiler.dll"),
        ["ArtifactStore:BaseUrl"] = "http://artifact-store.test",
        ["ReferenceSets:net10-ref:TargetFramework"] = "net10.0",
        ["ReferenceSets:net10-ref:FrameworkName"] = "Microsoft.NETCore.App",
        ["ReferenceSets:net10-ref:FrameworkVersion"] = "10.0.9",
        ["ReferenceSets:net10-ref:RuntimeFamily"] = "coreclr",
        ["ReferenceSets:net10-ref:Architecture"] = "anycpu"
    };

    private static async Task<OperationHandle> StartAsync<TRequest>(
        HttpClient client,
        string path,
        TRequest request)
    {
        using var response = await client.PostAsJsonAsync(
            path,
            request,
            JsonOptions,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, body);
        return System.Text.Json.JsonSerializer.Deserialize<OperationHandle>(body, JsonOptions)
            ?? throw new InvalidOperationException("Operation handle was empty.");
    }

    private static async Task<OperationEvent[]> WaitForEventsAsync(HttpClient client, string operationId)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var state = await client.GetFromJsonAsync<OperationState>(
                $"/api/v1/operations/{operationId}",
                JsonOptions,
                TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("Operation state was empty.");
            if (state.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
            {
                var events = await client.GetFromJsonAsync<OperationEvent[]>(
                    $"/api/v1/operations/{operationId}/events?FromSequence=0",
                    JsonOptions,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(events);
                Assert.Equal(OperationStatus.Completed, state.Status);
                return events;
            }
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Artifact operation did not become terminal.");
    }

    private static ArtifactManifest CreateSourceManifest(byte[] content)
    {
        var file = new ArtifactFileDescriptor(
            "generated-il",
            "Program.il",
            content.LongLength,
            ContentIdentity.Compute(content).Value);
        return ArtifactIdentity.WithComputedId(new ArtifactManifest(
            1,
            new ArtifactRef($"sha256:{new string('0', 64)}"),
            new ArtifactProducer(
                "test-release",
                "minilang",
                "minilang-stable",
                "1.0.0",
                null,
                $"sha256:{new string('b', 64)}"),
            "net10-ref",
            "net10.0",
            "cil-text-v1",
            new ArtifactRuntimeRequirement("none", [], "any", []),
            ["cil.ecma-335"],
            BuildOutputKind.Console,
            file.Path,
            "Program::Main",
            [file]));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpLabNext", "il-assembler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
            return;
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class CorruptArtifactStoreHandler : HttpMessageHandler
    {
        private readonly ArtifactBundleDescriptor _bundle;
        private readonly byte[] _content;

        public CorruptArtifactStoreHandler(ArtifactManifest manifest, byte[] content)
        {
            _content = content;
            var file = Assert.Single(manifest.Files);
            _bundle = new ArtifactBundleDescriptor(
                manifest,
                [new ArtifactBundleEntry(file.Path, file.Size, file.Digest, file.Role, new ContentRef(file.Digest))]);
        }

        public int DownloadCount { get; private set; }

        public int LeaseReleaseCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var digest = ArtifactStoreProtocol.GetDigest(_bundle.Manifest.ArtifactId);
            var artifactPath = $"{ArtifactStoreProtocol.ApiPrefix}/artifacts/sha256/{digest}";
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == artifactPath)
                return Task.FromResult(Json(_bundle));
            if (request.Method == HttpMethod.Post && path == $"{artifactPath}/leases")
            {
                return Task.FromResult(Json(new ArtifactLeaseResponse(
                    "lease_corrupt",
                    _bundle.Manifest.ArtifactId,
                    "il-assembler:op_corrupt",
                    DateTimeOffset.UtcNow.AddMinutes(1))));
            }
            if (request.Method == HttpMethod.Get && path == $"{artifactPath}/files/Program.il")
            {
                DownloadCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_content)
                });
            }
            if (request.Method == HttpMethod.Delete &&
                path == $"{ArtifactStoreProtocol.ApiPrefix}/leases/lease_corrupt")
            {
                LeaseReleaseCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value, options: JsonOptions)
        };
    }

    private const string ValidCil = """
        .assembly extern System.Runtime {}
        .assembly extern System.Console {}
        .assembly MiniLanguageProgram {}
        .module MiniLanguageProgram.dll

        .class public auto ansi abstract sealed Program extends [System.Runtime]System.Object
        {
          .method public hidebysig static void Main() cil managed
          {
            .entrypoint
            .maxstack 1
            ldstr "Hello from generated IL"
            call void [System.Console]System.Console::WriteLine(string)
            ret
          }
        }
        """;
}

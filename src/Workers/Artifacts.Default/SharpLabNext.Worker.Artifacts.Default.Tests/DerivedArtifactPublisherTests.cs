using SharpLabNext.ArtifactProcessing.Protocol;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class DerivedArtifactPublisherTests
{
    [Fact]
    public async Task InstrumentationPublisherCreatesContentAddressedDerivedArtifact()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var input = Path.Combine(root, "input");
            Directory.CreateDirectory(input);
            var sourceAssembly = typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location;
            var assemblyPath = Path.Combine(input, "app.dll");
            var pdbPath = Path.Combine(input, "app.pdb");
            File.Copy(sourceAssembly, assemblyPath);
            File.Copy(Path.ChangeExtension(sourceAssembly, ".pdb"), pdbPath);

            var sourceManifest = Manifest(assemblyPath, pdbPath);
            var store = new RecordingArtifactStoreClient();
            await using var artifact = new MaterializedArtifact(input, assemblyPath, pdbPath, sourceManifest, new ArtifactReferenceSet("net10-ref", [Path.GetDirectoryName(typeof(object).Assembly.Location)!], "System.Private.CoreLib"), "lease_test", store);
            var settings = TestSettings.Create(root);
            var processor = await new ArtifactProcessorProcessRunner(settings).RunAsync(artifact, ProcessorOperation.RuntimeInstrumentationV1, includeSequencePoints: true, includeCompilerGeneratedMembers: true, includeMetadataTokens: false, maxCharacters: 1_000_000, maxFindings: 1_000, DateTimeOffset.UtcNow.AddSeconds(15), TestContext.Current.CancellationToken, ProcessorProtocol.RuntimeInstrumentationProfileId);

            var published = await new DerivedArtifactPublisher(store, settings).PublishRuntimeInstrumentationAsync(artifact, processor, new TransformArtifactOptions(RewriterProfileId: ProcessorProtocol.RuntimeInstrumentationProfileId), TestContext.Current.CancellationToken);

            Assert.NotEqual(sourceManifest.ArtifactId, published.ArtifactRef);
            var derived = Assert.IsType<ArtifactManifest>(store.Manifest);
            ArtifactIdentity.Validate(derived);
            Assert.Equal(sourceManifest.ArtifactId, derived.Derivation?.ParentArtifactId);
            Assert.Equal("artifacts-default", derived.Derivation?.ProcessorId);
            Assert.Equal("runtime-instrumentation-v1", derived.Metadata?["sharplabnext.instrumentation.transform"]);
            Assert.Equal("execution-flow-v1", derived.Metadata?["sharplabnext.instrumentation.profile"]);
            Assert.Equal("true", derived.Metadata?["sharplabnext.instrumentation.applied"]);
            Assert.Equal(2, store.UploadedFiles.Count);
            Assert.All(derived.Files, file =>
            {
                var bytes = store.UploadedFiles[file.Path];
                Assert.Equal(file.Size, bytes.LongLength);
                Assert.Equal(file.Digest, ContentIdentity.Compute(bytes).Value);
            });
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    private static ArtifactManifest Manifest(string assemblyPath, string pdbPath)
    {
        var assembly = File.ReadAllBytes(assemblyPath);
        var pdb = File.ReadAllBytes(pdbPath);
        var placeholder = new ArtifactRef($"sha256:{new string('0', 64)}");
        return ArtifactIdentity.WithComputedId(new ArtifactManifest(
            ArtifactStoreProtocol.ArtifactManifestVersion,
            placeholder,
            new ArtifactProducer("test-release", "csharp", "roslyn-stable", "5.6.0", null, "test-worker-image"),
            "net10-ref",
            "net10.0",
            "dotnet-managed-pe-v1",
            new ArtifactRuntimeRequirement("coreclr", [new FrameworkRequirement("Microsoft.NETCore.App", "10.0.9")], "anycpu", []),
            [],
            BuildOutputKind.Console,
            "app.dll",
            "Program.Main",
            [
                new ArtifactFileDescriptor("primary-assembly", "app.dll", assembly.LongLength, ContentIdentity.Compute(assembly).Value),
                new ArtifactFileDescriptor("portable-pdb", "app.pdb", pdb.LongLength, ContentIdentity.Compute(pdb).Value)
            ]));
    }

    private sealed class RecordingArtifactStoreClient : IArtifactStoreClient
    {
        public ArtifactManifest? Manifest { get; private set; }
        public Dictionary<string, byte[]> UploadedFiles { get; } = new(StringComparer.Ordinal);

        public async Task<PutArtifactResponse> PutArtifactAsync(ArtifactManifest manifest, IReadOnlyList<ArtifactFileUpload> files, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            Manifest = manifest;
            foreach (var file in files)
            {
                using var destination = new MemoryStream();
                await file.Content.CopyToAsync(destination, cancellationToken);
                UploadedFiles.Add(file.Path, destination.ToArray());
            }
            return new PutArtifactResponse(manifest.ArtifactId, UploadedFiles.Values.Sum(static value => value.LongLength), DateTimeOffset.UtcNow.Add(timeToLive ?? TimeSpan.FromHours(1)), false);
        }

        public Task ReleaseLeaseAsync(string leaseToken, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<PutContentResponse> PutContentAsync(ContentRef contentRef, Stream content, long? declaredSize = null, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ArtifactContentResponse> OpenContentReadAsync(ContentRef contentRef, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ArtifactBundleDescriptor?> GetArtifactAsync(ArtifactRef artifactRef, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ArtifactContentResponse> OpenArtifactFileReadAsync(ArtifactRef artifactRef, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ArtifactLeaseResponse> AcquireLeaseAsync(ArtifactRef artifactRef, string owner, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ArtifactLeaseResponse> RenewLeaseAsync(string leaseToken, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GarbageCollectionResponse> CollectGarbageAsync(int maxArtifacts = 1000, int maxContents = 5000, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

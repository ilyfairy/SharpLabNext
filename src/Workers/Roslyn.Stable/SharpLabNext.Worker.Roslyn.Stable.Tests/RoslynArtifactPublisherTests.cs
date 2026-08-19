using System.Net;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Roslyn;

namespace SharpLabNext.Worker.Roslyn.Stable.Tests;

public sealed class RoslynArtifactPublisherTests
{
    [Fact]
    public async Task PublishesEveryManifestFileWithTheConfiguredTtl()
    {
        var artifact = CreateArtifact();
        var store = new RecordingArtifactStore();
        var ttl = TimeSpan.FromMinutes(17);
        var publisher = new RoslynArtifactPublisher(
            store,
            new ArtifactBundlePublishingOptions(new Uri("http://artifact-store:8080"), ttl));

        var published = await publisher.PublishAsync(
            artifact,
            TestContext.Current.CancellationToken);

        Assert.Equal(artifact.ArtifactRef, published);
        Assert.Equal(artifact.Manifest, store.Manifest);
        Assert.Equal(ttl, store.TimeToLive);
        Assert.Equal(artifact.PeImage, store.UploadedFiles["SharpLabNext.User.dll"]);
        Assert.Equal(artifact.PortablePdb, store.UploadedFiles["SharpLabNext.User.pdb"]);
    }

    [Fact]
    public async Task RejectsAnArtifactStoreIdentityMismatch()
    {
        var artifact = CreateArtifact();
        var store = new RecordingArtifactStore
        {
            ResponseArtifactRef = new ArtifactRef($"sha256:{new string('f', 64)}")
        };
        var publisher = new RoslynArtifactPublisher(
            store,
            new ArtifactBundlePublishingOptions(
                new Uri("http://artifact-store:8080"),
                TimeSpan.FromHours(1)));

        var exception = await Assert.ThrowsAsync<ArtifactBundlePublicationException>(
            () => publisher.PublishAsync(artifact, TestContext.Current.CancellationToken));

        Assert.Equal(ArtifactBundlePublicationFailure.Rejected, exception.Failure);
    }

    [Fact]
    public async Task ClassifiesArtifactStorePayloadLimits()
    {
        var artifact = CreateArtifact();
        var store = new RecordingArtifactStore
        {
            Exception = new ArtifactStoreHttpException(HttpStatusCode.RequestEntityTooLarge, "too large")
        };
        var publisher = new RoslynArtifactPublisher(
            store,
            new ArtifactBundlePublishingOptions(
                new Uri("http://artifact-store:8080"),
                TimeSpan.FromHours(1)));

        var exception = await Assert.ThrowsAsync<ArtifactBundlePublicationException>(
            () => publisher.PublishAsync(artifact, TestContext.Current.CancellationToken));

        Assert.Equal(ArtifactBundlePublicationFailure.ResourceExhausted, exception.Failure);
    }

    [Fact]
    public async Task RejectsChildArtifactBytesThatDoNotMatchTheManifestDigest()
    {
        var artifact = CreateArtifact();
        var tampered = artifact with { PeImage = [0x4d, 0x5a, 0xff, 0xff] };
        var publisher = new RoslynArtifactPublisher(
            new RecordingArtifactStore(),
            new ArtifactBundlePublishingOptions(
                new Uri("http://artifact-store:8080"),
                TimeSpan.FromHours(1)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync(tampered, TestContext.Current.CancellationToken));
    }

    private static CompiledArtifact CreateArtifact()
    {
        byte[] peImage = [0x4d, 0x5a, 0x01, 0x02];
        byte[] portablePdb = [0x42, 0x53, 0x4a, 0x42];
        var files = new[]
        {
            new ArtifactFileDescriptor(
                "primary-assembly",
                "SharpLabNext.User.dll",
                peImage.LongLength,
                ContentIdentity.Compute(peImage).Value),
            new ArtifactFileDescriptor(
                "portable-pdb",
                "SharpLabNext.User.pdb",
                portablePdb.LongLength,
                ContentIdentity.Compute(portablePdb).Value)
        };
        var identity = new BuildIdentity(
            "test-release",
            "csharp",
            "roslyn-stable",
            "5.6.0",
            null,
            "net10-ref",
            "test-image");
        var manifest = ArtifactIdentity.WithComputedId(new ArtifactManifest(
            ContractSchemaVersions.ArtifactManifest,
            new ArtifactRef($"sha256:{new string('0', 64)}"),
            new ArtifactProducer(
                identity.ReleaseId,
                identity.LanguageId,
                identity.ToolchainId,
                identity.CompilerVersion,
                identity.CompilerCommit,
                identity.WorkerImageId),
            "net10-ref",
            "net10.0",
            "dotnet-managed-pe-v1",
            new ArtifactRuntimeRequirement(
                "coreclr",
                [new FrameworkRequirement("Microsoft.NETCore.App", "10.0.9")],
                "anycpu",
                []),
            [],
            BuildOutputKind.Console,
            "SharpLabNext.User.dll",
            "Program.Main",
            files));

        return new CompiledArtifact(
            manifest.ArtifactId,
            manifest.ArtifactFormat,
            "SharpLabNext.User",
            manifest.ReferenceSetId,
            manifest.TargetFramework,
            peImage,
            portablePdb,
            manifest,
            files,
            identity);
    }

    private sealed class RecordingArtifactStore : IArtifactStoreClient
    {
        public ArtifactRef? ResponseArtifactRef { get; init; }

        public Exception? Exception { get; init; }

        public ArtifactManifest? Manifest { get; private set; }

        public TimeSpan? TimeToLive { get; private set; }

        public Dictionary<string, byte[]> UploadedFiles { get; } = new(StringComparer.Ordinal);

        public Task<PutContentResponse> PutContentAsync(
            ContentRef contentRef,
            Stream content,
            long? declaredSize = null,
            TimeSpan? timeToLive = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ArtifactContentResponse> OpenContentReadAsync(
            ContentRef contentRef,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<PutArtifactResponse> PutArtifactAsync(
            ArtifactManifest manifest,
            IReadOnlyList<ArtifactFileUpload> files,
            TimeSpan? timeToLive = null,
            CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
                throw Exception;

            Manifest = manifest;
            TimeToLive = timeToLive;
            foreach (var file in files)
            {
                using var copy = new MemoryStream();
                await file.Content.CopyToAsync(copy, cancellationToken);
                UploadedFiles.Add(file.Path, copy.ToArray());
            }

            return new PutArtifactResponse(
                ResponseArtifactRef ?? manifest.ArtifactId,
                UploadedFiles.Values.Sum(static content => (long)content.Length),
                DateTimeOffset.UtcNow.Add(timeToLive ?? TimeSpan.FromHours(1)),
                AlreadyExisted: false);
        }

        public Task<ArtifactBundleDescriptor?> GetArtifactAsync(
            ArtifactRef artifactRef,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ArtifactContentResponse> OpenArtifactFileReadAsync(
            ArtifactRef artifactRef,
            string path,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ArtifactLeaseResponse> AcquireLeaseAsync(
            ArtifactRef artifactRef,
            string owner,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ArtifactLeaseResponse> RenewLeaseAsync(
            string leaseToken,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReleaseLeaseAsync(
            string leaseToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GarbageCollectionResponse> CollectGarbageAsync(
            int maxArtifacts = 1000,
            int maxContents = 5000,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

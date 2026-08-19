using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.IntegrationTests;

public sealed class BuildArtifactPublisherTests
{
    [Fact]
    public async Task PublishesGenericCilTextArtifactByManifestPath()
    {
        var content = ".assembly Sample {}\n"u8.ToArray();
        var descriptor = new ArtifactFileDescriptor(
            "generated-il",
            "Program.il",
            content.LongLength,
            ContentIdentity.Compute(content).Value);
        var manifest = CreateManifest(descriptor);
        var store = new RecordingArtifactStoreClient();
        var publisher = new BuildArtifactPublisher(store, new BuildPipelineOptions());
        var envelope = new WorkerArtifactEnvelope(
            manifest.ArtifactId,
            "cil-text-v1",
            "Sample",
            "net10-ref",
            "net10.0",
            null,
            null,
            manifest,
            [descriptor],
            new Dictionary<string, string> { [descriptor.Path] = Convert.ToBase64String(content) });

        var result = await publisher.PublishAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Equal(manifest.ArtifactId, result.ArtifactRef);
        Assert.Equal("cil-text-v1", result.ArtifactFormat);
        Assert.Equal(content, store.UploadedContent);
        Assert.Equal("Program.il", store.UploadedPath);
    }

    [Fact]
    public async Task RejectsGenericEnvelopeWithMismatchedContentDigest()
    {
        var declared = "declared"u8.ToArray();
        var actual = "different"u8.ToArray();
        var descriptor = new ArtifactFileDescriptor(
            "generated-il",
            "Program.il",
            declared.LongLength,
            ContentIdentity.Compute(declared).Value);
        var manifest = CreateManifest(descriptor);
        var publisher = new BuildArtifactPublisher(
            new RecordingArtifactStoreClient(),
            new BuildPipelineOptions());
        var envelope = new WorkerArtifactEnvelope(
            manifest.ArtifactId,
            "cil-text-v1",
            "Sample",
            "net10-ref",
            "net10.0",
            null,
            null,
            manifest,
            [descriptor],
            new Dictionary<string, string> { [descriptor.Path] = Convert.ToBase64String(actual) });

        var exception = await Assert.ThrowsAsync<BuildArtifactPublishingException>(() =>
            publisher.PublishAsync(envelope, TestContext.Current.CancellationToken));

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptsCompilerPublishedArtifactWithoutReadingArtifactBytes()
    {
        var content = "managed assembly"u8.ToArray();
        var descriptor = new ArtifactFileDescriptor(
            "primary-assembly",
            "app.dll",
            content.LongLength,
            ContentIdentity.Compute(content).Value);
        var manifest = CreateManifest(descriptor);
        var store = new RecordingArtifactStoreClient
        {
            PublishedBundle = new ArtifactBundleDescriptor(
                manifest,
                [new ArtifactBundleEntry(
                    descriptor.Path,
                    descriptor.Size,
                    descriptor.Digest,
                    descriptor.Role,
                    new ContentRef(descriptor.Digest))])
        };
        var publisher = new BuildArtifactPublisher(store, new BuildPipelineOptions());
        var identity = new BuildIdentity(
            manifest.Producer.ReleaseId,
            manifest.Producer.LanguageId,
            manifest.Producer.ToolchainId,
            manifest.Producer.CompilerVersion,
            manifest.Producer.CompilerCommit,
            manifest.ReferenceSetId,
            manifest.Producer.WorkerImageId);

        var result = await publisher.AcceptPublishedAsync(
            manifest.ArtifactId,
            identity,
            TestContext.Current.CancellationToken);

        Assert.Equal(manifest.ArtifactId, result.ArtifactRef);
        Assert.Equal(manifest.ArtifactFormat, result.ArtifactFormat);
        Assert.Equal(1, store.GetArtifactCallCount);
        Assert.Null(store.UploadedContent);
    }

    [Fact]
    public async Task RejectsCompilerPublishedArtifactWithDifferentProducerIdentity()
    {
        var content = "managed assembly"u8.ToArray();
        var descriptor = new ArtifactFileDescriptor(
            "primary-assembly",
            "app.dll",
            content.LongLength,
            ContentIdentity.Compute(content).Value);
        var manifest = CreateManifest(descriptor);
        var store = new RecordingArtifactStoreClient
        {
            PublishedBundle = new ArtifactBundleDescriptor(
                manifest,
                [new ArtifactBundleEntry(
                    descriptor.Path,
                    descriptor.Size,
                    descriptor.Digest,
                    descriptor.Role,
                    new ContentRef(descriptor.Digest))])
        };
        var publisher = new BuildArtifactPublisher(store, new BuildPipelineOptions());
        var identity = new BuildIdentity(
            manifest.Producer.ReleaseId,
            manifest.Producer.LanguageId,
            "another-toolchain",
            manifest.Producer.CompilerVersion,
            manifest.Producer.CompilerCommit,
            manifest.ReferenceSetId,
            manifest.Producer.WorkerImageId);

        var exception = await Assert.ThrowsAsync<BuildArtifactPublishingException>(() =>
            publisher.AcceptPublishedAsync(
                manifest.ArtifactId,
                identity,
                TestContext.Current.CancellationToken));

        Assert.Contains("producer identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ArtifactManifest CreateManifest(ArtifactFileDescriptor descriptor)
    {
        var placeholder = new ArtifactManifest(
            1,
            new ArtifactRef($"sha256:{new string('0', 64)}"),
            new ArtifactProducer(
                "development",
                "tiny-language",
                "tiny-language-stable",
                "1.0.0",
                null,
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            "net10-ref",
            "net10.0",
            "cil-text-v1",
            new ArtifactRuntimeRequirement(
                "coreclr",
                [new FrameworkRequirement("Microsoft.NETCore.App", "10.0.9")],
                "anycpu",
                []),
            [],
            BuildOutputKind.Console,
            descriptor.Path,
            null,
            [descriptor]);
        return ArtifactIdentity.WithComputedId(placeholder);
    }

    private sealed class RecordingArtifactStoreClient : IArtifactStoreClient
    {
        public ArtifactBundleDescriptor? PublishedBundle { get; init; }

        public int GetArtifactCallCount { get; private set; }

        public byte[]? UploadedContent { get; private set; }

        public string? UploadedPath { get; private set; }

        public Task<PutArtifactResponse> PutArtifactAsync(
            ArtifactManifest manifest,
            IReadOnlyList<ArtifactFileUpload> files,
            TimeSpan? timeToLive = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Single(files);
            UploadedPath = files[0].Path;
            using var buffer = new MemoryStream();
            files[0].Content.CopyTo(buffer);
            UploadedContent = buffer.ToArray();
            return Task.FromResult(new PutArtifactResponse(
                manifest.ArtifactId,
                UploadedContent.LongLength,
                DateTimeOffset.UtcNow.AddHours(1),
                false));
        }

        public Task<PutContentResponse> PutContentAsync(ContentRef contentRef, Stream content, long? declaredSize = null, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArtifactContentResponse> OpenContentReadAsync(ContentRef contentRef, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArtifactBundleDescriptor?> GetArtifactAsync(
            ArtifactRef artifactRef,
            CancellationToken cancellationToken = default)
        {
            GetArtifactCallCount++;
            return Task.FromResult(PublishedBundle);
        }
        public Task<ArtifactContentResponse> OpenArtifactFileReadAsync(ArtifactRef artifactRef, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArtifactLeaseResponse> AcquireLeaseAsync(ArtifactRef artifactRef, string owner, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArtifactLeaseResponse> RenewLeaseAsync(string leaseToken, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseLeaseAsync(string leaseToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GarbageCollectionResponse> CollectGarbageAsync(int maxArtifacts = 1000, int maxContents = 5000, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

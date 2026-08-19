using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactStore;

internal sealed record ArtifactUploadSource(string Path, Func<Stream> OpenReadStream);

internal sealed record StagedContent(
    ContentRef ContentRef,
    string Digest,
    string TemporaryPath,
    long Size);

internal sealed record PublishedContent(
    ContentRef ContentRef,
    string Digest,
    string RelativePath,
    long Size,
    bool AlreadyExisted);

internal sealed record StoredArtifactMetadata(
    ArtifactBundleDescriptor Descriptor,
    string RelativePath,
    long TotalSize,
    DateTimeOffset ExpiresAt);

internal sealed record ArtifactEntryMetadata(
    string Path,
    string Role,
    long Size,
    string Digest,
    ContentRef ContentRef);

internal sealed record LeaseMetadata(
    string LeaseToken,
    ArtifactRef ArtifactRef,
    string Owner,
    DateTimeOffset ExpiresAt);

internal sealed record GarbageCollectionPlan(
    int ExpiredLeasesDeleted,
    IReadOnlyList<string> ArtifactRelativePaths,
    IReadOnlyList<ContentDeletion> Contents);

internal sealed record ContentDeletion(string RelativePath, long Size);

internal sealed class VerifiedContent(Stream stream, long size, ContentRef contentRef) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public long Size { get; } = size;

    public ContentRef ContentRef { get; } = contentRef;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

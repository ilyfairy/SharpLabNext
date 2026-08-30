using System.Net;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactStore.Client;

public sealed record ArtifactBundleContent(string Path, byte[] Content);

public sealed record ArtifactBundlePublishingOptions(Uri BaseAddress, TimeSpan TimeToLive);

public enum ArtifactBundlePublicationFailure
{
    ResourceExhausted,
    Unavailable,
    Rejected
}

public sealed class ArtifactBundlePublicationException(ArtifactBundlePublicationFailure failure, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public ArtifactBundlePublicationFailure Failure { get; } = failure;
}

public sealed class ArtifactBundlePublisher(IArtifactStoreClient artifactStore)
{
    public async Task<PutArtifactResponse> PublishAsync(ArtifactManifest manifest, IReadOnlyList<ArtifactBundleContent> contents, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(contents);
        ArtifactIdentity.Validate(manifest);
        if (manifest.ArtifactId != ArtifactIdentity.Compute(manifest))
            throw new ArgumentException("The artifact manifest identity is not canonical.", nameof(manifest));

        var normalized = contents.Select(content =>
            {
                ArgumentNullException.ThrowIfNull(content);
                ArgumentNullException.ThrowIfNull(content.Content);
                return content with { Path = ArtifactPath.Normalize(content.Path) };
            }).ToArray();
        _ = ArtifactPath.NormalizeDistinct(normalized.Select(static content => content.Path));
        var expectedPaths = manifest.Files.Select(static file => ArtifactPath.Normalize(file.Path)).Order(StringComparer.Ordinal).ToArray();
        var actualPaths = normalized.Select(static content => content.Path).Order(StringComparer.Ordinal).ToArray();
        if (!expectedPaths.SequenceEqual(actualPaths, StringComparer.Ordinal))
            throw new ArgumentException("Artifact contents must contain every manifest file exactly once.", nameof(contents));

        var byPath = normalized.ToDictionary(static content => content.Path, StringComparer.Ordinal);
        long totalSize = 0;
        foreach (var file in manifest.Files)
        {
            var content = byPath[ArtifactPath.Normalize(file.Path)].Content;
            totalSize = checked(totalSize + content.LongLength);
            if (file.Size != content.LongLength || !string.Equals(file.Digest, ContentIdentity.Compute(content).Value, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Artifact file '{file.Path}' failed size or checksum validation.", nameof(contents));
            }
        }

        var uploads = manifest.Files.Select(file => new ArtifactFileUpload(ArtifactPath.Normalize(file.Path), new MemoryStream(byPath[ArtifactPath.Normalize(file.Path)].Content, writable: false), file.Size)).ToArray();
        try
        {
            PutArtifactResponse stored;
            try
            {
                stored = await artifactStore.PutArtifactAsync(manifest, uploads, timeToLive, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ArtifactStoreHttpException exception)
            {
                throw PublicationFailure(exception);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                throw new ArtifactBundlePublicationException(ArtifactBundlePublicationFailure.Unavailable, "The artifact could not be published because the Artifact Store is unavailable.", exception);
            }

            if (stored.ArtifactRef != manifest.ArtifactId || stored.TotalSize != totalSize)
            {
                throw new ArtifactBundlePublicationException(ArtifactBundlePublicationFailure.Rejected, "Artifact Store returned an unexpected artifact identity or size.");
            }

            return stored;
        }
        finally
        {
            foreach (var upload in uploads) upload.Content.Dispose();
        }
    }

    private static ArtifactBundlePublicationException PublicationFailure(ArtifactStoreHttpException exception)
    {
        var failure = exception.StatusCodeValue switch
        {
            HttpStatusCode.RequestEntityTooLarge => ArtifactBundlePublicationFailure.ResourceExhausted,
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout => ArtifactBundlePublicationFailure.Unavailable,
            _ => ArtifactBundlePublicationFailure.Rejected
        };
        var message = failure switch
        {
            ArtifactBundlePublicationFailure.ResourceExhausted =>
                "The artifact exceeds the Artifact Store limit.",
            ArtifactBundlePublicationFailure.Unavailable =>
                "The artifact could not be published because the Artifact Store is unavailable.",
            _ => "Artifact Store rejected the artifact."
        };
        return new ArtifactBundlePublicationException(failure, message, exception);
    }
}

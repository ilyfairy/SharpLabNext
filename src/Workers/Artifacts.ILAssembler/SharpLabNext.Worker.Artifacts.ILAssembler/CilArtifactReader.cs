using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Artifacts.ILAssembler;

internal sealed class ValidatedCilArtifact(
    ArtifactRef artifactRef,
    ArtifactManifest manifest,
    IlAssemblerReferenceSet referenceSet,
    string entryPath,
    byte[] utf8Content,
    string sourceText,
    string leaseToken,
    IArtifactStoreClient storeClient) : IAsyncDisposable
{
    public ArtifactRef ArtifactRef { get; } = artifactRef;

    public ArtifactManifest Manifest { get; } = manifest;

    public IlAssemblerReferenceSet ReferenceSet { get; } = referenceSet;

    public string EntryPath { get; } = entryPath;

    public byte[] Utf8Content { get; } = utf8Content;

    public string SourceText { get; } = sourceText;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await storeClient.ReleaseLeaseAsync(leaseToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
        }
    }
}

internal sealed class CilArtifactReader(
    IArtifactStoreClient storeClient,
    IlAssemblerWorkerSettings settings,
    ArtifactWorkerCapabilityManifest capabilityManifest)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<ValidatedCilArtifact> ReadAsync(
        ArtifactRef artifactRef,
        string operationId,
        CancellationToken cancellationToken)
    {
        ArtifactBundleDescriptor bundle;
        try
        {
            bundle = await storeClient.GetArtifactAsync(artifactRef, cancellationToken).ConfigureAwait(false)
                ?? throw new ArtifactWorkerArtifactNotFoundException("The requested CIL artifact was not found.");
        }
        catch (ArtifactWorkerException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArtifactStoreHttpException exception) when (exception.StatusCodeValue == HttpStatusCode.NotFound)
        {
            throw new ArtifactWorkerArtifactNotFoundException(
                "The requested CIL artifact was not found.",
                exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new ArtifactWorkerDependencyUnavailableException(
                "The Artifact Store is unavailable.",
                exception);
        }

        var (file, entry, referenceSet) = ValidateBundle(artifactRef, bundle);
        string? leaseToken = null;
        try
        {
            var lease = await storeClient.AcquireLeaseAsync(
                artifactRef,
                $"il-assembler:{operationId}",
                TimeSpan.FromMilliseconds(capabilityManifest.Limits.MaximumOperationMilliseconds + 30_000),
                cancellationToken).ConfigureAwait(false);
            leaseToken = lease.LeaseToken;
            if (lease.ArtifactRef != artifactRef)
                throw new ArtifactWorkerIncompatibleArtifactException("Artifact Store returned a mismatched lease.");

            var content = await ReadFileAsync(artifactRef, file, entry, cancellationToken).ConfigureAwait(false);
            string text;
            try
            {
                text = StrictUtf8.GetString(content);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ArtifactWorkerIncompatibleArtifactException(
                    "The CIL artifact is not valid UTF-8 text.",
                    exception);
            }
            if (text.Contains('\0'))
                throw new ArtifactWorkerIncompatibleArtifactException("The CIL artifact contains NUL characters.");
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text[1..];

            return new ValidatedCilArtifact(
                artifactRef,
                bundle.Manifest,
                referenceSet,
                file.Path,
                content,
                text,
                leaseToken,
                storeClient);
        }
        catch (ArtifactWorkerException)
        {
            if (leaseToken is not null)
                await ReleaseLeaseAsync(leaseToken).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (leaseToken is not null)
                await ReleaseLeaseAsync(leaseToken).ConfigureAwait(false);
            throw;
        }
        catch (ArtifactStoreHttpException exception) when (exception.StatusCodeValue == HttpStatusCode.NotFound)
        {
            if (leaseToken is not null)
                await ReleaseLeaseAsync(leaseToken).ConfigureAwait(false);
            throw new ArtifactWorkerArtifactNotFoundException(
                "The requested CIL artifact was not found.",
                exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            if (leaseToken is not null)
                await ReleaseLeaseAsync(leaseToken).ConfigureAwait(false);
            throw new ArtifactWorkerDependencyUnavailableException(
                "The Artifact Store is unavailable.",
                exception);
        }
    }

    private (ArtifactFileDescriptor File, ArtifactBundleEntry Entry, IlAssemblerReferenceSet ReferenceSet) ValidateBundle(
        ArtifactRef requestedRef,
        ArtifactBundleDescriptor bundle)
    {
        try
        {
            ArtifactIdentity.Validate(bundle.Manifest);
        }
        catch (ArgumentException exception)
        {
            throw new ArtifactWorkerIncompatibleArtifactException(
                "The CIL artifact manifest is invalid.",
                exception);
        }
        if (bundle.Manifest.ArtifactId != requestedRef)
            throw new ArtifactWorkerIncompatibleArtifactException("The CIL artifact identity does not match the request.");
        if (!string.Equals(bundle.Manifest.ArtifactFormat, "cil-text-v1", StringComparison.Ordinal))
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact is not a cil-text-v1 artifact.");
        if (bundle.Manifest.Files.Count != 1 || bundle.Entries.Count != 1)
            throw new ArtifactWorkerIncompatibleArtifactException("A CIL text artifact must contain exactly one file.");
        if (bundle.Manifest.MetadataFeatureTags.Count !=
            bundle.Manifest.MetadataFeatureTags.Distinct(StringComparer.Ordinal).Count() ||
            bundle.Manifest.MetadataFeatureTags.Any(static tag => tag != "cil.ecma-335"))
        {
            throw new ArtifactWorkerIncompatibleArtifactException(
                "The CIL artifact requires unsupported metadata features.");
        }

        var file = bundle.Manifest.Files[0];
        var entry = bundle.Entries[0];
        string normalizedPath;
        try
        {
            normalizedPath = ArtifactPath.Normalize(file.Path);
        }
        catch (ArgumentException exception)
        {
            throw new ArtifactWorkerIncompatibleArtifactException("The CIL artifact path is invalid.", exception);
        }
        if (!string.Equals(normalizedPath, file.Path, StringComparison.Ordinal) ||
            file.Path.Length > 240 ||
            !string.Equals(bundle.Manifest.EntryAssembly, file.Path, StringComparison.Ordinal) ||
            !string.Equals(file.Role, "generated-il", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(file.Path), ".il", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArtifactWorkerIncompatibleArtifactException(
                "The CIL artifact entry file or role is invalid.");
        }
        if (file.Size <= 0 || file.Size > capabilityManifest.Limits.MaximumInputArtifactBytes)
            throw new ArtifactWorkerLimitExceededException("The CIL artifact exceeds the configured input limit.");
        if (!string.Equals(entry.Path, file.Path, StringComparison.Ordinal) ||
            entry.Size != file.Size ||
            !string.Equals(entry.Role, file.Role, StringComparison.Ordinal) ||
            !string.Equals(entry.Digest, file.Digest, StringComparison.Ordinal) ||
            !string.Equals(entry.ContentRef.Value, file.Digest, StringComparison.Ordinal))
        {
            throw new ArtifactWorkerIncompatibleArtifactException(
                "The CIL artifact bundle does not match its manifest.");
        }
        try
        {
            _ = ArtifactStoreProtocol.ParseContentRef(file.Digest);
        }
        catch (ArgumentException exception)
        {
            throw new ArtifactWorkerIncompatibleArtifactException(
                "The CIL artifact content digest is invalid.",
                exception);
        }

        return (file, entry, settings.GetReferenceSet(
            bundle.Manifest.ReferenceSetId,
            bundle.Manifest.TargetFramework));
    }

    private async Task<byte[]> ReadFileAsync(
        ArtifactRef artifactRef,
        ArtifactFileDescriptor file,
        ArtifactBundleEntry entry,
        CancellationToken cancellationToken)
    {
        await using var response = await storeClient.OpenArtifactFileReadAsync(
            artifactRef,
            file.Path,
            cancellationToken).ConfigureAwait(false);
        if (response.Length is not null && response.Length != file.Size)
            throw new ArtifactWorkerIncompatibleArtifactException("The CIL artifact Content-Length is invalid.");

        using var output = new MemoryStream(checked((int)file.Size));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await response.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                total = checked(total + read);
                if (total > file.Size || total > capabilityManifest.Limits.MaximumInputArtifactBytes)
                    throw new ArtifactWorkerLimitExceededException("The CIL artifact exceeded its declared size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (total != file.Size ||
            !string.Equals(digest, ArtifactStoreProtocol.GetDigest(entry.ContentRef), StringComparison.Ordinal))
        {
            throw new ArtifactWorkerIncompatibleArtifactException(
                "The CIL artifact failed size or SHA-256 validation.");
        }
        return output.ToArray();
    }

    private async Task ReleaseLeaseAsync(string leaseToken)
    {
        try
        {
            await storeClient.ReleaseLeaseAsync(leaseToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
        }
    }
}

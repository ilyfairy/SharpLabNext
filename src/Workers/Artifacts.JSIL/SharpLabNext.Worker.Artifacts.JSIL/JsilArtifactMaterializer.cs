using System.Buffers;
using System.Security.Cryptography;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Artifacts.JSIL;

internal interface IJsilArtifactMaterializer
{
    Task<JsilMaterializedArtifact> MaterializeAsync(ArtifactRef artifactRef, string operationId, CancellationToken cancellationToken);
}

internal sealed class JsilMaterializedArtifact(string rootPath, string assemblyPath, ArtifactManifest manifest, JsilReferenceSet referenceSet, string leaseToken, IArtifactStoreClient storeClient) : IAsyncDisposable
{
    public string RootPath { get; } = rootPath;

    public string AssemblyPath { get; } = assemblyPath;

    public ArtifactManifest Manifest { get; } = manifest;

    public JsilReferenceSet ReferenceSet { get; } = referenceSet;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await storeClient.ReleaseLeaseAsync(leaseToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
        finally
        {
            JsilTemporaryDirectory.Delete(RootPath);
        }
    }
}

internal sealed class JsilArtifactMaterializer(IArtifactStoreClient storeClient, JsilWorkerSettings settings, ArtifactWorkerCapabilityManifest capabilityManifest) : IJsilArtifactMaterializer
{
    private const int MaximumArtifactFiles = 32;

    public async Task<JsilMaterializedArtifact> MaterializeAsync(ArtifactRef artifactRef, string operationId, CancellationToken cancellationToken)
    {
        var root = JsilTemporaryDirectory.Create(settings.WorkRoot, operationId);
        string? leaseToken = null;
        try
        {
            var lease = await storeClient.AcquireLeaseAsync(artifactRef, $"artifacts-jsil:{operationId}", TimeSpan.FromMilliseconds(capabilityManifest.Limits.MaximumOperationMilliseconds + 30_000), cancellationToken).ConfigureAwait(false);
            leaseToken = lease.LeaseToken;
            var bundle = await storeClient.GetArtifactAsync(artifactRef, cancellationToken).ConfigureAwait(false) ?? throw new ArtifactWorkerArtifactNotFoundException("The requested managed artifact was not found.");
            var referenceSet = ValidateBundle(artifactRef, bundle);
            var manifestFiles = bundle.Manifest.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
            string? assemblyPath = null;
            foreach (var entry in bundle.Entries)
            {
                if (Path.GetExtension(entry.Path).ToLowerInvariant() is not (".dll" or ".exe"))
                    continue;
                var destination = JsilTemporaryDirectory.ResolvePath(root, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await DownloadAndVerifyAsync(artifactRef, entry, manifestFiles[entry.Path], destination, cancellationToken).ConfigureAwait(false);
                if (StringComparer.Ordinal.Equals(entry.Path, bundle.Manifest.EntryAssembly))
                    assemblyPath = destination;
            }
            if (assemblyPath is null)
                throw new ArtifactWorkerIncompatibleArtifactException("The managed entry assembly is unavailable.");

            LinkReferenceAssemblies(Path.GetDirectoryName(assemblyPath)!, referenceSet);
            return new JsilMaterializedArtifact(root, assemblyPath, bundle.Manifest, referenceSet, leaseToken, storeClient);
        }
        catch
        {
            if (leaseToken is not null)
            {
                try
                {
                    await storeClient.ReleaseLeaseAsync(leaseToken, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
            }
            JsilTemporaryDirectory.Delete(root);
            throw;
        }
    }

    private JsilReferenceSet ValidateBundle(ArtifactRef requestedRef, ArtifactBundleDescriptor bundle)
    {
        try
        {
            ArtifactIdentity.Validate(bundle.Manifest);
        }
        catch (ArgumentException exception)
        {
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact manifest is invalid.", exception);
        }
        if (bundle.Manifest.ArtifactId != requestedRef || !StringComparer.Ordinal.Equals(bundle.Manifest.ArtifactFormat, "dotnet-managed-pe-v1") || bundle.Manifest.MetadataFeatureTags.Count != 0)
        {
            throw new ArtifactWorkerIncompatibleArtifactException("JSIL accepts ordinary managed PE artifacts without experimental metadata extensions.");
        }
        if (bundle.Manifest.Files.Count is 0 or > MaximumArtifactFiles || bundle.Entries.Count != bundle.Manifest.Files.Count)
        {
            throw new ArtifactWorkerLimitExceededException("The managed artifact file count exceeds the JSIL limit.");
        }

        var manifestByPath = bundle.Manifest.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in bundle.Entries)
        {
            if (!manifestByPath.TryGetValue(entry.Path, out var manifestFile) || manifestFile.Size != entry.Size || !StringComparer.Ordinal.Equals(manifestFile.Digest, entry.Digest) || !StringComparer.Ordinal.Equals(entry.Digest, entry.ContentRef.Value) || !StringComparer.Ordinal.Equals(manifestFile.Role, entry.Role))
            {
                throw new ArtifactWorkerIncompatibleArtifactException("The artifact bundle does not match its manifest.");
            }
            total = checked(total + entry.Size);
            if (total > capabilityManifest.Limits.MaximumInputArtifactBytes)
                throw new ArtifactWorkerLimitExceededException("The managed artifact exceeds the JSIL input limit.");
        }
        var entryAssembly = bundle.Manifest.Files.FirstOrDefault(file => StringComparer.Ordinal.Equals(file.Path, bundle.Manifest.EntryAssembly));
        if (entryAssembly is null || Path.GetExtension(entryAssembly.Path).ToLowerInvariant() is not (".dll" or ".exe"))
        {
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact entry assembly is invalid.");
        }
        return settings.GetReferenceSet(bundle.Manifest.ReferenceSetId, bundle.Manifest.TargetFramework);
    }

    private async Task DownloadAndVerifyAsync(ArtifactRef artifactRef, ArtifactBundleEntry entry, ArtifactFileDescriptor manifestFile, string destination, CancellationToken cancellationToken)
    {
        await using var response = await storeClient.OpenArtifactFileReadAsync(artifactRef, entry.Path, cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
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
                if (total > manifestFile.Size || total > capabilityManifest.Limits.MaximumInputArtifactBytes)
                    throw new ArtifactWorkerLimitExceededException("An artifact file exceeded its declared size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (total != entry.Size || total != manifestFile.Size || !StringComparer.Ordinal.Equals(digest, ArtifactStoreProtocol.GetDigest(entry.ContentRef)))
        {
            throw new ArtifactWorkerIncompatibleArtifactException("Artifact content failed integrity validation.");
        }
    }

    private static void LinkReferenceAssemblies(string assemblyDirectory, JsilReferenceSet referenceSet)
    {
        if (!Directory.Exists(referenceSet.Path))
            throw new ArtifactWorkerDependencyUnavailableException("The selected JSIL reference set is unavailable.");
        foreach (var source in Directory.EnumerateFiles(referenceSet.Path, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(assemblyDirectory, Path.GetFileName(source));
            if (!File.Exists(destination))
                File.CreateSymbolicLink(destination, source);
        }
    }
}

internal static class JsilTemporaryDirectory
{
    public static string Create(string configuredRoot, string operationId)
    {
        if (!operationId.All(static value => char.IsAsciiLetterOrDigit(value) || value is '_' or '-'))
            throw new ArtifactWorkerRequestException("invalid-operation-id", "The operation ID is invalid.");
        var root = Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(root);
        var path = ResolvePath(root, $"job-{operationId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string ResolvePath(string root, string relativePath)
    {
        var normalized = ArtifactPath.Normalize(relativePath);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(rootWithSeparator, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new ArtifactWorkerIncompatibleArtifactException("An artifact path escaped the temporary root.");
        return result;
    }

    public static void Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 6)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }
    }
}

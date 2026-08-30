using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Artifacts.ConstGenerics.Protocol;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics;

internal sealed record MaterializedConstGenericsArtifact(
    ArtifactRef ArtifactRef,
    string RootPath,
    string AssemblyPath,
    string? PortablePdbPath,
    ArtifactManifest Manifest,
    string LeaseToken,
    IArtifactStoreClient StoreClient) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StoreClient.ReleaseLeaseAsync(LeaseToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
        finally
        {
            ConstGenericsTemporaryDirectory.Delete(RootPath);
        }
    }
}

internal sealed class ConstGenericsArtifactMaterializer(IArtifactStoreClient storeClient, ConstGenericsArtifactWorkerSettings settings, ArtifactWorkerCapabilityManifest capabilityManifest)
{
    public async Task<MaterializedConstGenericsArtifact> MaterializeAsync(ArtifactRef artifactRef, string operationId, CancellationToken cancellationToken)
    {
        ArtifactBundleDescriptor bundle;
        try
        {
            bundle = await storeClient.GetArtifactAsync(artifactRef, cancellationToken).ConfigureAwait(false) ?? throw new ArtifactWorkerArtifactNotFoundException("The requested artifact was not found.");
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
            throw new ArtifactWorkerArtifactNotFoundException("The requested artifact was not found.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new ArtifactWorkerDependencyUnavailableException("The Artifact Store is unavailable.", exception);
        }

        ValidateBundle(artifactRef, bundle);
        var root = ConstGenericsTemporaryDirectory.Create(settings.WorkRoot, operationId);
        string? leaseToken = null;
        try
        {
            var lease = await storeClient.AcquireLeaseAsync(artifactRef, $"artifacts-const-generics:{operationId}", TimeSpan.FromMilliseconds(capabilityManifest.Limits.MaximumOperationMilliseconds + 30_000), cancellationToken).ConfigureAwait(false);
            leaseToken = lease.LeaseToken;
            if (lease.ArtifactRef != artifactRef)
                throw new ArtifactWorkerIncompatibleArtifactException("Artifact Store returned a mismatched lease.");

            var manifestFiles = bundle.Manifest.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
            string? assemblyPath = null;
            string? portablePdbPath = null;
            foreach (var entry in bundle.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ShouldMaterialize(entry.Path))
                    continue;
                var destination = ConstGenericsTemporaryDirectory.ResolvePath(root, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await DownloadAndVerifyAsync(artifactRef, entry, manifestFiles[entry.Path], destination, cancellationToken).ConfigureAwait(false);
                if (string.Equals(entry.Path, bundle.Manifest.EntryAssembly, StringComparison.Ordinal))
                    assemblyPath = destination;
                if (portablePdbPath is null && (string.Equals(entry.Role, "portable-pdb", StringComparison.Ordinal) || string.Equals(Path.GetExtension(entry.Path), ".pdb", StringComparison.OrdinalIgnoreCase)))
                {
                    portablePdbPath = destination;
                }
            }

            if (assemblyPath is null)
                throw new ArtifactWorkerIncompatibleArtifactException("The artifact entry assembly is unavailable.");
            return new MaterializedConstGenericsArtifact(artifactRef, root, assemblyPath, portablePdbPath, bundle.Manifest, leaseToken, storeClient);
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
            ConstGenericsTemporaryDirectory.Delete(root);
            throw;
        }
    }

    internal void ValidateBundle(ArtifactRef requestedRef, ArtifactBundleDescriptor bundle)
    {
        try
        {
            ArtifactIdentity.Validate(bundle.Manifest);
        }
        catch (ArgumentException exception)
        {
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact manifest is invalid.", exception);
        }
        var manifest = bundle.Manifest;
        if (manifest.ArtifactId != requestedRef)
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact identity does not match the request.");
        if (!string.Equals(manifest.ArtifactFormat, "dotnet-managed-pe-v1", StringComparison.Ordinal))
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact is not a managed PE.");
        if (!string.Equals(manifest.Producer.ToolchainId, "roslyn-const-generics", StringComparison.Ordinal) || !string.Equals(manifest.ReferenceSetId, "const-generics-ref", StringComparison.Ordinal) || !string.Equals(manifest.TargetFramework, "net9.0", StringComparison.Ordinal))
        {
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact was not produced by the approved ConstGenerics toolchain and reference set.");
        }
        if (!string.Equals(manifest.RuntimeRequirement.Family, "coreclr-const-generics", StringComparison.Ordinal) || !string.Equals(manifest.RuntimeRequirement.Architecture, "anycpu", StringComparison.Ordinal) || !Exact(manifest.RuntimeRequirement.RequiredRuntimeFeatureTags, ConstGenericsProcessorProtocol.RuntimeFeatureTag))
        {
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact runtime requirement is not compatible with the ConstGenerics profile.");
        }
        if (manifest.RuntimeRequirement.Frameworks.Count != 1 || !string.Equals(manifest.RuntimeRequirement.Frameworks[0].Name, "Microsoft.NETCore.App", StringComparison.Ordinal) || !string.Equals(manifest.RuntimeRequirement.Frameworks[0].MinimumVersion, settings.FrameworkVersion, StringComparison.Ordinal))
        {
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact framework requirement is not compatible with the ConstGenerics profile.");
        }
        if (!Exact(manifest.MetadataFeatureTags, ConstGenericsProcessorProtocol.MetadataFeatureTag) || manifest.Metadata is null || !manifest.Metadata.TryGetValue("compatibilityGroup", out var compatibilityGroup) || !string.Equals(compatibilityGroup, ConstGenericsProcessorProtocol.CompatibilityGroup, StringComparison.Ordinal))
        {
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact does not carry the approved ConstGenerics metadata compatibility identity.");
        }
        if (manifest.Files.Count is 0 || manifest.Files.Count > settings.MaximumArtifactFiles || bundle.Entries.Count != manifest.Files.Count)
        {
            throw new ArtifactWorkerLimitExceededException("The artifact file count exceeds the configured limit.");
        }

        var files = manifest.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
        long totalSize = 0;
        foreach (var entry in bundle.Entries)
        {
            if (!files.TryGetValue(entry.Path, out var file) || file.Size != entry.Size || !string.Equals(file.Digest, entry.Digest, StringComparison.Ordinal) || !string.Equals(entry.Digest, entry.ContentRef.Value, StringComparison.Ordinal) || !string.Equals(file.Role, entry.Role, StringComparison.Ordinal))
            {
                throw new ArtifactWorkerIncompatibleArtifactException("The artifact bundle does not match its manifest.");
            }
            if (entry.Size < 0 || entry.Size > capabilityManifest.Limits.MaximumInputArtifactBytes - totalSize)
                throw new ArtifactWorkerLimitExceededException("The artifact exceeds the configured input limit.");
            totalSize += entry.Size;
        }

        var assembly = manifest.Files.FirstOrDefault(file => string.Equals(file.Path, manifest.EntryAssembly, StringComparison.Ordinal));
        if (assembly is null || assembly.Size is <= 0 || assembly.Size > settings.MaximumAssemblyBytes || Path.GetExtension(assembly.Path).ToLowerInvariant() is not (".dll" or ".exe"))
        {
            throw new ArtifactWorkerIncompatibleArtifactException("The artifact entry assembly is invalid or exceeds its limit.");
        }
        if (manifest.Files.Any(file => string.Equals(Path.GetExtension(file.Path), ".pdb", StringComparison.OrdinalIgnoreCase) && file.Size > settings.MaximumPortablePdbBytes))
        {
            throw new ArtifactWorkerLimitExceededException("The portable PDB exceeds its configured limit.");
        }
    }

    private async Task DownloadAndVerifyAsync(ArtifactRef artifactRef, ArtifactBundleEntry entry, ArtifactFileDescriptor manifestFile, string destination, CancellationToken cancellationToken)
    {
        await using var response = await storeClient.OpenArtifactFileReadAsync(artifactRef, entry.Path, cancellationToken).ConfigureAwait(false);
        if (response.Length is not null && response.Length != entry.Size)
            throw new ArtifactWorkerIncompatibleArtifactException("An artifact file Content-Length is invalid.");
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
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var actualDigest = Convert.ToHexStringLower(hash.GetHashAndReset());
        var expectedDigest = ArtifactStoreProtocol.GetDigest(entry.ContentRef);
        if (total != entry.Size || total != manifestFile.Size || !string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal) || !string.Equals(entry.Digest, manifestFile.Digest, StringComparison.Ordinal))
        {
            throw new ArtifactWorkerIncompatibleArtifactException("Artifact content failed integrity validation.");
        }
    }

    private static bool Exact(IReadOnlyList<string> values, string expected) =>
        values.Count == 1 && string.Equals(values[0], expected, StringComparison.Ordinal);

    private static bool ShouldMaterialize(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe" or ".winmd" or ".pdb";
}

internal static class ConstGenericsTemporaryDirectory
{
    public static string Create(string configuredRoot, string operationId)
    {
        var root = Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(root);
        if (operationId.Length is 0 or > 128 || !operationId.All(static value => char.IsAsciiLetterOrDigit(value) || value is '_' or '-'))
        {
            throw new ArtifactWorkerRequestException("invalid-operation", "The artifact operation ID is invalid.");
        }
        var path = ResolvePath(root, $"job-{operationId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string ResolvePath(string root, string relativePath)
    {
        var normalized = ArtifactPath.Normalize(relativePath);
        var fullRoot = Path.GetFullPath(root);
        var destination = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArtifactWorkerIncompatibleArtifactException("An artifact path escaped the job directory.");
        return destination;
    }

    public static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

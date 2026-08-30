using System.Buffers;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker;

internal static class ArtifactFormatContract
{
    public const string ManagedPe = "dotnet-managed-pe-v1";
    public const string NetFxManagedPe = "dotnet-framework-managed-pe-v1";
    public const string NetFxMixedPe = "dotnet-framework-mixed-pe-v1";
    public const string JSharpLanguage = "jsharp";
    public const string JSharpToolchain = "vjc-jsharp20";
    public const string JSharpReferenceSet = "jsharp20-ref";
    public const string JSharpTargetFramework = "net20";
    public const string JSharpRuntimeFeature = "runtime.jsharp20-wine";

    public static bool IsSupported(string artifactFormat) =>
        artifactFormat is ManagedPe or NetFxManagedPe or NetFxMixedPe;

    public static bool IsNetFx(string artifactFormat) =>
        artifactFormat is NetFxManagedPe or NetFxMixedPe;

    public static bool IsNetFxMixedPe(string artifactFormat) =>
        StringComparer.Ordinal.Equals(artifactFormat, NetFxMixedPe);

    public static bool IsJSharp(ArtifactManifest manifest) =>
        StringComparer.Ordinal.Equals(manifest.Producer.LanguageId, JSharpLanguage) ||
        StringComparer.Ordinal.Equals(manifest.Producer.ToolchainId, JSharpToolchain) ||
        StringComparer.Ordinal.Equals(manifest.ReferenceSetId, JSharpReferenceSet) ||
        manifest.RuntimeRequirement.RequiredRuntimeFeatureTags.Contains(JSharpRuntimeFeature, StringComparer.Ordinal);
}

internal sealed record NetFxManagedReferenceSetContract(string ReferenceSetId, string TargetFramework, string FrameworkVersion);

internal static class NetFxManagedReferenceSets
{
    public static IReadOnlyDictionary<string, NetFxManagedReferenceSetContract> ById { get; } =
        new[]
        {
            new NetFxManagedReferenceSetContract("netfx20-managed-ref", "net20", "2.0"),
            new NetFxManagedReferenceSetContract("netfx30-managed-ref", "net30", "3.0"),
            new NetFxManagedReferenceSetContract("netfx35-managed-ref", "net35", "3.5"),
            new NetFxManagedReferenceSetContract("netfx40-managed-ref", "net40", "4.0"),
            new NetFxManagedReferenceSetContract("netfx45-managed-ref", "net45", "4.5"),
            new NetFxManagedReferenceSetContract("netfx451-managed-ref", "net451", "4.5.1"),
            new NetFxManagedReferenceSetContract("netfx452-managed-ref", "net452", "4.5.2"),
            new NetFxManagedReferenceSetContract("netfx46-managed-ref", "net46", "4.6"),
            new NetFxManagedReferenceSetContract("netfx461-managed-ref", "net461", "4.6.1"),
            new NetFxManagedReferenceSetContract("netfx462-managed-ref", "net462", "4.6.2"),
            new NetFxManagedReferenceSetContract("netfx47-managed-ref", "net47", "4.7"),
            new NetFxManagedReferenceSetContract("netfx471-managed-ref", "net471", "4.7.1"),
            new NetFxManagedReferenceSetContract("netfx472-managed-ref", "net472", "4.7.2"),
            new NetFxManagedReferenceSetContract("netfx48-managed-ref", "net48", "4.8")
        }.ToDictionary(static item => item.ReferenceSetId, StringComparer.Ordinal);
}

internal sealed record MaterializedArtifact(
    string RootPath,
    string AssemblyPath,
    string? PortablePdbPath,
    ArtifactManifest Manifest,
    ArtifactReferenceSet? ReferenceSet,
    string LeaseToken,
    IArtifactStoreClient StoreClient) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StoreClient.ReleaseLeaseAsync(LeaseToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
        finally
        {
            TemporaryArtifactDirectory.Delete(RootPath);
        }
    }
}

internal sealed class ArtifactBundleMaterializer(IArtifactStoreClient storeClient, ArtifactWorkerSettings settings)
{
    public async Task<MaterializedArtifact> MaterializeAsync(ArtifactRef artifactRef, string operationId, CancellationToken cancellationToken)
    {
        var root = TemporaryArtifactDirectory.Create(settings.WorkRoot, operationId);
        string? leaseToken = null;
        try
        {
            var lease = await storeClient.AcquireLeaseAsync(artifactRef, $"artifacts-default:{operationId}", TimeSpan.FromMilliseconds(settings.Limits.MaxProcessorMilliseconds + 30_000), cancellationToken);
            leaseToken = lease.LeaseToken;
            var bundle = await storeClient.GetArtifactAsync(artifactRef, cancellationToken) ?? throw new ArtifactNotFoundException("The requested artifact was not found.");
            ValidateBundle(artifactRef, bundle, settings.Limits);

            var manifestEntries = bundle.Manifest.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
            string? assemblyPath = null;
            string? pdbPath = null;
            foreach (var entry in bundle.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ShouldMaterialize(entry.Path))
                    continue;
                var destination = TemporaryArtifactDirectory.ResolvePath(root, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await DownloadAndVerifyAsync(artifactRef, entry, destination, manifestEntries[entry.Path], cancellationToken);
                if (StringComparer.Ordinal.Equals(entry.Path, bundle.Manifest.EntryAssembly))
                    assemblyPath = destination;
                if (entry.Role == "portable-pdb" || Path.GetExtension(entry.Path).Equals(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    pdbPath ??= destination;
                }
            }

            if (assemblyPath is null)
                throw new ArtifactRequestValidationException("The artifact entry assembly is unavailable.");
            if (ArtifactFormatContract.IsJSharp(bundle.Manifest))
                ValidateMaterializedJSharpPe(bundle.Manifest, assemblyPath);
            settings.ReferenceSets.TryGetValue(bundle.Manifest.ReferenceSetId, out var referenceSet);
            return new MaterializedArtifact(root, assemblyPath, pdbPath, bundle.Manifest, referenceSet, leaseToken, storeClient);
        }
        catch
        {
            if (leaseToken is not null)
            {
                try
                {
                    await storeClient.ReleaseLeaseAsync(leaseToken, CancellationToken.None);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
            }
            TemporaryArtifactDirectory.Delete(root);
            throw;
        }
    }

    private async Task DownloadAndVerifyAsync(ArtifactRef artifactRef, ArtifactBundleEntry entry, string destination, ArtifactFileDescriptor manifestFile, CancellationToken cancellationToken)
    {
        await using var response = await storeClient.OpenArtifactFileReadAsync(artifactRef, entry.Path, cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await response.Content.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                total = checked(total + read);
                if (total > manifestFile.Size || total > settings.Limits.MaxArtifactBytes)
                    throw new ArtifactRequestValidationException("An artifact file exceeded its declared size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var actualDigest = Convert.ToHexStringLower(hash.GetHashAndReset());
        var expectedDigest = ArtifactStoreProtocol.GetDigest(entry.ContentRef);
        if (total != entry.Size || total != manifestFile.Size || !StringComparer.Ordinal.Equals(actualDigest, expectedDigest) || !StringComparer.Ordinal.Equals(entry.Digest, manifestFile.Digest))
        {
            throw new ArtifactRequestValidationException("Artifact content failed integrity validation.");
        }
    }

    private static bool ShouldMaterialize(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe" or ".winmd" or ".pdb";

    private static void ValidateBundle(ArtifactRef requestedRef, ArtifactBundleDescriptor bundle, ArtifactProcessorLimits limits)
    {
        try
        {
            ArtifactIdentity.Validate(bundle.Manifest);
        }
        catch (ArgumentException exception)
        {
            throw new ArtifactRequestValidationException($"The artifact manifest is invalid: {exception.Message}");
        }
        if (bundle.Manifest.ArtifactId != requestedRef)
            throw new ArtifactRequestValidationException("The artifact manifest identity does not match the request.");
        if (!ArtifactFormatContract.IsSupported(bundle.Manifest.ArtifactFormat))
            throw new ArtifactRequestValidationException("The artifact format is not supported by this processor.");
        if (bundle.Manifest.MetadataFeatureTags.Count != 0)
            throw new ArtifactRequestValidationException("The artifact requires a specialized metadata processor.");
        if (ArtifactFormatContract.IsNetFx(bundle.Manifest.ArtifactFormat))
            ValidateNetFxManifest(bundle.Manifest);
        if (bundle.Manifest.Files.Count is 0 || bundle.Manifest.Files.Count > limits.MaxArtifactFiles)
            throw new ArtifactRequestValidationException("The artifact file count exceeds the configured limit.");
        if (bundle.Entries.Count != bundle.Manifest.Files.Count)
            throw new ArtifactRequestValidationException("The artifact bundle is incomplete.");

        var manifestByPath = bundle.Manifest.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
        long totalSize = 0;
        foreach (var entry in bundle.Entries)
        {
            if (!manifestByPath.TryGetValue(entry.Path, out var manifestFile) || manifestFile.Size != entry.Size || !StringComparer.Ordinal.Equals(manifestFile.Digest, entry.Digest) || !StringComparer.Ordinal.Equals(entry.Digest, entry.ContentRef.Value) || !StringComparer.Ordinal.Equals(manifestFile.Role, entry.Role))
            {
                throw new ArtifactRequestValidationException("The artifact bundle does not match its manifest.");
            }
            if (entry.Size > limits.MaxArtifactBytes - totalSize)
                throw new ArtifactRequestValidationException("The artifact exceeds the configured size limit.");
            totalSize += entry.Size;
        }
        if (totalSize > limits.MaxArtifactBytes)
            throw new ArtifactRequestValidationException("The artifact exceeds the configured size limit.");

        var assembly = bundle.Manifest.Files.FirstOrDefault(file => StringComparer.Ordinal.Equals(file.Path, bundle.Manifest.EntryAssembly));
        if (assembly is null || assembly.Size > limits.MaxAssemblyBytes || Path.GetExtension(assembly.Path).ToLowerInvariant() is not (".dll" or ".exe"))
        {
            throw new ArtifactRequestValidationException("The artifact entry assembly is invalid or too large.");
        }
        if (bundle.Manifest.Files.Any(file => Path.GetExtension(file.Path).Equals(".pdb", StringComparison.OrdinalIgnoreCase) && file.Size > limits.MaxPortablePdbBytes))
        {
            throw new ArtifactRequestValidationException("The portable PDB exceeds the configured size limit.");
        }
    }

    private static void ValidateNetFxManifest(ArtifactManifest manifest)
    {
        var mixedPe = ArtifactFormatContract.IsNetFxMixedPe(manifest.ArtifactFormat);
        if (!mixedPe && ArtifactFormatContract.IsJSharp(manifest))
        {
            ValidateJSharpManifest(manifest);
            return;
        }

        var contract = mixedPe
            ? new NetFxManagedReferenceSetContract("netfx48-ref", "net48", "4.8") : NetFxManagedReferenceSets.ById.GetValueOrDefault(manifest.ReferenceSetId);
        if (contract is null ||
            !StringComparer.Ordinal.Equals(manifest.ReferenceSetId, contract.ReferenceSetId) ||
            !StringComparer.Ordinal.Equals(manifest.TargetFramework, contract.TargetFramework) ||
            !StringComparer.Ordinal.Equals(manifest.RuntimeRequirement.Family, "netfx-clr-wine") ||
            manifest.RuntimeRequirement.Frameworks.Count != 1 ||
            !StringComparer.Ordinal.Equals(manifest.RuntimeRequirement.Frameworks[0].Name, ".NETFramework") ||
            !StringComparer.Ordinal.Equals(manifest.RuntimeRequirement.Frameworks[0].MinimumVersion, contract.FrameworkVersion) ||
            !mixedPe && manifest.RuntimeRequirement.RequiredRuntimeFeatureTags.Count != 0)
        {
            throw new ArtifactRequestValidationException("The artifact does not match an approved exact .NET Framework contract.");
        }
        if (mixedPe && (!StringComparer.Ordinal.Equals(manifest.RuntimeRequirement.Architecture, "x64") || !Path.GetExtension(manifest.EntryAssembly).Equals(".exe", StringComparison.OrdinalIgnoreCase) || manifest.EntryPoint is not null || manifest.Metadata is null || !manifest.Metadata.TryGetValue("mixedMode", out var mixedMode) || !StringComparer.Ordinal.Equals(mixedMode, "true")))
        {
            throw new ArtifactRequestValidationException("The C++/CLI artifact does not match the approved x64 mixed-PE contract.");
        }
        if (!mixedPe)
        {
            var expectedExtension = manifest.OutputKind == BuildOutputKind.Library ? ".dll" : ".exe";
            var hasExpectedEntryPoint = manifest.OutputKind == BuildOutputKind.Library
                ? manifest.EntryPoint is null : !string.IsNullOrWhiteSpace(manifest.EntryPoint);
            if (manifest.RuntimeRequirement.Architecture is not ("anycpu" or "x64") || !Path.GetExtension(manifest.EntryAssembly).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase) || !hasExpectedEntryPoint)
            {
                throw new ArtifactRequestValidationException("The managed .NET Framework artifact does not match the approved application contract.");
            }
        }
    }

    private static void ValidateJSharpManifest(ArtifactManifest manifest)
    {
        if (!StringComparer.Ordinal.Equals(manifest.ArtifactFormat, ArtifactFormatContract.NetFxManagedPe) ||
            !StringComparer.Ordinal.Equals(manifest.Producer.LanguageId, ArtifactFormatContract.JSharpLanguage) ||
            !StringComparer.Ordinal.Equals(manifest.Producer.ToolchainId, ArtifactFormatContract.JSharpToolchain) ||
            !StringComparer.Ordinal.Equals(manifest.ReferenceSetId, ArtifactFormatContract.JSharpReferenceSet) ||
            !StringComparer.Ordinal.Equals(manifest.TargetFramework, ArtifactFormatContract.JSharpTargetFramework) ||
            !StringComparer.Ordinal.Equals(manifest.RuntimeRequirement.Family, "netfx-clr-wine") ||
            manifest.RuntimeRequirement.Frameworks.Count != 1 ||
            !StringComparer.Ordinal.Equals(manifest.RuntimeRequirement.Frameworks[0].Name, ".NETFramework") ||
            !StringComparer.Ordinal.Equals(manifest.RuntimeRequirement.Frameworks[0].MinimumVersion, "2.0") ||
            !StringComparer.Ordinal.Equals(manifest.RuntimeRequirement.Architecture, "x64") ||
            !manifest.RuntimeRequirement.RequiredRuntimeFeatureTags.SequenceEqual([ArtifactFormatContract.JSharpRuntimeFeature], StringComparer.Ordinal) ||
            manifest.OutputKind != BuildOutputKind.Console ||
            !Path.GetExtension(manifest.EntryAssembly).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(manifest.EntryPoint))
        {
            throw new ArtifactRequestValidationException("The J# artifact does not match the approved x64 CLR 2.0 managed-PE contract.");
        }
    }

    private static void ValidateMaterializedJSharpPe(ArtifactManifest manifest, string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            var headers = peReader.PEHeaders;
            if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus || headers.CorHeader is null || (headers.CorHeader.Flags & CorFlags.ILOnly) == 0 || (headers.CorHeader.Flags & CorFlags.NativeEntryPoint) != 0 || (headers.CorHeader.Flags & CorFlags.Requires32Bit) != 0 || (headers.CorHeader.Flags & CorFlags.Prefers32Bit) != 0 || !peReader.HasMetadata)
            {
                throw new BadImageFormatException();
            }

            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly || !StringComparer.Ordinal.Equals(metadata.MetadataVersion, "v2.0.50727"))
            {
                throw new BadImageFormatException();
            }

            var entryPointToken = headers.CorHeader.EntryPointTokenOrRelativeVirtualAddress;
            if (entryPointToken == 0)
                throw new BadImageFormatException();
            var entryPointHandle = MetadataTokens.EntityHandle(entryPointToken);
            if (entryPointHandle.Kind != HandleKind.MethodDefinition)
                throw new BadImageFormatException();
            var method = metadata.GetMethodDefinition((MethodDefinitionHandle)entryPointHandle);
            var declaringType = metadata.GetTypeDefinition(method.GetDeclaringType());
            var typeName = metadata.GetString(declaringType.Name);
            var typeNamespace = metadata.GetString(declaringType.Namespace);
            var methodName = metadata.GetString(method.Name);
            var actualEntryPoint = string.IsNullOrEmpty(typeNamespace)
                ? $"{typeName}::{methodName}" : $"{typeNamespace}.{typeName}::{methodName}";
            if (!StringComparer.Ordinal.Equals(manifest.EntryPoint, actualEntryPoint))
                throw new BadImageFormatException();
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or InvalidOperationException or ArgumentException)
        {
            throw new ArtifactRequestValidationException("The J# entry assembly is not an AMD64 PE32+ IL-only CLR 2.0 executable with the declared managed entry point.");
        }
    }
}

internal static class TemporaryArtifactDirectory
{
    private const int MaximumDeleteAttempts = 6;

    public static string Create(string configuredRoot, string operationId)
    {
        var root = Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(root);
        var safeOperation = operationId.All(static value => char.IsAsciiLetterOrDigit(value) || value is '_' or '-')
            ? operationId : throw new ArtifactRequestValidationException("The operation ID is invalid.");
        var path = ResolvePath(root, $"job-{safeOperation}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string ResolvePath(string root, string relativePath)
    {
        var normalized = ArtifactPath.Normalize(relativePath);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(rootWithSeparator, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new ArtifactRequestValidationException("An artifact path escaped the temporary root.");
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < MaximumDeleteAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }
    }
}

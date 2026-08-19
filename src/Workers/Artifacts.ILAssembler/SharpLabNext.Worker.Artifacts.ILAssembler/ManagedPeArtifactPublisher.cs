using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Artifacts.ILAssembler;

internal sealed record PublishedManagedPe(ArtifactRef ArtifactRef, ArtifactManifest Manifest);

internal sealed class ManagedPeArtifactPublisher(
    IArtifactStoreClient storeClient,
    IlAssemblerWorkerSettings settings)
{
    public async Task<PublishedManagedPe> PublishAsync(
        ValidatedCilArtifact source,
        byte[] peImage,
        TransformArtifactOptions options,
        CancellationToken cancellationToken)
    {
        var inspection = Inspect(peImage);
        var outputPath = OutputPath(source.EntryPath);
        var contentRef = ContentIdentity.Compute(peImage);
        var file = new ArtifactFileDescriptor(
            "primary-assembly",
            outputPath,
            peImage.LongLength,
            contentRef.Value);
        var metadata = source.Manifest.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source.Manifest.Metadata, StringComparer.Ordinal);
        metadata["sharplabnext.transform"] = "assemble-il";
        metadata["sharplabnext.assembler"] = "Mobius.ILasm";
        metadata["sharplabnext.assembler.version"] = settings.CompilerVersion;
        metadata["sharplabnext.source.artifact-format"] = source.Manifest.ArtifactFormat;
        metadata["sharplabnext.assembly.name"] = inspection.AssemblyName;

        var manifest = ArtifactIdentity.WithComputedId(source.Manifest with
        {
            ArtifactFormat = "dotnet-managed-pe-v1",
            RuntimeRequirement = new ArtifactRuntimeRequirement(
                source.ReferenceSet.RuntimeFamily,
                [new FrameworkRequirement(
                    source.ReferenceSet.FrameworkName,
                    source.ReferenceSet.FrameworkVersion)],
                source.ReferenceSet.Architecture,
                source.Manifest.RuntimeRequirement.RequiredRuntimeFeatureTags),
            MetadataFeatureTags = [],
            EntryAssembly = outputPath,
            EntryPoint = inspection.EntryPoint,
            Files = [file],
            Derivation = new ArtifactDerivation(
                source.ArtifactRef,
                "il-assembler",
                settings.CompilerVersion,
                ComputeOptionsDigest(options)),
            Metadata = metadata
        });

        await using var content = new MemoryStream(peImage, writable: false);
        PutArtifactResponse stored;
        try
        {
            stored = await storeClient.PutArtifactAsync(
                manifest,
                [new ArtifactFileUpload(file.Path, content, peImage.LongLength)],
                settings.ArtifactTimeToLive,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArtifactStoreHttpException or HttpRequestException or TaskCanceledException)
        {
            throw new ArtifactWorkerDependencyUnavailableException(
                "The assembled artifact could not be published to the Artifact Store.",
                exception);
        }
        if (stored.ArtifactRef != manifest.ArtifactId)
        {
            throw new ArtifactWorkerDependencyUnavailableException(
                "Artifact Store returned an unexpected assembled artifact identity.");
        }
        return new PublishedManagedPe(stored.ArtifactRef, manifest);
    }

    private static PeInspection Inspect(byte[] peImage)
    {
        try
        {
            using var stream = new MemoryStream(peImage, writable: false);
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!reader.HasMetadata || reader.PEHeaders.CorHeader is null)
                throw new BadImageFormatException("The assembled image has no managed metadata.");
            var metadata = reader.GetMetadataReader();
            if (!metadata.IsAssembly)
                throw new BadImageFormatException("The assembled image is not an assembly.");
            var assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            string? entryPoint = null;
            var token = reader.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress;
            if (token != 0 && (reader.PEHeaders.CorHeader.Flags & CorFlags.NativeEntryPoint) == 0)
            {
                var handle = MetadataTokens.EntityHandle(token);
                if (handle.Kind != HandleKind.MethodDefinition)
                    throw new BadImageFormatException("The managed entry point is invalid.");
                var method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                var type = metadata.GetTypeDefinition(method.GetDeclaringType());
                var typeName = metadata.GetString(type.Name);
                var typeNamespace = metadata.GetString(type.Namespace);
                var methodName = metadata.GetString(method.Name);
                entryPoint = string.IsNullOrEmpty(typeNamespace)
                    ? $"{typeName}::{methodName}"
                    : $"{typeNamespace}.{typeName}::{methodName}";
            }
            return new PeInspection(Limit(assemblyName, 256), LimitNullable(entryPoint, 512));
        }
        catch (BadImageFormatException exception)
        {
            throw new ArtifactWorkerProcessorException(
                "The isolated IL compiler produced an invalid managed PE.",
                exception);
        }
    }

    private static string OutputPath(string inputPath)
    {
        var separator = inputPath.LastIndexOf('/');
        var directory = separator >= 0 ? inputPath[..(separator + 1)] : string.Empty;
        var fileName = separator >= 0 ? inputPath[(separator + 1)..] : inputPath;
        var extension = fileName.LastIndexOf('.');
        var stem = extension > 0 ? fileName[..extension] : fileName;
        return ArtifactPath.Normalize($"{directory}{stem}.dll");
    }

    private static string ComputeOptionsDigest(TransformArtifactOptions options)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(options, ContractJson.CreateCanonicalSerializerOptions());
        return ContentIdentity.Compute(bytes).Value;
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static string? LimitNullable(string? value, int maximum) =>
        value is null || value.Length <= maximum ? value : value[..maximum];

    private sealed record PeInspection(string AssemblyName, string? EntryPoint);
}

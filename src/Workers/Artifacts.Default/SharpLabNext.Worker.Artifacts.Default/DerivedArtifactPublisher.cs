using System.Security.Cryptography;
using System.Text.Json;
using SharpLabNext.ArtifactProcessing.Protocol;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker;

internal sealed record PublishedDerivedArtifact(
    ArtifactRef ArtifactRef,
    string ArtifactFormat,
    bool RewriteApplied,
    int InstrumentationPointCount,
    string? PublicMessage);

internal sealed class DerivedArtifactPublisher(
    IArtifactStoreClient storeClient,
    ArtifactWorkerSettings settings)
{
    public async Task<PublishedDerivedArtifact> PublishRuntimeInstrumentationAsync(
        MaterializedArtifact source,
        ProcessorRunResult processor,
        TransformArtifactOptions options,
        CancellationToken cancellationToken)
    {
        if (processor.Response.Outcome != ProcessorOutcome.Succeeded ||
            processor.Response.RewriteApplied is null ||
            processor.Response.InstrumentationPointCount is null ||
            !File.Exists(processor.OutputPath))
        {
            throw new ArtifactProcessorCrashedException(
                "The isolated instrumentation processor returned an invalid result.");
        }

        var rewrittenSources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in source.Manifest.Files)
        {
            var materialized = TemporaryArtifactDirectory.ResolvePath(source.RootPath, file.Path);
            if (StringComparer.Ordinal.Equals(file.Path, source.Manifest.EntryAssembly))
            {
                rewrittenSources.Add(file.Path, processor.OutputPath);
            }
            else if (source.PortablePdbPath is not null &&
                     StringComparer.Ordinal.Equals(materialized, source.PortablePdbPath))
            {
                if (processor.PortablePdbOutputPath is null || !File.Exists(processor.PortablePdbOutputPath))
                {
                    throw new ArtifactProcessorCrashedException(
                        "The instrumentation processor did not preserve the portable PDB.");
                }
                rewrittenSources.Add(file.Path, processor.PortablePdbOutputPath);
            }
            else
            {
                if (!File.Exists(materialized))
                    throw new ArtifactProcessorCrashedException("A derived artifact input file is unavailable.");
                rewrittenSources.Add(file.Path, materialized);
            }
        }

        var files = new List<ArtifactFileDescriptor>(source.Manifest.Files.Count);
        foreach (var file in source.Manifest.Files)
        {
            await using var content = OpenRead(rewrittenSources[file.Path]);
            var contentRef = await ContentIdentity.ComputeAsync(content, cancellationToken);
            files.Add(file with
            {
                Size = content.Length,
                Digest = contentRef.Value
            });
        }

        var optionsDigest = ComputeOptionsDigest(options);
        var metadata = source.Manifest.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source.Manifest.Metadata, StringComparer.Ordinal);
        metadata["sharplabnext.instrumentation.transform"] = "runtime-instrumentation-v1";
        metadata["sharplabnext.instrumentation.profile"] = ProcessorProtocol.RuntimeInstrumentationProfileId;
        metadata["sharplabnext.instrumentation.applied"] =
            processor.Response.RewriteApplied.Value ? "true" : "false";
        metadata["sharplabnext.instrumentation.points"] =
            processor.Response.InstrumentationPointCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var manifest = ArtifactIdentity.WithComputedId(source.Manifest with
        {
            Files = files,
            Derivation = new ArtifactDerivation(
                source.Manifest.ArtifactId,
                settings.Identity.ProcessorId,
                ProcessorProtocol.RuntimeInstrumentationVersion,
                optionsDigest),
            Metadata = metadata
        });

        var uploads = rewrittenSources
            .Select(pair => new ArtifactFileUpload(
                pair.Key,
                OpenRead(pair.Value),
                new FileInfo(pair.Value).Length))
            .ToArray();
        try
        {
            var stored = await storeClient.PutArtifactAsync(
                manifest,
                uploads,
                TimeSpan.FromHours(1),
                cancellationToken);
            if (stored.ArtifactRef != manifest.ArtifactId)
            {
                throw new ArtifactStoreUnavailableException(
                    "Artifact Store returned an unexpected derived artifact identity.",
                    new InvalidDataException("Derived artifact identity mismatch."));
            }
        }
        finally
        {
            foreach (var upload in uploads)
                upload.Content.Dispose();
        }

        return new PublishedDerivedArtifact(
            manifest.ArtifactId,
            manifest.ArtifactFormat,
            processor.Response.RewriteApplied.Value,
            processor.Response.InstrumentationPointCount.Value,
            processor.Response.PublicMessage);
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static string ComputeOptionsDigest(TransformArtifactOptions options)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(options, ContractJson.CreateCanonicalSerializerOptions());
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }
}

using System.Text;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Artifacts.JSIL;

internal sealed class JsilArtifactHandler(IJsilArtifactMaterializer materializer, IJsilProcessRunner runner, IArtifactStoreClient storeClient, JsilWorkerSettings settings, ArtifactWorkerCapabilityManifest capabilityManifest) : IArtifactRenderHandler
{
    public string OutputId => "javascript";

    public async Task<ArtifactWorkerJobExecution> RenderAsync(RenderArtifactRequest request, string operationId, CancellationToken cancellationToken)
    {
        await using var artifact = await materializer.MaterializeAsync(request.ArtifactRef, operationId, cancellationToken).ConfigureAwait(false);
        var maximumCharacters = Math.Min(request.Options.MaxCharacters, capabilityManifest.Limits.MaximumOutputArtifactBytes);
        var translation = await runner.TranslateAsync(artifact, maximumCharacters, request.DeadlineUtc, cancellationToken).ConfigureAwait(false);
        if (!translation.Succeeded || translation.JavaScript is null)
        {
            return new ArtifactWorkerJobExecution(new RenderArtifactResult(ArtifactJobOutcome.UnsupportedArtifact, null, "text/plain; charset=utf-8", [], [Diagnostic("jsil-translation-unsupported", translation.PublicMessage ?? "JSIL could not translate this managed assembly.", translation.Detail)], ProcessorIdentity()));
        }

        var bytes = Encoding.UTF8.GetBytes(translation.JavaScript);
        var contentRef = ContentIdentity.Compute(bytes);
        await using var content = new MemoryStream(bytes, writable: false);
        var stored = await storeClient.PutContentAsync(contentRef, content, bytes.LongLength, settings.ArtifactTimeToLive, cancellationToken).ConfigureAwait(false);
        if (stored.ContentRef != contentRef || stored.Size != bytes.LongLength)
            throw new ArtifactWorkerDependencyUnavailableException("Artifact Store returned an unexpected JavaScript content identity.");

        const string mediaType = "text/javascript; charset=utf-8";
        return new ArtifactWorkerJobExecution(new RenderArtifactResult(ArtifactJobOutcome.Succeeded, stored.ContentRef, mediaType, [], [], ProcessorIdentity()), new ArtifactWorkerProducedContent(stored.ContentRef, mediaType, stored.Size));
    }

    private ArtifactProcessorIdentity ProcessorIdentity() => new(settings.ReleaseId, capabilityManifest.WorkerId, $"{settings.Version}+{settings.Commit[..12]}", settings.WorkerImageId);

    private static Diagnostic Diagnostic(string code, string message, string? detail)
    {
        var combined = detail is null ? message : $"{message} {detail}";
        return new Diagnostic("jsil", code, DiagnosticSeverity.Error, Limit(combined, 4_096), null, null, [], [], 0, 0);
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}

using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.IL.Compiler;

namespace SharpLabNext.Worker.Artifacts.ILAssembler;

internal sealed class IlAssemblerArtifactHandler(CilArtifactReader reader, IlCompilerProcessRunner compiler, ManagedPeArtifactPublisher publisher, IArtifactStoreClient storeClient, IlAssemblerWorkerSettings settings, ArtifactWorkerCapabilityManifest capabilityManifest) : IArtifactTransformHandler, IArtifactRenderHandler
{
    public string TransformId => "assemble-il";

    public string OutputId => "generated-il";

    public async Task<ArtifactWorkerJobExecution> TransformAsync(TransformArtifactRequest request, string operationId, CancellationToken cancellationToken)
    {
        if (request.Options.RewriterProfileId is not null)
        {
            throw new ArtifactWorkerRequestException("invalid-request", "assemble-il does not accept a rewriter profile.");
        }

        await using var source = await reader.ReadAsync(request.ArtifactRef, operationId, cancellationToken).ConfigureAwait(false);
        var compilation = await compiler.AssembleAsync(source, source.Manifest.OutputKind, cancellationToken).ConfigureAwait(false);
        var diagnostics = MapDiagnostics(compilation.Diagnostics, source);
        if (!compilation.Succeeded)
        {
            if (diagnostics.Length == 0)
            {
                diagnostics =
                [
                    Diagnostic("ILASM999", DiagnosticSeverity.Error, "The isolated IL assembler rejected the generated CIL.", source.EntryPath, null)
                ];
            }
            return new ArtifactWorkerJobExecution(new TransformArtifactResult(ArtifactJobOutcome.InvalidArtifact, null, source.ArtifactRef, null, diagnostics, ProcessorIdentity()));
        }

        var published = await publisher.PublishAsync(source, compilation.PeImage, request.Options, cancellationToken).ConfigureAwait(false);
        return new ArtifactWorkerJobExecution(new TransformArtifactResult(ArtifactJobOutcome.Succeeded, published.ArtifactRef, source.ArtifactRef, "dotnet-managed-pe-v1", diagnostics, ProcessorIdentity()), Artifact: new ArtifactWorkerProducedArtifact(published.ArtifactRef, "dotnet-managed-pe-v1", "assembled-managed-pe"));
    }

    public async Task<ArtifactWorkerJobExecution> RenderAsync(RenderArtifactRequest request, string operationId, CancellationToken cancellationToken)
    {
        await using var source = await reader.ReadAsync(request.ArtifactRef, operationId, cancellationToken).ConfigureAwait(false);
        var maximumCharacters = Math.Min(request.Options.MaxCharacters, capabilityManifest.Limits.MaximumInputArtifactBytes);
        if (source.SourceText.Length > maximumCharacters)
        {
            return new ArtifactWorkerJobExecution(new RenderArtifactResult(ArtifactJobOutcome.LimitExceeded, null, "text/plain; charset=utf-8", [], [Diagnostic("generated-il-limit-exceeded", DiagnosticSeverity.Error, "Generated IL exceeds the requested character limit.", source.EntryPath, null)], ProcessorIdentity()));
        }

        var contentRef = ContentIdentity.Compute(source.Utf8Content);
        await using var content = new MemoryStream(source.Utf8Content, writable: false);
        PutContentResponse stored;
        try
        {
            stored = await storeClient.PutContentAsync(contentRef, content, source.Utf8Content.LongLength, settings.ArtifactTimeToLive, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArtifactStoreHttpException or HttpRequestException or TaskCanceledException)
        {
            throw new ArtifactWorkerDependencyUnavailableException("Generated IL could not be published to the Artifact Store.", exception);
        }
        if (stored.ContentRef != contentRef || stored.Size != source.Utf8Content.LongLength)
        {
            throw new ArtifactWorkerDependencyUnavailableException("Artifact Store returned an unexpected generated IL content identity.");
        }
        return new ArtifactWorkerJobExecution(new RenderArtifactResult(ArtifactJobOutcome.Succeeded, stored.ContentRef, "text/plain; charset=utf-8", [], [], ProcessorIdentity()), new ArtifactWorkerProducedContent(stored.ContentRef, "text/plain; charset=utf-8", stored.Size));
    }

    private ArtifactProcessorIdentity ProcessorIdentity() => new(settings.ReleaseId, capabilityManifest.WorkerId, settings.CompilerVersion, settings.WorkerImageId);

    private static Diagnostic[] MapDiagnostics(IReadOnlyList<IlCompilerDiagnostic> diagnostics, ValidatedCilArtifact source)
    {
        var lines = source.SourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return diagnostics.Take(IlCompilerProtocol.MaxDiagnostics).Select(item =>
        {
            TextRange? range = null;
            if (item.StartLine is not null && item.StartCharacter is not null)
            {
                var startLine = Math.Clamp(item.StartLine.Value, 0, Math.Max(0, lines.Length - 1));
                var endLine = Math.Clamp(item.EndLine ?? startLine, startLine, Math.Max(startLine, lines.Length - 1));
                var startCharacter = Math.Clamp(item.StartCharacter.Value, 0, lines[startLine].Length);
                var endCharacter = Math.Clamp(item.EndCharacter ?? startCharacter + 1, 0, lines[endLine].Length);
                if (endLine == startLine && endCharacter < startCharacter)
                    endCharacter = startCharacter;
                range = new TextRange(startLine, startCharacter, endLine, endCharacter);
            }
            return Diagnostic(
                item.Code,
                item.Severity switch
                {
                    IlCompilerDiagnosticSeverity.Information => DiagnosticSeverity.Information,
                    IlCompilerDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                    _ => DiagnosticSeverity.Error
                },
                item.Message,
                source.EntryPath,
                range);
        }).ToArray();
    }

    private static Diagnostic Diagnostic(string code, DiagnosticSeverity severity, string message, string? path, TextRange? range) => new("mobius-ilasm", Limit(code, 64), severity, Limit(message.Replace('\r', ' ').Replace('\n', ' '), 8_192), path, range, [], [], 0, 0);

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}

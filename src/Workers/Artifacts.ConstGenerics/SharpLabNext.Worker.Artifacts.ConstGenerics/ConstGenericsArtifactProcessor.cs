using System.Text;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Artifacts.ConstGenerics.Protocol;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics;

internal sealed class ConstGenericsArtifactProcessor(ConstGenericsArtifactMaterializer materializer, ConstGenericsProcessorRunner processorRunner, IArtifactStoreClient storeClient, ConstGenericsArtifactWorkerSettings settings, ArtifactWorkerCapabilityManifest capabilityManifest)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<ArtifactWorkerJobExecution> RenderAsync(RenderArtifactRequest request, string operationId, ConstGenericsProcessorOperation operation, CancellationToken cancellationToken)
    {
        await using var artifact = await materializer.MaterializeAsync(request.ArtifactRef, operationId, cancellationToken).ConfigureAwait(false);
        var processed = await processorRunner.RunAsync(artifact, operation, request.Options.IncludeSequencePoints, request.Options.IncludeCompilerGeneratedMembers, includeMetadataTokens: true, Math.Min(request.Options.MaxCharacters, capabilityManifest.Limits.MaximumOutputArtifactBytes), ConstGenericsProcessorProtocol.MaximumFindings, cancellationToken).ConfigureAwait(false);
        var response = processed.Response;
        if (response.Outcome != ConstGenericsProcessorOutcome.Succeeded)
        {
            return new ArtifactWorkerJobExecution(new RenderArtifactResult(MapOutcome(response), null, response.MediaType, [], [Diagnostic(response)], ProcessorIdentity(response.ProcessorVersion)));
        }

        var content = await ReadOutputAsync(processed.OutputPath, request.Options.MaxCharacters, cancellationToken).ConfigureAwait(false);
        var contentRef = ContentIdentity.Compute(content);
        await using var stream = new MemoryStream(content, writable: false);
        PutContentResponse stored;
        try
        {
            stored = await storeClient.PutContentAsync(contentRef, stream, content.LongLength, settings.ArtifactTimeToLive, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArtifactStoreHttpException or HttpRequestException or TaskCanceledException)
        {
            throw new ArtifactWorkerDependencyUnavailableException("Artifact output could not be published to the Artifact Store.", exception);
        }
        if (stored.ContentRef != contentRef || stored.Size != content.LongLength)
        {
            throw new ArtifactWorkerDependencyUnavailableException("Artifact Store returned an unexpected output identity.");
        }

        var ranges = response.LinkedRanges.Take(ConstGenericsProcessorProtocol.MaximumLinkedRanges).Select(MapRange).ToArray();
        return new ArtifactWorkerJobExecution(new RenderArtifactResult(ArtifactJobOutcome.Succeeded, stored.ContentRef, response.MediaType, ranges, [], ProcessorIdentity(response.ProcessorVersion)), new ArtifactWorkerProducedContent(stored.ContentRef, response.MediaType, stored.Size));
    }

    public async Task<ArtifactWorkerJobExecution> VerifyAsync(VerifyArtifactRequest request, string operationId, CancellationToken cancellationToken)
    {
        await using var artifact = await materializer.MaterializeAsync(request.ArtifactRef, operationId, cancellationToken).ConfigureAwait(false);
        var processed = await processorRunner.RunAsync(artifact, ConstGenericsProcessorOperation.Verify, includeSequencePoints: false, includeCompilerGeneratedMembers: true, request.Options.IncludeMetadataTokens, maxCharacters: 1, request.Options.MaxFindings, cancellationToken).ConfigureAwait(false);
        var response = processed.Response;
        var outcome = response.Outcome switch
        {
            ConstGenericsProcessorOutcome.Succeeded => ArtifactVerificationOutcome.Valid,
            ConstGenericsProcessorOutcome.Findings => ArtifactVerificationOutcome.Findings,
            ConstGenericsProcessorOutcome.InvalidArtifact => ArtifactVerificationOutcome.InvalidArtifact,
            ConstGenericsProcessorOutcome.LimitExceeded => ArtifactVerificationOutcome.LimitExceeded,
            _ => throw new ArtifactWorkerProcessorException("The isolated verifier failed.")
        };
        var findings = response.Findings.Take(Math.Min(request.Options.MaxFindings, ConstGenericsProcessorProtocol.MaximumFindings)).Select(MapFinding).ToArray();
        if (response.PublicMessage is not null && findings.Length == 0 && outcome != ArtifactVerificationOutcome.Valid)
        {
            findings =
            [
                new VerificationFinding("const-generics-verifier", Limit(response.PublicMessage, 4_096), null, null, null, null, null)
            ];
        }
        return new ArtifactWorkerJobExecution(new VerifyArtifactResult(outcome, findings, response.ProcessorId, response.ProcessorVersion, ProcessorIdentity(response.ProcessorVersion)));
    }

    private ArtifactProcessorIdentity ProcessorIdentity(string version) => new(settings.ReleaseId, capabilityManifest.WorkerId, version, settings.WorkerImageId);

    private static async Task<byte[]> ReadOutputAsync(string path, int maximumCharacters, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0)
            throw new ArtifactWorkerProcessorException("The isolated processor did not produce output.");
        var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArtifactWorkerProcessorException("The isolated processor output was not UTF-8.", exception);
        }
        if (text.Length > maximumCharacters)
            throw new ArtifactWorkerLimitExceededException("The artifact output exceeded its character limit.");
        return content;
    }

    private static ArtifactJobOutcome MapOutcome(ConstGenericsProcessorResponse response) => response.Outcome switch
    {
        ConstGenericsProcessorOutcome.InvalidArtifact => ArtifactJobOutcome.InvalidArtifact,
        ConstGenericsProcessorOutcome.LimitExceeded => ArtifactJobOutcome.LimitExceeded,
        ConstGenericsProcessorOutcome.Failed => throw new ArtifactWorkerProcessorException(response.PublicMessage ?? "The isolated artifact processor failed."),
        _ => ArtifactJobOutcome.UnsupportedArtifact
    };

    private static Diagnostic Diagnostic(ConstGenericsProcessorResponse response) => new(response.ProcessorId, response.Outcome.ToString().ToLowerInvariant(), response.Outcome == ConstGenericsProcessorOutcome.LimitExceeded ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, Limit(response.PublicMessage ?? "The ConstGenerics artifact could not be processed.", 4_096), null, null, [], [], 0, 0);

    private static LinkedRange MapRange(ConstGenericsProcessorLinkedRange range) => new(range.SourceFilePath is null ? null : Limit(range.SourceFilePath, 512), range.SourceRange is null ? null : MapTextRange(range.SourceRange), MapTextRange(range.OutputRange));

    private static VerificationFinding MapFinding(ConstGenericsProcessorFinding finding) => new(Limit(finding.Code, 128), Limit(finding.Message, 4_096), finding.TypeName is null ? null : Limit(finding.TypeName, 1_024), finding.MethodName is null ? null : Limit(finding.MethodName, 1_024), finding.MetadataToken, finding.FilePath is null ? null : Limit(finding.FilePath, 512), finding.Range is null ? null : MapTextRange(finding.Range));

    private static TextRange MapTextRange(ConstGenericsProcessorTextRange range) => new(Math.Max(0, range.StartLine), Math.Max(0, range.StartCharacter), Math.Max(Math.Max(0, range.StartLine), range.EndLine), Math.Max(0, range.EndCharacter));

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}

internal sealed class ConstGenericsIlRenderHandler(ConstGenericsArtifactProcessor processor) : IArtifactRenderHandler
{
    public string OutputId => "il";

    public Task<ArtifactWorkerJobExecution> RenderAsync(RenderArtifactRequest request, string operationId, CancellationToken cancellationToken) => processor.RenderAsync(request, operationId, ConstGenericsProcessorOperation.Il, cancellationToken);
}

internal sealed class ConstGenericsCSharpRenderHandler(ConstGenericsArtifactProcessor processor) : IArtifactRenderHandler
{
    public string OutputId => "decompiled-csharp";

    public Task<ArtifactWorkerJobExecution> RenderAsync(RenderArtifactRequest request, string operationId, CancellationToken cancellationToken) => processor.RenderAsync(request, operationId, ConstGenericsProcessorOperation.DecompiledCSharp, cancellationToken);
}

internal sealed class ConstGenericsVerificationHandler(ConstGenericsArtifactProcessor processor) : IArtifactVerificationHandler
{
    public string VerificationProfileId => "il-verify";

    public Task<ArtifactWorkerJobExecution> VerifyAsync(VerifyArtifactRequest request, string operationId, CancellationToken cancellationToken) => processor.VerifyAsync(request, operationId, cancellationToken);
}

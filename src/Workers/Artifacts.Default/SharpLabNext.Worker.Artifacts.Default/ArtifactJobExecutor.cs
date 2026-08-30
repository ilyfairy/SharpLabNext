using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLabNext.ArtifactProcessing.Protocol;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker;

internal sealed class ArtifactJobExecutor(ArtifactBundleMaterializer materializer, IArtifactProcessorRunner processorRunner, IArtifactStoreClient storeClient, ArtifactWorkerSettings settings) : IArtifactJobExecutor, IDisposable
{
    private readonly SemaphoreSlim _concurrency = new(settings.Limits.MaxConcurrentJobs);
    private readonly ConcurrentDictionary<string, ArtifactJobExecution> _completedCache = new(StringComparer.Ordinal);
    private readonly DerivedArtifactPublisher _derivedPublisher = new(storeClient, settings);

    public async Task<ArtifactJobExecution> TransformAsync(TransformArtifactRequest request, string operationId, CancellationToken cancellationToken)
    {
        ValidateCommon(request.RequestId, request.IdempotencyKey, request.PipelineResolutionId, request.ProcessorId, request.DeadlineUtc);
        var identityTransform = StringComparer.Ordinal.Equals(request.TransformId, "identity");
        if (!identityTransform && (!StringComparer.Ordinal.Equals(request.TransformId, "runtime-instrumentation-v1") || !StringComparer.Ordinal.Equals(request.Options.RewriterProfileId, ProcessorProtocol.RuntimeInstrumentationProfileId)))
        {
            return new ArtifactJobExecution(new TransformArtifactResult(ArtifactJobOutcome.UnsupportedArtifact, null, request.ArtifactRef, null, [Diagnostic("unsupported-transform", "The requested artifact transform is not supported.")]));
        }
        if (!identityTransform && (!request.Options.PreservePortablePdb || !request.Options.PreserveSequencePoints))
        {
            throw new ArtifactRequestValidationException("Runtime instrumentation must preserve portable PDB sequence points.");
        }

        var cacheKey = CacheKey("transform", request.ArtifactRef, request.TransformId, request.Options);
        if (await TryGetCachedAsync(cacheKey, cancellationToken) is { } cached)
            return cached;

        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            if (await TryGetCachedAsync(cacheKey, cancellationToken) is { } cachedWithinLock)
                return cachedWithinLock;
            ArtifactJobExecution execution;
            try
            {
                await using var artifact = await materializer.MaterializeAsync(request.ArtifactRef, operationId, cancellationToken);
                if (identityTransform)
                {
                    execution = new ArtifactJobExecution(new TransformArtifactResult(ArtifactJobOutcome.Succeeded, request.ArtifactRef, request.ArtifactRef, artifact.Manifest.ArtifactFormat, []));
                }
                else if (ArtifactFormatContract.IsNetFxMixedPe(artifact.Manifest.ArtifactFormat))
                {
                    execution = UnsupportedTransform(request, "C++/CLI mixed PE artifacts cannot be rewritten or instrumented.");
                }
                else if (ArtifactFormatContract.IsJSharp(artifact.Manifest))
                {
                    execution = UnsupportedTransform(request, "J# CLR 2.0 artifacts cannot be rewritten or instrumented.");
                }
                else
                {
                    var processor = await processorRunner.RunAsync(artifact, ProcessorOperation.RuntimeInstrumentationV1, includeSequencePoints: true, includeCompilerGeneratedMembers: true, includeMetadataTokens: false, settings.Limits.MaxOutputCharacters, settings.Limits.MaxFindings, request.DeadlineUtc, cancellationToken, request.Options.RewriterProfileId);
                    execution = await CreateTransformExecutionAsync(request, artifact, processor, cancellationToken);
                }
            }
            catch (ArtifactRequestValidationException exception)
            {
                execution = InvalidTransform(request, exception.Message);
            }
            catch (ArtifactStoreHttpException exception)
            {
                throw new ArtifactStoreUnavailableException("The Artifact Store is unavailable.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new ArtifactStoreUnavailableException("The Artifact Store is unavailable.", exception);
            }

            Cache(cacheKey, execution);
            return execution;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public async Task<ArtifactJobExecution> RenderAsync(RenderArtifactRequest request, string operationId, CancellationToken cancellationToken)
    {
        ValidateCommon(request.RequestId, request.IdempotencyKey, request.PipelineResolutionId, request.ProcessorId, request.DeadlineUtc);
        var operation = request.OutputId switch
        {
            "il" or "run-il" => ProcessorOperation.Il,
            "decompiled-csharp" => ProcessorOperation.DecompiledCSharp,
            _ => throw new ArtifactRequestValidationException("The requested artifact output is not supported.")
        };
        if (request.Options.MaxCharacters <= 0)
            throw new ArtifactRequestValidationException("MaxCharacters must be positive.");
        var maxCharacters = Math.Min(request.Options.MaxCharacters, settings.Limits.MaxOutputCharacters);
        var cacheKey = CacheKey("render", request.ArtifactRef, request.OutputId, request.Options);
        if (await TryGetCachedAsync(cacheKey, cancellationToken) is { } cached)
            return cached;

        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            if (await TryGetCachedAsync(cacheKey, cancellationToken) is { } cachedWithinLock)
                return cachedWithinLock;
            ArtifactJobExecution execution;
            try
            {
                await using var artifact = await materializer.MaterializeAsync(request.ArtifactRef, operationId, cancellationToken);
                if (ArtifactFormatContract.IsNetFxMixedPe(artifact.Manifest.ArtifactFormat) && request.OutputId is not ("il" or "decompiled-csharp"))
                {
                    execution = UnsupportedRender("C++/CLI mixed PE artifacts support only IL and Decompiled C# rendering.");
                }
                else if (ArtifactFormatContract.IsJSharp(artifact.Manifest) && request.OutputId is not ("il" or "decompiled-csharp"))
                {
                    execution = UnsupportedRender("J# CLR 2.0 artifacts support only IL and Decompiled C# rendering.");
                }
                else
                {
                    var processor = await processorRunner.RunAsync(artifact, operation, request.Options.IncludeSequencePoints, request.Options.IncludeCompilerGeneratedMembers, includeMetadataTokens: true, maxCharacters, settings.Limits.MaxFindings, request.DeadlineUtc, cancellationToken);
                    execution = await CreateRenderExecutionAsync(processor, cancellationToken);
                }
            }
            catch (ArtifactRequestValidationException exception)
            {
                execution = InvalidRender(exception.Message);
            }
            catch (ArtifactStoreHttpException exception)
            {
                throw new ArtifactStoreUnavailableException("The Artifact Store is unavailable.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new ArtifactStoreUnavailableException("The Artifact Store is unavailable.", exception);
            }

            Cache(cacheKey, execution);
            return execution;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public async Task<ArtifactJobExecution> VerifyAsync(VerifyArtifactRequest request, string operationId, CancellationToken cancellationToken)
    {
        ValidateCommon(request.RequestId, request.IdempotencyKey, request.PipelineResolutionId, request.ProcessorId, request.DeadlineUtc);
        if (!settings.VerificationProfiles.Contains(request.Options.VerificationProfileId))
            throw new ArtifactRequestValidationException("The verification profile is not allowed.");
        if (request.Options.MaxFindings <= 0)
            throw new ArtifactRequestValidationException("MaxFindings must be positive.");
        var maxFindings = Math.Min(request.Options.MaxFindings, settings.Limits.MaxFindings);
        var cacheKey = CacheKey("verify", request.ArtifactRef, request.Options.VerificationProfileId, request.Options);
        if (await TryGetCachedAsync(cacheKey, cancellationToken) is { } cached)
            return cached;

        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            if (await TryGetCachedAsync(cacheKey, cancellationToken) is { } cachedWithinLock)
                return cachedWithinLock;
            ArtifactJobExecution execution;
            try
            {
                await using var artifact = await materializer.MaterializeAsync(request.ArtifactRef, operationId, cancellationToken);
                if (ArtifactFormatContract.IsNetFxMixedPe(artifact.Manifest.ArtifactFormat))
                {
                    execution = new ArtifactJobExecution(new VerifyArtifactResult(ArtifactVerificationOutcome.UnsupportedArtifact, [new VerificationFinding("mixed-pe-verification-unsupported", "IL verification is not supported for C++/CLI mixed PE artifacts.", null, null, null, null, null)], "microsoft-ilverification", settings.Identity.IlVerificationVersion));
                }
                else if (ArtifactFormatContract.IsJSharp(artifact.Manifest))
                {
                    execution = new ArtifactJobExecution(new VerifyArtifactResult(ArtifactVerificationOutcome.UnsupportedArtifact, [new VerificationFinding("jsharp20-verification-unsupported", "IL verification is not supported for J# CLR 2.0 artifacts.", null, null, null, null, null)], "microsoft-ilverification", settings.Identity.IlVerificationVersion));
                }
                else if (artifact.ReferenceSet is null || artifact.ReferenceSet.Paths.Count == 0)
                {
                    execution = new ArtifactJobExecution(new VerifyArtifactResult(ArtifactVerificationOutcome.UnsupportedArtifact, [new VerificationFinding("reference-set-unavailable", "The verification reference set is unavailable.", null, null, null, null, null)], "microsoft-ilverification", settings.Identity.IlVerificationVersion));
                }
                else
                {
                    var processor = await processorRunner.RunAsync(artifact, ProcessorOperation.Verify, includeSequencePoints: false, includeCompilerGeneratedMembers: true, request.Options.IncludeMetadataTokens, settings.Limits.MaxOutputCharacters, maxFindings, request.DeadlineUtc, cancellationToken);
                    execution = CreateVerifyExecution(processor.Response);
                }
            }
            catch (ArtifactRequestValidationException)
            {
                execution = InvalidVerification();
            }
            catch (ArtifactStoreHttpException exception)
            {
                throw new ArtifactStoreUnavailableException("The Artifact Store is unavailable.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new ArtifactStoreUnavailableException("The Artifact Store is unavailable.", exception);
            }

            Cache(cacheKey, execution);
            return execution;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void Dispose() => _concurrency.Dispose();

    private async Task<ArtifactJobExecution> CreateTransformExecutionAsync(TransformArtifactRequest request, MaterializedArtifact artifact, ProcessorRunResult processor, CancellationToken cancellationToken)
    {
        var response = processor.Response;
        if (response.Outcome == ProcessorOutcome.Failed)
            throw new ArtifactProcessorCrashedException("The isolated artifact rewriter failed.");
        if (response.Outcome == ProcessorOutcome.InvalidArtifact)
            return InvalidTransform(request, response.PublicMessage ?? "The managed PE is invalid.");
        if (response.Outcome == ProcessorOutcome.LimitExceeded)
        {
            return new ArtifactJobExecution(new TransformArtifactResult(ArtifactJobOutcome.LimitExceeded, null, request.ArtifactRef, null, [Diagnostic("processor-limit-exceeded", response.PublicMessage ?? "Artifact rewriting exceeded a limit.")]));
        }

        var published = await _derivedPublisher.PublishRuntimeInstrumentationAsync(artifact, processor, request.Options, cancellationToken);
        var diagnostics = published.PublicMessage is null
            ? Array.Empty<Diagnostic>() : [Diagnostic("rewrite-skipped", published.PublicMessage, DiagnosticSeverity.Warning)];
        return new ArtifactJobExecution(new TransformArtifactResult(ArtifactJobOutcome.Succeeded, published.ArtifactRef, request.ArtifactRef, published.ArtifactFormat, diagnostics), Artifact: new ProducedArtifact(published.ArtifactRef, published.ArtifactFormat, "runtime-instrumented"));
    }

    private async Task<ArtifactJobExecution> CreateRenderExecutionAsync(ProcessorRunResult processor, CancellationToken cancellationToken)
    {
        var response = processor.Response;
        if (response.Outcome == ProcessorOutcome.Failed)
            throw new ArtifactProcessorCrashedException("The isolated artifact processor failed.");
        if (response.Outcome == ProcessorOutcome.InvalidArtifact)
            return InvalidRender(response.PublicMessage ?? "The managed PE is invalid.");
        if (response.Outcome == ProcessorOutcome.LimitExceeded)
        {
            return new ArtifactJobExecution(new RenderArtifactResult(ArtifactJobOutcome.LimitExceeded, null, response.MediaType, MapLinkedRanges(response.LinkedRanges), [Diagnostic("processor-limit-exceeded", response.PublicMessage ?? "Artifact processing exceeded a limit.")]));
        }
        if (response.Outcome != ProcessorOutcome.Succeeded || !File.Exists(processor.OutputPath))
            throw new ArtifactProcessorCrashedException("The isolated artifact processor returned an invalid result.");

        await using var content = new FileStream(processor.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var contentRef = await ContentIdentity.ComputeAsync(content, cancellationToken);
        content.Position = 0;
        var stored = await storeClient.PutContentAsync(contentRef, content, content.Length, TimeSpan.FromHours(1), cancellationToken);
        var result = new RenderArtifactResult(ArtifactJobOutcome.Succeeded, stored.ContentRef, response.MediaType, MapLinkedRanges(response.LinkedRanges), []);
        return new ArtifactJobExecution(result, new ProducedContent(stored.ContentRef, response.MediaType, stored.Size));
    }

    private static ArtifactJobExecution CreateVerifyExecution(ProcessorResponse response)
    {
        if (response.Outcome == ProcessorOutcome.Failed)
            throw new ArtifactProcessorCrashedException("The isolated verifier failed.");
        var outcome = response.Outcome switch
        {
            ProcessorOutcome.Succeeded => ArtifactVerificationOutcome.Valid,
            ProcessorOutcome.Findings => ArtifactVerificationOutcome.Findings,
            ProcessorOutcome.InvalidArtifact => ArtifactVerificationOutcome.InvalidArtifact,
            ProcessorOutcome.LimitExceeded => ArtifactVerificationOutcome.LimitExceeded,
            _ => ArtifactVerificationOutcome.InvalidArtifact
        };
        return new ArtifactJobExecution(new VerifyArtifactResult(outcome, response.Findings.Select(MapFinding).ToArray(), response.ProcessorId, response.ProcessorVersion));
    }

    private static ArtifactJobExecution InvalidRender(string message) => new(new RenderArtifactResult(ArtifactJobOutcome.InvalidArtifact, null, "text/plain", [], [Diagnostic("invalid-artifact", message)]));

    private static ArtifactJobExecution UnsupportedRender(string message) => new(new RenderArtifactResult(ArtifactJobOutcome.UnsupportedArtifact, null, "text/plain", [], [Diagnostic("unsupported-artifact", message)]));

    private static ArtifactJobExecution InvalidTransform(TransformArtifactRequest request, string message) => new(new TransformArtifactResult(ArtifactJobOutcome.InvalidArtifact, null, request.ArtifactRef, null, [Diagnostic("invalid-artifact", message)]));

    private static ArtifactJobExecution UnsupportedTransform(TransformArtifactRequest request, string message) => new(new TransformArtifactResult(ArtifactJobOutcome.UnsupportedArtifact, null, request.ArtifactRef, null, [Diagnostic("unsupported-artifact", message)]));

    private ArtifactJobExecution InvalidVerification() => new(new VerifyArtifactResult(ArtifactVerificationOutcome.InvalidArtifact, [], "microsoft-ilverification", settings.Identity.IlVerificationVersion));

    private static LinkedRange[] MapLinkedRanges(IReadOnlyList<ProcessorLinkedRange> ranges) => ranges.Select(range => new LinkedRange(range.SourceFilePath, MapRange(range.SourceRange), MapRange(range.OutputRange)!)).ToArray();

    private static VerificationFinding MapFinding(ProcessorFinding finding) => new(finding.Code, finding.Message, finding.TypeName, finding.MethodName, finding.MetadataToken, finding.FilePath, MapRange(finding.Range));

    private static TextRange? MapRange(ProcessorTextRange? range) => range is null
        ? null : new TextRange(range.StartLine, range.StartCharacter, range.EndLine, range.EndCharacter);

    private static Diagnostic Diagnostic(string code, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) => new("artifacts-default", code, severity, Sanitize(message), null, null, [], [], 0, 0);

    private static string Sanitize(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= 1_024 ? singleLine : singleLine[..1_024];
    }

    private void ValidateCommon(string requestId, string idempotencyKey, string pipelineResolutionId, string processorId, DateTimeOffset deadlineUtc)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
            throw new ArtifactRequestValidationException("RequestId is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
            throw new ArtifactRequestValidationException("IdempotencyKey is required.");
        if (string.IsNullOrWhiteSpace(pipelineResolutionId) || pipelineResolutionId.Length > 256)
            throw new ArtifactRequestValidationException("PipelineResolutionId is required.");
        if (!StringComparer.Ordinal.Equals(processorId, settings.Identity.ProcessorId))
            throw new ArtifactRequestValidationException("The request targets a different artifact processor.");
        if (deadlineUtc <= DateTimeOffset.UtcNow)
            throw new ArtifactRequestValidationException("The request deadline has elapsed.");
    }

    private static string CacheKey(string operation, ArtifactRef artifactRef, string id, object options)
    {
        var optionBytes = JsonSerializer.SerializeToUtf8Bytes(options, ContractJson.CreateCanonicalSerializerOptions());
        var optionsDigest = Convert.ToHexStringLower(SHA256.HashData(optionBytes));
        return $"{operation}|{artifactRef.Value}|{id}|{optionsDigest}";
    }

    private async Task<ArtifactJobExecution?> TryGetCachedAsync(string key, CancellationToken cancellationToken)
    {
        if (!_completedCache.TryGetValue(key, out var cached))
            return null;

        try
        {
            if (cached.Content is { } content)
                await using (await storeClient.OpenContentReadAsync(content.ContentRef, cancellationToken)) { }

            if (cached.Artifact is { } artifact &&
                await storeClient.GetArtifactAsync(artifact.ArtifactRef, cancellationToken) is null)
            {
                RemoveCached(key, cached);
                return null;
            }

            return cached;
        }
        catch (ArtifactStoreHttpException exception) when (exception.StatusCodeValue == HttpStatusCode.NotFound)
        {
            RemoveCached(key, cached);
            return null;
        }
        catch (ArtifactStoreHttpException exception)
        {
            throw new ArtifactStoreUnavailableException("The Artifact Store is unavailable.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ArtifactStoreUnavailableException("The Artifact Store is unavailable.", exception);
        }
    }

    private void RemoveCached(string key, ArtifactJobExecution cached) =>
        ((ICollection<KeyValuePair<string, ArtifactJobExecution>>)_completedCache).Remove(new KeyValuePair<string, ArtifactJobExecution>(key, cached));

    private void Cache(string key, ArtifactJobExecution execution)
    {
        var cacheable = execution.Result switch
        {
            TransformArtifactResult { Outcome: ArtifactJobOutcome.Succeeded } => true,
            RenderArtifactResult { Outcome: ArtifactJobOutcome.Succeeded } => true,
            VerifyArtifactResult { Outcome: ArtifactVerificationOutcome.Valid or ArtifactVerificationOutcome.Findings } => true,
            _ => false
        };
        if (!cacheable)
            return;
        if (_completedCache.Count >= settings.Limits.MaxRetainedOperations)
            _completedCache.Clear();
        _completedCache.TryAdd(key, execution);
    }
}

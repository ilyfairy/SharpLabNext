using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Client;

public interface IToolchainWorkerClient
{
    Task<WorkerDescriptor> DescribeAsync(CancellationToken cancellationToken = default);

    Task<ToolchainBuildResponse> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default);

    Task<ToolchainExplainResponse> ExplainAsync(ExplainRequest request, CancellationToken cancellationToken = default);
}

public sealed class ToolchainWorkerClient(HttpClient httpClient, ToolchainWorkerClientSettings settings) : IToolchainWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    public async Task<WorkerDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/worker/describe");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var descriptor = await response.Content.ReadFromJsonAsync<WorkerDescriptor>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw ProtocolFailure("Worker describe response was empty.");
        ValidateDescriptor(descriptor);
        return descriptor;
    }

    public async Task<ToolchainBuildResponse> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = await DescribeAsync(cancellationToken).ConfigureAwait(false);
        ValidateCapability(descriptor, request);
        ValidateRequestedReferenceSet(descriptor, request.ReferenceSetId);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/build")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<ToolchainBuildResponse>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw ProtocolFailure("Worker build response was empty.", descriptor.WorkerImageId);
        ValidateBuildResponse(request, result, descriptor.WorkerImageId);
        return result;
    }

    public async Task<ToolchainExplainResponse> ExplainAsync(ExplainRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = await DescribeAsync(cancellationToken).ConfigureAwait(false);
        ValidateCapability(descriptor, "explain");
        ValidateRequestedReferenceSet(descriptor, request.Workspace.ReferenceSetId);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/explain")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<ToolchainExplainResponse>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw ProtocolFailure("Worker explain response was empty.", descriptor.WorkerImageId);
        if (!StringComparer.Ordinal.Equals(request.RequestId, result.RequestId))
            throw ProtocolFailure("Worker explain response request ID did not match the request.", descriptor.WorkerImageId);
        if (result.Result.Identity is null)
            throw ProtocolFailure("Worker explain response omitted its build identity.", descriptor.WorkerImageId);
        ValidateExplainIdentity(request, result.Result.Identity, descriptor, descriptor.WorkerImageId);
        if (!StringComparer.Ordinal.Equals(request.Workspace.LanguageId, result.Result.Document.LanguageId) || !StringComparer.Ordinal.Equals(result.Result.Identity.ToolchainId, result.Result.Document.ToolchainId) || request.Workspace.Revision != result.Result.Document.WorkspaceRevision || request.Workspace.SelectionRevision != result.Result.Document.SelectionRevision)
        {
            throw ProtocolFailure("Worker explain response identity did not match the request.", descriptor.WorkerImageId);
        }
        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Failure("worker-unavailable", WorkerErrorCategory.Unavailable, "The toolchain worker is unavailable.", retryable: true, safeToRetry: true, traceId: "worker-client", settings.ExpectedWorkerImageId ?? "unknown", null, exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        try
        {
            throw await CreateHttpFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<ToolchainWorkerException> CreateHttpFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null;
        string? detail = null;
        string? traceId = null;
        string? workerId = null;
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                body,
                new JsonDocumentOptions { MaxDepth = 16 },
                cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            code = GetString(root, "Code") ?? GetString(root, "Title");
            detail = GetString(root, "Detail");
            traceId = GetString(root, "TraceId");
            workerId = GetString(root, "WorkerId");
        }
        catch (JsonException) { }

        var statusCode = (int)response.StatusCode;
        var category = statusCode switch
        {
            StatusCodes.BadRequest => WorkerErrorCategory.InvalidArgument,
            StatusCodes.NotFound => WorkerErrorCategory.NotFound,
            StatusCodes.RequestTimeout => WorkerErrorCategory.DeadlineExceeded,
            StatusCodes.PayloadTooLarge => WorkerErrorCategory.ResourceExhausted,
            499 => WorkerErrorCategory.Cancelled,
            StatusCodes.ServiceUnavailable => WorkerErrorCategory.Unavailable,
            _ => WorkerErrorCategory.Internal
        };
        var retryable = category is WorkerErrorCategory.DeadlineExceeded
            or WorkerErrorCategory.Unavailable
            or WorkerErrorCategory.Internal;
        return Failure(code ?? $"worker-http-{statusCode}", category, detail ?? PublicMessage(category), retryable, safeToRetry: retryable, traceId ?? "worker-client", settings.ExpectedWorkerImageId ?? "unknown", statusCode, innerException: null, workerId ?? settings.WorkerId);
    }

    private void ValidateDescriptor(WorkerDescriptor descriptor)
    {
        if (!string.Equals(descriptor.Service.Id, settings.WorkerId, StringComparison.Ordinal) || descriptor.Service.Kind != ServiceKind.ToolchainWorker || descriptor.WorkerKind != WorkerKind.Toolchain)
        {
            throw ProtocolFailure("The endpoint did not describe the expected toolchain worker.", descriptor.WorkerImageId);
        }

        if (!string.Equals(descriptor.Service.ReleaseId, settings.ExpectedReleaseId, StringComparison.Ordinal))
        {
            throw ProtocolFailure("The toolchain worker release does not match the Gateway release.", descriptor.WorkerImageId);
        }

        if (settings.ExpectedWorkerImageId is not null && !string.Equals(descriptor.WorkerImageId, settings.ExpectedWorkerImageId, StringComparison.Ordinal))
        {
            throw ProtocolFailure("The toolchain worker image identity is not approved.", descriptor.WorkerImageId);
        }

        if (descriptor.NegotiatedProtocol.Major != ProtocolVersion.WorkerV1.Major || descriptor.Service.Protocol.Major != ProtocolVersion.WorkerV1.Major || !descriptor.SupportedProtocolVersions.Any(version => version.Major == ProtocolVersion.WorkerV1.Major))
        {
            throw Failure("worker-protocol-incompatible", WorkerErrorCategory.UnsupportedCapability, "The toolchain worker protocol is incompatible with the Gateway.", retryable: false, safeToRetry: false, traceId: "worker-client", descriptor.WorkerImageId);
        }

        ValidateReferenceSetAttestations(descriptor);
    }

    private void ValidateReferenceSetAttestations(WorkerDescriptor descriptor)
    {
        if (settings.ExpectedReferenceSetDigests is not { Count: > 0 } expected)
            return;
        if (descriptor.ReferenceSets is null)
            throw ProtocolFailure("The toolchain worker omitted reference-set attestations.", descriptor.WorkerImageId);

        var attestations = new Dictionary<string, ReferenceSetAttestation>(StringComparer.Ordinal);
        foreach (var attestation in descriptor.ReferenceSets)
        {
            if (string.IsNullOrWhiteSpace(attestation.Id) || !attestations.TryAdd(attestation.Id, attestation) || string.IsNullOrWhiteSpace(attestation.TargetFramework) || !IsSha256(attestation.ContentDigest) || attestation.Provenance is null || string.IsNullOrWhiteSpace(attestation.Provenance.Kind) || string.IsNullOrWhiteSpace(attestation.Provenance.ResolvedVersion))
            {
                throw ProtocolFailure("The toolchain worker reported an invalid reference-set attestation.", descriptor.WorkerImageId);
            }
        }

        foreach (var pair in expected)
        {
            if (!attestations.TryGetValue(pair.Key, out var attestation) || !string.Equals(attestation.Digest, pair.Value, StringComparison.Ordinal))
            {
                throw ProtocolFailure($"The toolchain worker reference set '{pair.Key}' does not match the active release lock.", descriptor.WorkerImageId);
            }
        }
    }

    private void ValidateRequestedReferenceSet(WorkerDescriptor descriptor, string referenceSetId)
    {
        if (settings.ExpectedReferenceSetDigests is not { Count: > 0 } expected)
            return;
        if (!expected.ContainsKey(referenceSetId) || descriptor.ReferenceSets?.Any(item => string.Equals(item.Id, referenceSetId, StringComparison.Ordinal) && string.Equals(item.Digest, expected[referenceSetId], StringComparison.Ordinal)) != true)
        {
            throw ProtocolFailure("The requested reference set is not attested for this toolchain worker.", descriptor.WorkerImageId);
        }
    }

    private static bool IsSha256(string? value)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;
        foreach (var character in value.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private void ValidateCapability(WorkerDescriptor descriptor, BuildRequest request)
    {
        if (request.Target == BuildTarget.Artifact)
        {
            ValidateAnyCapability(descriptor, ["artifact", "managed-pe"], request.ToolchainId);
        }
        else
        {
            var requiredCapability = request.Target switch
            {
                BuildTarget.CompileCheck => "compile-check",
                BuildTarget.Ast => "ast",
                BuildTarget.GeneratedSource => "generated-source",
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
            ValidateCapability(descriptor, requiredCapability, request.ToolchainId);
        }

        if (!descriptor.ProfileIds.Contains(request.ToolchainId, StringComparer.Ordinal))
        {
            throw ProtocolFailure("The toolchain worker does not host the requested toolchain profile.", descriptor.WorkerImageId);
        }
    }

    private void ValidateAnyCapability(WorkerDescriptor descriptor, IReadOnlyList<string> acceptedCapabilities, string toolchainId)
    {
        if (descriptor.Capabilities.Any(capability => capability.Available && acceptedCapabilities.Contains(capability.Id, StringComparer.Ordinal) && capability.ProfileIds.Contains(toolchainId, StringComparer.Ordinal)))
        {
            return;
        }
        throw Failure("worker-capability-unavailable", WorkerErrorCategory.UnsupportedCapability, $"The toolchain worker does not provide any required capability: {string.Join(", ", acceptedCapabilities)}.", retryable: false, safeToRetry: false, traceId: "worker-client", descriptor.WorkerImageId);
    }

    private void ValidateCapability(WorkerDescriptor descriptor, string requiredCapability, string? toolchainId = null)
    {
        var available = descriptor.Capabilities.Any(item => string.Equals(item.Id, requiredCapability, StringComparison.Ordinal) && item.Available && (toolchainId is null || item.ProfileIds.Contains(toolchainId, StringComparer.Ordinal)));
        if (!available)
        {
            throw Failure("worker-capability-unavailable", WorkerErrorCategory.UnsupportedCapability, $"The toolchain worker does not provide the required '{requiredCapability}' capability.", retryable: false, safeToRetry: false, traceId: "worker-client", descriptor.WorkerImageId);
        }

    }

    private void ValidateBuildResponse(BuildRequest request, ToolchainBuildResponse response, string workerImageId)
    {
        if (!string.Equals(request.RequestId, response.RequestId, StringComparison.Ordinal))
        {
            throw ProtocolFailure("Worker build response request ID did not match the request.", workerImageId);
        }

        var resultMatchesTarget = request.Target switch
        {
            BuildTarget.Artifact => response.Result is BuildResult,
            BuildTarget.CompileCheck => response.Result is CompilationCheckResult,
            BuildTarget.Ast => response.Result is AstResult,
            BuildTarget.GeneratedSource => response.Result is GeneratedSourceResult,
            _ => false
        };
        if (!resultMatchesTarget)
        {
            throw ProtocolFailure("Worker build response type did not match the requested target.", workerImageId);
        }

        switch (response.Result)
        {
            case BuildResult build:
                ValidateBuildIdentity(request, build.Identity, build.WorkspaceRevision, build.SelectionRevision, workerImageId);
                break;
            case CompilationCheckResult check:
                ValidateBuildIdentity(request, check.Identity, check.WorkspaceRevision, check.SelectionRevision, workerImageId);
                break;
            case GeneratedSourceResult generated:
                ValidateBuildIdentity(request, generated.Identity, generated.WorkspaceRevision, generated.SelectionRevision, workerImageId);
                break;
            case AstResult { Identity: null }:
                throw ProtocolFailure("Worker AST result omitted its build identity.", workerImageId);
            case AstResult ast:
                ValidateBuildIdentity(request, ast.Identity, ast.Document.WorkspaceRevision, request.Workspace.SelectionRevision, workerImageId);
                if (!string.Equals(ast.Document.LanguageId, request.Workspace.LanguageId, StringComparison.Ordinal) || !string.Equals(ast.Document.ToolchainId, request.ToolchainId, StringComparison.Ordinal) || ast.Document.WorkspaceRevision != request.Workspace.Revision)
                {
                    throw ProtocolFailure("Worker AST result identity did not match the request.", workerImageId);
                }
                break;
        }

        if (response.Result is BuildResult { Outcome: BuildOutcome.Succeeded, ArtifactRef: null })
        {
            throw ProtocolFailure("A successful artifact build did not include an artifact reference.", workerImageId);
        }

        if (response.Result is BuildResult { Outcome: not BuildOutcome.Succeeded } unsuccessful &&
            (unsuccessful.ArtifactRef is not null || response.DevelopmentArtifact is not null))
        {
            throw ProtocolFailure("An unsuccessful artifact build unexpectedly returned an artifact.", workerImageId);
        }

        if (request.Target != BuildTarget.Artifact && response.DevelopmentArtifact is not null)
        {
            throw ProtocolFailure("A non-artifact build unexpectedly returned artifact bytes.", workerImageId);
        }

        if (response.Result is BuildResult { Outcome: BuildOutcome.Succeeded, ArtifactRef: { } artifactRef } &&
            response.DevelopmentArtifact is { } envelope &&
            (envelope.ArtifactRef != artifactRef || envelope.Manifest.ArtifactId != artifactRef))
        {
            throw ProtocolFailure("The development artifact envelope identity did not match the build result.", workerImageId);
        }
    }

    private void ValidateBuildIdentity(BuildRequest request, BuildIdentity identity, long workspaceRevision, long selectionRevision, string workerImageId)
    {
        if (!string.Equals(identity.ReleaseId, settings.ExpectedReleaseId, StringComparison.Ordinal) ||
            !string.Equals(identity.LanguageId, request.Workspace.LanguageId, StringComparison.Ordinal) ||
            !string.Equals(identity.ToolchainId, request.ToolchainId, StringComparison.Ordinal) ||
            !string.Equals(identity.ReferenceSetId, request.ReferenceSetId, StringComparison.Ordinal) ||
            !string.Equals(identity.WorkerImageId, workerImageId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(identity.CompilerVersion) ||
            workspaceRevision != request.Workspace.Revision ||
            selectionRevision != request.Workspace.SelectionRevision)
        {
            throw ProtocolFailure("Worker build result identity did not match the request.", workerImageId);
        }
    }

    private void ValidateExplainIdentity(ExplainRequest request, BuildIdentity identity, WorkerDescriptor descriptor, string workerImageId)
    {
        var supportsExplainProfile = descriptor.Capabilities.Any(item => string.Equals(item.Id, "explain", StringComparison.Ordinal) && item.Available && item.ProfileIds.Contains(identity.ToolchainId, StringComparer.Ordinal));
        if (!string.Equals(identity.ReleaseId, settings.ExpectedReleaseId, StringComparison.Ordinal) ||
            !string.Equals(identity.LanguageId, request.Workspace.LanguageId, StringComparison.Ordinal) ||
            !descriptor.ProfileIds.Contains(identity.ToolchainId, StringComparer.Ordinal) ||
            !supportsExplainProfile ||
            !string.Equals(identity.ReferenceSetId, request.Workspace.ReferenceSetId, StringComparison.Ordinal) ||
            !string.Equals(identity.WorkerImageId, workerImageId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(identity.CompilerVersion))
        {
            throw ProtocolFailure("Worker explain result identity did not match the request.", workerImageId);
        }
    }

    private ToolchainWorkerException ProtocolFailure(string message, string? workerImageId = null) =>
        Failure("worker-protocol-invalid", WorkerErrorCategory.Internal, message, retryable: false, safeToRetry: false, traceId: "worker-client", workerImageId ?? settings.ExpectedWorkerImageId ?? "unknown");

    private ToolchainWorkerException Failure(string code, WorkerErrorCategory category, string publicMessage, bool retryable, bool safeToRetry, string traceId, string workerImageId, int? statusCode = null, Exception? innerException = null, string? workerId = null) =>
        new(new WorkerError(code, category, publicMessage, retryable, safeToRetry, traceId, workerId ?? settings.WorkerId, workerImageId), statusCode, innerException);

    private static string? GetString(JsonElement root, string propertyName) => ContractJson.GetString(root, propertyName);

    private static string PublicMessage(WorkerErrorCategory category) => category switch
    {
        WorkerErrorCategory.InvalidArgument => "The toolchain worker rejected the build request.",
        WorkerErrorCategory.NotFound => "The requested toolchain resource was not found.",
        WorkerErrorCategory.DeadlineExceeded => "The toolchain build deadline elapsed.",
        WorkerErrorCategory.ResourceExhausted => "The toolchain build exceeded a configured limit.",
        WorkerErrorCategory.Cancelled => "The toolchain build was cancelled.",
        WorkerErrorCategory.Unavailable => "The toolchain worker is unavailable.",
        _ => "The toolchain worker failed to process the build."
    };

    private static class StatusCodes
    {
        public const int BadRequest = (int)HttpStatusCode.BadRequest;
        public const int NotFound = (int)HttpStatusCode.NotFound;
        public const int RequestTimeout = (int)HttpStatusCode.RequestTimeout;
        public const int PayloadTooLarge = (int)HttpStatusCode.RequestEntityTooLarge;
        public const int ServiceUnavailable = (int)HttpStatusCode.ServiceUnavailable;
    }
}

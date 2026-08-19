using System.Diagnostics;
using Microsoft.Extensions.Options;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.Operations;
using SharpLabNext.RuntimeProfile.Sdk;
using SharpLabNext.RuntimeProtocol;

namespace SharpLabNext.RuntimeSupervisor;

public static class RuntimePerformanceScenarios
{
    public const string Run = "run";
    public const string Jit = "jit";
    public const string Mapping = "mapping";
    public const string NotApplicableMapping = "not-applicable";
}

public sealed record RuntimePerformanceSampleRequest(
    string RuntimeProfileId,
    string PlanSha256,
    ArtifactRef ArtifactRef,
    string SecurityPolicyId,
    string Scenario,
    string? MethodFilter = null);

public sealed record RuntimePerformanceImageIdentity(
    string Reference,
    string ImageId,
    long SizeBytes);

public sealed record RuntimePerformanceSampleEnvironment(
    string RunnerId,
    string OperatingSystem,
    string Architecture,
    long NanoCpus,
    long MemoryLimitBytes);

public sealed record RuntimePerformanceSampleValue(
    double LatencyMilliseconds,
    long PeakMemoryBytes);

public sealed record RuntimePerformanceSampleResponse(
    string ProfileId,
    string Scenario,
    string OperationId,
    RuntimePerformanceImageIdentity Image,
    IReadOnlyList<string> Capabilities,
    string SourceMappingKind,
    RuntimePerformanceSampleEnvironment Environment,
    RuntimePerformanceSampleValue Sample,
    int ResourceSampleCount,
    int DistinctSequencePointRangeCount,
    DateTimeOffset CompletedAtUtc);

internal sealed record RuntimeJobMeasurementCompletion(
    bool ExecutionStarted,
    string? FailureCode,
    string? FailureMessage,
    TimeSpan Latency,
    RuntimeContainerResourceUsage? ResourceUsage,
    OperationResult? Result,
    bool CleanupSucceeded,
    RuntimeJobAudit? Audit);

internal sealed record RuntimeObservedException(
    string TypeName,
    string Message,
    string? StackTrace,
    RuntimeObservedException? InnerException);

internal sealed record RuntimeJitEvidenceRange(
    int IlOffset,
    int NativeStartOffset,
    int NativeEndOffset,
    string Document,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

internal sealed record RuntimeJitAuditMethod(
    string MetadataToken,
    string DisplayName,
    int NativeCodeBytes,
    int InstructionCount,
    IReadOnlyList<RuntimeJitEvidenceRange> EvidenceRanges,
    string MappingSource);

internal sealed record RuntimeJobAudit(
    string ContainerId,
    IReadOnlyList<string> Command,
    string Implementation,
    string EntryAssemblyPath,
    string EntryAssemblySha256,
    int RuntimeFrameCount,
    IReadOnlyDictionary<string, int> FrameKinds,
    byte[] Stdout,
    byte[] Stderr,
    IReadOnlyList<RuntimeInspectionPayload> InspectionPayloads,
    IReadOnlyList<RuntimeFlowPayload> FlowPayloads,
    RuntimeObservedException? Exception,
    string? TerminalStatus,
    int? TerminalExitCode,
    IReadOnlyList<RuntimeJitAuditMethod> JitMethods,
    bool ContainerRemoved,
    bool ProcessTreeRemoved);

internal sealed class RuntimeJobMeasurementRegistration(bool collectResources = true)
{
    private readonly Lock _gate = new();
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private readonly TaskCompletionSource<RuntimeJobMeasurementCompletion> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _containerStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _cancellationRegistration;
    private bool _executionStarted;
    private bool _completed;

    public Task<RuntimeJobMeasurementCompletion> Completion => _completion.Task;

    public Task ContainerStarted => _containerStarted.Task;

    public bool CollectResources { get; } = collectResources;

    public void BindCancellation(CancellationToken cancellationToken)
    {
        var registration = cancellationToken.Register(
            static state => ((RuntimeJobMeasurementRegistration)state!).CancelBeforeExecution(),
            this);
        lock (_gate)
        {
            if (!_completed)
            {
                _cancellationRegistration = registration;
                return;
            }
        }
        registration.Dispose();
    }

    public void MarkExecutionStarted()
    {
        lock (_gate)
        {
            if (!_completed)
                _executionStarted = true;
        }
    }

    public void MarkContainerStarted() => _containerStarted.TrySetResult();

    public void Reject(string code, string message) =>
        CompleteCore(code, message, resourceUsage: null, result: null, cleanupSucceeded: false, audit: null);

    public void Complete(
        string? failureCode,
        string? failureMessage,
        RuntimeContainerResourceUsage? resourceUsage,
        OperationResult? result,
        bool cleanupSucceeded,
        RuntimeJobAudit? audit) =>
        CompleteCore(failureCode, failureMessage, resourceUsage, result, cleanupSucceeded, audit);

    private void CancelBeforeExecution()
    {
        lock (_gate)
        {
            if (_completed || _executionStarted)
                return;
        }

        CompleteCore(
            "operation-cancelled-before-execution",
            "The preflight operation was cancelled before execution started.",
            resourceUsage: null,
            result: null,
            cleanupSucceeded: false,
            audit: null);
    }

    private void CompleteCore(
        string? failureCode,
        string? failureMessage,
        RuntimeContainerResourceUsage? resourceUsage,
        OperationResult? result,
        bool cleanupSucceeded,
        RuntimeJobAudit? audit)
    {
        RuntimeJobMeasurementCompletion completion;
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            completion = new RuntimeJobMeasurementCompletion(
                _executionStarted,
                failureCode,
                failureMessage,
                Stopwatch.GetElapsedTime(_startedTimestamp),
                resourceUsage,
                result,
                cleanupSucceeded,
                audit);
        }

        _containerStarted.TrySetCanceled();
        _cancellationRegistration.Dispose();
        _completion.TrySetResult(completion);
    }
}

public sealed class RuntimePerformancePreflightCoordinator(
    OperationStore operations,
    RuntimeJobExecutor executor,
    IDockerEngineClient docker,
    IOptions<RuntimeSupervisorOptions> configuredOptions)
{
    public const string RunnerId = "runtime-preflight-linux-x64-v1";
    private readonly RuntimeSupervisorOptions _options = configuredOptions.Value;

    public async Task<RuntimePerformanceSampleResponse> MeasureAsync(
        RuntimePerformanceSampleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RuntimeProfileValidation.IsSha256(request.PlanSha256) ||
            _options.PromotionPreflightPlanSha256 is null ||
            !StringComparer.Ordinal.Equals(
                _options.PromotionPreflightPlanSha256,
                request.PlanSha256))
        {
            throw Invalid(
                "performance-plan-not-installed",
                "The requested promotion plan is not installed in the local Supervisor preflight profile.");
        }
        ValidateStableId(request.RuntimeProfileId, nameof(request.RuntimeProfileId));
        ValidateStableId(request.SecurityPolicyId, nameof(request.SecurityPolicyId));
        try
        {
            _ = ArtifactStoreProtocol.GetDigest(request.ArtifactRef);
        }
        catch (ArgumentException)
        {
            throw Invalid("invalid-performance-artifact-ref", "The artifact reference is malformed.");
        }
        if (request.Scenario is not (
            RuntimePerformanceScenarios.Run or
            RuntimePerformanceScenarios.Jit or
            RuntimePerformanceScenarios.Mapping))
        {
            throw Invalid("invalid-performance-scenario", "The performance scenario is not supported.");
        }

        var profile = GetProfile(request.RuntimeProfileId);
        var policy = GetPolicy(request.SecurityPolicyId);
        ValidateSelection(profile, policy, request);
        var inspection = await InspectImageAsync(profile, cancellationToken).ConfigureAwait(false);

        var nonce = Guid.NewGuid().ToString("N");
        var requestId = $"perf_{nonce}";
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(policy.MaximumDurationSeconds);
        var operationKind = request.Scenario == RuntimePerformanceScenarios.Run
            ? OperationKind.Run
            : OperationKind.Jit;
        var operation = operations.Start(
            requestId,
            $"runtime-performance-{nonce}",
            operationKind,
            requestId,
            DateTimeOffset.UtcNow);
        var measurement = new RuntimeJobMeasurementRegistration();
        var queued = request.Scenario == RuntimePerformanceScenarios.Run
            ? executor.QueueRunForMeasurement(
                operation,
                new RunRequest(
                    requestId,
                    $"runtime-performance-run-{nonce}",
                    "runtime-performance-preflight",
                    request.ArtifactRef,
                    profile.Id,
                    new RunOptions([], null, RunInstrumentation.None, policy.Id),
                    deadlineUtc),
                measurement)
            : executor.QueueJitForMeasurement(
                operation,
                new JitRequest(
                    requestId,
                    $"runtime-performance-jit-{nonce}",
                    "runtime-performance-preflight",
                    request.ArtifactRef,
                    profile.Id,
                    new JitOptions(
                        request.MethodFilter,
                        "tier0-diffable",
                        "disabled",
                        "coreclr-jitdisasm",
                        policy.Id),
                    deadlineUtc),
                measurement);
        if (!queued)
        {
            throw Unavailable(
                "performance-queue-rejected",
                "The runtime operation queue rejected the performance sample.");
        }

        RuntimeJobMeasurementCompletion completion;
        try
        {
            var cleanupBudget = TimeSpan.FromSeconds(45);
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(policy.MaximumDurationSeconds) + cleanupBudget);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            completion = await measurement.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            operations.Cancel(operation.Handle.OperationId, "performance-preflight-cancelled", DateTimeOffset.UtcNow);
            throw;
        }

        ValidateCompletion(completion, request.Scenario, policy);
        var distinctSequencePoints = CountDistinctSequencePointRanges(completion.Result);
        if (request.Scenario == RuntimePerformanceScenarios.Mapping && distinctSequencePoints < 2)
        {
            throw Failed(
                "performance-mapping-unavailable",
                "The mapping sample did not produce at least two distinct sequence-point ranges.");
        }

        var mappingKind = profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal)
            ? profile.Operations?.Jit?.SourceMappingKind ?? RuntimeJitSourceMappingKinds.None
            : RuntimePerformanceScenarios.NotApplicableMapping;
        return new RuntimePerformanceSampleResponse(
            profile.Id,
            request.Scenario,
            operation.Handle.OperationId,
            new RuntimePerformanceImageIdentity(
                inspection.ImmutableReference,
                inspection.ImageId,
                inspection.SizeBytes),
            profile.Capabilities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            mappingKind,
            new RuntimePerformanceSampleEnvironment(
                RunnerId,
                "linux",
                "x64",
                policy.NanoCpus,
                policy.MemoryBytes),
            new RuntimePerformanceSampleValue(
                completion.Latency.TotalMilliseconds,
                completion.ResourceUsage!.PeakMemoryBytes),
            completion.ResourceUsage.SampleCount,
            distinctSequencePoints,
            DateTimeOffset.UtcNow);
    }

    private async Task<RuntimeImageInspection> InspectImageAsync(
        RuntimeProfileOptions profile,
        CancellationToken cancellationToken)
    {
        RuntimeImageInspection inspection;
        try
        {
            inspection = await docker.InspectImageAsync(profile.Image, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            throw Failed("performance-image-not-immutable", exception.Message);
        }
        catch (DockerEngineException exception)
        {
            throw Unavailable("performance-image-inspection-failed", exception.Message);
        }

        if (!string.Equals(profile.RuntimeImageId, inspection.ImageId, StringComparison.Ordinal) ||
            !string.Equals(inspection.OperatingSystem, "linux", StringComparison.Ordinal) ||
            !string.Equals(inspection.Architecture, "amd64", StringComparison.Ordinal))
        {
            throw Failed(
                "performance-image-identity-mismatch",
                "The inspected Linux x64 image identity does not match the selected Runtime Profile.");
        }
        return inspection;
    }

    private static void ValidateSelection(
        RuntimeProfileOptions profile,
        RuntimeSecurityPolicyOptions policy,
        RuntimePerformanceSampleRequest request)
    {
        if (!profile.AllowedSecurityPolicyIds.Contains(policy.Id, StringComparer.Ordinal))
        {
            throw Invalid(
                "performance-policy-not-allowed",
                "The selected Runtime Profile does not allow this security policy.");
        }
        if (!string.Equals(profile.Architecture, "x64", StringComparison.Ordinal) ||
            !string.Equals(profile.Rid, "linux-x64", StringComparison.Ordinal))
        {
            throw Invalid(
                "performance-platform-not-supported",
                "The current performance policy only supports Linux x64 runtime profiles.");
        }
        if (!profile.Capabilities.Contains("run", StringComparer.Ordinal))
        {
            throw Invalid("performance-run-not-supported", "The Runtime Profile does not support Run.");
        }
        if (request.Scenario is RuntimePerformanceScenarios.Jit or RuntimePerformanceScenarios.Mapping)
        {
            if (!profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal))
                throw Invalid("performance-jit-not-supported", "The Runtime Profile does not support JIT.");
            if (string.IsNullOrWhiteSpace(request.MethodFilter) ||
                request.MethodFilter.Length > 256 ||
                request.MethodFilter.Any(char.IsControl))
            {
                throw Invalid(
                    "invalid-performance-method-filter",
                    "JIT performance samples require one concrete method filter.");
            }
        }
        else if (request.MethodFilter is not null)
        {
            throw Invalid(
                "unexpected-performance-method-filter",
                "Run performance samples cannot declare a method filter.");
        }
        var mappingKind = profile.Operations?.Jit?.SourceMappingKind;
        if (request.Scenario == RuntimePerformanceScenarios.Mapping &&
            mappingKind is null or RuntimeJitSourceMappingKinds.None or RuntimePerformanceScenarios.NotApplicableMapping)
        {
            throw Invalid(
                "performance-mapping-not-supported",
                "The Runtime Profile does not declare a source-mapped JIT implementation.");
        }
    }

    private static void ValidateCompletion(
        RuntimeJobMeasurementCompletion completion,
        string scenario,
        RuntimeSecurityPolicyOptions policy)
    {
        if (!completion.ExecutionStarted || completion.FailureCode is not null)
        {
            throw Failed(
                completion.FailureCode ?? "performance-execution-not-started",
                completion.FailureMessage ?? "The performance sample did not complete execution.");
        }
        if (!completion.CleanupSucceeded)
            throw Failed("performance-cleanup-failed", "The one-shot runtime resources were not fully cleaned up.");
        if (completion.ResourceUsage is not { PeakMemoryBytes: > 0, SampleCount: > 0 })
            throw Failed("performance-resource-sample-missing", "Docker returned no usable memory sample.");
        if (completion.ResourceUsage.PeakMemoryBytes > policy.MemoryBytes)
            throw Failed("performance-memory-limit-exceeded", "Peak memory exceeded the selected container limit.");
        if (!(completion.Latency > TimeSpan.Zero) || completion.Latency > TimeSpan.FromSeconds(120))
            throw Failed("performance-latency-invalid", "The measured lifecycle latency is outside the accepted range.");

        var succeeded = scenario == RuntimePerformanceScenarios.Run
            ? completion.Result is RunResult { Status: RunTerminalStatus.Completed, OutputTruncated: false }
            : completion.Result is JitResult
            {
                Status: JitTerminalStatus.Completed,
                Methods.Count: > 0
            };
        if (!succeeded)
            throw Failed("performance-operation-failed", "The runtime operation did not produce a successful result.");
    }

    private static int CountDistinctSequencePointRanges(OperationResult? result)
    {
        if (result is not JitResult jit)
            return 0;
        return jit.Methods
            .SelectMany(static method => method.LinkedRanges)
            .Where(static range =>
                range.Precision == "sequence-point" &&
                range.SourceRange is not null &&
                !string.IsNullOrWhiteSpace(range.SourceFilePath))
            .Select(static range =>
                $"{range.SourceFilePath}\0{range.SourceRange!.StartLine}\0" +
                $"{range.SourceRange.StartCharacter}\0{range.SourceRange.EndLine}\0" +
                $"{range.SourceRange.EndCharacter}")
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    private RuntimeProfileOptions GetProfile(string id)
    {
        try
        {
            return _options.GetProfile(id);
        }
        catch (KeyNotFoundException)
        {
            throw new RuntimePerformancePreflightException(
                "performance-profile-not-installed",
                "The selected Runtime Profile is not installed.",
                StatusCodes.Status404NotFound);
        }
    }

    private RuntimeSecurityPolicyOptions GetPolicy(string id)
    {
        try
        {
            return _options.GetSecurityPolicy(id);
        }
        catch (KeyNotFoundException)
        {
            throw new RuntimePerformancePreflightException(
                "performance-policy-not-installed",
                "The selected security policy is not installed.",
                StatusCodes.Status404NotFound);
        }
    }

    private static void ValidateStableId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The identifier is malformed.", parameterName);
        }
    }

    private static RuntimePerformancePreflightException Invalid(string code, string message) =>
        new(code, message, StatusCodes.Status400BadRequest);

    private static RuntimePerformancePreflightException Failed(string code, string message) =>
        new(code, message, StatusCodes.Status422UnprocessableEntity);

    private static RuntimePerformancePreflightException Unavailable(string code, string message) =>
        new(code, message, StatusCodes.Status503ServiceUnavailable);
}

public sealed class RuntimePerformancePreflightException(
    string code,
    string publicMessage,
    int statusCode) : Exception(publicMessage)
{
    public string Code { get; } = code;
    public string PublicMessage { get; } = publicMessage;
    public int StatusCode { get; } = statusCode;
}

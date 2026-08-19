using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.Operations;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.RuntimeSupervisor;

public sealed record RuntimeCapabilityPreflightRequest(
    string RuntimeProfileId,
    string SecurityPolicyId,
    string SourceRevision,
    string PlanSha256,
    string PreflightProfileSha256,
    ArtifactRef ProbeArtifactRef,
    ArtifactRef? ExecutionFlowArtifactRef = null,
    string? MethodFilter = null,
    string? JitLibraryPath = null);

public sealed record RuntimeCapabilityPreflightResponse(
    IReadOnlyList<JsonObject> Documents);

internal static class RuntimeCapabilityPreflightEndpoint
{
    public static async Task<IResult> HandleAsync(
        RuntimeCapabilityPreflightRequest request,
        RuntimeCapabilityPreflightCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await coordinator.ProduceAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (RuntimeCapabilityPreflightException exception)
        {
            return Results.Json(
                new { Error = exception.Code, Message = exception.PublicMessage },
                ContractJson.CreateSerializerOptions(),
                statusCode: exception.StatusCode);
        }
        catch (ArgumentException exception)
        {
            return Results.Json(
                new { Error = "invalid-capability-request", Message = exception.Message },
                ContractJson.CreateSerializerOptions(),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }
}

public sealed partial class RuntimeCapabilityPreflightCoordinator(
    OperationStore operations,
    RuntimeJobExecutor executor,
    IDockerEngineClient docker,
    IArtifactStoreClient artifactStore,
    RuntimeSandboxPolicy sandbox,
    IOptions<RuntimeSupervisorOptions> configuredOptions)
{
    public const string ProducerId = "sharplabnext-runtime-preflight-v1";
    private const string StdoutMarker = "SLN-CAPABILITY-STDOUT-V1";
    private const string StderrMarker = "SLN-CAPABILITY-STDERR-V1";
    private const string NetworkBlockedMarker = "SLN-CAPABILITY-NETWORK-BLOCKED-V1";
    private const string ReadOnlyBlockedMarker = "SLN-CAPABILITY-ROOTFS-READONLY-V1";
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly RuntimeSupervisorOptions _options = configuredOptions.Value;

    public async Task<RuntimeCapabilityPreflightResponse> ProduceAsync(
        RuntimeCapabilityPreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        ValidatePromotionPlanBinding(
            request.PlanSha256,
            request.PreflightProfileSha256,
            request.SourceRevision);
        var profile = GetProfile(request.RuntimeProfileId);
        var policy = GetPolicy(request.SecurityPolicyId);
        ValidateSelection(profile, policy, request);
        var image = await InspectImageAsync(profile, cancellationToken).ConfigureAwait(false);

        var probeDescriptor = await GetArtifactAsync(request.ProbeArtifactRef, cancellationToken)
            .ConfigureAwait(false);
        RuntimeJobExecutor.ValidateCompatibility(probeDescriptor.Manifest, profile);
        var canonicalProbe = await ValidateCanonicalProbeArtifactAsync(
            request,
            profile,
            probeDescriptor,
            cancellationToken).ConfigureAwait(false);
        var flowArtifactRef = request.ExecutionFlowArtifactRef;
        ArtifactBundleDescriptor? flowDescriptor = null;
        RuntimeCapabilityProbeArtifactBinding? flowBinding = null;
        if (profile.Capabilities.Contains("execution-flow", StringComparer.Ordinal))
        {
            flowDescriptor = await GetArtifactAsync(flowArtifactRef!.Value, cancellationToken)
                .ConfigureAwait(false);
            RuntimeJobExecutor.ValidateCompatibility(flowDescriptor.Manifest, profile);
            flowBinding = ValidateExecutionFlowArtifact(
                canonicalProbe.Binding,
                probeDescriptor.Manifest,
                flowDescriptor.Manifest);
            RuntimeJobExecutor.ValidateInstrumentation(
                flowDescriptor.Manifest,
                RunInstrumentation.ExecutionFlow);
        }

        var runFiles = BuildImageFileRequests(profile, jit: false, request.JitLibraryPath);
        var runArtifacts = await InspectImageFilesAsync(
            image,
            runFiles,
            cancellationToken).ConfigureAwait(false);
        ValidateImageArtifacts(profile, runArtifacts, jit: false);

        IReadOnlyList<RuntimeImageFileInspection>? jitArtifacts = null;
        if (profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal))
        {
            var jitFiles = BuildImageFileRequests(profile, jit: true, request.JitLibraryPath);
            jitArtifacts = await InspectImageFilesAsync(
                image,
                jitFiles,
                cancellationToken).ConfigureAwait(false);
            ValidateImageArtifacts(profile, jitArtifacts, jit: true);
        }

        var success = await ExecuteRunProbeAsync(
            profile,
            policy,
            request.ProbeArtifactRef,
            ["success-security"],
            RunInstrumentation.None,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        RequireSuccessfulRun(success, "Run/security");
        var successStdout = Utf8(success.Audit!.Stdout, "Run stdout");
        var successStderr = Utf8(success.Audit.Stderr, "Run stderr");
        RequireMarker(successStdout, StdoutMarker);
        RequireMarker(successStdout, NetworkBlockedMarker);
        RequireMarker(successStdout, ReadOnlyBlockedMarker);
        RequireMarker(successStderr, StderrMarker);

        var exception = await ExecuteRunProbeAsync(
            profile,
            policy,
            request.ProbeArtifactRef,
            ["user-exception"],
            RunInstrumentation.None,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        RequireAuditedCompletion(exception, "exception");
        if (exception.Result is not RunResult { Status: RunTerminalStatus.UserException, Exception: not null } ||
            exception.Audit!.Exception is null ||
            !StringComparer.Ordinal.Equals(exception.Audit.TerminalStatus, "user-exception") ||
            !exception.Audit.FrameKinds.ContainsKey("Exception"))
        {
            throw Failed("capability-exception-probe-failed", "The structured user-exception probe did not pass.");
        }

        var lifecycle = await RunLifecycleProbesAsync(
            profile,
            policy,
            request.ProbeArtifactRef,
            cancellationToken).ConfigureAwait(false);

        var evidence = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        evidence.Add("run", BuildEvidenceDocument(
            request,
            profile,
            policy,
            image,
            runArtifacts,
            success,
            lifecycle,
            canonicalProbe.Binding,
            "run",
            new
            {
                expectedStdoutMarker = StdoutMarker,
                observedStdoutMarker = StdoutMarker,
                expectedStderrMarker = StderrMarker,
                observedStderrMarker = StderrMarker,
                exceptionFrameValidated = true
            }));

        RuntimeProbeExecution? jit = null;
        PortablePdbEvidence? pdb = null;
        string? mappingSource = null;
        if (profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal))
        {
            jit = await ExecuteJitProbeAsync(
                profile,
                policy,
                request.ProbeArtifactRef,
                request.MethodFilter!,
                cancellationToken).ConfigureAwait(false);
            RequireAuditedCompletion(jit, "JIT");
            if (jit.Result is not JitResult { Status: JitTerminalStatus.Completed, Methods.Count: > 0 } ||
                jit.Audit!.JitMethods.Count == 0 ||
                jit.Audit.JitMethods.Any(static method =>
                    method.NativeCodeBytes <= 0 || method.InstructionCount <= 0))
            {
                throw Failed("capability-jit-probe-failed", "The JIT probe returned no non-empty prepared method.");
            }

            var mappingKind = profile.Operations!.Jit!.SourceMappingKind;
            if (!StringComparer.Ordinal.Equals(mappingKind, RuntimeJitSourceMappingKinds.None))
            {
                pdb = canonicalProbe.PortablePdb;
                mappingSource = ValidateJitMappings(jit.Audit.JitMethods, pdb, mappingKind);
            }
            else
            {
                if (jit.Audit.JitMethods.Any(static method => method.EvidenceRanges.Count != 0))
                    throw Failed("capability-jit-unexpected-mapping", "A method-only JIT probe emitted source-map evidence.");
                var unmappedSources = jit.Audit.JitMethods
                    .Select(static method => method.MappingSource)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                mappingSource = unmappedSources is ["none"] or ["method"]
                    ? unmappedSources[0]
                    : throw Failed(
                        "capability-jit-mapping-source-invalid",
                        "An unmapped JIT probe must report one truthful 'none' or method-level mapping source.");
            }

            var methods = jit.Audit.JitMethods.Select(static method => new
            {
                metadataToken = method.MetadataToken,
                displayName = method.DisplayName,
                nativeCodeBytes = method.NativeCodeBytes,
                instructionCount = method.InstructionCount,
                sourceRanges = method.EvidenceRanges.Select(static range => new
                {
                    ilOffset = range.IlOffset,
                    nativeStartOffset = range.NativeStartOffset,
                    nativeEndOffset = range.NativeEndOffset,
                    document = range.Document,
                    startLine = range.StartLine,
                    startColumn = range.StartColumn,
                    endLine = range.EndLine,
                    endColumn = range.EndColumn
                }).ToArray()
            }).ToArray();
            var allRanges = jit.Audit.JitMethods.SelectMany(static method => method.EvidenceRanges).ToArray();
            var distinctRanges = allRanges.Select(SourceIdentity).Distinct(StringComparer.Ordinal).Count();
            var jitDetails = new JsonObject
            {
                ["runtimeVersion"] = profile.RuntimeVersion,
                ["jitVersion"] = profile.JitVersion,
                ["methods"] = JsonSerializer.SerializeToNode(methods, EvidenceJsonOptions),
                ["mapping"] = JsonSerializer.SerializeToNode(new
                {
                    kind = mappingKind,
                    source = mappingSource,
                    rangeCount = allRanges.Length,
                    distinctSourceRangeCount = distinctRanges,
                    allRangesMatchPdb = pdb is not null
                }, EvidenceJsonOptions)
            };
            if (pdb is not null)
            {
                jitDetails["pdb"] = JsonSerializer.SerializeToNode(new
                {
                    path = pdb.Path,
                    sha256 = pdb.Sha256,
                    contentId = pdb.ContentId,
                    sequencePointCount = pdb.SequencePointCount
                }, EvidenceJsonOptions);
            }
            evidence.Add("jit-asm", BuildEvidenceDocument(
                request,
                profile,
                policy,
                image,
                jitArtifacts!,
                jit,
                lifecycle,
                canonicalProbe.Binding,
                "jit-asm",
                jitDetails));
        }

        if (profile.Capabilities.Contains("inspection", StringComparer.Ordinal))
        {
            var inspection = await ExecuteRunProbeAsync(
                profile,
                policy,
                request.ProbeArtifactRef,
                ["inspection"],
                RunInstrumentation.Inspection,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            RequireSuccessfulRun(inspection, "inspection");
            var kinds = inspection.Audit!.InspectionPayloads
                .Select(static payload => payload.Kind)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!kinds.Contains("Value", StringComparer.Ordinal) ||
                !kinds.Contains("MemoryGraph", StringComparer.Ordinal) ||
                inspection.Audit.InspectionPayloads.Count < 2)
            {
                throw Failed("capability-inspection-probe-failed", "Inspection did not emit Value and MemoryGraph records.");
            }
            evidence.Add("inspection", BuildEvidenceDocument(
                request,
                profile,
                policy,
                image,
                runArtifacts,
                inspection,
                lifecycle,
                canonicalProbe.Binding,
                "inspection",
                new
                {
                    recordCount = inspection.Audit.InspectionPayloads.Count,
                    kinds,
                    valueProbePassed = true,
                    memoryGraphProbePassed = true
                }));
        }

        if (profile.Capabilities.Contains("execution-flow", StringComparer.Ordinal))
        {
            var flow = await ExecuteRunProbeAsync(
                profile,
                policy,
                flowArtifactRef!.Value,
                ["execution-flow"],
                RunInstrumentation.ExecutionFlow,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            RequireSuccessfulRun(flow, "execution-flow");
            var flowPayloads = flow.Audit!.FlowPayloads;
            var sequencePoints = flowPayloads.Count(static payload => payload.EventKind == "sequence-point");
            var branches = flowPayloads.Count(static payload => payload.EventKind == "branch");
            var sourceRanges = flowPayloads
                .Where(static payload => payload.Range is not null && !string.IsNullOrWhiteSpace(payload.DocumentPath))
                .Select(static payload =>
                    $"{payload.DocumentPath}\0{payload.Range!.StartLine}\0{payload.Range.StartColumn}\0" +
                    $"{payload.Range.EndLine}\0{payload.Range.EndColumn}")
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (flowPayloads.Count < 2 || sequencePoints < 1 || branches < 1 || sourceRanges < 2)
            {
                throw Failed(
                    "capability-execution-flow-probe-failed",
                    "Execution Flow did not emit sequence, branch, and multiple source-range records.");
            }
            evidence.Add("execution-flow", BuildEvidenceDocument(
                request,
                profile,
                policy,
                image,
                runArtifacts,
                flow,
                lifecycle,
                flowBinding!,
                "execution-flow",
                new
                {
                    recordCount = flowPayloads.Count,
                    sequencePointCount = sequencePoints,
                    branchCount = branches,
                    sourceRangeCount = sourceRanges,
                    derivedArtifactSha256 = flowArtifactRef.Value.Value,
                    parentArtifactSha256 = canonicalProbe.Binding.SourceArtifactSha256,
                    processorId = flowBinding!.Derivation!.ProcessorId,
                    processorVersion = flowBinding.Derivation.ProcessorVersion,
                    optionsSha256 = flowBinding.Derivation.OptionsSha256,
                    transformId = flowBinding.Derivation.TransformId,
                    profileId = flowBinding.Derivation.ProfileId,
                    applied = flowBinding.Derivation.Applied
                }));
        }

        var context = BuildValidationContext(
            request,
            profile,
            image,
            runArtifacts,
            jitArtifacts,
            mappingSource);
        ValidateEvidenceSet(context, evidence);
        return new RuntimeCapabilityPreflightResponse(
            evidence.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => item.Value)
                .ToArray());
    }

    private async Task<RuntimeLifecycleEvidence> RunLifecycleProbesAsync(
        RuntimeProfileOptions profile,
        RuntimeSecurityPolicyOptions policy,
        ArtifactRef artifactRef,
        CancellationToken cancellationToken)
    {
        var overflow = await ExecuteRunProbeAsync(
            profile,
            policy,
            artifactRef,
            ["output-overflow"],
            RunInstrumentation.None,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        RequireAuditedCompletion(overflow, "output-overflow");
        if (overflow.Result is not RunResult { Status: RunTerminalStatus.OutputLimitExceeded, OutputTruncated: true })
        {
            throw Failed("capability-output-overflow-probe-failed", "The output-overflow probe did not hit the Supervisor limit.");
        }

        var timeout = await ExecuteRunProbeAsync(
            profile,
            policy,
            artifactRef,
            ["hang"],
            RunInstrumentation.None,
            deadline: TimeSpan.FromMilliseconds(250),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        RequireAuditedCompletion(timeout, "timeout");
        if (timeout.Result is not RunResult { Status: RunTerminalStatus.Timeout })
            throw Failed("capability-timeout-probe-failed", "The timeout probe did not terminate at its deadline.");

        var cancellation = await ExecuteRunProbeAsync(
            profile,
            policy,
            artifactRef,
            ["hang"],
            RunInstrumentation.None,
            cancelAfterContainerStart: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        RequireAuditedCompletion(cancellation, "cancellation");
        if (cancellation.Result is not RunResult { Status: RunTerminalStatus.Cancelled })
            throw Failed("capability-cancellation-probe-failed", "The cancellation probe did not report cancellation.");

        var processTree = await ExecuteRunProbeAsync(
            profile,
            policy,
            artifactRef,
            ["process-tree"],
            RunInstrumentation.None,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        RequireSuccessfulRun(processTree, "process-tree");

        return new RuntimeLifecycleEvidence(
            Probe("output-limit-exceeded", overflow),
            Probe("timeout", timeout),
            Probe("cancelled", cancellation),
            Probe("completed", processTree));
    }

    private async Task<RuntimeProbeExecution> ExecuteRunProbeAsync(
        RuntimeProfileOptions profile,
        RuntimeSecurityPolicyOptions policy,
        ArtifactRef artifactRef,
        IReadOnlyList<string> arguments,
        RunInstrumentation instrumentation,
        TimeSpan? deadline = null,
        bool cancelAfterContainerStart = false,
        CancellationToken cancellationToken = default)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var requestId = $"cap_{nonce}";
        var operation = operations.Start(
            requestId,
            $"runtime-capability-run-{nonce}",
            OperationKind.Run,
            requestId,
            DateTimeOffset.UtcNow);
        var measurement = new RuntimeJobMeasurementRegistration(collectResources: false);
        var queued = executor.QueueRunForMeasurement(
            operation,
            new RunRequest(
                requestId,
                $"runtime-capability-run-{nonce}",
                "runtime-capability-preflight",
                artifactRef,
                profile.Id,
                new RunOptions(arguments, null, instrumentation, policy.Id),
                DateTimeOffset.UtcNow.Add(deadline ?? TimeSpan.FromSeconds(policy.MaximumDurationSeconds))),
            measurement);
        if (!queued)
            throw Unavailable("capability-queue-rejected", "The runtime queue rejected a capability probe.");

        if (cancelAfterContainerStart)
        {
            try
            {
                await measurement.ContainerStarted.WaitAsync(
                    TimeSpan.FromSeconds(Math.Min(policy.MaximumDurationSeconds, 30)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Failed("capability-cancellation-start-timeout", "The cancellation probe never started its container.");
            }
            operations.Cancel(operation.Handle.OperationId, "capability-cancellation-probe", DateTimeOffset.UtcNow);
        }

        var completion = await AwaitCompletionAsync(
            operation,
            measurement,
            policy,
            cancellationToken).ConfigureAwait(false);
        return new RuntimeProbeExecution(operation.Handle.OperationId, completion.Result, completion.Audit, completion);
    }

    private async Task<RuntimeProbeExecution> ExecuteJitProbeAsync(
        RuntimeProfileOptions profile,
        RuntimeSecurityPolicyOptions policy,
        ArtifactRef artifactRef,
        string methodFilter,
        CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var requestId = $"cap_{nonce}";
        var operation = operations.Start(
            requestId,
            $"runtime-capability-jit-{nonce}",
            OperationKind.Jit,
            requestId,
            DateTimeOffset.UtcNow);
        var measurement = new RuntimeJobMeasurementRegistration(collectResources: false);
        var queued = executor.QueueJitForMeasurement(
            operation,
            new JitRequest(
                requestId,
                $"runtime-capability-jit-{nonce}",
                "runtime-capability-preflight",
                artifactRef,
                profile.Id,
                new JitOptions(
                    methodFilter,
                    "tier0-diffable",
                    "disabled",
                    "coreclr-jitdisasm",
                    policy.Id),
                DateTimeOffset.UtcNow.AddSeconds(policy.MaximumDurationSeconds)),
            measurement);
        if (!queued)
            throw Unavailable("capability-queue-rejected", "The runtime queue rejected the JIT capability probe.");

        var completion = await AwaitCompletionAsync(
            operation,
            measurement,
            policy,
            cancellationToken).ConfigureAwait(false);
        return new RuntimeProbeExecution(operation.Handle.OperationId, completion.Result, completion.Audit, completion);
    }

    private async Task<RuntimeJobMeasurementCompletion> AwaitCompletionAsync(
        OperationStart operation,
        RuntimeJobMeasurementRegistration measurement,
        RuntimeSecurityPolicyOptions policy,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Min(120, policy.MaximumDurationSeconds + 45)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            return await measurement.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            operations.Cancel(operation.Handle.OperationId, "capability-preflight-aborted", DateTimeOffset.UtcNow);
            throw;
        }
    }

    private static RuntimeLifecycleProbe Probe(string terminalStatus, RuntimeProbeExecution execution)
    {
        var audit = execution.Audit
            ?? throw Failed("capability-audit-missing", "A lifecycle probe returned no execution audit.");
        if (!execution.Completion.ExecutionStarted || !execution.Completion.CleanupSucceeded ||
            !audit.ContainerRemoved || !audit.ProcessTreeRemoved)
        {
            throw Failed(
                "capability-lifecycle-cleanup-failed",
                "A lifecycle probe did not start and finish with complete container and process-tree cleanup.");
        }
        return new RuntimeLifecycleProbe(
            "passed",
            terminalStatus,
            audit.ContainerRemoved,
            audit.ProcessTreeRemoved);
    }

    private const long MaximumEvidenceArtifactBytes = 256L * 1024 * 1024;
    private const long MaximumPortablePdbBytes = 64L * 1024 * 1024;
    private const string SupportAssemblyPath = "/opt/sharplabnext/SharpLab.Runtime.dll";

    private static void ValidateRequest(RuntimeCapabilityPreflightRequest request)
    {
        ValidateStableId(request.RuntimeProfileId, nameof(request.RuntimeProfileId));
        ValidateStableId(request.SecurityPolicyId, nameof(request.SecurityPolicyId));
        if (!GitCommitRegex().IsMatch(request.SourceRevision ?? string.Empty))
        {
            throw Invalid(
                "invalid-capability-source-revision",
                "The source revision must be a full lowercase Git commit.");
        }
        if (!Sha256Regex().IsMatch(request.PlanSha256 ?? string.Empty))
        {
            throw Invalid(
                "invalid-capability-plan-digest",
                "The promotion plan digest must be a canonical SHA-256 value.");
        }
        if (!Sha256Regex().IsMatch(request.PreflightProfileSha256 ?? string.Empty))
        {
            throw Invalid(
                "invalid-capability-preflight-profile-digest",
                "The immutable preflight Runtime Profile digest must be a canonical SHA-256 value.");
        }

        ValidateArtifactRef(request.ProbeArtifactRef, "probe");
        if (request.ExecutionFlowArtifactRef is { } flowArtifactRef)
            ValidateArtifactRef(flowArtifactRef, "Execution Flow");
        if (request.MethodFilter is { } methodFilter &&
            (string.IsNullOrWhiteSpace(methodFilter) || methodFilter.Length > 256 || methodFilter.Any(char.IsControl)))
        {
            throw Invalid(
                "invalid-capability-method-filter",
                "The JIT method filter must be non-empty, bounded, and free of control characters.");
        }
        if (request.JitLibraryPath is { } jitLibraryPath && !IsCanonicalImagePath(jitLibraryPath))
        {
            throw Invalid(
                "invalid-capability-jit-library-path",
                "The JIT library path must be a canonical absolute image path.");
        }
    }

    private static void ValidateArtifactRef(ArtifactRef artifactRef, string label)
    {
        try
        {
            _ = ArtifactStoreProtocol.GetDigest(artifactRef);
        }
        catch (ArgumentException)
        {
            throw Invalid(
                "invalid-capability-artifact-ref",
                $"The {label} artifact reference is malformed.");
        }
    }

    private RuntimeProfileOptions GetProfile(string id)
    {
        try
        {
            return _options.GetProfile(id);
        }
        catch (KeyNotFoundException)
        {
            throw new RuntimeCapabilityPreflightException(
                "capability-profile-not-installed",
                "The selected Runtime Profile is not installed.",
                StatusCodes.Status404NotFound);
        }
    }

    private void ValidatePromotionPlanBinding(
        string planSha256,
        string preflightProfileSha256,
        string sourceRevision)
    {
        if (_options.PromotionPreflightPlanSha256 is null ||
            _options.PromotionPreflightProfileSha256 is null ||
            _options.PromotionPreflightSourceRevision is null ||
            !StringComparer.Ordinal.Equals(_options.PromotionPreflightPlanSha256, planSha256) ||
            !StringComparer.Ordinal.Equals(
                _options.PromotionPreflightProfileSha256,
                preflightProfileSha256) ||
            !StringComparer.Ordinal.Equals(
                _options.PromotionPreflightSourceRevision,
                sourceRevision))
        {
            throw Invalid(
                "capability-plan-not-installed",
                "The requested promotion plan, immutable preflight Runtime Profile, and source revision are not installed in the local Supervisor preflight profile.");
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
            throw new RuntimeCapabilityPreflightException(
                "capability-policy-not-installed",
                "The selected security policy is not installed.",
                StatusCodes.Status404NotFound);
        }
    }

    private static void ValidateSelection(
        RuntimeProfileOptions profile,
        RuntimeSecurityPolicyOptions policy,
        RuntimeCapabilityPreflightRequest request)
    {
        if (!profile.AllowedSecurityPolicyIds.Contains(policy.Id, StringComparer.Ordinal))
        {
            throw Invalid(
                "capability-policy-not-allowed",
                "The selected Runtime Profile does not allow this security policy.");
        }
        var embeddedPolicy = profile.SecurityPolicies.SingleOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Id, policy.Id));
        if (embeddedPolicy is null ||
            embeddedPolicy.MemoryBytes != policy.MemoryBytes ||
            embeddedPolicy.NanoCpus != policy.NanoCpus ||
            embeddedPolicy.PidsLimit != policy.PidsLimit ||
            embeddedPolicy.MaximumDurationSeconds != policy.MaximumDurationSeconds ||
            embeddedPolicy.MaximumArtifactBytes != policy.MaximumArtifactBytes ||
            embeddedPolicy.MaximumOutputBytes != policy.MaximumOutputBytes ||
            embeddedPolicy.TmpfsBytes != policy.TmpfsBytes)
        {
            throw Failed(
                "capability-profile-policy-mismatch",
                "The selected security policy is not identically embedded in the Runtime Profile.");
        }
        if (!StringComparer.Ordinal.Equals(profile.Rid, "linux-x64") ||
            !StringComparer.Ordinal.Equals(profile.Architecture, "x64"))
        {
            throw Invalid(
                "capability-platform-not-supported",
                "Capability preflight requires a Linux-container x64 Runtime Profile.");
        }
        if (!profile.Capabilities.Contains("run", StringComparer.Ordinal) || profile.Operations?.Run is null)
        {
            throw Invalid(
                "capability-run-not-supported",
                "Capability preflight requires an operation-based Run implementation.");
        }
        var unsupportedCapabilities = profile.Capabilities
            .Except(["run", "jit-asm", "inspection", "execution-flow"], StringComparer.Ordinal)
            .ToArray();
        if (unsupportedCapabilities.Length > 0)
        {
            throw Invalid(
                "capability-set-not-supported",
                "The Runtime Profile declares a capability that this evidence schema cannot represent.");
        }

        var supportsJit = profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal);
        if (supportsJit)
        {
            if (profile.Operations.Jit is null || string.IsNullOrWhiteSpace(request.MethodFilter))
            {
                throw Invalid(
                    "capability-jit-probe-incomplete",
                    "A JIT-capable Runtime Profile requires an operation and one concrete method filter.");
            }
            if (request.JitLibraryPath is null)
            {
                throw Invalid(
                    "capability-jit-library-required",
                    "A JIT-capable Runtime Profile requires the exact JIT library image path.");
            }
            if (profile.Operations.Jit.SourceMappingKind is not (
                RuntimeJitSourceMappingKinds.None or
                RuntimeJitSourceMappingKinds.LinuxProfiler or
                RuntimeJitSourceMappingKinds.CheckedJitDebugInfo))
            {
                throw Invalid(
                    "capability-jit-mapping-kind-invalid",
                    "The Runtime Profile declares an unsupported JIT source-mapping kind.");
            }
        }
        else if (request.MethodFilter is not null || request.JitLibraryPath is not null)
        {
            throw Invalid(
                "capability-jit-probe-unexpected",
                "A Runtime Profile without JIT support cannot declare JIT preflight inputs.");
        }

        var supportsFlow = profile.Capabilities.Contains("execution-flow", StringComparer.Ordinal);
        if (supportsFlow != request.ExecutionFlowArtifactRef.HasValue)
        {
            throw Invalid(
                supportsFlow
                    ? "capability-execution-flow-artifact-required"
                    : "capability-execution-flow-artifact-unexpected",
                supportsFlow
                    ? "Execution Flow requires its derived instrumentation artifact."
                    : "A Runtime Profile without Execution Flow cannot declare a derived artifact.");
        }
    }

    private async Task<RuntimeImageInspection> InspectImageAsync(
        RuntimeProfileOptions profile,
        CancellationToken cancellationToken)
    {
        if (!ImmutableImageReferenceRegex().IsMatch(profile.Image ?? string.Empty) ||
            !Sha256Regex().IsMatch(profile.RuntimeImageId ?? string.Empty))
        {
            throw Failed(
                "capability-image-not-immutable",
                "The selected Runtime Profile is not bound to an immutable repository digest and image ID.");
        }

        RuntimeImageInspection inspection;
        try
        {
            inspection = await docker.InspectImageAsync(profile.Image!, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            throw Failed("capability-image-not-immutable", exception.Message);
        }
        catch (DockerEngineException exception)
        {
            throw Unavailable("capability-image-inspection-failed", exception.Message);
        }

        if (!StringComparer.Ordinal.Equals(inspection.ImmutableReference, profile.Image) ||
            !StringComparer.Ordinal.Equals(inspection.ImageId, profile.RuntimeImageId) ||
            !Sha256Regex().IsMatch(inspection.ImageId ?? string.Empty) ||
            !StringComparer.Ordinal.Equals(inspection.OperatingSystem, "linux") ||
            !StringComparer.Ordinal.Equals(inspection.Architecture, "amd64") ||
            inspection.SizeBytes <= 0 ||
            !inspection.RepoDigests.Contains(profile.Image, StringComparer.Ordinal))
        {
            throw Failed(
                "capability-image-identity-mismatch",
                "The inspected Linux x64 image does not match the immutable Runtime Profile identity.");
        }
        return inspection;
    }

    private async Task<IReadOnlyList<RuntimeImageFileInspection>> InspectImageFilesAsync(
        RuntimeImageInspection image,
        IReadOnlyList<RuntimeImageFileRequest> files,
        CancellationToken cancellationToken)
    {
        try
        {
            return await docker.InspectImageFilesAsync(
                image.ImageId,
                files,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            throw Failed("capability-image-artifact-request-invalid", exception.Message);
        }
        catch (NotSupportedException exception)
        {
            throw Unavailable("capability-image-artifact-inspection-unavailable", exception.Message);
        }
        catch (DockerEngineException exception)
        {
            throw Unavailable("capability-image-artifact-inspection-failed", exception.Message);
        }
    }

    private async Task<ArtifactBundleDescriptor> GetArtifactAsync(
        ArtifactRef artifactRef,
        CancellationToken cancellationToken)
    {
        ArtifactBundleDescriptor? descriptor;
        try
        {
            descriptor = await artifactStore.GetArtifactAsync(artifactRef, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw Unavailable("capability-artifact-store-unavailable", exception.Message);
        }
        if (descriptor is null)
        {
            throw new RuntimeCapabilityPreflightException(
                "capability-artifact-not-found",
                "The capability probe artifact was not found.",
                StatusCodes.Status404NotFound);
        }
        if (descriptor.Manifest.ManifestVersion != ArtifactStoreProtocol.ArtifactManifestVersion ||
            descriptor.Manifest.ArtifactId != artifactRef)
        {
            throw Failed(
                "capability-artifact-identity-mismatch",
                "The Artifact Store returned a manifest with the wrong identity or version.");
        }
        try
        {
            ArtifactIdentity.Validate(descriptor.Manifest);
        }
        catch (ArgumentException)
        {
            throw Failed(
                "capability-artifact-identity-mismatch",
                "The capability artifact manifest is not bound to its content-addressed identity.");
        }

        var files = new Dictionary<string, ArtifactFileDescriptor>(StringComparer.Ordinal);
        foreach (var file in descriptor.Manifest.Files)
        {
            string path;
            try
            {
                path = ArtifactPath.Normalize(file.Path);
                _ = ArtifactStoreProtocol.ParseContentRef(file.Digest);
            }
            catch (ArgumentException)
            {
                throw Failed(
                    "capability-artifact-manifest-invalid",
                    "The capability artifact manifest contains an invalid path or digest.");
            }
            if (file.Size <= 0 || file.Size > MaximumEvidenceArtifactBytes || !files.TryAdd(path, file))
            {
                throw Failed(
                    "capability-artifact-manifest-invalid",
                    "The capability artifact manifest contains an invalid or duplicate file.");
            }
        }
        if (files.Count == 0 || descriptor.Entries.Count != files.Count)
        {
            throw Failed(
                "capability-artifact-descriptor-invalid",
                "The capability artifact descriptor does not match its manifest.");
        }
        foreach (var entry in descriptor.Entries)
        {
            var path = ArtifactPath.Normalize(entry.Path);
            if (!files.TryGetValue(path, out var file) ||
                entry.Size != file.Size ||
                !StringComparer.Ordinal.Equals(entry.Digest, file.Digest) ||
                !StringComparer.Ordinal.Equals(entry.Role, file.Role) ||
                entry.ContentRef.Value != file.Digest)
            {
                throw Failed(
                    "capability-artifact-descriptor-invalid",
                    "The capability artifact descriptor entries do not match its manifest.");
            }
        }
        return descriptor;
    }

    private async Task<CanonicalProbeArtifact> ValidateCanonicalProbeArtifactAsync(
        RuntimeCapabilityPreflightRequest request,
        RuntimeProfileOptions profile,
        ArtifactBundleDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var manifest = descriptor.Manifest;
        var target = ResolveCanonicalProbeTarget(profile);
        var acceptedFramework = profile.AcceptedFrameworks.Count == 1
            ? profile.AcceptedFrameworks[0]
            : null;
        var requiredFramework = manifest.RuntimeRequirement.Frameworks.Count == 1
            ? manifest.RuntimeRequirement.Frameworks[0]
            : null;
        var metadata = manifest.Metadata;
        var entryPath = ArtifactPath.Normalize(manifest.EntryAssembly);
        var pdbPath = Path.ChangeExtension(entryPath, ".pdb").Replace('\\', '/');
        var entry = manifest.Files.SingleOrDefault(file =>
            StringComparer.Ordinal.Equals(ArtifactPath.Normalize(file.Path), entryPath));
        var pdb = manifest.Files.SingleOrDefault(file =>
            StringComparer.Ordinal.Equals(ArtifactPath.Normalize(file.Path), pdbPath));

        if (manifest.Derivation is not null ||
            manifest.Producer.ReleaseId != RuntimeCapabilityProbeContract.ReleaseId ||
            manifest.Producer.LanguageId != RuntimeCapabilityProbeContract.LanguageId ||
            manifest.Producer.ToolchainId != RuntimeCapabilityProbeContract.ToolchainId ||
            manifest.Producer.CompilerVersion != RuntimeCapabilityProbeContract.CompilerVersion ||
            manifest.Producer.CompilerCommit != request.SourceRevision ||
            manifest.Producer.WorkerImageId != $"source-revision:{request.SourceRevision}" ||
            manifest.ReferenceSetId != target.ReferenceSetId ||
            manifest.TargetFramework != target.TargetFramework ||
            manifest.ArtifactFormat != target.ArtifactFormat ||
            manifest.RuntimeRequirement.Family != profile.Family ||
            manifest.RuntimeRequirement.Architecture != "anycpu" ||
            manifest.RuntimeRequirement.RequiredRuntimeFeatureTags.Count != 0 ||
            acceptedFramework?.ExactVersion is null ||
            requiredFramework?.Name != acceptedFramework.Name ||
            requiredFramework.MinimumVersion != acceptedFramework.ExactVersion ||
            manifest.MetadataFeatureTags.Count != 0 ||
            manifest.OutputKind != BuildOutputKind.Console ||
            entryPath != target.EntryAssembly ||
            manifest.EntryPoint != RuntimeCapabilityProbeContract.EntryPoint ||
            manifest.Files.Count != 2 ||
            entry?.Role != "managed-pe" ||
            pdb?.Role != "portable-pdb" ||
            metadata is null || metadata.Count != 4 ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataContractKey,
                out var contractVersion) ||
            contractVersion != RuntimeCapabilityProbeContract.MetadataContractValue ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataSourceRevisionKey,
                out var metadataRevision) ||
            metadataRevision != request.SourceRevision ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataPromotionPlanSha256Key,
                out var metadataPlanSha256) ||
            metadataPlanSha256 != request.PlanSha256 ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataPreflightProfileSha256Key,
                out var metadataPreflightProfileSha256) ||
            metadataPreflightProfileSha256 != request.PreflightProfileSha256)
        {
            throw Failed(
                "capability-probe-contract-mismatch",
                "The supplied artifact is not the canonical source-bound runtime capability probe.");
        }

        var portablePdb = await ReadPortablePdbEvidenceAsync(
            request.ProbeArtifactRef,
            descriptor,
            profile,
            cancellationToken).ConfigureAwait(false);
        return new CanonicalProbeArtifact(
            new RuntimeCapabilityProbeArtifactBinding(
                RuntimeCapabilityProbeContract.ContractId,
                request.ProbeArtifactRef.Value,
                request.ProbeArtifactRef.Value,
                entry.Digest,
                request.PlanSha256,
                request.PreflightProfileSha256,
                null),
            portablePdb);
    }

    private static RuntimeCapabilityProbeTarget ResolveCanonicalProbeTarget(
        RuntimeProfileOptions profile) => profile.Family switch
    {
        "coreclr" or "coreclr-wine" when
            profile.AcceptedArtifactFormats.Contains("dotnet-managed-pe-v1", StringComparer.Ordinal) =>
            new RuntimeCapabilityProbeTarget(
                "netcoreapp2.0",
                "SharpLabNext.RuntimeCapabilityProbe.dll",
                "dotnet-managed-pe-v1",
                "runtime-capability-probe-netcoreapp2.0-ref"),
        "mono" or "netfx-clr-wine" when
            profile.AcceptedArtifactFormats.Contains(
                "dotnet-framework-managed-pe-v1",
                StringComparer.Ordinal) =>
            new RuntimeCapabilityProbeTarget(
                "net20",
                "SharpLabNext.RuntimeCapabilityProbe.exe",
                "dotnet-framework-managed-pe-v1",
                "runtime-capability-probe-net20-ref"),
        _ => throw Failed(
            "capability-probe-contract-unsupported",
            "The selected Runtime Profile has no canonical capability probe artifact contract.")
    };

    private static RuntimeCapabilityProbeArtifactBinding ValidateExecutionFlowArtifact(
        RuntimeCapabilityProbeArtifactBinding sourceBinding,
        ArtifactManifest source,
        ArtifactManifest derived)
    {
        var derivation = derived.Derivation;
        var metadata = derived.Metadata;
        var entryPath = ArtifactPath.Normalize(derived.EntryAssembly);
        var entry = derived.Files.SingleOrDefault(file =>
            StringComparer.Ordinal.Equals(ArtifactPath.Normalize(file.Path), entryPath));
        var sourceFiles = source.Files.Select(static file =>
            (ArtifactPath.Normalize(file.Path), file.Role)).ToArray();
        var derivedFiles = derived.Files.Select(static file =>
            (ArtifactPath.Normalize(file.Path), file.Role)).ToArray();
        var pointsValid = metadata is not null &&
            metadata.TryGetValue(RuntimeCapabilityProbeContract.InstrumentationPointsKey, out var pointsText) &&
            int.TryParse(
                pointsText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var points) &&
            points > 0 &&
            StringComparer.Ordinal.Equals(pointsText, points.ToString(CultureInfo.InvariantCulture));

        if (derived.ArtifactId == source.ArtifactId ||
            derivation is null || derivation.ParentArtifactId != source.ArtifactId ||
            derivation.ProcessorId != RuntimeCapabilityProbeContract.ExecutionFlowProcessorId ||
            derivation.ProcessorVersion != RuntimeCapabilityProbeContract.ExecutionFlowProcessorVersion ||
            derivation.OptionsDigest != RuntimeCapabilityProbeContract.ExecutionFlowOptionsDigest ||
            derived.Producer != source.Producer ||
            derived.ReferenceSetId != source.ReferenceSetId ||
            derived.TargetFramework != source.TargetFramework ||
            derived.ArtifactFormat != source.ArtifactFormat ||
            derived.RuntimeRequirement.Family != source.RuntimeRequirement.Family ||
            !derived.RuntimeRequirement.Frameworks.SequenceEqual(source.RuntimeRequirement.Frameworks) ||
            derived.RuntimeRequirement.Architecture != source.RuntimeRequirement.Architecture ||
            !derived.RuntimeRequirement.RequiredRuntimeFeatureTags.SequenceEqual(
                source.RuntimeRequirement.RequiredRuntimeFeatureTags,
                StringComparer.Ordinal) ||
            !derived.MetadataFeatureTags.SequenceEqual(source.MetadataFeatureTags, StringComparer.Ordinal) ||
            derived.OutputKind != source.OutputKind ||
            derived.EntryAssembly != source.EntryAssembly ||
            derived.EntryPoint != source.EntryPoint ||
            !derivedFiles.SequenceEqual(sourceFiles) ||
            entry is null ||
            metadata is null || metadata.Count != 8 ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataContractKey,
                out var contractVersion) ||
            contractVersion != RuntimeCapabilityProbeContract.MetadataContractValue ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataSourceRevisionKey,
                out var sourceRevision) ||
            source.Metadata is null ||
            !source.Metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataSourceRevisionKey,
                out var expectedSourceRevision) ||
            sourceRevision != expectedSourceRevision ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataPromotionPlanSha256Key,
                out var planSha256) ||
            !source.Metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataPromotionPlanSha256Key,
                out var expectedPlanSha256) ||
            planSha256 != expectedPlanSha256 ||
            planSha256 != sourceBinding.PlanSha256 ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataPreflightProfileSha256Key,
                out var preflightProfileSha256) ||
            !source.Metadata.TryGetValue(
                RuntimeCapabilityProbeContract.MetadataPreflightProfileSha256Key,
                out var expectedPreflightProfileSha256) ||
            preflightProfileSha256 != expectedPreflightProfileSha256 ||
            preflightProfileSha256 != sourceBinding.PreflightProfileSha256 ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.InstrumentationTransformKey,
                out var transformId) ||
            transformId != RuntimeCapabilityProbeContract.ExecutionFlowTransformId ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.InstrumentationProfileKey,
                out var profileId) ||
            profileId != RuntimeCapabilityProbeContract.ExecutionFlowProfileId ||
            !metadata.TryGetValue(
                RuntimeCapabilityProbeContract.InstrumentationAppliedKey,
                out var applied) ||
            applied != "true" ||
            !pointsValid)
        {
            throw Failed(
                "capability-execution-flow-artifact-invalid",
                "The Execution Flow artifact is not the required applied, source-bound instrumentation derivation.");
        }

        return new RuntimeCapabilityProbeArtifactBinding(
            RuntimeCapabilityProbeContract.ContractId,
            sourceBinding.SourceArtifactSha256,
            derived.ArtifactId.Value,
            entry.Digest,
            sourceBinding.PlanSha256,
            sourceBinding.PreflightProfileSha256,
            new RuntimeCapabilityProbeDerivationBinding(
                derivation.ParentArtifactId.Value,
                derivation.ProcessorId,
                derivation.ProcessorVersion,
                derivation.OptionsDigest,
                transformId,
                profileId,
                true));
    }

    private static List<RuntimeImageFileRequest> BuildImageFileRequests(
        RuntimeProfileOptions profile,
        bool jit,
        string? jitLibraryPath)
    {
        RuntimeOperationDefinition? operation = jit
            ? profile.Operations?.Jit
            : profile.Operations?.Run;
        if (operation is null)
            throw Failed("capability-operation-missing", "The selected Runtime Profile operation is missing.");

        var files = new List<RuntimeImageFileRequest>
        {
            new("helper", ResolveHelperPath(operation))
        };
        foreach (var host in ResolveExecutableHosts(profile, operation))
            files.Add(host);
        if (profile.Capabilities.Any(static capability =>
                capability is "inspection" or "execution-flow"))
        {
            files.Add(new RuntimeImageFileRequest("support-assembly", SupportAssemblyPath));
        }
        if (jit)
        {
            if (jitLibraryPath is null)
                throw Invalid("capability-jit-library-required", "The JIT library path is required.");
            files.Add(new RuntimeImageFileRequest("jit-library", jitLibraryPath));
            if (operation is RuntimeJitOperationDefinition
                {
                    SourceMappingKind: RuntimeJitSourceMappingKinds.LinuxProfiler,
                    ProfilerPath: { } profilerPath
                })
            {
                files.Add(new RuntimeImageFileRequest("profiler", profilerPath));
            }
        }

        if (files.Count is < 2 or > 8 ||
            files.Select(static file => file.Role).Distinct(StringComparer.Ordinal).Count() != files.Count ||
            files.Select(static file => file.Path).Distinct(StringComparer.Ordinal).Count() != files.Count ||
            files.Any(static file => !IsCanonicalImagePath(file.Path)))
        {
            throw Failed(
                "capability-image-artifact-selection-invalid",
                "The Runtime Profile did not resolve to a unique bounded image-artifact set.");
        }
        return files;
    }

    private static string ResolveHelperPath(RuntimeOperationDefinition operation)
    {
        var candidates = operation.Command.Argv
            .Where(token => !token.StartsWith('{') && IsExpectedHelperToken(operation, token))
            .Select(NormalizeImagePathToken)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return candidates.Length == 1
            ? candidates[0]
            : throw Failed(
                "capability-helper-path-ambiguous",
                "The Runtime Profile operation does not bind exactly one managed helper assembly.");
    }

    private static bool IsExpectedHelperToken(RuntimeOperationDefinition operation, string token) =>
        StringComparer.Ordinal.Equals(
            operation.ImplementationId,
            RuntimeOperationImplementationIds.TargetRuntimeRunner)
            ? token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            : token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<RuntimeImageFileRequest> ResolveExecutableHosts(
        RuntimeProfileOptions profile,
        RuntimeOperationDefinition operation)
    {
        var executable = operation.Command.Executable;
        if (!IsCanonicalImagePath(executable))
            throw Failed("capability-host-path-invalid", "The operation executable is not a canonical image path.");

        string? innerHost = null;
        if (StringComparer.Ordinal.Equals(operation.ImplementationId, RuntimeOperationImplementationIds.WineRunner))
        {
            if (operation.Command.Argv.Count <= 2)
                throw Failed("capability-host-path-invalid", "The Wine runner has no fixed target host.");
            innerHost = NormalizeImagePathToken(operation.Command.Argv[2]);
        }
        else if (StringComparer.Ordinal.Equals(
                     operation.ImplementationId,
                     RuntimeOperationImplementationIds.LegacyJitInspector) &&
                 StringComparer.Ordinal.Equals(operation.PathStyle, RuntimeOperationPathStyles.WineZ))
        {
            innerHost = profile.Layout.DotNetHostPath;
            if (!IsCanonicalImagePath(innerHost))
                throw Failed("capability-host-path-invalid", "The Wine CoreCLR host path is invalid.");
        }
        else if (StringComparer.Ordinal.Equals(
                     operation.ImplementationId,
                     RuntimeOperationImplementationIds.MonoJitInspector))
        {
            innerHost = profile.Layout.DotNetHostPath;
            if (!StringComparer.Ordinal.Equals(innerHost, "/usr/bin/mono"))
                throw Failed("capability-host-path-invalid", "The Mono JIT runtime host path is invalid.");
        }

        return innerHost is null
            ? [new RuntimeImageFileRequest("runtime-host", executable)]
            :
            [
                new RuntimeImageFileRequest("control-host", executable),
                new RuntimeImageFileRequest("runtime-host", innerHost)
            ];
    }

    private static string NormalizeImagePathToken(string token)
    {
        if (IsCanonicalImagePath(token))
            return token;
        const string winePrefix = "Z:\\";
        if (token.StartsWith(winePrefix, StringComparison.Ordinal))
        {
            var normalized = "/" + token[winePrefix.Length..].Replace('\\', '/');
            if (IsCanonicalImagePath(normalized))
                return normalized;
        }
        throw Failed("capability-image-path-invalid", "A Runtime Profile image path is not canonical.");
    }

    private static void ValidateImageArtifacts(
        RuntimeProfileOptions profile,
        IReadOnlyList<RuntimeImageFileInspection> artifacts,
        bool jit)
    {
        if (artifacts.Count is < 2 or > 8)
            throw Failed("capability-image-artifacts-invalid", "Image artifact inspection returned an invalid count.");
        var byRole = new Dictionary<string, RuntimeImageFileInspection>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (!IsArtifactRole(artifact.Role) || !IsCanonicalImagePath(artifact.Path) ||
                !Sha256Regex().IsMatch(artifact.Sha256 ?? string.Empty) ||
                artifact.SizeBytes is <= 0 or > MaximumEvidenceArtifactBytes ||
                !IsArtifactMetadataValid(artifact.Role, artifact.Format, artifact.Architecture) ||
                !byRole.TryAdd(artifact.Role, artifact) || !paths.Add(artifact.Path))
            {
                throw Failed(
                    "capability-image-artifacts-invalid",
                    "Image artifact inspection returned an invalid, duplicate, or unbounded identity.");
            }
        }

        RuntimeOperationDefinition? operation = jit ? profile.Operations?.Jit : profile.Operations?.Run;
        if (operation is null ||
            !MatchesArtifact(byRole, "helper", ResolveHelperPath(operation), "managed-pe", "anycpu"))
        {
            throw Failed(
                "capability-helper-identity-mismatch",
                "The inspected helper does not match the Runtime Profile operation.");
        }
        var hosts = ResolveExecutableHosts(profile, operation);
        foreach (var host in hosts)
        {
            var expectedFormat = host.Role == "runtime-host" &&
                StringComparer.Ordinal.Equals(operation.PathStyle, RuntimeOperationPathStyles.WineZ) &&
                StringComparer.Ordinal.Equals(
                    operation.ImplementationId,
                    RuntimeOperationImplementationIds.LegacyJitInspector)
                ? "pe"
                : "elf";
            if (!MatchesArtifact(byRole, host.Role, host.Path, expectedFormat, "x64"))
            {
                throw Failed(
                    "capability-host-identity-mismatch",
                    $"The inspected {host.Role} does not match the Runtime Profile operation.");
            }
        }
        if (hosts.Count == 1 && byRole.ContainsKey("control-host"))
            throw Failed("capability-host-identity-mismatch", "A single-host operation declared a control host.");

        var requiresSupport = profile.Capabilities.Any(static capability =>
            capability is "inspection" or "execution-flow");
        if (requiresSupport != byRole.ContainsKey("support-assembly") ||
            requiresSupport && !MatchesArtifact(
                byRole,
                "support-assembly",
                SupportAssemblyPath,
                "managed-pe",
                "anycpu"))
        {
            throw Failed(
                "capability-support-assembly-mismatch",
                "The image does not bind the required SharpLab.Runtime support assembly.");
        }

        if (!jit)
        {
            if (byRole.ContainsKey("jit-library") || byRole.ContainsKey("profiler"))
                throw Failed("capability-image-artifacts-invalid", "Non-JIT evidence contains JIT-only artifacts.");
            return;
        }

        if (!byRole.TryGetValue("jit-library", out var jitLibrary))
            throw Failed("capability-jit-library-missing", "The image has no inspected JIT library.");
        var wine = StringComparer.Ordinal.Equals(profile.Container.IsolationKind, RuntimeContainerIsolationKinds.Wine);
        var mono = StringComparer.Ordinal.Equals(
            operation.ImplementationId,
            RuntimeOperationImplementationIds.MonoJitInspector);
        var validJitLibrary = mono
            ? jitLibrary.Format == "elf" && jitLibrary.Architecture == "x64" &&
              StringComparer.Ordinal.Equals(jitLibrary.Path, "/usr/bin/mono-sgen")
            : wine
                ? jitLibrary.Format == "pe" && jitLibrary.Architecture == "x64" &&
                  jitLibrary.Path.EndsWith("clrjit.dll", StringComparison.OrdinalIgnoreCase)
                : jitLibrary.Format == "elf" && jitLibrary.Architecture == "x64" &&
                  jitLibrary.Path.EndsWith("/libclrjit.so", StringComparison.Ordinal);
        if (!validJitLibrary)
        {
            throw Failed("capability-jit-library-invalid", "The inspected JIT library has the wrong platform identity.");
        }
        var profilerPath = (operation as RuntimeJitOperationDefinition)?.ProfilerPath;
        var requiresProfiler = (operation as RuntimeJitOperationDefinition)?.SourceMappingKind ==
            RuntimeJitSourceMappingKinds.LinuxProfiler;
        if (requiresProfiler != byRole.ContainsKey("profiler") ||
            requiresProfiler && (profilerPath is null ||
                !MatchesArtifact(byRole, "profiler", profilerPath, "elf", "x64")))
        {
            throw Failed("capability-profiler-identity-mismatch", "The inspected JIT profiler identity is invalid.");
        }
    }

    private static bool MatchesArtifact(
        Dictionary<string, RuntimeImageFileInspection> artifacts,
        string role,
        string path,
        string format,
        string architecture) =>
        artifacts.TryGetValue(role, out var artifact) &&
        StringComparer.Ordinal.Equals(artifact.Path, path) &&
        StringComparer.Ordinal.Equals(artifact.Format, format) &&
        StringComparer.Ordinal.Equals(artifact.Architecture, architecture);

    private static bool IsArtifactRole(string value) => value is
        "helper" or "control-host" or "runtime-host" or "support-assembly" or "jit-library" or "profiler";

    private static bool IsArtifactMetadataValid(string role, string format, string architecture) => role switch
    {
        "helper" or "support-assembly" => format == "managed-pe" && architecture == "anycpu",
        "control-host" or "runtime-host" or "jit-library" =>
            format is "elf" or "pe" && architecture == "x64",
        "profiler" => format == "elf" && architecture == "x64",
        _ => false
    };

    private static void RequireSuccessfulRun(RuntimeProbeExecution execution, string label)
    {
        RequireAuditedCompletion(execution, label);
        if (execution.Completion.FailureCode is not null ||
            execution.Result is not RunResult
            {
                Status: RunTerminalStatus.Completed,
                OutputTruncated: false
            } ||
            execution.Audit is not
            {
                TerminalStatus: "completed",
                TerminalExitCode: 0
            } audit ||
            !audit.FrameKinds.TryGetValue("Exit", out var exits) || exits != 1)
        {
            throw Failed(
                "capability-run-probe-failed",
                $"The {label} probe did not produce one successful RuntimeFrame exit.");
        }
    }

    private static void RequireAuditedCompletion(RuntimeProbeExecution execution, string label)
    {
        if (!execution.Completion.ExecutionStarted || execution.Result is null || execution.Audit is null)
        {
            throw Failed(
                execution.Completion.FailureCode ?? "capability-execution-not-audited",
                execution.Completion.FailureMessage ?? $"The {label} probe did not produce an execution audit.");
        }
        if (!execution.Completion.CleanupSucceeded ||
            !execution.Audit.ContainerRemoved || !execution.Audit.ProcessTreeRemoved)
        {
            throw Failed(
                "capability-cleanup-failed",
                $"The {label} probe did not prove complete one-shot resource cleanup.");
        }
        if (!ContainerIdRegex().IsMatch(execution.Audit.ContainerId ?? string.Empty))
            throw Failed("capability-container-id-invalid", $"The {label} probe returned an invalid container ID.");
    }

    private static string Utf8(byte[] bytes, string label)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Failed("capability-output-not-utf8", $"{label} is not strict UTF-8.");
        }
    }

    private static void RequireMarker(string value, string marker)
    {
        if (!value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Contains(marker, StringComparer.Ordinal))
        {
            throw Failed(
                "capability-security-marker-missing",
                $"The runtime probe did not emit the required marker '{marker}'.");
        }
    }

    private async Task<PortablePdbEvidence> ReadPortablePdbEvidenceAsync(
        ArtifactRef artifactRef,
        ArtifactBundleDescriptor descriptor,
        RuntimeProfileOptions profile,
        CancellationToken cancellationToken)
    {
        var entryPath = ArtifactPath.Normalize(descriptor.Manifest.EntryAssembly);
        var assembly = descriptor.Manifest.Files.SingleOrDefault(file =>
            StringComparer.Ordinal.Equals(ArtifactPath.Normalize(file.Path), entryPath));
        if (assembly is null || assembly.Role is not "managed-pe")
        {
            throw Failed(
                "capability-entry-assembly-invalid",
                "The mapped JIT probe artifact has no managed PE entry assembly.");
        }
        var pdbPath = Path.ChangeExtension(entryPath, ".pdb").Replace('\\', '/');
        var pdb = descriptor.Manifest.Files.SingleOrDefault(file =>
            file.Role == "portable-pdb" &&
            StringComparer.Ordinal.Equals(ArtifactPath.Normalize(file.Path), pdbPath));
        if (pdb is null)
        {
            throw Failed(
                "capability-portable-pdb-missing",
                "The mapped JIT probe artifact has no sibling Portable PDB.");
        }

        var assemblyBytes = await ReadArtifactFileAsync(
            artifactRef,
            descriptor,
            assembly,
            MaximumEvidenceArtifactBytes,
            cancellationToken).ConfigureAwait(false);
        var pdbBytes = await ReadArtifactFileAsync(
            artifactRef,
            descriptor,
            pdb,
            MaximumPortablePdbBytes,
            cancellationToken).ConfigureAwait(false);

        try
        {
            using var peStream = new MemoryStream(assemblyBytes, writable: false);
            using var peReader = new PEReader(peStream, PEStreamOptions.PrefetchEntireImage);
            if (!peReader.HasMetadata)
                throw Failed("capability-entry-assembly-invalid", "The capability probe entry assembly has no metadata.");
            var peMetadata = peReader.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
            var canonicalMethods = ValidateCanonicalProbeAssembly(peReader, peMetadata);

            using var pdbStream = new MemoryStream(pdbBytes, writable: false);
            using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(
                pdbStream,
                MetadataStreamOptions.PrefetchMetadata);
            var pdbReader = pdbProvider.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
            var header = pdbReader.DebugMetadataHeader;
            if (header is null || header.Id.IsDefaultOrEmpty || header.Id.Length != 20)
                throw Failed("capability-portable-pdb-id-invalid", "The Portable PDB has no canonical content ID.");
            var contentId = new BlobContentId(header.Id);
            var codeViewMatches = peReader.ReadDebugDirectory()
                .Where(static entry => entry.Type == DebugDirectoryEntryType.CodeView)
                .Any(entry =>
                {
                    if (entry.Stamp != contentId.Stamp)
                        return false;
                    var codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
                    return codeView.Age == 1 && codeView.Guid == contentId.Guid;
                });
            if (!codeViewMatches)
            {
                throw Failed(
                    "capability-portable-pdb-identity-mismatch",
                    "The Portable PDB content ID does not match the PE CodeView identity.");
            }

            var methodCount = Math.Min(
                peMetadata.GetTableRowCount(TableIndex.MethodDef),
                pdbReader.GetTableRowCount(TableIndex.MethodDebugInformation));
            var methods = new Dictionary<int, IReadOnlyList<PdbSequencePoint>>();
            var sequencePointCount = 0;
            for (var row = 1; row <= methodCount; row++)
            {
                var methodHandle = MetadataTokens.MethodDefinitionHandle(row);
                var method = peMetadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue;
                var information = pdbReader.GetMethodDebugInformation(
                    MetadataTokens.MethodDebugInformationHandle(row));
                var points = new List<PdbSequencePoint>();
                foreach (var point in information.GetSequencePoints())
                {
                    var documentHandle = point.Document.IsNil ? information.Document : point.Document;
                    if (point.IsHidden || documentHandle.IsNil)
                        continue;
                    var document = SanitizeDocumentPath(
                        pdbReader.GetString(pdbReader.GetDocument(documentHandle).Name));
                    points.Add(new PdbSequencePoint(
                        point.Offset,
                        document,
                        point.StartLine,
                        point.StartColumn,
                        point.EndLine,
                        point.EndColumn));
                    sequencePointCount = checked(sequencePointCount + 1);
                    if (sequencePointCount > 1_000_000)
                    {
                        throw Failed(
                            "capability-portable-pdb-too-large",
                            "The Portable PDB contains too many sequence points.");
                    }
                }
                if (points.Count > 0)
                    methods.Add(MetadataTokens.GetToken(methodHandle), points);
            }
            if (sequencePointCount == 0)
            {
                throw Failed(
                    "capability-portable-pdb-empty",
                    "The Portable PDB contains no visible sequence points for executable methods.");
            }
            if (!methods.TryGetValue(canonicalMethods.MultipleSequencePointsToken, out var probePoints) ||
                probePoints.Count < 2)
            {
                throw Failed(
                    "capability-probe-pdb-contract-mismatch",
                    "The canonical MultipleSequencePoints probe method has insufficient Portable PDB coverage.");
            }

            var workspacePath = RuntimeProfileCommandBuilder.WorkspaceFile(pdbPath);
            if (StringComparer.Ordinal.Equals(
                    profile.Operations?.Jit?.PathStyle,
                    RuntimeOperationPathStyles.WineZ))
            {
                workspacePath = $"Z:{workspacePath.Replace('/', '\\')}";
            }
            return new PortablePdbEvidence(
                workspacePath,
                pdb.Digest,
                Convert.ToHexStringLower(header.Id.AsSpan()),
                sequencePointCount,
                methods);
        }
        catch (RuntimeCapabilityPreflightException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            BadImageFormatException or
            InvalidOperationException or
            ArgumentException)
        {
            throw Failed("capability-portable-pdb-invalid", exception.Message);
        }
    }

    private static CanonicalProbeMethodTokens ValidateCanonicalProbeAssembly(
        PEReader peReader,
        MetadataReader metadata)
    {
        var corHeader = peReader.PEHeaders.CorHeader;
        if (corHeader is null ||
            (corHeader.Flags & CorFlags.NativeEntryPoint) != 0 ||
            (corHeader.Flags & CorFlags.ILOnly) == 0)
        {
            throw Failed(
                "capability-probe-pe-contract-mismatch",
                "The canonical capability probe must be an IL-only managed executable.");
        }
        var assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        if (!StringComparer.Ordinal.Equals(assemblyName, "SharpLabNext.RuntimeCapabilityProbe"))
        {
            throw Failed(
                "capability-probe-pe-contract-mismatch",
                "The capability probe assembly identity is not canonical.");
        }

        var programTypes = metadata.TypeDefinitions
            .Where(handle =>
            {
                var type = metadata.GetTypeDefinition(handle);
                return StringComparer.Ordinal.Equals(metadata.GetString(type.Namespace),
                           "SharpLabNext.RuntimeCapabilityProbe") &&
                       StringComparer.Ordinal.Equals(metadata.GetString(type.Name), "Program");
            })
            .ToArray();
        if (programTypes.Length != 1)
        {
            throw Failed(
                "capability-probe-pe-contract-mismatch",
                "The capability probe Program type is not canonical.");
        }

        var required = new Dictionary<string, (int ParameterCount, int Token)>(StringComparer.Ordinal)
        {
            ["Main"] = (1, 0),
            ["MultipleSequencePoints"] = (1, 0),
            ["WindowsAbi"] = (2, 0)
        };
        foreach (var methodHandle in metadata.GetTypeDefinition(programTypes[0]).GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            var name = metadata.GetString(method.Name);
            if (!required.TryGetValue(name, out var expected))
                continue;
            if (expected.Token != 0 ||
                method.RelativeVirtualAddress == 0 ||
                (method.Attributes & (MethodAttributes.Public | MethodAttributes.Static)) !=
                (MethodAttributes.Public | MethodAttributes.Static) ||
                method.GetParameters().Count(handle =>
                    metadata.GetParameter(handle).SequenceNumber != 0) != expected.ParameterCount)
            {
                throw Failed(
                    "capability-probe-pe-contract-mismatch",
                    $"The canonical capability probe method '{name}' is invalid or ambiguous.");
            }
            required[name] = (expected.ParameterCount, MetadataTokens.GetToken(methodHandle));
        }
        if (required.Values.Any(static method => method.Token == 0) ||
            corHeader.EntryPointTokenOrRelativeVirtualAddress != required["Main"].Token)
        {
            throw Failed(
                "capability-probe-pe-contract-mismatch",
                "The capability probe entry point or required method set is not canonical.");
        }
        return new CanonicalProbeMethodTokens(
            required["MultipleSequencePoints"].Token,
            required["WindowsAbi"].Token);
    }

    private async Task<byte[]> ReadArtifactFileAsync(
        ArtifactRef artifactRef,
        ArtifactBundleDescriptor descriptor,
        ArtifactFileDescriptor file,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (file.Size is <= 0 || file.Size > maximumBytes || file.Size > int.MaxValue ||
            !Sha256Regex().IsMatch(file.Digest ?? string.Empty))
        {
            throw Failed(
                "capability-artifact-file-invalid",
                "A required capability artifact file has an invalid declared identity or size.");
        }
        var entry = descriptor.Entries.SingleOrDefault(candidate =>
            StringComparer.Ordinal.Equals(
                ArtifactPath.Normalize(candidate.Path),
                ArtifactPath.Normalize(file.Path)));
        if (entry is null || entry.Size != file.Size ||
            !StringComparer.Ordinal.Equals(entry.Digest, file.Digest) ||
            entry.ContentRef.Value != file.Digest)
        {
            throw Failed(
                "capability-artifact-file-descriptor-mismatch",
                "A required artifact file does not match its bundle descriptor.");
        }

        try
        {
            await using var response = await artifactStore.OpenArtifactFileReadAsync(
                artifactRef,
                file.Path,
                cancellationToken).ConfigureAwait(false);
            if (response.Length is { } contentLength && contentLength != file.Size)
            {
                throw Failed(
                    "capability-artifact-file-size-mismatch",
                    "Artifact Store content length does not match the manifest.");
            }
            using var output = new MemoryStream(checked((int)file.Size));
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await response.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                total = checked(total + read);
                if (total > file.Size || total > maximumBytes)
                {
                    throw Failed(
                        "capability-artifact-file-size-mismatch",
                        "Artifact Store returned more bytes than the verified manifest permits.");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (total != file.Size)
            {
                throw Failed(
                    "capability-artifact-file-size-mismatch",
                    "Artifact Store returned fewer bytes than the verified manifest declares.");
            }
            var bytes = output.ToArray();
            var actual = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actual),
                    Encoding.ASCII.GetBytes(file.Digest)))
            {
                throw Failed(
                    "capability-artifact-file-digest-mismatch",
                    "Artifact Store bytes do not match the verified manifest digest.");
            }
            return bytes;
        }
        catch (RuntimeCapabilityPreflightException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Unavailable("capability-artifact-read-failed", exception.Message);
        }
    }

    private static string ValidateJitMappings(
        IReadOnlyList<RuntimeJitAuditMethod> methods,
        PortablePdbEvidence pdb,
        string mappingKind)
    {
        var mappingSources = methods
            .Select(static method => method.MappingSource)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var expectedSource = mappingKind switch
        {
            RuntimeJitSourceMappingKinds.LinuxProfiler when mappingSources is ["ordinary"] => "ordinary",
            RuntimeJitSourceMappingKinds.LinuxProfiler when mappingSources is ["rich"] => "rich",
            RuntimeJitSourceMappingKinds.CheckedJitDebugInfo
                when mappingSources is ["checked-jit-debug-info"] => "checked-jit-debug-info",
            _ => throw Failed(
                "capability-jit-mapping-source-invalid",
                "The JIT probe did not produce one mapping source permitted by its Runtime Profile.")
        };

        var allRanges = new List<RuntimeJitEvidenceRange>();
        foreach (var method in methods)
        {
            if (!MethodTokenRegex().IsMatch(method.MetadataToken ?? string.Empty) ||
                !int.TryParse(
                    method.MetadataToken.AsSpan(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var metadataToken) ||
                !pdb.Methods.TryGetValue(metadataToken, out var points))
            {
                throw Failed(
                    "capability-jit-method-token-invalid",
                    "A JIT method does not match a real MethodDef in the PE and Portable PDB.");
            }
            foreach (var range in method.EvidenceRanges)
            {
                if (range.IlOffset < 0 || range.NativeStartOffset < 0 ||
                    range.NativeEndOffset <= range.NativeStartOffset ||
                    !points.Any(point =>
                        point.IlOffset == range.IlOffset &&
                        StringComparer.Ordinal.Equals(point.Document, range.Document) &&
                        point.StartLine == range.StartLine &&
                        point.StartColumn == range.StartColumn &&
                        point.EndLine == range.EndLine &&
                        point.EndColumn == range.EndColumn))
                {
                    throw Failed(
                        "capability-jit-range-pdb-mismatch",
                        "A retained JIT source range does not match its MethodDef Portable PDB sequence point.");
                }
                allRanges.Add(range);
            }
        }
        var distinct = allRanges.Select(SourceIdentity).Distinct(StringComparer.Ordinal).Count();
        if (allRanges.Count < 2 || distinct < 2)
        {
            throw Failed(
                "capability-jit-mapping-insufficient",
                "Mapped JIT evidence requires at least two PDB-matched ranges and source spans.");
        }
        return expectedSource;
    }

    private JsonObject BuildEvidenceDocument(
        RuntimeCapabilityPreflightRequest request,
        RuntimeProfileOptions profile,
        RuntimeSecurityPolicyOptions policy,
        RuntimeImageInspection image,
        IReadOnlyList<RuntimeImageFileInspection> artifacts,
        RuntimeProbeExecution execution,
        RuntimeLifecycleEvidence lifecycle,
        RuntimeCapabilityProbeArtifactBinding probeArtifact,
        string capability,
        object details)
    {
        RequireAuditedCompletion(execution, capability);
        var audit = execution.Audit!;
        if (execution.Completion.FailureCode is not null || audit.TerminalStatus != "completed" ||
            audit.TerminalExitCode != 0 ||
            !audit.FrameKinds.TryGetValue("Exit", out var exits) || exits != 1 ||
            audit.RuntimeFrameCount is < 1 or > 100_000 ||
            audit.Stdout.LongLength > 16_777_216 || audit.Stderr.LongLength > 16_777_216 ||
            !Sha256Regex().IsMatch(audit.EntryAssemblySha256 ?? string.Empty) ||
            !StringComparer.Ordinal.Equals(
                audit.EntryAssemblySha256,
                probeArtifact.EntryAssemblySha256))
        {
            throw Failed(
                "capability-evidence-invocation-invalid",
                $"The {capability} evidence invocation was not a successful bounded RuntimeFrame execution.");
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["profileId"] = profile.Id,
            ["capability"] = capability,
            ["image"] = JsonSerializer.SerializeToNode(new
            {
                reference = image.ImmutableReference,
                imageId = image.ImageId
            }, EvidenceJsonOptions),
            ["sourceRevision"] = request.SourceRevision,
            ["completedAtUtc"] = DateTimeOffset.UtcNow.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture),
            ["result"] = "passed",
            ["producer"] = JsonSerializer.SerializeToNode(new
            {
                id = ProducerId,
                sourceRevision = request.SourceRevision,
                planSha256 = request.PlanSha256
            }, EvidenceJsonOptions),
            ["probeArtifact"] = JsonSerializer.SerializeToNode(probeArtifact, EvidenceJsonOptions),
            ["artifacts"] = JsonSerializer.SerializeToNode(
                artifacts.OrderBy(static artifact => artifact.Role, StringComparer.Ordinal)
                    .Select(static artifact => new
                    {
                        role = artifact.Role,
                        path = artifact.Path,
                        sha256 = artifact.Sha256,
                        sizeBytes = artifact.SizeBytes,
                        format = artifact.Format,
                        architecture = artifact.Architecture
                    }).ToArray(),
                EvidenceJsonOptions)
        };
        var invocation = new JsonObject
        {
            ["implementation"] = audit.Implementation,
            ["command"] = JsonSerializer.SerializeToNode(audit.Command, EvidenceJsonOptions),
            ["entryAssembly"] = JsonSerializer.SerializeToNode(new
            {
                path = audit.EntryAssemblyPath,
                sha256 = audit.EntryAssemblySha256
            }, EvidenceJsonOptions),
            ["outcome"] = "succeeded",
            ["exitCode"] = 0,
            ["runtimeFrameCount"] = audit.RuntimeFrameCount,
            ["terminalFrameKind"] = "Exit",
            ["terminalStatus"] = "completed",
            ["stdoutBytes"] = audit.Stdout.LongLength,
            ["stderrBytes"] = audit.Stderr.LongLength
        };
        if (capability == "jit-asm")
            invocation["methodFilter"] = request.MethodFilter;
        root["invocation"] = invocation;

        var sandboxNode = new JsonObject
        {
            ["supervisorPolicyId"] = sandbox.PolicyId,
            ["securityPolicyId"] = policy.Id,
            ["seccompSha256"] = sandbox.SeccompProfileSha256,
            ["containerId"] = audit.ContainerId,
            ["networkMode"] = "none",
            ["networkProbeBlocked"] = true,
            ["readOnlyRootFilesystem"] = true,
            ["readOnlyProbeBlocked"] = true,
            ["capDrop"] = new JsonArray("ALL"),
            ["noNewPrivileges"] = true,
            ["user"] = RuntimeContainerIsolation.ResolveWorkspaceOwner(
                RuntimeJobExecutor.ResolveIsolationKind(profile)).User,
            ["nanoCpus"] = policy.NanoCpus,
            ["memoryBytes"] = policy.MemoryBytes,
            ["pidsLimit"] = policy.PidsLimit,
            ["deadlineMilliseconds"] = checked(policy.MaximumDurationSeconds * 1000),
            ["outputLimitBytes"] = policy.MaximumOutputBytes,
            ["tmpfsBytes"] = policy.TmpfsBytes
        };
        if (!string.IsNullOrWhiteSpace(_options.Sandbox.AppArmorProfile))
            sandboxNode["apparmorProfile"] = _options.Sandbox.AppArmorProfile;
        root["sandbox"] = sandboxNode;
        root["lifecycle"] = JsonSerializer.SerializeToNode(new
        {
            outputOverflow = lifecycle.OutputOverflow,
            timeout = lifecycle.Timeout,
            cancellation = lifecycle.Cancellation,
            processTreeCleanup = lifecycle.ProcessTreeCleanup
        }, EvidenceJsonOptions);

        var detailName = capability switch
        {
            "run" => "run",
            "jit-asm" => "jit",
            "inspection" => "inspection",
            "execution-flow" => "executionFlow",
            _ => throw Failed("capability-detail-invalid", "The evidence capability is not supported.")
        };
        root[detailName] = details is JsonObject json
            ? json
            : JsonSerializer.SerializeToNode(details, EvidenceJsonOptions);
        return root;
    }

    private static RuntimeCapabilityValidationContext BuildValidationContext(
        RuntimeCapabilityPreflightRequest request,
        RuntimeProfileOptions profile,
        RuntimeImageInspection image,
        IReadOnlyList<RuntimeImageFileInspection> runArtifacts,
        IReadOnlyList<RuntimeImageFileInspection>? jitArtifacts,
        string? mappingSource)
    {
        var retained = new Dictionary<string, RuntimeImageFileInspection>(StringComparer.Ordinal);
        foreach (var artifact in runArtifacts.Concat(jitArtifacts ?? []))
        {
            if (retained.TryGetValue(artifact.Path, out var existing))
            {
                if (existing != artifact)
                {
                    throw Failed(
                        "capability-retained-artifact-conflict",
                        "Two capability documents observed conflicting identities for one image path.");
                }
                continue;
            }
            retained.Add(artifact.Path, artifact);
        }
        var expectedCapabilities = profile.Capabilities
            .Where(static capability => capability is "run" or "jit-asm" or "inspection" or "execution-flow")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (expectedCapabilities.Contains("jit-asm", StringComparer.Ordinal) != (mappingSource is not null))
            throw Failed("capability-jit-context-invalid", "The JIT mapping context is incomplete.");
        return new RuntimeCapabilityValidationContext(
            profile.Id,
            request.SourceRevision,
            image.ImmutableReference,
            image.ImageId,
            expectedCapabilities,
            retained,
            mappingSource);
    }

    private static void ValidateEvidenceSet(
        RuntimeCapabilityValidationContext context,
        IReadOnlyDictionary<string, JsonObject> evidence)
    {
        if (!context.ExpectedCapabilities.SequenceEqual(
                evidence.Keys.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw Failed(
                "capability-evidence-set-incomplete",
                "The preflight result does not contain exactly one document for every declared capability.");
        }
        foreach (var (capability, document) in evidence)
        {
            if (!StringComparer.Ordinal.Equals(document["profileId"]?.GetValue<string>(), context.ProfileId) ||
                !StringComparer.Ordinal.Equals(document["capability"]?.GetValue<string>(), capability) ||
                !StringComparer.Ordinal.Equals(document["sourceRevision"]?.GetValue<string>(), context.SourceRevision) ||
                !StringComparer.Ordinal.Equals(document["image"]?["reference"]?.GetValue<string>(), context.ImageReference) ||
                !StringComparer.Ordinal.Equals(document["image"]?["imageId"]?.GetValue<string>(), context.ImageId))
            {
                throw Failed(
                    "capability-evidence-context-mismatch",
                    "A capability document does not match the internally verified preflight context.");
            }
        }
        if (context.MappingSource is { } mappingSource &&
            !StringComparer.Ordinal.Equals(
                evidence["jit-asm"]["jit"]?["mapping"]?["source"]?.GetValue<string>(),
                mappingSource))
        {
            throw Failed(
                "capability-evidence-context-mismatch",
                "The JIT evidence does not retain the Supervisor-derived mapping source.");
        }
    }

    private static string SourceIdentity(RuntimeJitEvidenceRange range) =>
        $"{range.Document}\0{range.StartLine}\0{range.StartColumn}\0{range.EndLine}\0{range.EndColumn}";

    private static string SanitizeDocumentPath(string path)
    {
        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(static segment => segment is not ("." or "..") && !segment.EndsWith(':'))
            .TakeLast(8)
            .ToArray();
        var sanitized = segments.Length == 0 ? "source" : string.Join('/', segments);
        return sanitized.Length <= 512 ? sanitized : sanitized[^512..];
    }

    private static bool IsCanonicalImagePath(string? value) =>
        value is not null && value.Length is > 1 and <= 4096 && value[0] == '/' && value[^1] != '/' &&
        !value.Contains("//", StringComparison.Ordinal) && !value.Contains('\\') &&
        !value.Any(char.IsControl) &&
        value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(static segment => segment is not ("." or ".."));

    private static void ValidateStableId(string? value, string parameterName)
    {
        if (value is null || !StableIdRegex().IsMatch(value))
            throw Invalid("invalid-capability-request", $"{parameterName} is malformed.");
    }

    private static RuntimeCapabilityPreflightException Invalid(string code, string message) =>
        new(code, message, StatusCodes.Status400BadRequest);

    private static RuntimeCapabilityPreflightException Failed(string code, string message) =>
        new(code, message, StatusCodes.Status422UnprocessableEntity);

    private static RuntimeCapabilityPreflightException Unavailable(string code, string message) =>
        new(code, message, StatusCodes.Status503ServiceUnavailable);

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[^@\\s]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImmutableImageReferenceRegex();

    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex GitCommitRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerIdRegex();

    [GeneratedRegex("^0x06[0-9a-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex MethodTokenRegex();
}

internal sealed record RuntimeProbeExecution(
    string OperationId,
    OperationResult? Result,
    RuntimeJobAudit? Audit,
    RuntimeJobMeasurementCompletion Completion);

internal sealed record RuntimeLifecycleProbe(
    string Result,
    string TerminalStatus,
    bool ContainerRemoved,
    bool ProcessTreeRemoved);

internal sealed record RuntimeLifecycleEvidence(
    RuntimeLifecycleProbe OutputOverflow,
    RuntimeLifecycleProbe Timeout,
    RuntimeLifecycleProbe Cancellation,
    RuntimeLifecycleProbe ProcessTreeCleanup);

internal sealed record PdbSequencePoint(
    int IlOffset,
    string Document,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

internal sealed record PortablePdbEvidence(
    string Path,
    string Sha256,
    string ContentId,
    int SequencePointCount,
    IReadOnlyDictionary<int, IReadOnlyList<PdbSequencePoint>> Methods);

internal sealed record RuntimeCapabilityProbeTarget(
    string TargetFramework,
    string EntryAssembly,
    string ArtifactFormat,
    string ReferenceSetId);

internal sealed record CanonicalProbeMethodTokens(
    int MultipleSequencePointsToken,
    int WindowsAbiToken);

internal sealed record CanonicalProbeArtifact(
    RuntimeCapabilityProbeArtifactBinding Binding,
    PortablePdbEvidence PortablePdb);

internal sealed record RuntimeCapabilityProbeArtifactBinding(
    string Contract,
    string SourceArtifactSha256,
    string ArtifactSha256,
    string EntryAssemblySha256,
    string PlanSha256,
    string PreflightProfileSha256,
    RuntimeCapabilityProbeDerivationBinding? Derivation);

internal sealed record RuntimeCapabilityProbeDerivationBinding(
    string ParentArtifactSha256,
    string ProcessorId,
    string ProcessorVersion,
    string OptionsSha256,
    string TransformId,
    string ProfileId,
    bool Applied);

internal sealed record RuntimeCapabilityValidationContext(
    string ProfileId,
    string SourceRevision,
    string ImageReference,
    string ImageId,
    IReadOnlyList<string> ExpectedCapabilities,
    IReadOnlyDictionary<string, RuntimeImageFileInspection> RetainedImageFiles,
    string? MappingSource);

public sealed class RuntimeCapabilityPreflightException(
    string code,
    string publicMessage,
    int statusCode) : Exception(publicMessage)
{
    public string Code { get; } = code;
    public string PublicMessage { get; } = publicMessage;
    public int StatusCode { get; } = statusCode;
}

using System.Buffers;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;
using SharpLabNext.Operations;
using SharpLabNext.RuntimeProfile.Sdk;
using SharpLabNext.RuntimeProtocol;

namespace SharpLabNext.RuntimeSupervisor;

public static class RuntimeJobRequestValidator
{
    public static object? Validate(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var common = ValidateCommon(request.RequestId, request.IdempotencyKey, request.PipelineResolutionId, request.RuntimeProfileId, request.ArtifactRef, request.DeadlineUtc);
        if (common is not null) return common;

        if (request.Options.Arguments.Count > 32 || request.Options.Arguments.Any(static argument => argument.Length > 4096 || argument.Contains('\0')))
            return Error("invalid-arguments", "At most 32 arguments of 4096 characters are allowed.");

        if (request.Options.Stdin is { Length: > 65_536 } || request.Options.Stdin?.Contains('\0') == true)
            return Error("invalid-stdin", "Standard input exceeds the 65536 character limit or contains NUL.");

        return ValidateStableId(request.Options.SecurityPolicyId, "security-policy-id");
    }

    public static object? Validate(JitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var common = ValidateCommon(request.RequestId, request.IdempotencyKey, request.PipelineResolutionId, request.RuntimeProfileId, request.ArtifactRef, request.DeadlineUtc);
        if (common is not null) return common;

        if (request.Options.MethodFilter is { Length: > 256 } || request.Options.MethodFilter?.Contains('\0') == true)
            return Error("invalid-method-filter", "The JIT method filter exceeds 256 characters or contains NUL.");

        if (request.Options.ProviderId != "coreclr-jitdisasm")
        {
            return Error("unsupported-jit-provider", "Only the coreclr-jitdisasm provider is enabled.");
        }

        if (request.Options.TieringPolicyId is not ("tier0-diffable" or "tier1" or "default"))
        {
            return Error("unsupported-tiering-policy", "The requested JIT tiering policy is not enabled.");
        }

        if (request.Options.PgoPolicyId is not ("disabled" or "default"))
        {
            return Error("unsupported-pgo-policy", "The requested JIT PGO policy is not enabled.");
        }

        return ValidateStableId(request.Options.SecurityPolicyId, "security-policy-id");
    }

    private static object? ValidateCommon(string requestId, string idempotencyKey, string pipelineResolutionId, string runtimeProfileId, ArtifactRef artifactRef, DateTimeOffset deadlineUtc)
    {
        foreach (var (value, field) in new[]
                 {
                     (requestId, "request-id"),
                     (pipelineResolutionId, "pipeline-resolution-id"),
                     (runtimeProfileId, "runtime-profile-id")
                 })
        {
            var validation = ValidateStableId(value, field);
            if (validation is not null)
            {
                return validation;
            }
        }

        var idempotencyValidation = ValidateIdempotencyKey(idempotencyKey);
        if (idempotencyValidation is not null)
        {
            return idempotencyValidation;
        }

        try
        {
            _ = ArtifactStoreProtocol.GetDigest(artifactRef);
        }
        catch (ArgumentException)
        {
            return Error("invalid-artifact-ref", "The artifact reference is malformed.");
        }

        return deadlineUtc <= DateTimeOffset.UtcNow
            ? Error("deadline-expired", "The request deadline has already expired.") : null;
    }

    private static object? ValidateStableId(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            return Error("invalid-id", $"The {field} is malformed.");
        }

        return null;
    }

    private static object? ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(static character => character is < '!' or > '~'))
        {
            return Error("invalid-id", "The idempotency-key is malformed.");
        }

        return null;
    }

    private static object Error(string code, string message) => new { Error = code, Message = message };
}

public sealed partial class RuntimeJobExecutor(OperationStore operations, BoundedOperationScheduler scheduler, IArtifactStoreClient artifactStore, IDockerEngineClient docker, RuntimeSessionRegistry runtimeSessions, IOptions<RuntimeSupervisorOptions> configuredOptions, ServiceIdentity serviceIdentity, ILogger<RuntimeJobExecutor> logger)
{
    internal const string CapabilityHangReadyMarker = "SLN-CAPABILITY-HANG-READY-V1";
    private static readonly byte[] CapabilityHangReadyMarkerBytes =
        Encoding.UTF8.GetBytes(CapabilityHangReadyMarker);
    // Runtime child/frame and JIT-summary JSON are SharpLabNext-owned
    // interaction protocols. They use the same strict PascalCase wire shape
    // as the business contracts; external Docker/LSP protocols have separate
    // serializers at their own boundaries.
    private static readonly JsonSerializerOptions JsonOptions = CreateRuntimeJsonOptions();
    private static readonly IReadOnlyList<string> MeasurementKeeperEntrypoint = ["/bin/sh", "-c"];
    private static readonly IReadOnlyList<string> MeasurementKeeperCommand =
    [
        // Docker Desktop does not reliably forward SIGTERM to a bare `exec sleep`
        // process. Keep PID 1 in a blocking shell builtin instead: this gives
        // the supervisor a deterministic 143 exit without adding a child to the
        // measured cgroup (the helper deliberately requires exactly one live
        // keeper). Runtime workloads can create descendants under PID 1, so
        // reap every SIGCHLD before the sidecar snapshots the cgroup; otherwise
        // Wine's short-lived helper processes remain as zombies and count
        // against the strict PID contract.
        "trap 'while wait 2>/dev/null; do :; done' CHLD; trap 'exit 143' TERM INT; IFS= read -r _"
    ];

    private static JsonSerializerOptions CreateRuntimeJsonOptions()
    {
        var options = ContractJson.CreateSerializerOptions();
        options.MaxDepth = 32;
        return options;
    }
    private const int MaximumExceptionDepth = 32;
    private readonly RuntimeSupervisorOptions _options = configuredOptions.Value;

    public void QueueRun(OperationStart operation, RunRequest request, string? runtimeSessionId = null) =>
        _ = QueueRunCore(operation, request, runtimeSessionId, measurement: null);

    public void QueueJit(OperationStart operation, JitRequest request, string? runtimeSessionId = null) =>
        _ = QueueJitCore(operation, request, runtimeSessionId, measurement: null);

    internal bool QueueRunForMeasurement(OperationStart operation, RunRequest request, RuntimeJobMeasurementRegistration measurement) =>
        QueueRunCore(operation, request, runtimeSessionId: null, measurement);

    internal bool QueueRunForCapabilityProbe(OperationStart operation, RunRequest request, RuntimeJobMeasurementRegistration measurement, TimeSpan runtimeDeadlineAfterMarker)
    {
        if (runtimeDeadlineAfterMarker <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeDeadlineAfterMarker), "The post-marker runtime deadline must be positive.");
        }
        if (measurement.CollectResources)
        {
            throw new InvalidOperationException("Capability probe post-marker deadlines cannot be combined with resource measurements.");
        }
        return QueueRunCore(operation, request, runtimeSessionId: null, measurement, runtimeDeadlineAfterMarker);
    }

    internal bool QueueJitForMeasurement(OperationStart operation, JitRequest request, RuntimeJobMeasurementRegistration measurement) =>
        QueueJitCore(operation, request, runtimeSessionId: null, measurement);

    private bool QueueRunCore(OperationStart operation, RunRequest request, string? runtimeSessionId, RuntimeJobMeasurementRegistration? measurement, TimeSpan? runtimeDeadlineAfterMarker = null)
    {
        measurement?.BindCancellation(operation.CancellationToken);
        var queued = scheduler.TryQueue(operation, () => ExecuteRunAsync(operation, request, runtimeSessionId, measurement, runtimeDeadlineAfterMarker));
        if (!queued)
        {
            measurement?.Reject("operation-queue-rejected", "The runtime operation queue rejected the performance sample.");
        }
        return queued;
    }

    private bool QueueJitCore(OperationStart operation, JitRequest request, string? runtimeSessionId, RuntimeJobMeasurementRegistration? measurement)
    {
        measurement?.BindCancellation(operation.CancellationToken);
        var queued = scheduler.TryQueue(operation, () => ExecuteJitAsync(operation, request, runtimeSessionId, measurement));
        if (!queued)
        {
            measurement?.Reject("operation-queue-rejected", "The runtime operation queue rejected the performance sample.");
        }
        return queued;
    }

    private Task ExecuteRunAsync(OperationStart operation, RunRequest request, string? runtimeSessionId, RuntimeJobMeasurementRegistration? measurement, TimeSpan? runtimeDeadlineAfterMarker = null) =>
        ExecuteAsync(
            operation,
            request.RuntimeProfileId,
            request.ArtifactRef,
            request.Options.SecurityPolicyId,
            request.DeadlineUtc,
            RuntimeJobKind.Run,
            (profile, descriptor) => CreateRunCommand(profile, request, descriptor.Manifest.EntryAssembly),
            (profile, _) => CreateRunEnvironment(profile, request.Options.Instrumentation),
            (capture, exit, elapsed, profile, outputTruncated) =>
                CreateRunResult(capture, exit, elapsed, profile, outputTruncated),
            request.Options.Stdin,
            request.Options.Instrumentation,
            runtimeSessionId,
            measurement,
            runtimeDeadlineAfterMarker);

    private Task ExecuteJitAsync(OperationStart operation, JitRequest request, string? runtimeSessionId, RuntimeJobMeasurementRegistration? measurement) =>
        ExecuteAsync(
            operation,
            request.RuntimeProfileId,
            request.ArtifactRef,
            request.Options.SecurityPolicyId,
            request.DeadlineUtc,
            RuntimeJobKind.Jit,
            (profile, descriptor) => CreateJitCommand(profile, request, descriptor.Manifest.EntryAssembly),
            (profile, descriptor) => CreateJitEnvironment(profile, request, descriptor.Manifest.EntryAssembly),
            (capture, exit, elapsed, profile, outputTruncated) =>
                CreateJitResultAsync(operation.Handle.OperationId, request, capture, exit, elapsed, profile, outputTruncated),
            stdin: null,
            instrumentation: null,
            runtimeSessionId,
            measurement);

    private async Task ExecuteAsync(
        OperationStart operation,
        string runtimeProfileId,
        ArtifactRef artifactRef,
        string securityPolicyId,
        DateTimeOffset requestedDeadlineUtc,
        RuntimeJobKind kind,
        Func<RuntimeProfileOptions, ArtifactBundleDescriptor, IReadOnlyList<string>> createCommand,
        Func<RuntimeProfileOptions, ArtifactBundleDescriptor, IReadOnlyDictionary<string, string>> createEnvironment,
        Func<RuntimeFrameCapture, RuntimeContainerExit, TimeSpan, RuntimeProfileOptions, bool, object> createResult,
        string? stdin,
        RunInstrumentation? instrumentation,
        string? runtimeSessionId,
        RuntimeJobMeasurementRegistration? measurement,
        TimeSpan? runtimeDeadlineAfterMarker = null)
    {
        if (measurement is not null && runtimeSessionId is not null)
            throw new InvalidOperationException("Performance measurements require a one-shot runtime job.");
        measurement?.MarkExecutionStarted();
        var stopwatch = Stopwatch.StartNew();
        string? containerId = null;
        string? measurementSidecarContainerId = null;
        string? materializerContainerId = null;
        string? workspaceVolumeName = null;
        string? measurementVolumeName = null;
        string? leaseToken = null;
        RuntimeSessionLease? sessionLease = null;
        RuntimeSessionAdmissionLease? oneShotAdmission = null;
        var sessionReusable = false;
        RuntimeProfileOptions? profile = null;
        ArtifactBundleDescriptor? descriptor = null;
        IReadOnlyList<string>? command = null;
        string? implementation = null;
        RuntimeFrameCapture? capture = null;
        IRuntimeContainerResourceMonitor? resourceMonitor = null;
        RuntimeContainerMeasurement? runtimeMeasurement = null;
        var postCompletionSampleCheckpoint = 0;
        RuntimeContainerResourceUsage? resourceUsage = null;
        Stream? attachedOutput = null;
        OperationResult? measurementResult = null;
        string? measurementFailureCode = null;
        string? measurementFailureMessage = null;
        var cleanupSucceeded = true;
        var telemetryOutcome = SharpLabNextTelemetryOutcome.Failed;
        CancellationTokenSource? runtimeExecution = null;
        try
        {
            profile = GetProfile(runtimeProfileId);
            var measurementContext = measurement?.CollectResources == true
                ? measurement.Context ?? throw new InvalidOperationException("Resource measurements require a trusted helper registration.") : null;
            if (runtimeDeadlineAfterMarker is not null && measurementContext is not null)
            {
                throw new InvalidOperationException("Capability probe post-marker deadlines cannot be combined with resource measurements.");
            }
            ValidateCapability(profile, kind == RuntimeJobKind.Run ? "run" : "jit-asm");
            var isolationKind = ResolveIsolationKind(profile);
            var policy = GetPolicy(securityPolicyId);
            if (!profile.AllowedSecurityPolicyIds.Contains(policy.Id, StringComparer.Ordinal))
            {
                throw new RuntimeJobFailureException("security-policy-not-allowed", WorkerErrorCategory.InvalidArgument, "The selected runtime profile does not allow this security policy.", retryable: false);
            }
            var maximumDeadline = DateTimeOffset.UtcNow.AddSeconds(policy.MaximumDurationSeconds);
            var effectiveDeadline = requestedDeadlineUtc < maximumDeadline ? requestedDeadlineUtc : maximumDeadline;
            using var deadline = new CancellationTokenSource(effectiveDeadline - DateTimeOffset.UtcNow);
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationToken, deadline.Token);

            Append(operation, new ProgressOperationEventPayload("artifact", "Resolving immutable artifact.", 0.05));
            descriptor = await artifactStore.GetArtifactAsync(artifactRef, execution.Token) ?? throw new RuntimeJobFailureException("artifact-not-found", WorkerErrorCategory.NotFound, "The requested artifact is not available.", retryable: false);
            ValidateCompatibility(descriptor.Manifest, profile);
            ValidateInstrumentation(descriptor.Manifest, instrumentation);
            var lease = await artifactStore.AcquireLeaseAsync(artifactRef, operation.Handle.OperationId, TimeSpan.FromSeconds(_options.ArtifactLeaseSeconds), execution.Token);
            leaseToken = lease.LeaseToken;

            Append(operation, new ProgressOperationEventPayload("artifact", "Preparing isolated artifact archive.", 0.15));
            await using var archive = await BuildArtifactArchiveAsync(descriptor, policy.MaximumArtifactBytes, stdin, isolationKind, includeReady: true, execution.Token);

            command = createCommand(profile, descriptor);
            implementation = kind == RuntimeJobKind.Run
                ? profile.Operations?.Run?.ImplementationId ?? profile.Layout.RunnerKind : profile.Operations?.Jit?.ImplementationId ?? "legacy-layout-jit";
            var environment = new Dictionary<string, string>(createEnvironment(profile, descriptor), StringComparer.Ordinal)
            {
                // Legacy CoreCLR helpers tail redirected output files in
                // process.  Propagate the same policy budget that the
                // supervisor enforces so they can stop capturing as soon as
                // an output bomb crosses the limit.
                ["SHARPLABNEXT_MAX_OUTPUT_BYTES"] = policy.MaximumOutputBytes.ToString(CultureInfo.InvariantCulture)
            };
            Append(operation, new ProgressOperationEventPayload("workspace", "Materializing an isolated in-memory workspace.", 0.22));
            var createStopwatch = Stopwatch.StartNew();
            try
            {
                if (!string.IsNullOrWhiteSpace(runtimeSessionId) && runtimeSessions.Enabled)
                {
                    sessionLease = await runtimeSessions.AcquireAsync(new RuntimeSessionRequest(runtimeSessionId, serviceIdentity.ReleaseId, profile.Image, command, environment, policy, isolationKind, profile.Container.WinePrefixPath, _options.ContainerLabel, _options.ResourceScope), archive, execution.Token).ConfigureAwait(false);
                    containerId = sessionLease.ContainerId;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(runtimeSessionId))
                    {
                        oneShotAdmission = await runtimeSessions.AcquireOneShotAdmissionAsync(runtimeSessionId, execution.Token).ConfigureAwait(false);
                    }

                    var materialization = await docker.MaterializeWorkspaceAsync(operation.Handle.OperationId, serviceIdentity.ReleaseId, profile.Image, archive, policy, isolationKind, _options.ContainerLabel, _options.ResourceScope, createMeasurementControl: measurement?.CollectResources == true, cancellationToken: execution.Token).ConfigureAwait(false);
                    workspaceVolumeName = materialization.VolumeName;
                    measurementVolumeName = materialization.MeasurementVolumeName;
                    materializerContainerId = materialization.MaterializerContainerId;
                    if (measurement?.CollectResources == true && measurementVolumeName is null)
                    {
                        throw new InvalidOperationException("A measured runtime has no isolated measurement volume.");
                    }
                    var spec = new RuntimeContainerSpec(
                        CreateContainerName(kind),
                        operation.Handle.OperationId,
                        serviceIdentity.ReleaseId,
                        profile.Image,
                        measurementContext is null ? command : MeasurementKeeperCommand,
                        environment,
                        policy,
                        _options.ContainerLabel,
                        _options.ResourceScope,
                        workspaceVolumeName,
                        CaptureTraceParent(),
                        isolationKind,
                        profile.Container.WinePrefixPath,
                        measurementContext is null ? null : MeasurementKeeperEntrypoint);
                    containerId = await docker.CreateContainerAsync(spec, execution.Token).ConfigureAwait(false);
                }
                SharpLabNextTelemetry.Metrics.RecordContainerPhase(SharpLabNextContainerPhase.Create, runtimeProfileId, createStopwatch.Elapsed, SharpLabNextTelemetryOutcome.Succeeded);
            }
            catch
            {
                SharpLabNextTelemetry.Metrics.RecordContainerPhase(SharpLabNextContainerPhase.Create, runtimeProfileId, createStopwatch.Elapsed, SharpLabNextTelemetryOutcome.Failed);
                throw;
            }
            Append(operation, new ProgressOperationEventPayload("container", sessionLease?.Reused == true ? $"Reused session-isolated container {containerId}." : $"Created isolated container {containerId}.", 0.35));
            var startStopwatch = Stopwatch.StartNew();
            RuntimeContainerExit exit;
            try
            {
                if (measurementContext is null)
                {
                    attachedOutput = await docker.AttachContainerOutputAsync(containerId, execution.Token).ConfigureAwait(false);
                }
                if (measurementContext is not null)
                {
                    try
                    {
                        resourceMonitor = await docker.StartContainerResourceMonitorAsync(containerId, execution.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (execution.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        measurementFailureCode = "resource-monitor-start-failed";
                        measurementFailureMessage = exception.Message;
                        throw;
                    }
                }
                await docker.StartContainerAsync(containerId, execution.Token);
                measurement?.MarkContainerStarted();
                SharpLabNextTelemetry.Metrics.RecordContainerPhase(SharpLabNextContainerPhase.Start, runtimeProfileId, startStopwatch.Elapsed, SharpLabNextTelemetryOutcome.Succeeded);
            }
            catch
            {
                SharpLabNextTelemetry.Metrics.RecordContainerPhase(SharpLabNextContainerPhase.Start, runtimeProfileId, startStopwatch.Elapsed, SharpLabNextTelemetryOutcome.Failed);
                throw;
            }
            Append(operation, new ProgressOperationEventPayload("runtime", "Runtime child started.", 0.5));

            capture = new RuntimeFrameCapture();
            if (measurementContext is null)
            {
                Action? hangReadyObserver = measurement is not null
                    ? measurement.MarkProbeReady : null;
                var runtimeToken = execution.Token;
                if (runtimeDeadlineAfterMarker is { } postMarkerDeadline)
                {
                    runtimeExecution = CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationToken, execution.Token);
                    var runtimeTimer = runtimeExecution;
                    var timeoutArmed = 0;
                    var markReady = hangReadyObserver;
                    hangReadyObserver = () =>
                    {
                        markReady?.Invoke();
                        if (Interlocked.Exchange(ref timeoutArmed, 1) == 0)
                            runtimeTimer.CancelAfter(postMarkerDeadline);
                    };
                    runtimeToken = runtimeExecution.Token;
                }
                if (sessionLease is null && await RemoveQuietlyAsync(materializerContainerId).ConfigureAwait(false))
                {
                    materializerContainerId = null;
                }

                var output = attachedOutput ?? throw new InvalidOperationException("The runtime output stream was not attached before container start.");
                attachedOutput = null;
                await using (output.ConfigureAwait(false))
                {
                    await CaptureFramesAsync(operation, output, capture, policy.MaximumOutputBytes, kind, instrumentation, runtimeToken, hangReadyObserver);
                }
                exit = await docker.WaitContainerAsync(containerId, runtimeToken).ConfigureAwait(false);
            }
            else
            {
                var monitor = resourceMonitor ?? throw new InvalidOperationException("A measured runtime has no resource monitor.");
                var measurementVolume = measurementVolumeName ?? throw new InvalidOperationException("A measured runtime has no isolated measurement control volume.");
                if (!StringComparer.Ordinal.Equals(measurementContext.MeasurementHelper.Image.Reference, _options.MeasurementHelperImage) ||
                    !StringComparer.Ordinal.Equals(measurementContext.MeasurementHelper.Image.ImageId, _options.MeasurementHelperImageId) ||
                    !StringComparer.Ordinal.Equals(measurementContext.MeasurementHelper.Implementation, RuntimePerformancePreflightCoordinator.MeasurementHelperImplementation) ||
                    !StringComparer.Ordinal.Equals(measurementContext.MeasurementHelper.Entrypoint, RuntimePerformancePreflightCoordinator.MeasurementHelperEntrypoint) ||
                    !StringComparer.Ordinal.Equals(measurementContext.MeasurementHelper.SourceRevision, _options.PromotionPreflightSourceRevision) ||
                    !StringComparer.Ordinal.Equals(measurementContext.MeasurementHelper.ContentSha256, RuntimePerformancePreflightCoordinator.MeasurementHelperContentSha256))
                {
                    throw MeasurementProtocolFailure("The registered measurement helper no longer matches Supervisor configuration.");
                }

                var targetExitTask = docker.WaitContainerAsync(containerId, execution.Token);
                try
                {
                    await AwaitMeasurementPhaseAsync(monitor.WaitForFirstSampleAsync(execution.Token), targetExitTask, "positive stats baseline", execution.Token).ConfigureAwait(false);

                    var running = await AwaitMeasurementPhaseAsync(docker.InspectRunningContainerAsync(containerId, execution.Token), targetExitTask, "target PID inspection", execution.Token).ConfigureAwait(false);
                    if (!running.Running || running.HostPid <= 0 || !StringComparer.Ordinal.Equals(running.ContainerId, containerId))
                    {
                        throw MeasurementProtocolFailure("The measured keeper did not expose a trusted running host PID.");
                    }

                    var token = Guid.NewGuid().ToString("N");
                    measurementSidecarContainerId = await AwaitMeasurementPhaseAsync(docker.CreateRuntimeMeasurementSidecarAsync(new RuntimeMeasurementSidecarSpec(operation.Handle.OperationId, serviceIdentity.ReleaseId, measurementContext.MeasurementHelper.Image.ImageId, containerId, running.HostPid, token, measurementVolume, _options.ContainerLabel, _options.ResourceScope, CaptureTraceParent()), execution.Token), targetExitTask, "measurement sidecar creation", execution.Token).ConfigureAwait(false);
                    await AwaitMeasurementPhaseAsync(docker.StartContainerAsync(measurementSidecarContainerId, execution.Token), targetExitTask, "measurement sidecar start", execution.Token).ConfigureAwait(false);
                    var sidecarExitTask = docker.WaitContainerAsync(measurementSidecarContainerId, execution.Token);
                    await AwaitMeasurementPhaseAsync(
                        docker.WaitForRuntimeMeasurementArmedAsync(measurementSidecarContainerId, token, containerId, execution.Token),
                        targetExitTask,
                        sidecarExitTask,
                        "measurement sidecar armed record",
                        execution.Token).ConfigureAwait(false);

                    if (await RemoveQuietlyAsync(materializerContainerId).ConfigureAwait(false))
                        materializerContainerId = null;

                    var runtimeUser = RuntimeContainerIsolation.ResolveWorkspaceOwner(isolationKind).User;
                    var execCommand = new[] { measurementContext.RuntimeEntrypoint }.Concat(command).ToArray();
                    IReadOnlyDictionary<string, string>? execEnvironment =
                        isolationKind is RuntimeContainerIsolationKind.WineRoot or RuntimeContainerIsolationKind.WineNonRoot
                            ? new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                // Keep the cleanup mode off the keeper's
                                // container-wide environment. It is a
                                // workload-exec concern; enabling it on PID1
                                // would fork the keeper and break the strict
                                // one-process measurement contract.
                                ["SHARPLABNEXT_WINE_CLEANUP"] = "1"
                            }
                            : null;
                    var execId = await AwaitMeasurementPhaseAsync(docker.CreateContainerExecAsync(containerId, new RuntimeExecSpec(execCommand, runtimeUser, "/workspace", execEnvironment), execution.Token), targetExitTask, sidecarExitTask, "runtime Exec creation", execution.Token).ConfigureAwait(false);
                    await using (var execOutput = await AwaitMeasurementPhaseAsync(docker.StartContainerExecAsync(execId, execution.Token), targetExitTask, sidecarExitTask, "runtime Exec start", execution.Token).ConfigureAwait(false))
                    {
                        await AwaitMeasurementPhaseAsync(CaptureFramesAsync(operation, execOutput, capture, policy.MaximumOutputBytes, kind, instrumentation, execution.Token), targetExitTask, sidecarExitTask, "runtime Exec frame capture", execution.Token).ConfigureAwait(false);
                    }

                    var execInspection = await AwaitMeasurementPhaseAsync(docker.InspectContainerExecAsync(execId, execution.Token), targetExitTask, sidecarExitTask, "runtime Exec inspection", execution.Token).ConfigureAwait(false);
                    var execExitCode = execInspection.ExitCode;
                    var protocolExitCode = capture.Exit?.ExitCode;
                    if (execInspection.Running || execExitCode is null || protocolExitCode is null || execExitCode.Value != protocolExitCode.Value)
                    {
                        throw MeasurementProtocolFailure("The runtime Exec and framed protocol exit states do not match.");
                    }

                    var afterExec = await AwaitMeasurementPhaseAsync(docker.InspectRunningContainerAsync(containerId, execution.Token), targetExitTask, sidecarExitTask, "post-Exec keeper inspection", execution.Token).ConfigureAwait(false);
                    if (!afterExec.Running || afterExec.HostPid != running.HostPid)
                    {
                        throw MeasurementProtocolFailure("The measured keeper was not running after workload completion.");
                    }

                    await AwaitMeasurementPhaseAsync(docker.UploadRuntimeMeasurementSignalAsync(measurementSidecarContainerId, token, containerId, RuntimeMeasurementSignalKind.Capture, execution.Token), targetExitTask, sidecarExitTask, "measurement capture signal", execution.Token).ConfigureAwait(false);
                    runtimeMeasurement = await AwaitMeasurementPhaseAsync(
                        docker.WaitForRuntimeMeasurementAsync(measurementSidecarContainerId, token, containerId, execution.Token),
                        targetExitTask,
                        sidecarExitTask,
                        "cgroup completion record",
                        execution.Token).ConfigureAwait(false);
                    postCompletionSampleCheckpoint = monitor.SampleCount;
                    await AwaitMeasurementPhaseAsync(monitor.WaitForSampleAfterAsync(postCompletionSampleCheckpoint, execution.Token), targetExitTask, sidecarExitTask, "post-completion stats sample", execution.Token).ConfigureAwait(false);
                    if (monitor.SampleCount <= postCompletionSampleCheckpoint)
                    {
                        throw MeasurementProtocolFailure("Docker stats did not produce a positive sample after completion.");
                    }
                    await AwaitMeasurementPhaseAsync(docker.UploadRuntimeMeasurementSignalAsync(measurementSidecarContainerId, token, containerId, RuntimeMeasurementSignalKind.Finish, execution.Token), targetExitTask, sidecarExitTask, "measurement finish signal", execution.Token, allowSidecarExitAfterPhase: true).ConfigureAwait(false);
                    var sidecarExit = await AwaitMeasurementPhaseAsync(sidecarExitTask, targetExitTask, "measurement sidecar exit", execution.Token).ConfigureAwait(false);
                    if (sidecarExit.StatusCode != 0 || sidecarExit.OomKilled || !string.IsNullOrWhiteSpace(sidecarExit.Error))
                    {
                        throw MeasurementProtocolFailure("The measurement sidecar did not exit cleanly after the finish signal.");
                    }
                    if (await RemoveQuietlyAsync(measurementSidecarContainerId).ConfigureAwait(false))
                        measurementSidecarContainerId = null;

                    await docker.StopContainerAsync(containerId, TimeSpan.FromSeconds(1), execution.Token).ConfigureAwait(false);
                    var keeperExit = await targetExitTask.ConfigureAwait(false);
                    if (keeperExit.StatusCode != 143 || keeperExit.OomKilled || !string.IsNullOrWhiteSpace(keeperExit.Error))
                    {
                        throw MeasurementProtocolFailure("The measured keeper did not exit cleanly after Supervisor stopped it.");
                    }
                    exit = new RuntimeContainerExit(execExitCode.Value, OomKilled: false, Error: null);
                }
                catch (OperationCanceledException) when (execution.IsCancellationRequested)
                {
                    throw;
                }
                catch (RuntimeJobFailureException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw MeasurementProtocolFailure($"The measured runtime protocol failed: {exception.Message}");
                }

                try
                {
                    var completionMeasurement = runtimeMeasurement ?? throw MeasurementProtocolFailure("The measurement sidecar did not publish a completion record.");
                    resourceUsage = await StopResourceMonitorAsync(monitor).ConfigureAwait(false);
                    resourceMonitor = null;
                    resourceUsage = resourceUsage with { PeakMemoryBytes = Math.Max(resourceUsage.PeakMemoryBytes, completionMeasurement.PeakMemoryBytes), CompletionPeakMemoryBytes = completionMeasurement.PeakMemoryBytes, PostCompletionSampleCount = checked(resourceUsage.SampleCount - postCompletionSampleCheckpoint) };
                }
                catch (Exception exception)
                {
                    cleanupSucceeded = false;
                    resourceMonitor = null;
                    measurementFailureCode = "resource-monitor-stop-failed";
                    measurementFailureMessage = exception.Message;
                    throw;
                }
            }
            stopwatch.Stop();
            var result = (OperationResult)await ResolveResultAsync(createResult(capture, exit, stopwatch.Elapsed, profile, capture.OutputTruncated));
            measurementResult = result;
            telemetryOutcome = TelemetryOutcome(result);
            sessionReusable = result switch
            {
                RunResult { Status: RunTerminalStatus.OutOfMemory or RunTerminalStatus.ProcessCrash } => false,
                JitResult { Status: JitTerminalStatus.OutOfMemory or JitTerminalStatus.ProcessCrash } => false,
                _ => !exit.OomKilled
            };
            Append(operation, new TypedResultOperationEventPayload(result));
            Append(operation, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, stopwatch.Elapsed));
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            measurementFailureCode ??= "operation-cancelled";
            measurementFailureMessage ??= "The runtime operation was cancelled.";
            telemetryOutcome = SharpLabNextTelemetryOutcome.Cancelled;
            sessionReusable = sessionLease is not null
                ? await StopSessionContainerQuietlyAsync(containerId).ConfigureAwait(false) : false;
            if (sessionLease is null && measurement?.CollectResources != true)
                await KillQuietlyAsync(containerId);
            stopwatch.Stop();
            if (profile is not null)
            {
                OperationResult result = kind == RuntimeJobKind.Run
                    ? CreateCancelledRunResult(stopwatch.Elapsed, profile) : CreateCancelledJitResult(stopwatch.Elapsed, profile);
                measurementResult = result;
                Append(operation, new TypedResultOperationEventPayload(result));
            }

            Append(operation, new CompletedOperationEventPayload(OperationCompletionStatus.Cancelled, stopwatch.Elapsed));
        }
        catch (OperationCanceledException)
        {
            measurementFailureCode ??= "operation-timeout";
            measurementFailureMessage ??= "The runtime operation exceeded its deadline.";
            telemetryOutcome = SharpLabNextTelemetryOutcome.TimedOut;
            sessionReusable = sessionLease is not null
                ? await StopSessionContainerQuietlyAsync(containerId).ConfigureAwait(false) : false;
            if (sessionLease is null && measurement?.CollectResources != true)
                await KillQuietlyAsync(containerId);
            stopwatch.Stop();
            if (profile is not null)
            {
                OperationResult result = kind == RuntimeJobKind.Run
                    ? CreateTimeoutRunResult(stopwatch.Elapsed, profile) : CreateTimeoutJitResult(stopwatch.Elapsed, profile);
                measurementResult = result;
                Append(operation, new TypedResultOperationEventPayload(result));
                Append(operation, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, stopwatch.Elapsed));
            }
            else
            {
                Fail(operation, "deadline-exceeded", WorkerErrorCategory.DeadlineExceeded, "The runtime job deadline was exceeded.", false);
            }
        }
        catch (RuntimeOutputLimitException)
        {
            measurementFailureCode ??= "operation-output-limit-exceeded";
            measurementFailureMessage ??= "The runtime operation exceeded its output limit.";
            telemetryOutcome = SharpLabNextTelemetryOutcome.Overloaded;
            sessionReusable = sessionLease is not null
                ? await StopSessionContainerQuietlyAsync(containerId).ConfigureAwait(false) : false;
            if (sessionLease is null && measurement?.CollectResources != true)
                await KillQuietlyAsync(containerId);
            stopwatch.Stop();
            if (profile is not null)
            {
                OperationResult result = kind == RuntimeJobKind.Run
                    ? CreateOutputLimitRunResult(stopwatch.Elapsed, profile) : CreateOutputLimitJitResult(stopwatch.Elapsed, profile);
                measurementResult = result;
                Append(operation, new TypedResultOperationEventPayload(result));
                Append(operation, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, stopwatch.Elapsed));
            }
        }
        catch (RuntimeJobFailureException exception)
        {
            measurementFailureCode ??= exception.Code;
            measurementFailureMessage ??= exception.PublicMessage;
            Fail(operation, exception.Code, exception.Category, exception.PublicMessage, exception.Retryable);
        }
        catch (Exception exception)
        {
            measurementFailureCode ??= "runtime-job-failed";
            measurementFailureMessage ??= exception.Message;
            LogRuntimeJobFailed(logger, operation.Handle.OperationId, exception);
            Fail(operation, "runtime-job-failed", WorkerErrorCategory.Unavailable, "The isolated runtime job could not be completed.", retryable: true);
        }
        finally
        {
            runtimeExecution?.Dispose();
            stopwatch.Stop();
            if (attachedOutput is not null)
            {
                try
                {
                    await attachedOutput.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupSucceeded = false;
                    measurementFailureCode ??= "runtime-output-detach-failed";
                    measurementFailureMessage ??= exception.Message;
                }
            }
            if (resourceMonitor is not null)
            {
                try
                {
                    resourceUsage = await StopResourceMonitorAsync(resourceMonitor).ConfigureAwait(false);
                    resourceMonitor = null;
                }
                catch (Exception exception)
                {
                    cleanupSucceeded = false;
                    resourceMonitor = null;
                    measurementFailureCode ??= "resource-monitor-stop-failed";
                    measurementFailureMessage ??= exception.Message;
                }
            }
            SharpLabNextTelemetry.Metrics.RecordRuntime(kind == RuntimeJobKind.Run ? SharpLabNextRuntimeOperation.Run : SharpLabNextRuntimeOperation.Jit, runtimeProfileId, stopwatch.Elapsed, telemetryOutcome);
            if (sessionLease is not null)
            {
                cleanupSucceeded = false;
                await sessionLease.CompleteAsync(sessionReusable).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    cleanupSucceeded &= await RemoveQuietlyAsync(measurementSidecarContainerId);
                    cleanupSucceeded &= await RemoveQuietlyAsync(containerId);
                    cleanupSucceeded &= await RemoveQuietlyAsync(materializerContainerId);
                    cleanupSucceeded &= await RemoveWorkspaceVolumeQuietlyAsync(workspaceVolumeName);
                    cleanupSucceeded &= await RemoveWorkspaceVolumeQuietlyAsync(measurementVolumeName);
                }
                finally
                {
                    if (oneShotAdmission is not null)
                        await oneShotAdmission.DisposeAsync().ConfigureAwait(false);
                }
            }
            cleanupSucceeded &= await ReleaseLeaseQuietlyAsync(leaseToken);
            var audit = measurement is null
                ? null : await CreateAuditAsync(containerId, command, implementation, descriptor, profile, capture, kind, cleanupSucceeded).ConfigureAwait(false);
            measurement?.Complete(measurementFailureCode, measurementFailureMessage, resourceUsage, measurementResult, cleanupSucceeded, audit);
        }
    }

    private async Task<RuntimeJobAudit?> CreateAuditAsync(string? containerId, IReadOnlyList<string>? command, string? implementation, ArtifactBundleDescriptor? descriptor, RuntimeProfileOptions? profile, RuntimeFrameCapture? capture, RuntimeJobKind kind, bool cleanupSucceeded)
    {
        if (containerId is null || command is null || implementation is null || descriptor is null || profile is null || capture is null)
        {
            return null;
        }

        var normalizedEntry = ArtifactPath.Normalize(descriptor.Manifest.EntryAssembly);
        var entry = descriptor.Manifest.Files.SingleOrDefault(file => StringComparer.Ordinal.Equals(ArtifactPath.Normalize(file.Path), normalizedEntry));
        if (entry is null)
            return null;

        var entryPath = kind == RuntimeJobKind.Jit
            ? RuntimeProfileCommandBuilder.CreateJitCommand(profile, normalizedEntry, null).FirstOrDefault(token => IsWorkspaceAssemblyPath(token, profile.Operations?.Jit?.PathStyle)) : RuntimeProfileCommandBuilder.CreateRunCommand(profile, normalizedEntry, []).FirstOrDefault(token => IsWorkspaceAssemblyPath(token, profile.Operations?.Run?.PathStyle));
        entryPath ??= command.FirstOrDefault(token => IsWorkspaceAssemblyPath(token, kind == RuntimeJobKind.Jit ? profile.Operations?.Jit?.PathStyle : profile.Operations?.Run?.PathStyle));
        if (entryPath is null)
            return null;

        var containerRemoved = false;
        if (cleanupSucceeded)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var remaining = await docker.ListManagedContainersAsync(_options.ContainerLabel, _options.ResourceScope, timeout.Token).ConfigureAwait(false);
                containerRemoved = remaining.All(container => !StringComparer.Ordinal.Equals(container.Id, containerId));
            }
            catch (Exception exception)
            {
                LogAuditCleanupVerificationFailed(logger, containerId, exception);
            }
        }

        return new RuntimeJobAudit(
            containerId,
            command.ToArray(),
            implementation,
            entryPath,
            entry.Digest,
            capture.RuntimeFrameCount,
            new Dictionary<string, int>(capture.FrameKinds, StringComparer.Ordinal),
            capture.Stdout.ToArray(),
            capture.Stderr.ToArray(),
            capture.InspectionPayloads.ToArray(),
            capture.FlowPayloads.ToArray(),
            MapObservedException(capture.Exception),
            capture.Exit?.Status,
            capture.Exit?.ExitCode,
            ParseJitAuditMethods(capture.JitSummary.ToArray()),
            containerRemoved,
            containerRemoved);
    }

    private static bool IsWorkspaceAssemblyPath(string token, string? pathStyle) =>
        StringComparer.Ordinal.Equals(pathStyle, RuntimeOperationPathStyles.WineZ)
            ? token.StartsWith("Z:\\workspace\\", StringComparison.OrdinalIgnoreCase) &&
              (token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) : token.StartsWith("/workspace/", StringComparison.Ordinal) &&
              (token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    private static RuntimeObservedException? MapObservedException(ChildExceptionPayload? exception) =>
        exception is null
            ? null : new RuntimeObservedException(exception.TypeName, exception.Message, exception.StackTrace, MapObservedException(exception.InnerException));

    private static RuntimeJitAuditMethod[] ParseJitAuditMethods(ReadOnlySpan<byte> summaryBytes)
    {
        if (summaryBytes.IsEmpty)
            return [];

        var summary = JsonSerializer.Deserialize<JitSummaryPayload>(summaryBytes, JsonOptions);
        return summary?.Methods.Where(static method => method.Status == "prepared").Select(static method => new RuntimeJitAuditMethod(
                method.Method,
                method.DisplayName ?? method.Method,
                method.NativeCodeSize,
                method.InstructionCount,
                method.EvidenceRanges?.Where(static range => range.IlOffset >= 0 && range.NativeStartOffset >= 0 && range.NativeEndOffset > range.NativeStartOffset && !string.IsNullOrWhiteSpace(range.Document) && range.StartLine >= 1 && range.StartColumn >= 1 && range.EndLine >= range.StartLine && range.EndColumn >= 1).Select(static range => new RuntimeJitEvidenceRange(range.IlOffset, range.NativeStartOffset, range.NativeEndOffset, range.Document, range.StartLine, range.StartColumn, range.EndLine, range.EndColumn))
                    .ToArray() ?? [],
                method.MappingSource ?? "none")).ToArray() ?? [];
    }

    private static ValueTask<object> ResolveResultAsync(object result) =>
        result is Task<object> task ? new ValueTask<object>(task) : ValueTask.FromResult(result);

    private static SharpLabNextTelemetryOutcome TelemetryOutcome(OperationResult result) => result switch
    {
        RunResult { Status: RunTerminalStatus.Completed } => SharpLabNextTelemetryOutcome.Succeeded,
        RunResult { Status: RunTerminalStatus.Cancelled } => SharpLabNextTelemetryOutcome.Cancelled,
        RunResult { Status: RunTerminalStatus.Timeout } => SharpLabNextTelemetryOutcome.TimedOut,
        RunResult { Status: RunTerminalStatus.OutOfMemory } => SharpLabNextTelemetryOutcome.OutOfMemory,
        RunResult { Status: RunTerminalStatus.ProcessCrash } => SharpLabNextTelemetryOutcome.Crashed,
        RunResult { Status: RunTerminalStatus.OutputLimitExceeded } => SharpLabNextTelemetryOutcome.Overloaded,
        JitResult { Status: JitTerminalStatus.Completed } => SharpLabNextTelemetryOutcome.Succeeded,
        JitResult { Status: JitTerminalStatus.Cancelled } => SharpLabNextTelemetryOutcome.Cancelled,
        JitResult { Status: JitTerminalStatus.Timeout } => SharpLabNextTelemetryOutcome.TimedOut,
        JitResult { Status: JitTerminalStatus.OutOfMemory } => SharpLabNextTelemetryOutcome.OutOfMemory,
        JitResult { Status: JitTerminalStatus.ProcessCrash } => SharpLabNextTelemetryOutcome.Crashed,
        JitResult { Status: JitTerminalStatus.OutputLimitExceeded } => SharpLabNextTelemetryOutcome.Overloaded,
        _ => SharpLabNextTelemetryOutcome.Failed
    };

    private RuntimeProfileOptions GetProfile(string id)
    {
        try
        {
            return _options.GetProfile(id);
        }
        catch (KeyNotFoundException)
        {
            throw new RuntimeJobFailureException("runtime-profile-not-installed", WorkerErrorCategory.UnsupportedCapability, "The selected runtime profile is not installed.", retryable: false);
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
            throw new RuntimeJobFailureException("security-policy-not-installed", WorkerErrorCategory.InvalidArgument, "The selected runtime security policy is not installed.", retryable: false);
        }
    }

    internal static void ValidateCompatibility(ArtifactManifest manifest, RuntimeProfileOptions profile)
    {
        if (manifest.OutputKind is not (BuildOutputKind.Console or BuildOutputKind.Library or BuildOutputKind.WindowsApplication))
        {
            throw new RuntimeJobFailureException("incompatible-artifact", WorkerErrorCategory.IncompatibleArtifact, "The artifact manifest does not declare a concrete output kind.", retryable: false);
        }

        ValidateJSharp20Compatibility(manifest, profile);
        var acceptedRuntimeFamilies = profile.AcceptedRuntimeFamilies.Count == 0
            ? [profile.Family] : profile.AcceptedRuntimeFamilies;
        if (!profile.AcceptedArtifactFormats.Contains(manifest.ArtifactFormat, StringComparer.Ordinal) || !acceptedRuntimeFamilies.Contains(manifest.RuntimeRequirement.Family, StringComparer.Ordinal))
        {
            throw new RuntimeJobFailureException("incompatible-artifact", WorkerErrorCategory.IncompatibleArtifact, "The selected runtime cannot load this artifact format or runtime family.", retryable: false);
        }

        var frameworkNames = new HashSet<string>(StringComparer.Ordinal);
        var incompatibleFramework = manifest.RuntimeRequirement.Frameworks.Any(framework => string.IsNullOrWhiteSpace(framework.Name) || string.IsNullOrWhiteSpace(framework.MinimumVersion) || !frameworkNames.Add(framework.Name) || !AcceptsFramework(profile, acceptedRuntimeFamilies, framework));
        if (incompatibleFramework)
        {
            throw new RuntimeJobFailureException("incompatible-framework", WorkerErrorCategory.IncompatibleArtifact, "The selected runtime does not satisfy the artifact's framework requirements.", retryable: false);
        }

        if (!RuntimeArchitectureCompatibility.IsCompatible(manifest.RuntimeRequirement.Architecture, profile.Architecture))
        {
            throw new RuntimeJobFailureException("incompatible-architecture", WorkerErrorCategory.IncompatibleArtifact, "The artifact architecture is incompatible with the selected runtime.", retryable: false);
        }

        var missingRuntimeFeatures = manifest.RuntimeRequirement.RequiredRuntimeFeatureTags.Except(profile.ProvidedRuntimeFeatureTags, StringComparer.Ordinal).ToArray();
        var missingMetadataFeatures = manifest.MetadataFeatureTags.Except(profile.ProvidedMetadataFeatureTags, StringComparer.Ordinal).ToArray();
        if (missingRuntimeFeatures.Length > 0 || missingMetadataFeatures.Length > 0)
        {
            throw new RuntimeJobFailureException("incompatible-feature-tags", WorkerErrorCategory.IncompatibleArtifact, "The selected runtime does not provide the artifact's required feature tags.", retryable: false);
        }
    }

    private static bool AcceptsFramework(RuntimeProfileOptions profile, IReadOnlyList<string> acceptedRuntimeFamilies, FrameworkRequirement framework)
    {
        if (profile.AcceptedFrameworks.Any(accepted => RuntimeProfileValidation.AcceptsFramework(accepted, framework.Name, framework.MinimumVersion)))
        {
            return true;
        }

        if (!acceptedRuntimeFamilies.Contains("coreclr", StringComparer.Ordinal) || profile.Family is not ("coreclr" or "coreclr-wine") || !string.Equals(framework.Name, "Microsoft.NETCore.App", StringComparison.Ordinal))
        {
            return false;
        }

        // User assemblies are loaded into the selected runtime's isolated
        // runner process, so a newer CoreCLR can intentionally exercise an
        // older target framework without relying on the app's runtimeconfig.
        return RuntimeProfileValidation.AcceptsFramework(
            new RuntimeFrameworkCompatibilityDefinition { Name = "Microsoft.NETCore.App", MinimumVersion = "2.0.0", MaximumVersion = profile.RuntimeVersion },
            framework.Name,
            framework.MinimumVersion);
    }

    internal static void ValidateCapability(RuntimeProfileOptions profile, string capability)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        if (profile.Capabilities.Contains(capability, StringComparer.Ordinal))
            return;

        throw new RuntimeJobFailureException("runtime-capability-not-supported", WorkerErrorCategory.UnsupportedCapability, $"The selected runtime does not support the '{capability}' operation.", retryable: false);
    }

    internal static RuntimeContainerIsolationKind ResolveIsolationKind(RuntimeProfileOptions profile) =>
        (profile.Container.IsolationKind, profile.Container.ExecutionUser) switch
        {
            (RuntimeContainerIsolationKinds.Standard, RuntimeContainerExecutionUsers.NonRoot) =>
                RuntimeContainerIsolationKind.Standard,
            (RuntimeContainerIsolationKinds.Wine, RuntimeContainerExecutionUsers.Root) =>
                RuntimeContainerIsolationKind.WineRoot,
            (RuntimeContainerIsolationKinds.Wine, RuntimeContainerExecutionUsers.NonRoot) =>
                RuntimeContainerIsolationKind.WineNonRoot,
            _ => throw new InvalidOperationException($"Runtime container isolation kind '{profile.Container.IsolationKind}' and execution user " + $"'{profile.Container.ExecutionUser}' are not a supported combination.")
        };

    internal static void ValidateInstrumentation(ArtifactManifest manifest, RunInstrumentation? instrumentation)
    {
        if (instrumentation != RunInstrumentation.ExecutionFlow)
            return;

        var metadata = manifest.Metadata;
        var isInstrumented = manifest.Derivation is not null &&
            metadata is not null &&
            metadata.TryGetValue("sharplabnext.instrumentation.transform", out var transform) &&
            StringComparer.Ordinal.Equals(transform, "runtime-instrumentation-v1") &&
            metadata.TryGetValue("sharplabnext.instrumentation.profile", out var profile) &&
            StringComparer.Ordinal.Equals(profile, "execution-flow-v1");
        if (!isInstrumented)
        {
            throw new RuntimeJobFailureException("execution-flow-artifact-required", WorkerErrorCategory.IncompatibleArtifact, "Execution Flow requires an artifact derived by runtime-instrumentation-v1.", retryable: false);
        }
    }

    private async Task<MemoryStream> BuildArtifactArchiveAsync(ArtifactBundleDescriptor descriptor, long maximumBytes, string? stdin, RuntimeContainerIsolationKind isolationKind, bool includeReady, CancellationToken cancellationToken)
    {
        var declaredSize = descriptor.Entries.Sum(static entry => entry.Size);
        var stdinBytes = stdin is null ? 0 : Encoding.UTF8.GetByteCount(stdin);
        if (declaredSize < 0 || declaredSize + stdinBytes > maximumBytes)
        {
            throw new RuntimeJobFailureException("artifact-limit-exceeded", WorkerErrorCategory.ResourceExhausted, "The artifact exceeds the runtime job size limit.", retryable: false);
        }

        var archive = new MemoryStream();
        var workspaceOwner = RuntimeContainerIsolation.ResolveWorkspaceOwner(isolationKind);
        using (var writer = new TarWriter(archive, leaveOpen: true))
        {
            var writtenDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in descriptor.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedPath = ArtifactPath.Normalize(entry.Path);
                if (IsSupervisorWorkspacePath(normalizedPath))
                {
                    throw new RuntimeJobFailureException("artifact-path-reserved", WorkerErrorCategory.IncompatibleArtifact, "The artifact uses a Supervisor-reserved workspace path.", retryable: false);
                }
                await using var source = await artifactStore.OpenArtifactFileReadAsync(descriptor.Manifest.ArtifactId, entry.Path, cancellationToken);
                var data = new MemoryStream(capacity: checked((int)entry.Size));
                await source.Content.CopyToAsync(data, cancellationToken);
                if (data.Length != entry.Size)
                {
                    throw new RuntimeJobFailureException("artifact-size-mismatch", WorkerErrorCategory.Internal, "The stored artifact failed integrity validation.", retryable: true);
                }

                data.Position = 0;
                WriteArchiveEntry(writer, writtenDirectories, normalizedPath, data, workspaceOwner.Uid, workspaceOwner.Gid);
            }

            if (stdin is not null)
            {
                WriteArchiveEntry(writer, writtenDirectories, ".sharplabnext/stdin.txt", new MemoryStream(Encoding.UTF8.GetBytes(stdin), writable: false), workspaceOwner.Uid, workspaceOwner.Gid);
            }

            if (includeReady)
            {
                WriteArchiveEntry(writer, writtenDirectories, ".sharplabnext/ready", new MemoryStream("ready\n"u8.ToArray(), writable: false), workspaceOwner.Uid, workspaceOwner.Gid);
            }
        }

        archive.Position = 0;
        return archive;
    }

    internal static bool IsSupervisorWorkspacePath(string path)
    {
        var normalized = ArtifactPath.Normalize(path);
        return StringComparer.Ordinal.Equals(normalized, ".sharplabnext") ||
            normalized.StartsWith(".sharplabnext/", StringComparison.Ordinal);
    }

    internal static void WriteArchiveEntry(TarWriter writer, ISet<string> writtenDirectories, string path, Stream data, int uid = 1654, int gid = 1654)
    {
        var normalizedPath = ArtifactPath.Normalize(path);
        var segments = normalizedPath.Split('/');
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var directoryPath = string.Join('/', segments, 0, index + 1);
            if (!writtenDirectories.Add(directoryPath))
                continue;

            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, directoryPath)
            {
                Gid = gid,
                Uid = uid,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                ModificationTime = DateTimeOffset.UnixEpoch
            });
        }

        var entry = new PaxTarEntry(TarEntryType.RegularFile, normalizedPath)
        {
            DataStream = data,
            Gid = gid,
            Uid = uid,
            Mode = UnixFileMode.UserRead | UnixFileMode.GroupRead,
            ModificationTime = DateTimeOffset.UnixEpoch
        };
        writer.WriteEntry(entry);
    }

    private async Task CaptureFramesAsync(OperationStart operation, Stream logs, RuntimeFrameCapture capture, long maximumOutputBytes, RuntimeJobKind kind, RunInstrumentation? instrumentation, CancellationToken cancellationToken, Action? hangReadyObserver = null)
    {
        var reader = new RuntimeFrameLogReader(logs);
        long previousSequence = 0;
        while (await reader.ReadAsync(cancellationToken: cancellationToken) is { } frame)
        {
            if (frame.Sequence <= previousSequence)
            {
                throw new InvalidDataException("Runtime child frame sequence is not strictly increasing.");
            }

            previousSequence = frame.Sequence;
            capture.RecordFrame(frame.Kind);
            switch (frame.Kind)
            {
                case RuntimeFrameKind.Stdout:
                    capture.Stdout.Write(frame.Payload.Span);
                    if (hangReadyObserver is not null && capture.ObserveCapabilityMarker(CapabilityHangReadyMarkerBytes))
                        hangReadyObserver?.Invoke();
                    EmitOutput(operation, capture, frame.Payload, OutputChannel.Stdout, maximumOutputBytes);
                    break;
                case RuntimeFrameKind.Stderr:
                    capture.Stderr.Write(frame.Payload.Span);
                    EmitOutput(operation, capture, frame.Payload, OutputChannel.Stderr, maximumOutputBytes);
                    break;
                case RuntimeFrameKind.Inspection:
                case RuntimeFrameKind.MemoryGraph:
                    if (kind != RuntimeJobKind.Run)
                        throw new InvalidDataException("Inspection frames are only valid for Run jobs.");
                    RuntimeStructuredPayloadCodec.Validate(frame.Kind, frame.Payload.Span);
                    capture.InspectionPayloads.Add(RuntimeStructuredPayloadCodec.DeserializeInspection(frame.Payload.Span));
                    EmitOutput(operation, capture, frame.Payload, OutputChannel.Inspection, maximumOutputBytes);
                    break;
                case RuntimeFrameKind.Flow:
                    if (kind != RuntimeJobKind.Run || instrumentation != RunInstrumentation.ExecutionFlow)
                        throw new InvalidDataException("Flow frames require an execution-flow Run job.");
                    RuntimeStructuredPayloadCodec.Validate(frame.Kind, frame.Payload.Span);
                    capture.FlowPayloads.Add(RuntimeStructuredPayloadCodec.DeserializeFlow(frame.Payload.Span));
                    EmitOutput(operation, capture, frame.Payload, OutputChannel.Flow, maximumOutputBytes);
                    break;
                case RuntimeFrameKind.JitAssembly when kind == RuntimeJobKind.Jit:
                    capture.JitAssembly.Write(frame.Payload.Span);
                    EmitOutput(operation, capture, frame.Payload, OutputChannel.Jit, maximumOutputBytes);
                    break;
                case RuntimeFrameKind.JitSummary when kind == RuntimeJobKind.Jit:
                    capture.JitSummary.Write(frame.Payload.Span);
                    break;
                case RuntimeFrameKind.Exception:
                    capture.Exception = JsonSerializer.Deserialize<ChildExceptionPayload>(frame.Payload.Span, JsonOptions) ?? throw new InvalidDataException("Runtime child returned an empty exception payload.");
                    break;
                case RuntimeFrameKind.Exit:
                    capture.Exit = JsonSerializer.Deserialize<ChildExitPayload>(frame.Payload.Span, JsonOptions) ?? throw new InvalidDataException("Runtime child returned an empty exit payload.");
                    break;
                case RuntimeFrameKind.ProtocolError:
                    EmitOutput(operation, capture, frame.Payload, OutputChannel.Log, maximumOutputBytes);
                    break;
                default:
                    throw new InvalidDataException($"Runtime child frame '{frame.Kind}' is invalid for a {kind} job.");
            }
        }
    }

    private void EmitOutput(OperationStart operation, RuntimeFrameCapture capture, ReadOnlyMemory<byte> payload, OutputChannel channel, long maximumOutputBytes)
    {
        var observed = checked(capture.ObservedOutputBytes + payload.Length);
        if (observed > maximumOutputBytes)
        {
            capture.OutputTruncated = true;
            Append(operation, new OutputTruncatedOperationEventPayload(channel, "runtime-output-limit", observed, maximumOutputBytes));
            throw new RuntimeOutputLimitException();
        }

        capture.ObservedOutputBytes = observed;
        if (!payload.IsEmpty)
        {
            Append(operation, new OutputChunkOperationEventPayload(new OutputChunk(channel, OutputEncoding.Utf8, Convert.ToBase64String(payload.Span), false)));
        }
    }

    private static IReadOnlyList<string> CreateRunCommand(RuntimeProfileOptions profile, RunRequest request, string entryAssembly) =>
        RuntimeProfileCommandBuilder.CreateRunCommand(profile, ArtifactPath.Normalize(entryAssembly), request.Options.Arguments);

    private static IReadOnlyList<string> CreateJitCommand(RuntimeProfileOptions profile, JitRequest request, string entryAssembly) =>
        RuntimeProfileCommandBuilder.CreateJitCommand(profile, ArtifactPath.Normalize(entryAssembly), request.Options.MethodFilter);

    internal static Dictionary<string, string> CreateRunEnvironment(RuntimeProfileOptions profile, RunInstrumentation instrumentation)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SHARPLABNEXT_STDIN_PATH"] = $"{RuntimeImageLayout.WorkspacePath}/.sharplabnext/stdin.txt",
            ["SHARPLABNEXT_INSTRUMENTATION"] = instrumentation switch
            {
                RunInstrumentation.ExecutionFlow => "execution-flow",
                RunInstrumentation.Inspection => "inspection",
                _ => "none"
            }
        };

        if (profile.Container.EnvironmentKind == RuntimeContainerEnvironmentKinds.CoreClr)
        {
            environment["DOTNET_EnableDiagnostics"] = "0";
            environment["COMPlus_EnableDiagnostics"] = "0";
            environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        }
        else if (profile.Container.EnvironmentKind == RuntimeContainerEnvironmentKinds.Wine)
        {
            if (string.IsNullOrWhiteSpace(profile.Container.WinePrefixPath))
                throw new InvalidOperationException("The Wine runtime profile does not declare its prefix.");
            environment["WINEPREFIX"] = profile.Container.WinePrefixPath;
            environment["WINEARCH"] = "win64";
            environment["WINEDEBUG"] = "-all";
            environment["SHARPLABNEXT_CAPTURE_DIRECTORY"] = @"Z:\tmp";
        }
        else if (profile.Container.EnvironmentKind != RuntimeContainerEnvironmentKinds.Mono)
        {
            throw new InvalidOperationException($"Runtime environment kind '{profile.Container.EnvironmentKind}' is not supported.");
        }

        return environment;
    }

    private static void ValidateJSharp20Compatibility(ArtifactManifest manifest, RuntimeProfileOptions profile)
    {
        var jsharpProfile = profile.ProvidedRuntimeFeatureTags.Contains("runtime.jsharp20-wine", StringComparer.Ordinal);
        var jsharpArtifact =
            StringComparer.Ordinal.Equals(manifest.Producer.LanguageId, "jsharp") ||
            StringComparer.Ordinal.Equals(manifest.Producer.ToolchainId, "vjc-jsharp20") ||
            StringComparer.Ordinal.Equals(manifest.ReferenceSetId, "jsharp20-ref") ||
            manifest.RuntimeRequirement.RequiredRuntimeFeatureTags.Contains("runtime.jsharp20-wine", StringComparer.Ordinal);
        if (!jsharpProfile && !jsharpArtifact)
            return;

        var framework = manifest.RuntimeRequirement.Frameworks.Count == 1
            ? manifest.RuntimeRequirement.Frameworks[0] : null;
        if (!jsharpProfile || !jsharpArtifact ||
            !StringComparer.Ordinal.Equals(profile.Id, "wine-jsharp20-linux-x64") ||
            !StringComparer.Ordinal.Equals(manifest.ArtifactFormat, "dotnet-framework-managed-pe-v1") ||
            !StringComparer.Ordinal.Equals(manifest.Producer.LanguageId, "jsharp") ||
            !StringComparer.Ordinal.Equals(manifest.Producer.ToolchainId, "vjc-jsharp20") ||
            !StringComparer.Ordinal.Equals(manifest.ReferenceSetId, "jsharp20-ref") ||
            !StringComparer.Ordinal.Equals(manifest.TargetFramework, "net20") ||
            !StringComparer.Ordinal.Equals(manifest.RuntimeRequirement.Family, "netfx-clr-wine") ||
            framework is null ||
            !StringComparer.Ordinal.Equals(framework.Name, ".NETFramework") ||
            !StringComparer.Ordinal.Equals(framework.MinimumVersion, "2.0") ||
            !StringComparer.Ordinal.Equals(manifest.RuntimeRequirement.Architecture, "x64") ||
            !manifest.RuntimeRequirement.RequiredRuntimeFeatureTags.SequenceEqual(["runtime.jsharp20-wine"], StringComparer.Ordinal) ||
            manifest.OutputKind != BuildOutputKind.Console ||
            !Path.GetExtension(manifest.EntryAssembly).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(manifest.EntryPoint))
        {
            throw new RuntimeJobFailureException("incompatible-jsharp20-contract", WorkerErrorCategory.IncompatibleArtifact, "J# execution requires the exact x64 CLR 2.0 artifact and dedicated Wine runtime profile.", retryable: false);
        }
    }

    internal static Dictionary<string, string> CreateJitEnvironment(RuntimeProfileOptions profile, JitRequest request, string entryAssembly)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(ArtifactPath.Normalize(entryAssembly));
        var jitOperation = profile.Operations?.Jit;
        var mappingKind = jitOperation?.SourceMappingKind ?? RuntimeJitSourceMappingKinds.LinuxProfiler;
        var usesCheckedJitBridge = string.Equals(jitOperation?.ImplementationId, RuntimeOperationImplementationIds.CheckedJitBridge, StringComparison.Ordinal);
        var usesMonoJitInspector = string.Equals(jitOperation?.ImplementationId, RuntimeOperationImplementationIds.MonoJitInspector, StringComparison.Ordinal);
        var usesDesktopClrJitInspector = string.Equals(jitOperation?.ImplementationId, RuntimeOperationImplementationIds.DesktopClrJitInspector, StringComparison.Ordinal);
        var outputPath = ToRuntimeTemporaryPath(jitOperation?.PathStyle ?? RuntimeOperationPathStyles.Unix, "sharplabnext-jit.asm");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        };
        if (!usesCheckedJitBridge && !usesMonoJitInspector && !usesDesktopClrJitInspector)
        {
            // The runtime entrypoint removes these files before every JIT process
            // start. A reusable container keeps its /tmp tmpfs across stop/start,
            // so stale sections or maps must not leak into the next artifact.
            environment["SHARPLABNEXT_JIT_RESET_OUTPUT"] = "1";
            environment["COMPlus_JitDisasm"] = ToJitDisasmFilter(request.Options.MethodFilter);
            environment["COMPlus_JitDisasmAssemblies"] = assemblyName;
            environment["COMPlus_JitDisasmWithCodeBytes"] = "1";
            environment["DOTNET_JitDisasmWithCodeBytes"] = "1";
            environment["COMPlus_JitStdOutFile"] = outputPath;
            environment["SHARPLABNEXT_JIT_OUTPUT_PATH"] = outputPath;
        }
        else if (usesDesktopClrJitInspector)
        {
            // WineRunner owns the bounded Desktop CLR capture. A stopped
            // reusable container retains /tmp, so the entrypoint must remove
            // both the final capture and an interrupted temporary write.
            environment["SHARPLABNEXT_JIT_RESET_OUTPUT"] = "1";
        }

        if (profile.Container.EnvironmentKind == RuntimeContainerEnvironmentKinds.Wine)
        {
            if (string.IsNullOrWhiteSpace(profile.Container.WinePrefixPath))
                throw new InvalidOperationException("The Wine runtime profile does not declare its prefix.");
            environment["WINEPREFIX"] = profile.Container.WinePrefixPath;
            environment["WINEARCH"] = "win64";
            environment["WINEDEBUG"] = "-all";
        }
        else if (profile.Container.EnvironmentKind == RuntimeContainerEnvironmentKinds.Mono)
        {
            if (!usesMonoJitInspector || mappingKind != RuntimeJitSourceMappingKinds.None)
            {
                throw new InvalidOperationException("Mono JIT inspection requires the bounded Mono helper with no claimed source mapping.");
            }
            environment["DOTNET_EnableDiagnostics"] = "0";
            environment["COMPlus_EnableDiagnostics"] = "0";
            environment["MONO_LOG_LEVEL"] = "error";
        }
        else if (profile.Container.EnvironmentKind != RuntimeContainerEnvironmentKinds.CoreClr)
        {
            throw new InvalidOperationException($"Runtime environment kind '{profile.Container.EnvironmentKind}' is not supported.");
        }

        if (mappingKind == RuntimeJitSourceMappingKinds.LinuxProfiler)
        {
            environment["DOTNET_EnableDiagnostics"] = "1";
            environment["COMPlus_EnableDiagnostics"] = "1";
            environment["DOTNET_EnableDiagnostics_IPC"] = "0";
            environment["COMPlus_EnableDiagnostics_IPC"] = "0";
            environment["DOTNET_EnableDiagnostics_Debugger"] = "0";
            environment["COMPlus_EnableDiagnostics_Debugger"] = "0";
            environment["DOTNET_EnableDiagnostics_Profiler"] = "1";
            environment["COMPlus_EnableDiagnostics_Profiler"] = "1";
            environment["CORECLR_ENABLE_PROFILING"] = "1";
            environment["CORECLR_PROFILER"] = "{cf0d821e-299b-5307-a3d8-b283c03916dd}";
            environment["CORECLR_PROFILER_PATH"] =
                jitOperation?.ProfilerPath ?? "/opt/sharplabnext/SharpLabNext.JitProfiler.so";
            environment["COMPlus_RichDebugInfo"] = "1";
            environment["DOTNET_RichDebugInfo"] = "1";
            environment["SHARPLABNEXT_JIT_MAP_MODULE"] = Path.GetFileName(entryAssembly);
            environment["SHARPLABNEXT_JIT_MAP_PATH"] = "/tmp/sharplabnext-jit.map";
            environment["SHARPLABNEXT_JIT_RICH_MAP_PATH"] = "/tmp/sharplabnext-jit-rich.map";
        }
        else if (mappingKind == RuntimeJitSourceMappingKinds.CheckedJitDebugInfo)
        {
            environment["DOTNET_EnableDiagnostics"] = "0";
            environment["COMPlus_EnableDiagnostics"] = "0";
        }
        else if (mappingKind == RuntimeJitSourceMappingKinds.None)
        {
            environment["DOTNET_EnableDiagnostics"] = "0";
            environment["COMPlus_EnableDiagnostics"] = "0";
        }
        else
        {
            throw new InvalidOperationException($"JIT source mapping kind '{mappingKind}' is not supported.");
        }

        if (!usesDesktopClrJitInspector && request.Options.TieringPolicyId == "tier0-diffable")
        {
            environment["COMPlus_TieredCompilation"] = "0";
            environment["COMPlus_JitDisasmDiffable"] = "0";
        }
        else if (!usesDesktopClrJitInspector && request.Options.TieringPolicyId == "tier1")
        {
            environment["COMPlus_TieredCompilation"] = "1";
            environment["COMPlus_TC_QuickJit"] = "0";
        }

        if (!usesDesktopClrJitInspector && request.Options.PgoPolicyId == "disabled")
        {
            environment["COMPlus_TieredPGO"] = "0";
        }

        return environment;
    }

    private static string ToRuntimeTemporaryPath(string pathStyle, string fileName) => pathStyle switch
    {
        RuntimeOperationPathStyles.Unix => $"/tmp/{fileName}",
        RuntimeOperationPathStyles.WineZ => $"Z:\\tmp\\{fileName}",
        _ => throw new InvalidOperationException($"Runtime path style '{pathStyle}' is not supported.")
    };

    private static string ToJitDisasmFilter(string? methodFilter)
    {
        if (string.IsNullOrWhiteSpace(methodFilter))
            return "*";

        // CoreCLR's JitDisasm grammar separates the declaring type and method
        // with a colon. The managed inspector intentionally keeps receiving
        // the original dotted filter, so only the native JIT filter is
        // normalized here. This matters on newer runtimes (notably .NET 10),
        // where a dotted fully-qualified method silently produces no listing.
        var normalized = NormalizeCoreClrJitFilter(methodFilter);
        return normalized.IndexOfAny(['*', '?']) >= 0
            ? normalized
            : $"*{normalized}*";
    }

    private static string NormalizeCoreClrJitFilter(string methodFilter)
    {
        // A caller may already use the CoreCLR Type:Method form. Preserve it
        // and retain explicit wildcard semantics for filters such as
        // Namespace.Type.*.
        if (methodFilter.Contains(':', StringComparison.Ordinal))
            return methodFilter;

        // Reflection renders constructors as Type..ctor/Type..cctor. The
        // second dot belongs to the method name, so keep it after replacing
        // the type/method separator (Type:.ctor).
        var constructorSeparator = methodFilter.LastIndexOf("..", StringComparison.Ordinal);
        var separator = constructorSeparator >= 0
            ? constructorSeparator : methodFilter.LastIndexOf('.');
        return separator > 0 && separator < methodFilter.Length - 1
            ? string.Concat(methodFilter.AsSpan(0, separator), ":", methodFilter.AsSpan(separator + 1)) : methodFilter;
    }

    private static RunResult CreateRunResult(RuntimeFrameCapture capture, RuntimeContainerExit exit, TimeSpan elapsed, RuntimeProfileOptions profile, bool outputTruncated)
    {
        var status = ClassifyRunStatus(capture.Exit?.Status, capture.Exit?.ExitCode, exit);
        var exception = capture.Exception is null
            ? null : MapUserException(capture.Exception);
        return new RunResult(status, capture.Exit?.ExitCode is null ? checked((int?)exit.StatusCode) : capture.Exit.ExitCode, exception, elapsed, outputTruncated, RuntimeIdentity(profile));
    }

    private static UserExceptionInfo MapUserException(ChildExceptionPayload exception, int depth = 0) =>
        new(
            exception.TypeName,
            exception.Message,
            exception.StackTrace,
            depth < MaximumExceptionDepth && exception.InnerException is { } inner
                ? MapUserException(inner, depth + 1) : null);

    internal static RunTerminalStatus ClassifyRunStatus(string? reportedStatus, int? reportedExitCode, RuntimeContainerExit exit)
    {
        if (exit.OomKilled || StringComparer.Ordinal.Equals(reportedStatus, "out-of-memory"))
            return RunTerminalStatus.OutOfMemory;
        if (StringComparer.Ordinal.Equals(reportedStatus, "process-crash") || reportedStatus is null)
            return RunTerminalStatus.ProcessCrash;
        if (StringComparer.Ordinal.Equals(reportedStatus, "user-exception"))
            return RunTerminalStatus.UserException;
        return reportedExitCode == 0 && exit.StatusCode == 0
            ? RunTerminalStatus.Completed : RunTerminalStatus.NonZeroExit;
    }

    private async Task<object> CreateJitResultAsync(string operationId, JitRequest request, RuntimeFrameCapture capture, RuntimeContainerExit exit, TimeSpan elapsed, RuntimeProfileOptions profile, bool outputTruncated)
    {
        var rawBytes = capture.JitAssembly.ToArray();
        var summaryBytes = capture.JitSummary.ToArray();
        var rawRef = await PublishContentAsync(operationId, rawBytes, "text/x-asm; charset=utf-8");
        var structuredRef = await PublishContentAsync(operationId, summaryBytes, "application/json");
        var methods = ParseJitMethods(summaryBytes);
        var status = ClassifyJitStatus(capture.Exit?.Status, exit, methods.Length, outputTruncated);
        return new JitResult(status, structuredRef, rawRef, methods, elapsed, JitIdentity(profile, request.Options));
    }

    internal static JitMethodSummary[] ParseJitMethods(ReadOnlySpan<byte> summaryBytes)
    {
        if (summaryBytes.IsEmpty)
            return [];

        var summary = JsonSerializer.Deserialize<JitSummaryPayload>(summaryBytes, JsonOptions);
        return summary?.Methods.Where(static method => method.Status == "prepared").Select(static method => new JitMethodSummary(method.Method, method.DisplayName ?? method.Method, method.NativeCodeSize, method.InstructionCount, MapJitLinkedRanges(method.LinkedRanges)))
            .ToArray() ?? [];
    }

    private static LinkedRange[] MapJitLinkedRanges(IReadOnlyList<JitLinkedRangePayload>? ranges) =>
        ranges?.Take(4_096).Select(static range => MapJitLinkedRange(range)).OfType<LinkedRange>()
            .ToArray() ?? [];

    private static LinkedRange? MapJitLinkedRange(JitLinkedRangePayload range)
    {
        if (!TryMapJitTextRange(range.SourceRange, out var sourceRange) || !TryMapJitTextRange(range.OutputRange, out var outputRange))
        {
            return null;
        }

        var sourceFilePath = SanitizeJitSourcePath(range.SourceFilePath);
        if (sourceFilePath is null)
            return null;
        var precision = range.Precision is "sequence-point" or "method"
            ? range.Precision : null;
        return new LinkedRange(sourceFilePath, sourceRange, outputRange, precision);
    }

    private static bool TryMapJitTextRange(JitTextRangePayload? range, out TextRange mapped)
    {
        mapped = new TextRange(0, 0, 0, 0);
        if (range is null || range.StartLine < 0 || range.StartCharacter < 0 || range.EndLine < range.StartLine || range.EndCharacter < 0 || (range.EndLine == range.StartLine && range.EndCharacter < range.StartCharacter) || range.EndLine > 1_000_000 || range.EndCharacter > 1_000_000)
        {
            return false;
        }

        mapped = new TextRange(range.StartLine, range.StartCharacter, range.EndLine, range.EndCharacter);
        return true;
    }

    private static string? SanitizeJitSourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(static segment => segment is not "." and not ".." && !segment.EndsWith(':')).TakeLast(8).ToArray();
        if (segments.Length == 0)
            return null;
        var sanitized = string.Join('/', segments);
        return sanitized.Length <= 512 ? sanitized : sanitized[^512..];
    }

    internal static JitTerminalStatus ClassifyJitStatus(string? reportedStatus, RuntimeContainerExit exit, int methodCount, bool outputTruncated)
    {
        if (outputTruncated)
            return JitTerminalStatus.OutputLimitExceeded;
        if (exit.OomKilled || StringComparer.Ordinal.Equals(reportedStatus, "out-of-memory"))
            return JitTerminalStatus.OutOfMemory;
        if (reportedStatus is null && exit.StatusCode != 0)
            return JitTerminalStatus.ProcessCrash;
        if (StringComparer.Ordinal.Equals(reportedStatus, "inspection-failed"))
            return JitTerminalStatus.InspectionFailed;
        if (methodCount == 0)
            return JitTerminalStatus.NoMatchingMethods;
        return JitTerminalStatus.Completed;
    }

    private async Task<ContentRef?> PublishContentAsync(string operationId, byte[] content, string mediaType)
    {
        if (content.Length == 0)
        {
            return null;
        }

        var contentRef = ContentIdentity.Compute(content);
        await using var stream = new MemoryStream(content, writable: false);
        await artifactStore.PutContentAsync(contentRef, stream, content.Length, TimeSpan.FromHours(1));
        operations.Append(operationId, new ContentProducedOperationEventPayload(contentRef, mediaType, content.Length), DateTimeOffset.UtcNow);
        return contentRef;
    }

    private static RunResult CreateCancelledRunResult(TimeSpan elapsed, RuntimeProfileOptions profile) =>
        TerminalRunResult(RunTerminalStatus.Cancelled, elapsed, profile);

    private static RunResult CreateTimeoutRunResult(TimeSpan elapsed, RuntimeProfileOptions profile) =>
        TerminalRunResult(RunTerminalStatus.Timeout, elapsed, profile);

    private static RunResult CreateOutputLimitRunResult(TimeSpan elapsed, RuntimeProfileOptions profile) =>
        TerminalRunResult(RunTerminalStatus.OutputLimitExceeded, elapsed, profile, outputTruncated: true);

    private static RunResult TerminalRunResult(RunTerminalStatus status, TimeSpan elapsed, RuntimeProfileOptions profile, bool outputTruncated = false) =>
        new(status, null, null, elapsed, outputTruncated, RuntimeIdentity(profile));

    private static JitResult CreateCancelledJitResult(TimeSpan elapsed, RuntimeProfileOptions profile) =>
        TerminalJitResult(JitTerminalStatus.Cancelled, elapsed, profile);

    private static JitResult CreateTimeoutJitResult(TimeSpan elapsed, RuntimeProfileOptions profile) =>
        TerminalJitResult(JitTerminalStatus.Timeout, elapsed, profile);

    private static JitResult CreateOutputLimitJitResult(TimeSpan elapsed, RuntimeProfileOptions profile) =>
        TerminalJitResult(JitTerminalStatus.OutputLimitExceeded, elapsed, profile);

    private static JitResult TerminalJitResult(JitTerminalStatus status, TimeSpan elapsed, RuntimeProfileOptions profile) =>
        new(status, null, null, [], elapsed, JitIdentity(profile, new JitOptions(null, "default", "default", "coreclr-jitdisasm", "runtime-job-default")));

    private static RuntimeIdentity RuntimeIdentity(RuntimeProfileOptions profile) =>
        new(profile.RuntimeVersion, profile.RuntimeCommit, profile.RuntimeImageId, profile.Rid, profile.Architecture);

    private static JitIdentity JitIdentity(RuntimeProfileOptions profile, JitOptions options)
    {
        var desktopClr = StringComparer.Ordinal.Equals(profile.Operations?.Jit?.ImplementationId, RuntimeOperationImplementationIds.DesktopClrJitInspector);
        return new(
            profile.RuntimeVersion,
            profile.RuntimeCommit,
            profile.JitVersion,
            profile.JitCommit,
            profile.RuntimeImageId,
            profile.Rid,
            profile.Architecture,
            profile.CpuFeatureProfile,
            desktopClr ? "not-applicable" : options.TieringPolicyId,
            desktopClr ? "not-applicable" : options.PgoPolicyId,
            desktopClr
                ? RuntimeOperationImplementationIds.DesktopClrJitInspector : options.ProviderId,
            desktopClr
                ? "prepare-method+rtl-lookup-function-entry+iced" : "prepare-method+jitdisasm");
    }

    private void Append(OperationStart operation, OperationEventPayload payload) =>
        operations.Append(operation.Handle.OperationId, payload, DateTimeOffset.UtcNow);

    private void Fail(OperationStart operation, string code, WorkerErrorCategory category, string publicMessage, bool retryable) =>
        Append(operation, new FailedOperationEventPayload(new WorkerError(code, category, publicMessage, retryable, retryable, operation.Handle.RequestId, "runtime-supervisor", "runtime-supervisor")));

    private async Task KillQuietlyAsync(string? containerId)
    {
        if (containerId is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await docker.KillContainerAsync(containerId, timeout.Token);
        }
        catch (Exception exception)
        {
            LogContainerKillFailed(logger, containerId, exception);
        }
    }

    private async Task<bool> StopSessionContainerQuietlyAsync(string? containerId)
    {
        if (containerId is null)
            return false;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await docker.KillContainerAsync(containerId, timeout.Token).ConfigureAwait(false);
            var exit = await docker.WaitContainerAsync(containerId, timeout.Token).ConfigureAwait(false);
            return !exit.OomKilled;
        }
        catch (Exception exception)
        {
            LogSessionContainerStopFailed(logger, containerId, exception);
            return false;
        }
    }

    private async Task<bool> RemoveQuietlyAsync(string? containerId)
    {
        if (containerId is null)
            return true;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await docker.RemoveContainerAsync(containerId, timeout.Token);
            return true;
        }
        catch (Exception exception)
        {
            LogContainerRemovalFailed(logger, containerId, exception);
            return false;
        }
    }

    private async Task<bool> ReleaseLeaseQuietlyAsync(string? leaseToken)
    {
        if (leaseToken is null)
        {
            return true;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await artifactStore.ReleaseLeaseAsync(leaseToken, timeout.Token);
            return true;
        }
        catch (Exception exception)
        {
            LogLeaseReleaseFailed(logger, exception);
            return false;
        }
    }

    private async Task<bool> RemoveWorkspaceVolumeQuietlyAsync(string? volumeName)
    {
        if (volumeName is null)
        {
            return true;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await docker.RemoveWorkspaceVolumeAsync(volumeName, timeout.Token);
            return true;
        }
        catch (Exception exception)
        {
            LogWorkspaceVolumeRemovalFailed(logger, volumeName, exception);
            return false;
        }
    }

    private static async Task<RuntimeContainerResourceUsage> StopResourceMonitorAsync(IRuntimeContainerResourceMonitor monitor)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await monitor.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Task AwaitMeasurementPhaseAsync(Task phase, Task<RuntimeContainerExit> targetExit, string phaseName, CancellationToken cancellationToken) =>
        AwaitMeasurementPhaseAsync(phase, targetExit, sidecarExit: null, phaseName, cancellationToken);

    private static async Task AwaitMeasurementPhaseAsync(Task phase, Task<RuntimeContainerExit> targetExit, Task<RuntimeContainerExit>? sidecarExit, string phaseName, CancellationToken cancellationToken, bool allowSidecarExitAfterPhase = false)
    {
        var completed = await (sidecarExit is null ? Task.WhenAny(phase, targetExit) : Task.WhenAny(phase, targetExit, sidecarExit)).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(completed, targetExit) || targetExit.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveFault(phase);
            throw await CreateEarlyTargetExitFailureAsync(targetExit, phaseName).ConfigureAwait(false);
        }
        if (sidecarExit is not null && ReferenceEquals(completed, sidecarExit) && !(allowSidecarExitAfterPhase && phase.IsCompletedSuccessfully))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveFault(phase);
            throw await CreateEarlySidecarExitFailureAsync(sidecarExit, phaseName).ConfigureAwait(false);
        }

        await phase.ConfigureAwait(false);
        if (targetExit.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw await CreateEarlyTargetExitFailureAsync(targetExit, phaseName).ConfigureAwait(false);
        }
        if (!allowSidecarExitAfterPhase && sidecarExit?.IsCompleted == true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw await CreateEarlySidecarExitFailureAsync(sidecarExit, phaseName).ConfigureAwait(false);
        }
    }

    private static Task<T> AwaitMeasurementPhaseAsync<T>(Task<T> phase, Task<RuntimeContainerExit> targetExit, string phaseName, CancellationToken cancellationToken) =>
        AwaitMeasurementPhaseAsync(phase, targetExit, sidecarExit: null, phaseName, cancellationToken);

    private static async Task<T> AwaitMeasurementPhaseAsync<T>(Task<T> phase, Task<RuntimeContainerExit> targetExit, Task<RuntimeContainerExit>? sidecarExit, string phaseName, CancellationToken cancellationToken, bool allowSidecarExitAfterPhase = false)
    {
        var completed = await (sidecarExit is null ? Task.WhenAny(phase, targetExit) : Task.WhenAny(phase, targetExit, sidecarExit)).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(completed, targetExit) || targetExit.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveFault(phase);
            throw await CreateEarlyTargetExitFailureAsync(targetExit, phaseName).ConfigureAwait(false);
        }
        if (sidecarExit is not null && ReferenceEquals(completed, sidecarExit) && !(allowSidecarExitAfterPhase && phase.IsCompletedSuccessfully))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveFault(phase);
            throw await CreateEarlySidecarExitFailureAsync(sidecarExit, phaseName).ConfigureAwait(false);
        }

        var result = await phase.ConfigureAwait(false);
        if (targetExit.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw await CreateEarlyTargetExitFailureAsync(targetExit, phaseName).ConfigureAwait(false);
        }
        if (!allowSidecarExitAfterPhase && sidecarExit?.IsCompleted == true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw await CreateEarlySidecarExitFailureAsync(sidecarExit, phaseName).ConfigureAwait(false);
        }
        return result;
    }

    private static async Task<RuntimeJobFailureException> CreateEarlyTargetExitFailureAsync(Task<RuntimeContainerExit> targetExit, string phaseName)
    {
        try
        {
            var exit = await targetExit.ConfigureAwait(false);
            return MeasurementProtocolFailure($"The measured keeper exited during {phaseName} " + $"(status {exit.StatusCode}, OOM {exit.OomKilled}, error '{exit.Error ?? string.Empty}').");
        }
        catch (Exception exception)
        {
            return MeasurementProtocolFailure($"The measured keeper wait failed during {phaseName}: {exception.Message}");
        }
    }

    private static async Task<RuntimeJobFailureException> CreateEarlySidecarExitFailureAsync(Task<RuntimeContainerExit> sidecarExit, string phaseName)
    {
        try
        {
            var exit = await sidecarExit.ConfigureAwait(false);
            return MeasurementProtocolFailure($"The measurement sidecar exited during {phaseName} " + $"(status {exit.StatusCode}, OOM {exit.OomKilled}, error '{exit.Error ?? string.Empty}').");
        }
        catch (Exception exception)
        {
            return MeasurementProtocolFailure($"The measurement sidecar wait failed during {phaseName}: {exception.Message}");
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static RuntimeJobFailureException MeasurementProtocolFailure(string message) =>
        new("runtime-measurement-protocol-failed", WorkerErrorCategory.Unavailable, message, retryable: false);

    private static string CreateContainerName(RuntimeJobKind kind) =>
        $"sln-{kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";

    internal static string? CaptureTraceParent() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } id } ? id : null;

    private enum RuntimeJobKind
    {
        Run,
        Jit
    }

    private sealed class RuntimeFrameCapture
    {
        private readonly Dictionary<string, int> _frameKinds = new(StringComparer.Ordinal);

        public long ObservedOutputBytes { get; set; }
        public bool OutputTruncated { get; set; }
        public ChildExitPayload? Exit { get; set; }
        public ChildExceptionPayload? Exception { get; set; }
        public int RuntimeFrameCount { get; private set; }
        public IReadOnlyDictionary<string, int> FrameKinds => _frameKinds;
        public MemoryStream Stdout { get; } = new();
        public MemoryStream Stderr { get; } = new();
        public List<RuntimeInspectionPayload> InspectionPayloads { get; } = [];
        public List<RuntimeFlowPayload> FlowPayloads { get; } = [];
        public MemoryStream JitAssembly { get; } = new();
        public MemoryStream JitSummary { get; } = new();
        public bool HangReadyMarkerObserved { get; private set; }
        private int _markerScanOffset;

        public bool ObserveCapabilityMarker(ReadOnlySpan<byte> marker)
        {
            if (HangReadyMarkerObserved || marker.IsEmpty || Stdout.Length > int.MaxValue)
                return false;

            var output = Stdout.GetBuffer().AsSpan(0, checked((int)Stdout.Length));
            // Re-scan only the small overlap needed for a marker split across
            // frames. This keeps marker recognition linear in captured output
            // instead of repeatedly walking the complete stdout buffer.
            var scanStart = Math.Max(0, _markerScanOffset - marker.Length - 1);
            for (var offset = scanStart; offset <= output.Length - marker.Length; offset++)
            {
                if (!output.Slice(offset, marker.Length).SequenceEqual(marker))
                    continue;

                var startsLine = offset == 0 || output[offset - 1] is (byte)'\r' or (byte)'\n';
                var end = offset + marker.Length;
                // A frame boundary is not a line boundary. If the marker is
                // the final bytes currently captured, defer recognition until
                // a later frame supplies the required newline.
                var endsLine = end < output.Length &&
                    (output[end] is (byte)'\r' or (byte)'\n');
                if (startsLine && endsLine)
                {
                    HangReadyMarkerObserved = true;
                    return true;
                }
            }

            _markerScanOffset = output.Length;

            return false;
        }

        public void RecordFrame(RuntimeFrameKind kind)
        {
            RuntimeFrameCount = checked(RuntimeFrameCount + 1);
            var name = kind.ToString();
            _frameKinds.TryGetValue(name, out var count);
            _frameKinds[name] = checked(count + 1);
        }
    }

    private sealed record ChildExitPayload(string Status, int? ExitCode, double ElapsedMilliseconds);

    private sealed record ChildExceptionPayload(string TypeName, string Message, string? StackTrace, ChildExceptionPayload? InnerException = null, double? ElapsedMilliseconds = null);

    private sealed record JitSummaryPayload(string RuntimeVersion, string? Assembly, string? MethodFilter, IReadOnlyList<JitMethodPayload> Methods);

    private sealed record JitMethodPayload(
        string Method,
        string? DisplayName,
        string Status,
        string? Address,
        string? Error,
        int NativeCodeSize,
        int InstructionCount,
        IReadOnlyList<JitLinkedRangePayload>? LinkedRanges,
        string? MappingSource,
        IReadOnlyList<JitEvidenceRangePayload>? EvidenceRanges = null);

    private sealed record JitEvidenceRangePayload(int IlOffset, int NativeStartOffset, int NativeEndOffset, string Document, int StartLine, int StartColumn, int EndLine, int EndColumn);

    private sealed record JitLinkedRangePayload(string? SourceFilePath, JitTextRangePayload? SourceRange, JitTextRangePayload? OutputRange, string? Precision);

    private sealed record JitTextRangePayload(int StartLine, int StartCharacter, int EndLine, int EndCharacter);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Error, Message = "Runtime job {OperationId} failed.")]
    private static partial void LogRuntimeJobFailed(ILogger logger, string operationId, Exception exception);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "Failed to kill runtime container {ContainerId}.")]
    private static partial void LogContainerKillFailed(ILogger logger, string containerId, Exception exception);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning, Message = "Failed to remove runtime container {ContainerId}; the reaper will retry.")]
    private static partial void LogContainerRemovalFailed(ILogger logger, string containerId, Exception exception);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning, Message = "Failed to release artifact lease for a completed runtime job.")]
    private static partial void LogLeaseReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning, Message = "Failed to remove runtime workspace volume {VolumeName}; the reaper will retry.")]
    private static partial void LogWorkspaceVolumeRemovalFailed(ILogger logger, string volumeName, Exception exception);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning, Message = "Failed to stop reusable runtime session container {ContainerId} cleanly.")]
    private static partial void LogSessionContainerStopFailed(ILogger logger, string containerId, Exception exception);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Warning, Message = "Failed to verify removal of audited runtime container {ContainerId}.")]
    private static partial void LogAuditCleanupVerificationFailed(ILogger logger, string containerId, Exception exception);
}

public sealed partial class RuntimeContainerReaper(IDockerEngineClient docker, IOptions<RuntimeSupervisorOptions> configuredOptions, ILogger<RuntimeContainerReaper> logger) : BackgroundService
{
    private readonly RuntimeSupervisorOptions _options = configuredOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.ReaperIntervalSeconds));
        do
        {
            await ReapAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ReapAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var removedContainers = 0;
        var failures = 0;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-_options.StaleContainerSeconds);
            var containers = await docker.ListManagedContainersAsync(_options.ContainerLabel, _options.ResourceScope, cancellationToken);
            foreach (var container in containers.Where(container => container.CreatedAtUtc < cutoff))
            {
                await docker.KillContainerAsync(container.Id, cancellationToken);
                await docker.RemoveContainerAsync(container.Id, cancellationToken);
                removedContainers++;
                LogStaleContainerRemoved(logger, container.Id, container.State);
            }

            var volumes = await docker.ListManagedWorkspaceVolumesAsync(_options.ContainerLabel, _options.ResourceScope, cancellationToken);
            foreach (var volume in volumes.Where(volume => volume.CreatedAtUtc < cutoff))
            {
                await docker.RemoveWorkspaceVolumeAsync(volume.Name, cancellationToken);
                LogStaleWorkspaceVolumeRemoved(logger, volume.Name);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            failures = 1;
            LogReaperFailed(logger, exception);
        }
        finally
        {
            stopwatch.Stop();
            SharpLabNextTelemetry.Metrics.RecordReaperPass(_options.ResourceScope, stopwatch.Elapsed, removedContainers, failures);
        }
    }

    [LoggerMessage(EventId = 4010, Level = LogLevel.Warning, Message = "Removed stale runtime container {ContainerId} in state {State}.")]
    private static partial void LogStaleContainerRemoved(ILogger logger, string containerId, string state);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Error, Message = "Runtime container reaper iteration failed.")]
    private static partial void LogReaperFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4012, Level = LogLevel.Warning, Message = "Removed stale runtime workspace volume {VolumeName}.")]
    private static partial void LogStaleWorkspaceVolumeRemoved(ILogger logger, string volumeName);
}

internal sealed class RuntimeJobFailureException(string code, WorkerErrorCategory category, string publicMessage, bool retryable) : Exception(publicMessage)
{
    public string Code { get; } = code;
    public WorkerErrorCategory Category { get; } = category;
    public string PublicMessage { get; } = publicMessage;
    public bool Retryable { get; } = retryable;
}

internal sealed class RuntimeOutputLimitException : Exception;

internal static class RuntimeArchitectureCompatibility
{
    public static bool IsCompatible(string artifactArchitecture, string runtimeArchitecture) =>
        string.Equals(artifactArchitecture, "any", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artifactArchitecture, "anycpu", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artifactArchitecture, runtimeArchitecture, StringComparison.OrdinalIgnoreCase);
}

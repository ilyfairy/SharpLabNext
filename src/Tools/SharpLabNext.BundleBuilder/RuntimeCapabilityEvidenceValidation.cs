using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.BundleBuilder;

internal static partial class RuntimeCapabilityEvidenceValidation
{
    private const long MaximumArtifactBytes = 256L * 1024 * 1024;
    private const string DesktopClrCaptureHelperPath =
        "/opt/sharplabnext/SharpLabNext.DesktopClrJitInspector.exe";
    // Reviewed Supervisor sandbox identity from the checked-in appsettings and
    // runtime-job-seccomp.v1.json policy. A well-formed but weaker policy is
    // not valid promotion evidence.
    private const string ApprovedSupervisorPolicyId = "runtime-linux-v1";
    private const string ApprovedSeccompSha256 =
        "sha256:01536f1d1df938ae611eba20d6349e0de7a99b6ecdee1549427a0b01b8301e28";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static IReadOnlyList<RuntimePromotionImageFileSnapshot> Validate(
        byte[] bytes,
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt,
        RuntimePromotionCapabilityCheck check) =>
        Validate(bytes, profile, receipt, check, out _);

    public static IReadOnlyList<RuntimePromotionImageFileSnapshot> Validate(
        byte[] bytes,
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt,
        RuntimePromotionCapabilityCheck check,
        out RuntimeCapabilityProbeArtifactSnapshot probeArtifact)
    {
        var evidence = RuntimePromotionJson.Deserialize<RuntimeCapabilityEvidenceDocument>(
            bytes,
            JsonOptions,
            $"Runtime '{profile.Id}' {check.Capability} evidence");

        var prefix = $"Runtime '{profile.Id}' {check.Capability} evidence";
        Require(evidence.SchemaVersion == 1, $"{prefix} must use schema version 1.");
        RequireEqual(evidence.ProfileId, profile.Id, $"{prefix} profile ID");
        RequireEqual(evidence.Capability, check.Capability, $"{prefix} capability");
        RequireEqual(evidence.Result, "passed", $"{prefix} result");
        RequireEqual(evidence.SourceRevision, receipt.SourceRevision, $"{prefix} source revision");
        Require(IsGitCommit(evidence.SourceRevision), $"{prefix} source revision is invalid.");
        Require(IsCanonicalUtcTimestamp(evidence.CompletedAtUtc), $"{prefix} timestamp is not canonical UTC.");

        var image = evidence.Image
            ?? throw new BundleValidationException($"{prefix} image identity is missing.");
        RequireEqual(image.Reference, receipt.Image.Reference, $"{prefix} image reference");
        RequireEqual(image.ImageId, receipt.Image.ImageId, $"{prefix} image ID");
        Require(IsSha256(image.ImageId), $"{prefix} image ID is invalid.");
        var producer = evidence.Producer
            ?? throw new BundleValidationException($"{prefix} producer identity is missing.");
        RequireEqual(
            producer.Id,
            "sharplabnext-runtime-preflight-v1",
            $"{prefix} producer ID");
        RequireEqual(
            producer.SourceRevision,
            receipt.SourceRevision,
            $"{prefix} producer source revision");
        RequireEqual(producer.PlanSha256, receipt.PlanSha256, $"{prefix} producer plan digest");
        Require(IsSha256(producer.PlanSha256), $"{prefix} producer plan digest is invalid.");

        var artifacts = ValidateArtifacts(profile, receipt, check, evidence.Artifacts, prefix);
        ValidateInvocation(profile, receipt, check, evidence.Invocation, artifacts, prefix);
        probeArtifact = ValidateProbeArtifact(check, evidence, prefix);
        ValidateSandbox(profile, receipt, evidence.Sandbox, prefix);
        ValidateLifecycle(evidence.Lifecycle, prefix);
        ValidateDetails(receipt, check, evidence, artifacts, prefix);
        return artifacts.Values
            .OrderBy(static artifact => artifact.Path, StringComparer.Ordinal)
            .Select(static artifact => new RuntimePromotionImageFileSnapshot(
                artifact.Path,
                artifact.Sha256,
                artifact.SizeBytes,
                artifact.Role,
                artifact.Format,
                artifact.Architecture))
            .ToArray();
    }

    public static void ValidateProbeSet(
        string profileId,
        IReadOnlyDictionary<string, RuntimeCapabilityProbeArtifactSnapshot> bindings)
    {
        if (!bindings.TryGetValue("run", out var run))
            throw new BundleValidationException($"Runtime '{profileId}' has no canonical Run probe binding.");
        foreach (var (capability, binding) in bindings)
        {
            if (!StringComparer.Ordinal.Equals(binding.SourceArtifactSha256, run.SourceArtifactSha256))
            {
                throw new BundleValidationException(
                    $"Runtime '{profileId}' capability documents do not bind one canonical source probe artifact.");
            }
            if (!StringComparer.Ordinal.Equals(binding.PlanSha256, run.PlanSha256) ||
                !StringComparer.Ordinal.Equals(
                    binding.PreflightProfileSha256,
                    run.PreflightProfileSha256))
            {
                throw new BundleValidationException(
                    $"Runtime '{profileId}' capability documents do not bind one promotion plan and immutable preflight Runtime Profile.");
            }
            if (capability != "execution-flow" &&
                (!StringComparer.Ordinal.Equals(binding.ArtifactSha256, run.ArtifactSha256) ||
                 !StringComparer.Ordinal.Equals(
                     binding.EntryAssemblySha256,
                     run.EntryAssemblySha256)))
            {
                throw new BundleValidationException(
                    $"Runtime '{profileId}' capability '{capability}' does not execute the canonical Run probe bytes.");
            }
            if (capability == "execution-flow" &&
                (binding.Derivation is null ||
                 !StringComparer.Ordinal.Equals(
                     binding.Derivation.ParentArtifactSha256,
                     run.SourceArtifactSha256)))
            {
                throw new BundleValidationException(
                    $"Runtime '{profileId}' Execution Flow evidence is not derived from the canonical Run probe.");
            }
        }
    }

    private static RuntimeCapabilityProbeArtifactSnapshot ValidateProbeArtifact(
        RuntimePromotionCapabilityCheck check,
        RuntimeCapabilityEvidenceDocument evidence,
        string prefix)
    {
        var probe = evidence.ProbeArtifact;
        Require(probe is not null &&
                probe.Contract == RuntimeCapabilityProbeContract.ContractId &&
                IsSha256(probe.SourceArtifactSha256) &&
                IsSha256(probe.ArtifactSha256) &&
                IsSha256(probe.EntryAssemblySha256) &&
                probe.PlanSha256 == evidence.Producer!.PlanSha256 &&
                IsSha256(probe.PlanSha256) &&
                IsSha256(probe.PreflightProfileSha256) &&
                evidence.Invocation is not null &&
                evidence.Invocation.EntryAssembly is not null &&
                probe.EntryAssemblySha256 == evidence.Invocation.EntryAssembly.Sha256,
            $"{prefix} does not bind the executed canonical probe artifact and entry assembly.");

        RuntimeCapabilityProbeDerivationSnapshot? derivation = null;
        if (check.Capability == "execution-flow")
        {
            var value = probe!.Derivation;
            Require(value is not null &&
                    probe.ArtifactSha256 != probe.SourceArtifactSha256 &&
                    value.ParentArtifactSha256 == probe.SourceArtifactSha256 &&
                    value.ProcessorId == RuntimeCapabilityProbeContract.ExecutionFlowProcessorId &&
                    value.ProcessorVersion == RuntimeCapabilityProbeContract.ExecutionFlowProcessorVersion &&
                    value.OptionsSha256 == RuntimeCapabilityProbeContract.ExecutionFlowOptionsDigest &&
                    value.TransformId == RuntimeCapabilityProbeContract.ExecutionFlowTransformId &&
                    value.ProfileId == RuntimeCapabilityProbeContract.ExecutionFlowProfileId &&
                    value.Applied,
                $"{prefix} does not bind the required applied Execution Flow derivation.");
            derivation = new RuntimeCapabilityProbeDerivationSnapshot(
                value!.ParentArtifactSha256,
                value.ProcessorId,
                value.ProcessorVersion,
                value.OptionsSha256,
                value.TransformId,
                value.ProfileId,
                value.Applied);
        }
        else
        {
            Require(probe!.Derivation is null &&
                    probe.ArtifactSha256 == probe.SourceArtifactSha256,
                $"{prefix} cannot substitute or derive the canonical source probe artifact.");
        }

        return new RuntimeCapabilityProbeArtifactSnapshot(
            probe!.Contract,
            probe.SourceArtifactSha256,
            probe.ArtifactSha256,
            probe.EntryAssemblySha256,
            probe.PlanSha256,
            probe.PreflightProfileSha256,
            derivation);
    }

    private static Dictionary<string, RuntimeCapabilityArtifact> ValidateArtifacts(
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt,
        RuntimePromotionCapabilityCheck check,
        List<RuntimeCapabilityArtifact?>? values,
        string prefix)
    {
        Require(values is { Count: >= 2 and <= 8 } && values.All(static value => value is not null),
            $"{prefix} must contain between 2 and 8 non-null artifacts.");
        var byRole = new Dictionary<string, RuntimeCapabilityArtifact>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in values!.Select(static value => value!))
        {
            Require(IsArtifactRole(artifact.Role), $"{prefix} artifact role '{artifact.Role}' is invalid.");
            Require(byRole.TryAdd(artifact.Role, artifact),
                $"{prefix} contains duplicate artifact role '{artifact.Role}'.");
            Require(IsCanonicalImagePath(artifact.Path),
                $"{prefix} artifact '{artifact.Role}' has an invalid image path.");
            Require(paths.Add(artifact.Path),
                $"{prefix} contains duplicate artifact path '{artifact.Path}'.");
            Require(IsSha256(artifact.Sha256) && artifact.SizeBytes is > 0 and <= MaximumArtifactBytes,
                $"{prefix} artifact '{artifact.Role}' has an invalid byte identity.");
            Require(IsArtifactFormat(artifact.Format, artifact.Architecture),
                $"{prefix} artifact '{artifact.Role}' has an invalid format or architecture.");
        }

        var operationName = check.Capability == "jit-asm" ? "jit" : "run";
        var receiptOperation = operationName == "jit" ? receipt.Operations.Jit : receipt.Operations.Run;
        RuntimeOperationDefinition? profileOperation = operationName == "jit"
            ? profile.Operations?.Jit
            : profile.Operations?.Run;
        Require(receiptOperation is not null, $"{prefix} receipt has no {operationName} operation.");
        Require(profileOperation is not null, $"{prefix} profile has no {operationName} operation.");
        Require(byRole.TryGetValue("helper", out var helper) &&
                helper.Path == receiptOperation!.AssemblyPath &&
                helper.Sha256 == receiptOperation.AssemblySha256 &&
                helper.Format == "managed-pe" && helper.Architecture == "anycpu",
            $"{prefix} helper artifact does not match receipt operations.{operationName}.");
        ValidateExecutableHosts(profile, profileOperation!, byRole, prefix);

        var isCoreClr2 = IsCoreClrMajor(receipt, 2);
        var supportsSupportAssembly = (receipt.Family is "coreclr" or "coreclr-wine") && !isCoreClr2;
        var requiresSupportAssembly = profile.Capabilities.Any(static capability =>
            capability is "inspection" or "execution-flow");
        if (isCoreClr2 && requiresSupportAssembly)
        {
            throw new BundleValidationException(
                $"{prefix} CoreCLR 2.x cannot declare SharpLab.Runtime instrumentation capabilities.");
        }
        if (byRole.TryGetValue("support-assembly", out var support))
        {
            Require(!isCoreClr2,
                $"{prefix} CoreCLR 2.x cannot bind a SharpLab.Runtime support-assembly artifact.");
            Require(supportsSupportAssembly &&
                    support.Path == "/opt/sharplabnext/SharpLab.Runtime.dll" &&
                    support.Format == "managed-pe" && support.Architecture == "anycpu",
                $"{prefix} has an invalid SharpLab.Runtime support-assembly artifact.");
        }
        else if (requiresSupportAssembly)
        {
            throw new BundleValidationException(
                $"{prefix} has no valid SharpLab.Runtime support-assembly artifact for its instrumentation capabilities.");
        }

        if (check.Capability == "jit-asm")
        {
            Require(byRole.TryGetValue("jit-library", out var jitLibrary),
                $"{prefix} has no jit-library artifact.");
            if (receipt.Platform == "mono")
            {
                Require(jitLibrary!.Format == "elf" && jitLibrary.Architecture == "x64" &&
                        jitLibrary.Path == "/usr/bin/mono-sgen",
                    $"{prefix} Mono jit-library must be the fixed x64 ELF /usr/bin/mono-sgen host.");
            }
            else if (receipt.Platform == "linux")
            {
                Require(jitLibrary!.Format == "elf" && jitLibrary.Architecture == "x64" &&
                        jitLibrary.Path.EndsWith("/libclrjit.so", StringComparison.Ordinal),
                    $"{prefix} Linux jit-library must be the x64 ELF libclrjit.so.");
            }
            else if (receipt.Platform == "wine")
            {
                Require(jitLibrary!.Format == "pe" && jitLibrary.Architecture == "x64" &&
                        jitLibrary.Path.EndsWith("clrjit.dll", StringComparison.OrdinalIgnoreCase),
                    $"{prefix} Wine jit-library must be the x64 PE clrjit.dll.");
            }

            if (check.SourceMappingKind == RuntimeJitSourceMappingKinds.LinuxProfiler)
            {
                Require(byRole.TryGetValue("profiler", out var profiler) &&
                        profiler.Path == receiptOperation!.ProfilerPath &&
                        profiler.Sha256 == receiptOperation.ProfilerSha256 &&
                        profiler.Format == "elf" && profiler.Architecture == "x64",
                    $"{prefix} profiler artifact does not match the receipt JIT profiler.");
            }
            else
            {
                Require(!byRole.ContainsKey("profiler"),
                    $"{prefix} cannot bind a profiler for mapping kind '{check.SourceMappingKind}'.");
            }
            var requiresDesktopHelper = profileOperation!.ImplementationId ==
                RuntimeOperationImplementationIds.DesktopClrJitInspector;
            Require(requiresDesktopHelper == byRole.ContainsKey("desktop-helper"),
                $"{prefix} Desktop CLR helper presence does not match the JIT provider.");
            if (requiresDesktopHelper)
            {
                var desktopHelper = byRole["desktop-helper"];
                Require(desktopHelper.Path == DesktopClrCaptureHelperPath &&
                        desktopHelper.Format == "managed-pe" &&
                        desktopHelper.Architecture == "anycpu",
                    $"{prefix} Desktop CLR capture helper is invalid.");
            }
        }
        else
        {
            Require(!byRole.ContainsKey("jit-library") && !byRole.ContainsKey("profiler"),
                $"{prefix} non-JIT capability cannot bind JIT artifacts.");
        }
        return byRole;
    }

    private static void ValidateExecutableHosts(
        RuntimeProfileDefinition profile,
        RuntimeOperationDefinition operation,
        Dictionary<string, RuntimeCapabilityArtifact> artifacts,
        string prefix)
    {
        var command = operation.Command
            ?? throw new BundleValidationException($"{prefix} operation command is missing.");
        var executable = command.Executable;
        Require(IsCanonicalImagePath(executable),
            $"{prefix} operation executable must be a canonical absolute image path.");

        string? innerHostToken = null;
        string? innerHostPath = null;
        switch (operation.ImplementationId)
        {
            case RuntimeOperationImplementationIds.WineRunner:
                Require(command.Argv is { Count: > 2 },
                    $"{prefix} Wine runner command has no fixed target host.");
                innerHostToken = command.Argv[2];
                innerHostPath = NormalizeHostImagePath(innerHostToken, prefix);
                break;
            case RuntimeOperationImplementationIds.LegacyJitInspector
                when operation.PathStyle == RuntimeOperationPathStyles.WineZ:
                Require(command.Argv is { Count: > 0 },
                    $"{prefix} Wine CoreCLR command has no fixed dotnet.exe host.");
                innerHostToken = command.Argv[0];
                innerHostPath = profile.Layout.DotNetHostPath;
                Require(IsCanonicalImagePath(innerHostPath) &&
                        string.Equals(
                            innerHostToken,
                            $"Z:{innerHostPath.Replace('/', '\\')}",
                            StringComparison.Ordinal),
                    $"{prefix} Wine dotnet.exe command token does not match the Runtime Profile image path.");
                break;
            case RuntimeOperationImplementationIds.MonoJitInspector:
                innerHostToken = "/usr/bin/mono";
                innerHostPath = "/usr/bin/mono";
                break;
            case RuntimeOperationImplementationIds.DesktopClrJitInspector:
                innerHostToken = profile.Layout.WineHostPath;
                innerHostPath = profile.Layout.WineHostPath;
                Require(innerHostPath == "/usr/lib/wine/wine64",
                    $"{prefix} Desktop CLR JIT requires the fixed x64 Wine host.");
                break;
        }

        if (innerHostPath is null)
        {
            Require(!artifacts.ContainsKey("control-host"),
                $"{prefix} single-host command cannot declare a control-host artifact.");
            ValidateExecutableHostArtifact(
                artifacts,
                "runtime-host",
                executable!,
                expectedFormat: "elf",
                prefix);
            return;
        }

        Require(!string.Equals(executable, innerHostPath, StringComparison.Ordinal),
            $"{prefix} control and runtime hosts must resolve to distinct image paths.");
        ValidateExecutableHostArtifact(
            artifacts,
            "control-host",
            executable!,
            expectedFormat: "elf",
            prefix);
        ValidateExecutableHostArtifact(
            artifacts,
            "runtime-host",
            innerHostPath,
                expectedFormat: innerHostToken!.Contains('\\') ? "pe" : "elf",
            prefix);
    }

    private static void ValidateExecutableHostArtifact(
        Dictionary<string, RuntimeCapabilityArtifact> artifacts,
        string role,
        string expectedPath,
        string expectedFormat,
        string prefix)
    {
        Require(artifacts.TryGetValue(role, out var artifact) &&
                artifact.Path == expectedPath &&
                artifact.Format == expectedFormat &&
                artifact.Architecture == "x64",
            $"{prefix} {role} artifact does not match the Runtime Profile executable host '{expectedPath}'.");
    }

    private static string NormalizeHostImagePath(string token, string prefix)
    {
        if (IsCanonicalImagePath(token))
            return token;
        const string wineZPrefix = "Z:\\";
        Require(token.StartsWith(wineZPrefix, StringComparison.Ordinal),
            $"{prefix} target host is not a canonical Unix or Wine Z: image path.");
        var imagePath = "/" + token[wineZPrefix.Length..].Replace('\\', '/');
        Require(IsCanonicalImagePath(imagePath),
            $"{prefix} target host does not normalize to a canonical image path.");
        return imagePath;
    }

    private static void ValidateInvocation(
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt,
        RuntimePromotionCapabilityCheck check,
        RuntimeCapabilityInvocation? invocation,
        IReadOnlyDictionary<string, RuntimeCapabilityArtifact> artifacts,
        string prefix)
    {
        Require(invocation is not null, $"{prefix} invocation is missing.");
        var isJit = check.Capability == "jit-asm";
        var implementation = isJit
            ? profile.Operations?.Jit?.ImplementationId
            : profile.Operations?.Run?.ImplementationId;
        var pathStyle = isJit
            ? profile.Operations?.Jit?.PathStyle
            : profile.Operations?.Run?.PathStyle;
        Require(implementation is not null && pathStyle is not null,
            $"{prefix} profile operation is missing.");
        var actualInvocation = invocation!;
        RequireEqual(actualInvocation.Implementation, implementation, $"{prefix} implementation");
        Require(actualInvocation.Command is { Count: >= 2 and <= 64 } &&
                actualInvocation.Command.All(IsCommandToken),
            $"{prefix} command is invalid.");
        var relativeEntry = NormalizeEntryAssembly(actualInvocation.EntryAssembly?.Path, pathStyle!);
        Require(actualInvocation.EntryAssembly is not null && IsSha256(actualInvocation.EntryAssembly.Sha256),
            $"{prefix} entry assembly identity is invalid.");
        IReadOnlyList<string> expectedCommand = isJit
            ? RuntimeProfileCommandBuilder.CreateJitCommand(profile, relativeEntry, actualInvocation.MethodFilter)
            : RuntimeProfileCommandBuilder.CreateRunCommand(
                profile,
                relativeEntry,
                ExpectedRunProbeArguments(check.Capability));
        Require(expectedCommand.SequenceEqual(actualInvocation.Command!, StringComparer.Ordinal),
            $"{prefix} command does not match the selected Runtime Profile operation.");
        if (isJit)
        {
            Require(!string.IsNullOrWhiteSpace(actualInvocation.MethodFilter) &&
                    actualInvocation.MethodFilter.Length <= 256 &&
                    !actualInvocation.MethodFilter.Any(char.IsControl),
                $"{prefix} JIT method filter is invalid.");
        }
        else
        {
            Require(actualInvocation.MethodFilter is null,
                $"{prefix} non-JIT invocation cannot declare a method filter.");
        }
        var firstCommand = actualInvocation.Command![0];
        Require(artifacts.Values.Any(artifact =>
                artifact.Role is "runtime-host" or "control-host" && artifact.Path == firstCommand),
            $"{prefix} command does not start with a bound host artifact.");
        Require(actualInvocation.Outcome == "succeeded" && actualInvocation.ExitCode == 0 &&
                actualInvocation.RuntimeFrameCount is >= 1 and <= 100_000 &&
                actualInvocation.TerminalFrameKind == "Exit" && actualInvocation.TerminalStatus == "completed" &&
                actualInvocation.StdoutBytes is >= 0 and <= 16_777_216 &&
                actualInvocation.StderrBytes is >= 0 and <= 16_777_216,
            $"{prefix} did not produce a successful bounded RuntimeFrame result.");
    }

    private static IReadOnlyList<string> ExpectedRunProbeArguments(string capability) =>
        capability switch
        {
            "run" => ["success-security"],
            "inspection" => ["inspection"],
            "execution-flow" => ["execution-flow"],
            _ => throw new BundleValidationException(
                $"Runtime capability evidence uses unsupported Run probe '{capability}'.")
        };

    private static void ValidateSandbox(
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt,
        RuntimeCapabilitySandbox? sandbox,
        string prefix)
    {
        Require(sandbox is not null, $"{prefix} sandbox is missing.");
        Require(IsStableId(sandbox!.SupervisorPolicyId) && IsStableId(sandbox.SecurityPolicyId) &&
                IsSha256(sandbox.SeccompSha256) &&
                ContainerIdRegex().IsMatch(sandbox.ContainerId ?? string.Empty) &&
                sandbox.NetworkMode == "none" && sandbox.NetworkProbeBlocked &&
                sandbox.ReadOnlyRootFilesystem && sandbox.ReadOnlyProbeBlocked &&
                sandbox.CapDrop is ["ALL"] && sandbox.NoNewPrivileges,
            $"{prefix} does not prove the required Supervisor isolation.");
        RequireEqual(
            sandbox.SupervisorPolicyId,
            ApprovedSupervisorPolicyId,
            $"{prefix} Supervisor sandbox policy");
        RequireEqual(
            sandbox.SeccompSha256,
            ApprovedSeccompSha256,
            $"{prefix} Supervisor seccomp policy");
        RequireEqual(
            sandbox.User,
            profile.Container.ExecutionUser,
            $"{prefix} sandbox user");
        var policy = profile.SecurityPolicies.SingleOrDefault(policy =>
            string.Equals(policy.Id, sandbox.SecurityPolicyId, StringComparison.Ordinal));
        Require(policy is not null && profile.AllowedSecurityPolicyIds.Contains(policy.Id, StringComparer.Ordinal),
            $"{prefix} security policy is not selected by the Runtime Profile.");
        Require(sandbox.MemoryBytes == policy!.MemoryBytes && sandbox.NanoCpus == policy.NanoCpus &&
                sandbox.PidsLimit == policy.PidsLimit &&
                sandbox.DeadlineMilliseconds == checked(policy.MaximumDurationSeconds * 1000) &&
                sandbox.OutputLimitBytes == policy.MaximumOutputBytes &&
                sandbox.TmpfsBytes == policy.TmpfsBytes,
            $"{prefix} resource limits do not match the selected security policy.");
    }

    private static void ValidateLifecycle(RuntimeCapabilityLifecycle? lifecycle, string prefix)
    {
        Require(lifecycle is not null, $"{prefix} lifecycle probes are missing.");
        ValidateProbe(lifecycle!.OutputOverflow, "output-limit-exceeded", "outputOverflow", prefix);
        ValidateProbe(lifecycle.Timeout, "timeout", "timeout", prefix);
        ValidateProbe(lifecycle.Cancellation, "cancelled", "cancellation", prefix);
        ValidateProbe(lifecycle.ProcessTreeCleanup, "completed", "processTreeCleanup", prefix);
    }

    private static void ValidateProbe(
        RuntimeCapabilityLifecycleProbe? probe,
        string terminalStatus,
        string name,
        string prefix)
    {
        Require(probe is not null && probe.Result == "passed" &&
                probe.TerminalStatus == terminalStatus &&
                probe.ContainerRemoved && probe.ProcessTreeRemoved,
            $"{prefix} lifecycle.{name} did not pass with complete cleanup.");
    }

    private static void ValidateDetails(
        RuntimePromotionReceiptDocument receipt,
        RuntimePromotionCapabilityCheck check,
        RuntimeCapabilityEvidenceDocument evidence,
        IReadOnlyDictionary<string, RuntimeCapabilityArtifact> artifacts,
        string prefix)
    {
        var present = new[]
        {
            evidence.Run is not null,
            evidence.Jit is not null,
            evidence.Inspection is not null,
            evidence.ExecutionFlow is not null
        }.Count(static value => value);
        Require(present == 1, $"{prefix} must contain exactly one capability result.");
        switch (check.Capability)
        {
            case "run":
                Require(evidence.Run is not null &&
                        !string.IsNullOrEmpty(evidence.Run.ExpectedStdoutMarker) &&
                        evidence.Run.ExpectedStdoutMarker == evidence.Run.ObservedStdoutMarker &&
                        !string.IsNullOrEmpty(evidence.Run.ExpectedStderrMarker) &&
                        evidence.Run.ExpectedStderrMarker == evidence.Run.ObservedStderrMarker &&
                        evidence.Run.ExceptionFrameValidated,
                    $"{prefix} Run markers or structured exception probe did not pass.");
                break;
            case "jit-asm":
                ValidateJit(receipt, check, evidence.Jit, artifacts, prefix);
                break;
            case "inspection":
                Require(evidence.Inspection is not null && evidence.Inspection.RecordCount >= 2 &&
                        evidence.Inspection.Kinds is not null &&
                        evidence.Inspection.Kinds.Count == evidence.Inspection.Kinds.Distinct(StringComparer.Ordinal).Count() &&
                        evidence.Inspection.Kinds.Contains("Value", StringComparer.Ordinal) &&
                        evidence.Inspection.Kinds.Contains("MemoryGraph", StringComparer.Ordinal) &&
                        evidence.Inspection.ValueProbePassed && evidence.Inspection.MemoryGraphProbePassed,
                    $"{prefix} did not prove Value and MemoryGraph behavior.");
                break;
            case "execution-flow":
                var flow = evidence.ExecutionFlow;
                var derivation = evidence.ProbeArtifact?.Derivation;
                Require(flow is not null && flow.RecordCount >= 2 &&
                        flow.SequencePointCount >= 1 && flow.BranchCount >= 1 &&
                        flow.SourceRangeCount >= 2 &&
                        IsSha256(flow.DerivedArtifactSha256) &&
                        flow.DerivedArtifactSha256 == evidence.ProbeArtifact?.ArtifactSha256 &&
                        flow.ParentArtifactSha256 == evidence.ProbeArtifact?.SourceArtifactSha256 &&
                        derivation is not null &&
                        flow.ParentArtifactSha256 == derivation?.ParentArtifactSha256 &&
                        flow.ProcessorId == derivation?.ProcessorId &&
                        flow.ProcessorVersion == derivation?.ProcessorVersion &&
                        flow.OptionsSha256 == derivation?.OptionsSha256 &&
                        flow.TransformId == derivation?.TransformId &&
                        flow.ProfileId == derivation?.ProfileId &&
                        flow.Applied && derivation.Applied,
                    $"{prefix} lacks sequence, branch, or source-range proof.");
                break;
            default:
                throw new BundleValidationException($"{prefix} uses an unsupported capability.");
        }
    }

    private static void ValidateJit(
        RuntimePromotionReceiptDocument receipt,
        RuntimePromotionCapabilityCheck check,
        RuntimeCapabilityJit? jit,
        IReadOnlyDictionary<string, RuntimeCapabilityArtifact> artifacts,
        string prefix)
    {
        Require(jit is not null, $"{prefix} JIT result is missing.");
        RequireEqual(jit!.RuntimeVersion, receipt.ResolvedVersion, $"{prefix} runtime version");
        RequireEqual(jit.JitVersion, receipt.RuntimeIdentity.JitVersion, $"{prefix} JIT version");
        Require(jit.Methods is { Count: >= 1 and <= 10_000 } &&
                jit.Methods.All(static method => method is not null),
            $"{prefix} has no bounded method list.");
        var ranges = new List<RuntimeCapabilitySourceRange>();
        foreach (var method in jit.Methods!.Select(static method => method!))
        {
            Require(MethodTokenRegex().IsMatch(method.MetadataToken ?? string.Empty) &&
                    !string.IsNullOrWhiteSpace(method.DisplayName) &&
                    method.NativeCodeBytes > 0 && method.InstructionCount > 0 &&
                    method.SourceRanges is not null,
                $"{prefix} contains an invalid or empty JIT method.");
            foreach (var range in method.SourceRanges!)
            {
                Require(range is not null && IsValidSourceRange(range),
                    $"{prefix} contains an invalid JIT source range.");
                ranges.Add(range!);
            }
        }
        var distinctRanges = ranges.Select(static range =>
                $"{range.Document}\0{range.StartLine}\0{range.StartColumn}\0{range.EndLine}\0{range.EndColumn}")
            .Distinct(StringComparer.Ordinal)
            .Count();
        var mapping = jit.Mapping
            ?? throw new BundleValidationException($"{prefix} JIT mapping result is missing.");
        RequireEqual(mapping.Kind, check.SourceMappingKind, $"{prefix} mapping kind");
        RequireEqual(mapping.Source, check.MappingSource, $"{prefix} mapping source");
        Require(mapping.RangeCount == ranges.Count &&
                mapping.DistinctSourceRangeCount == distinctRanges,
            $"{prefix} mapping counts do not match the retained ranges.");
        if (check.SourceMappingKind == RuntimeJitSourceMappingKinds.None)
        {
            Require(jit.Pdb is null && ranges.Count == 0 && !mapping.AllRangesMatchPdb,
                $"{prefix} mapping-free or method-level JIT evidence cannot claim PDB source ranges.");
        }
        else
        {
            var pdb = jit.Pdb;
            Require(pdb is not null && IsWorkspacePdb(pdb.Path) &&
                    IsSha256(pdb.Sha256) && PdbContentIdRegex().IsMatch(pdb.ContentId ?? string.Empty),
                $"{prefix} PDB identity is invalid.");
            Require(pdb!.SequencePointCount >= 2 && ranges.Count >= 2 && distinctRanges >= 2 &&
                    mapping.AllRangesMatchPdb,
                $"{prefix} mapped JIT evidence lacks multiple PDB-matched source ranges.");
        }
        if (check.SourceMappingKind == RuntimeJitSourceMappingKinds.LinuxProfiler)
        {
            Require(artifacts.ContainsKey("profiler"),
                $"{prefix} profiler mapping has no bound profiler bytes.");
        }
    }

    private static string NormalizeEntryAssembly(string? path, string pathStyle)
    {
        const string unixPrefix = "/workspace/";
        const string winePrefix = "Z:\\workspace\\";
        return pathStyle switch
        {
            RuntimeOperationPathStyles.Unix when path?.StartsWith(unixPrefix, StringComparison.Ordinal) == true =>
                path[unixPrefix.Length..],
            RuntimeOperationPathStyles.WineZ when path?.StartsWith(winePrefix, StringComparison.Ordinal) == true =>
                path[winePrefix.Length..].Replace('\\', '/'),
            _ => throw new BundleValidationException("Runtime capability evidence entry assembly path is invalid.")
        };
    }

    private static bool IsValidSourceRange(RuntimeCapabilitySourceRange range) =>
        range.IlOffset >= 0 && range.NativeStartOffset >= 0 &&
        range.NativeEndOffset > range.NativeStartOffset &&
        !string.IsNullOrWhiteSpace(range.Document) &&
        range.StartLine >= 1 && range.StartColumn >= 1 &&
        range.EndLine >= range.StartLine && range.EndColumn >= 1 &&
        (range.EndLine > range.StartLine || range.EndColumn > range.StartColumn);

    private static bool IsWorkspacePdb(string? value)
    {
        if (value is null || value.Length is < 16 or > 4096 ||
            !value.EndsWith(".pdb", StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            return false;
        }

        const string unixPrefix = "/workspace/";
        if (value.StartsWith(unixPrefix, StringComparison.Ordinal))
        {
            return !value.Contains('\\') && IsCanonicalPathSuffix(value[unixPrefix.Length..], '/');
        }

        const string winePrefix = "Z:\\workspace\\";
        return value.StartsWith(winePrefix, StringComparison.Ordinal) &&
               !value.Contains('/') && IsCanonicalPathSuffix(value[winePrefix.Length..], '\\');
    }

    private static bool IsCanonicalPathSuffix(string value, char separator) =>
        value.Split(separator, StringSplitOptions.None)
            .All(static segment => segment.Length > 0 && segment is not ("." or ".."));

    private static bool IsCoreClrMajor(RuntimePromotionReceiptDocument receipt, int major)
    {
        if (receipt.Family is not ("coreclr" or "coreclr-wine"))
            return false;
        var separator = receipt.ResolvedVersion.IndexOfAny(['.', '-']);
        var majorText = separator < 0 ? receipt.ResolvedVersion : receipt.ResolvedVersion[..separator];
        return int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
               parsed == major;
    }

    private static bool IsCanonicalImagePath(string? value) =>
        value is not null && value.Length is > 1 and <= 4096 && value[0] == '/' && value[^1] != '/' &&
        !value.Contains("//", StringComparison.Ordinal) && !value.Contains('\\') &&
        !value.Any(char.IsControl) &&
        value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(static segment => segment is not ("." or ".."));

    private static bool IsCommandToken(string? value) =>
        value is not null && value.Length is > 0 and <= 4096 &&
        !value.Any(static character => character is '\0' or '\r' or '\n');

    private static bool IsArtifactRole(string? value) =>
        value is "helper" or "desktop-helper" or "control-host" or "runtime-host" or
            "support-assembly" or "jit-library" or "profiler";

    private static bool IsArtifactFormat(string? format, string? architecture) =>
        (format is "elf" or "pe" or "managed-pe" or "script") &&
        (architecture is "x64" or "anycpu" or "shell");

    private static bool IsSha256(string? value) =>
        value is not null && Sha256Regex().IsMatch(value);

    private static bool IsGitCommit(string? value) =>
        value is not null && GitCommitRegex().IsMatch(value);

    private static bool IsStableId(string? value) =>
        value is not null && StableIdRegex().IsMatch(value);

    private static bool IsCanonicalUtcTimestamp(string? value) =>
        value is not null && CanonicalUtcRegex().IsMatch(value) &&
        DateTimeOffset.TryParseExact(
            value,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new BundleValidationException(message);
    }

    private static void RequireEqual(string? actual, string? expected, string label)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new BundleValidationException($"{label} does not match its promotion receipt.");
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex GitCommitRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerIdRegex();

    [GeneratedRegex("^0x06[0-9a-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex MethodTokenRegex();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex PdbContentIdRegex();

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,7})?Z$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalUtcRegex();
}

internal sealed class RuntimeCapabilityEvidenceDocument
{
    public required int SchemaVersion { get; init; }
    public required string ProfileId { get; init; }
    public required string Capability { get; init; }
    public required RuntimeCapabilityImage Image { get; init; }
    public required string SourceRevision { get; init; }
    public required string CompletedAtUtc { get; init; }
    public required string Result { get; init; }
    public required RuntimeCapabilityProducer Producer { get; init; }
    public required RuntimeCapabilityProbeArtifact ProbeArtifact { get; init; }
    public required List<RuntimeCapabilityArtifact?> Artifacts { get; init; }
    public required RuntimeCapabilityInvocation Invocation { get; init; }
    public required RuntimeCapabilitySandbox Sandbox { get; init; }
    public required RuntimeCapabilityLifecycle Lifecycle { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeCapabilityRun? Run { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeCapabilityJit? Jit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeCapabilityInspection? Inspection { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeCapabilityExecutionFlow? ExecutionFlow { get; init; }
}

internal sealed class RuntimeCapabilityImage
{
    public required string Reference { get; init; }
    public required string ImageId { get; init; }
}

internal sealed class RuntimeCapabilityProducer
{
    public required string Id { get; init; }
    public required string SourceRevision { get; init; }
    public required string PlanSha256 { get; init; }
}

internal sealed class RuntimeCapabilityProbeArtifact
{
    public required string Contract { get; init; }
    public required string SourceArtifactSha256 { get; init; }
    public required string ArtifactSha256 { get; init; }
    public required string EntryAssemblySha256 { get; init; }
    public required string PlanSha256 { get; init; }
    public required string PreflightProfileSha256 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeCapabilityProbeDerivation? Derivation { get; init; }
}

internal sealed class RuntimeCapabilityProbeDerivation
{
    public required string ParentArtifactSha256 { get; init; }
    public required string ProcessorId { get; init; }
    public required string ProcessorVersion { get; init; }
    public required string OptionsSha256 { get; init; }
    public required string TransformId { get; init; }
    public required string ProfileId { get; init; }
    public required bool Applied { get; init; }
}

internal sealed class RuntimeCapabilityArtifact
{
    public required string Role { get; init; }
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required string Format { get; init; }
    public required string Architecture { get; init; }
}

internal sealed class RuntimeCapabilityInvocation
{
    public required string Implementation { get; init; }
    public required List<string> Command { get; init; }
    public required RuntimeCapabilityEntryAssembly EntryAssembly { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodFilter { get; init; }

    public required string Outcome { get; init; }
    public required int ExitCode { get; init; }
    public required int RuntimeFrameCount { get; init; }
    public required string TerminalFrameKind { get; init; }
    public required string TerminalStatus { get; init; }
    public required long StdoutBytes { get; init; }
    public required long StderrBytes { get; init; }
}

internal sealed class RuntimeCapabilityEntryAssembly
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
}

internal sealed class RuntimeCapabilitySandbox
{
    public required string SupervisorPolicyId { get; init; }
    public required string SecurityPolicyId { get; init; }
    public required string SeccompSha256 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApparmorProfile { get; init; }

    public required string ContainerId { get; init; }
    public required string NetworkMode { get; init; }
    public required bool NetworkProbeBlocked { get; init; }
    public required bool ReadOnlyRootFilesystem { get; init; }
    public required bool ReadOnlyProbeBlocked { get; init; }
    public required List<string> CapDrop { get; init; }
    public required bool NoNewPrivileges { get; init; }
    public required string User { get; init; }
    public required long NanoCpus { get; init; }
    public required long MemoryBytes { get; init; }
    public required long PidsLimit { get; init; }
    public required int DeadlineMilliseconds { get; init; }
    public required long OutputLimitBytes { get; init; }
    public required int TmpfsBytes { get; init; }
}

internal sealed class RuntimeCapabilityLifecycle
{
    public required RuntimeCapabilityLifecycleProbe OutputOverflow { get; init; }
    public required RuntimeCapabilityLifecycleProbe Timeout { get; init; }
    public required RuntimeCapabilityLifecycleProbe Cancellation { get; init; }
    public required RuntimeCapabilityLifecycleProbe ProcessTreeCleanup { get; init; }
}

internal sealed class RuntimeCapabilityLifecycleProbe
{
    public required string Result { get; init; }
    public required string TerminalStatus { get; init; }
    public required bool ContainerRemoved { get; init; }
    public required bool ProcessTreeRemoved { get; init; }
}

internal sealed class RuntimeCapabilityRun
{
    public required string ExpectedStdoutMarker { get; init; }
    public required string ObservedStdoutMarker { get; init; }
    public required string ExpectedStderrMarker { get; init; }
    public required string ObservedStderrMarker { get; init; }
    public required bool ExceptionFrameValidated { get; init; }
}

internal sealed class RuntimeCapabilityJit
{
    public required string RuntimeVersion { get; init; }
    public required string JitVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeCapabilityPdb? Pdb { get; init; }

    public required List<RuntimeCapabilityJitMethod?> Methods { get; init; }
    public required RuntimeCapabilityJitMapping Mapping { get; init; }
}

internal sealed class RuntimeCapabilityPdb
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required string ContentId { get; init; }
    public required int SequencePointCount { get; init; }
}

internal sealed class RuntimeCapabilityJitMethod
{
    public required string MetadataToken { get; init; }
    public required string DisplayName { get; init; }
    public required int NativeCodeBytes { get; init; }
    public required int InstructionCount { get; init; }
    public required List<RuntimeCapabilitySourceRange?> SourceRanges { get; init; }
}

internal sealed class RuntimeCapabilitySourceRange
{
    public required int IlOffset { get; init; }
    public required int NativeStartOffset { get; init; }
    public required int NativeEndOffset { get; init; }
    public required string Document { get; init; }
    public required int StartLine { get; init; }
    public required int StartColumn { get; init; }
    public required int EndLine { get; init; }
    public required int EndColumn { get; init; }
}

internal sealed class RuntimeCapabilityJitMapping
{
    public required string Kind { get; init; }
    public required string Source { get; init; }
    public required int RangeCount { get; init; }
    public required int DistinctSourceRangeCount { get; init; }
    public required bool AllRangesMatchPdb { get; init; }
}

internal sealed class RuntimeCapabilityInspection
{
    public required int RecordCount { get; init; }
    public required List<string> Kinds { get; init; }
    public required bool ValueProbePassed { get; init; }
    public required bool MemoryGraphProbePassed { get; init; }
}

internal sealed class RuntimeCapabilityExecutionFlow
{
    public required int RecordCount { get; init; }
    public required int SequencePointCount { get; init; }
    public required int BranchCount { get; init; }
    public required int SourceRangeCount { get; init; }
    public required string DerivedArtifactSha256 { get; init; }
    public required string ParentArtifactSha256 { get; init; }
    public required string ProcessorId { get; init; }
    public required string ProcessorVersion { get; init; }
    public required string OptionsSha256 { get; init; }
    public required string TransformId { get; init; }
    public required string ProfileId { get; init; }
    public required bool Applied { get; init; }
}

public sealed record RuntimeCapabilityProbeArtifactSnapshot(
    string Contract,
    string SourceArtifactSha256,
    string ArtifactSha256,
    string EntryAssemblySha256,
    string PlanSha256,
    string PreflightProfileSha256,
    RuntimeCapabilityProbeDerivationSnapshot? Derivation);

public sealed record RuntimeCapabilityProbeDerivationSnapshot(
    string ParentArtifactSha256,
    string ProcessorId,
    string ProcessorVersion,
    string OptionsSha256,
    string TransformId,
    string ProfileId,
    bool Applied);

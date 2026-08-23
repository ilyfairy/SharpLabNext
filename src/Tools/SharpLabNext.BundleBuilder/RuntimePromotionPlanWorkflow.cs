using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.BundleBuilder;

public static class RuntimePromotionPlanWorkflow
{
    public const string ProducerId = "sharplabnext-runtime-preflight-v1";
    internal const int MaximumPromotionDocumentBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions InputJsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        NewLine = "\n",
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static RuntimePromotionPlanContext CreateContext(
        byte[] profileBytes,
        byte[] preflightProfileBytes,
        byte[] planBytes,
        byte[] performancePolicyBytes)
    {
        ArgumentNullException.ThrowIfNull(profileBytes);
        ArgumentNullException.ThrowIfNull(preflightProfileBytes);
        ArgumentNullException.ThrowIfNull(planBytes);
        ArgumentNullException.ThrowIfNull(performancePolicyBytes);

        RequireBoundedDocument(profileBytes, "Runtime Profile");
        RequireBoundedDocument(preflightProfileBytes, "immutable preflight Runtime Profile");
        RequireBoundedDocument(planBytes, "runtime promotion plan");
        RequireBoundedDocument(performancePolicyBytes, "runtime performance policy");

        var profile = RuntimePromotionJson.Deserialize<RuntimeProfileDefinition>(
            profileBytes,
            InputJsonOptions,
            "Runtime Profile");
        var failures = RuntimeProfileValidation.ValidatePackage(profile, requireDigestPinnedImage: false);
        if (failures.Count > 0)
        {
            throw new BundleValidationException(
                $"Runtime Profile '{profile.Id}' is invalid: {string.Join(" ", failures)}");
        }

        var preflightProfile = RuntimePromotionJson.Deserialize<RuntimeProfileDefinition>(
            preflightProfileBytes,
            InputJsonOptions,
            "Immutable preflight Runtime Profile");
        failures = RuntimeProfileValidation.ValidatePackage(
            preflightProfile,
            requireDigestPinnedImage: true);
        if (failures.Count > 0)
        {
            throw new BundleValidationException(
                $"Immutable preflight Runtime Profile '{preflightProfile.Id}' is invalid: " +
                string.Join(" ", failures));
        }

        var plan = RuntimePromotionJson.Deserialize<RuntimePromotionPlanDocument>(
            planBytes,
            InputJsonOptions,
            $"Runtime '{profile.Id}' promotion plan");
        var policy = RuntimePromotionJson.Deserialize<RuntimePerformancePolicyDocument>(
            performancePolicyBytes,
            InputJsonOptions,
            $"Runtime '{profile.Id}' performance policy");
        ValidatePlan(
            profileBytes,
            profile,
            preflightProfileBytes,
            preflightProfile,
            planBytes,
            plan,
            performancePolicyBytes,
            policy);
        return new RuntimePromotionPlanContext(
            preflightProfile,
            plan,
            policy,
            preflightProfileBytes,
            planBytes);
    }

    public static RuntimePromotionFinalizationResult Finalize(
        byte[] profileBytes,
        byte[] preflightProfileBytes,
        byte[] planBytes,
        byte[] performancePolicyBytes,
        IReadOnlyDictionary<string, byte[]> capabilityEvidence,
        byte[] performanceEvidenceBytes,
        RuntimeCapabilityRequestBinding requestBinding)
    {
        ArgumentNullException.ThrowIfNull(capabilityEvidence);
        ArgumentNullException.ThrowIfNull(performanceEvidenceBytes);
        ArgumentNullException.ThrowIfNull(requestBinding);
        RequireBoundedDocument(performanceEvidenceBytes, "runtime performance evidence");
        var context = CreateContext(
            profileBytes,
            preflightProfileBytes,
            planBytes,
            performancePolicyBytes);
        ValidateRequestBinding(context, requestBinding);
        var expected = context.Capabilities.Order(StringComparer.Ordinal).ToArray();
        var observed = capabilityEvidence.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(observed, StringComparer.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{context.ProfileId}' finalization requires exactly one capability document for " +
                $"[{string.Join(", ", expected)}].");
        }

        var checks = new List<RuntimePromotionCapabilityCheck>(expected.Length);
        var retainedFiles = new Dictionary<string, RuntimeCapabilityEvidenceImageFile>(StringComparer.Ordinal);
        var probeArtifacts = new Dictionary<string, RuntimeCapabilityProbeArtifactSnapshot>(StringComparer.Ordinal);
        foreach (var capability in expected)
        {
            var bytes = capabilityEvidence[capability]
                ?? throw new BundleValidationException(
                    $"Runtime '{context.ProfileId}' {capability} evidence is missing.");
            var validated = context.ValidateDocument(bytes, requestBinding);
            if (!StringComparer.Ordinal.Equals(validated.Capability, capability))
            {
                throw new BundleValidationException(
                    $"Runtime '{context.ProfileId}' capability evidence key does not match its document.");
            }
            probeArtifacts.Add(capability, validated.ProbeArtifact);
            foreach (var file in validated.ImageFiles)
            {
                if (retainedFiles.TryGetValue(file.Path, out var existing) && existing != file)
                {
                    throw new BundleValidationException(
                        $"Runtime '{context.ProfileId}' capability documents disagree about image file '{file.Path}'.");
                }
                retainedFiles.TryAdd(file.Path, file);
            }

            checks.Add(new RuntimePromotionCapabilityCheck
            {
                Capability = capability,
                Result = "passed",
                NetworkDisabled = true,
                SupervisorSandbox = true,
                OutputLimitValidated = true,
                SourceMappingKind = validated.SourceMappingKind,
                MappingSource = validated.MappingSource,
                EvidencePath = validated.EvidencePath,
                EvidenceSha256 = Sha256(bytes)
            });
        }
        RuntimeCapabilityEvidenceValidation.ValidateProbeSet(context.ProfileId, probeArtifacts);

        var performanceBinding = new RuntimePromotionPerformanceBinding
        {
            Result = "passed",
            PolicyId = context.PerformancePolicyId,
            PolicyPath = context.PerformancePolicyPath,
            PolicySha256 = context.PerformancePolicySha256,
            EvidencePath = context.PerformanceEvidencePath,
            EvidenceSha256 = Sha256(performanceEvidenceBytes)
        };
        var receipt = context.BuildReceipt(checks, performanceBinding);
        foreach (var capability in expected)
        {
            var check = checks.Single(item => StringComparer.Ordinal.Equals(item.Capability, capability));
            _ = RuntimeCapabilityEvidenceValidation.Validate(
                capabilityEvidence[capability],
                context.Profile,
                receipt,
                check);
        }

        var performanceEvidence = RuntimePromotionJson.Deserialize<RuntimePerformanceEvidenceDocument>(
            performanceEvidenceBytes,
            InputJsonOptions,
            $"Runtime '{context.ProfileId}' performance evidence");
        RuntimePromotionTrust.ValidatePerformancePolicy(
            context.ProfileId,
            performanceBinding,
            context.PerformancePolicy);
        RuntimePromotionTrust.ValidatePerformanceEvidence(
            context.Profile,
            context.CreateInspectedImage(),
            receipt,
            performanceBinding,
            context.PerformancePolicy,
            performanceEvidence);
        _ = RuntimePromotionTrust.ValidateOperationBindings(context.Profile, receipt);

        var receiptBytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(receipt, OutputJsonOptions) + "\n");
        RequireBoundedDocument(receiptBytes, "runtime promotion receipt");
        return new RuntimePromotionFinalizationResult(
            context.ProfileId,
            receiptBytes,
            Sha256(receiptBytes),
            checks.ToDictionary(
                static check => check.Capability,
                static check => check.EvidenceSha256,
                StringComparer.Ordinal),
            performanceBinding.EvidenceSha256);
    }

    private static void ValidatePlan(
        byte[] profileBytes,
        RuntimeProfileDefinition profile,
        byte[] preflightProfileBytes,
        RuntimeProfileDefinition preflightProfile,
        byte[] planBytes,
        RuntimePromotionPlanDocument plan,
        byte[] performancePolicyBytes,
        RuntimePerformancePolicyDocument policy)
    {
        Require(plan.SchemaVersion == 1, "The runtime promotion plan must use schema version 1.");
        RequireEqual(plan.ProfileId, profile.Id, "promotion plan profile ID");
        RequireEqual(plan.ProfileSha256, Sha256(profileBytes), "promotion plan Runtime Profile digest");
        var image = plan.Image
            ?? throw new BundleValidationException("The promotion plan has no image identity.");
        var expectedCapabilities = preflightProfile.Capabilities.Order(StringComparer.Ordinal).ToArray();
        Require(
            plan.Capabilities is { Count: >= 1 and <= 4 } &&
            expectedCapabilities.SequenceEqual(plan.Capabilities, StringComparer.Ordinal),
            "The promotion plan capability set is not canonical or does not match the immutable preflight Runtime Profile.");
        ValidatePreflightProfile(
            profile,
            preflightProfile,
            plan.PreflightProfile,
            preflightProfileBytes,
            image,
            expectedCapabilities);
        RequireEqual(
            plan.MatrixTargetId,
            RuntimePromotionTrust.ExpectedMatrixTargetId(profile),
            "promotion plan matrix target ID");
        RequireEqual(
            plan.Platform,
            RuntimePromotionTrust.ExpectedPlatform(profile.Family),
            "promotion plan platform");
        RequireEqual(plan.Family, profile.Family, "promotion plan family");
        RequireEqual(plan.ResolvedVersion, profile.RuntimeVersion, "promotion plan runtime version");
        Require(IsGitCommit(plan.SourceRevision), "The promotion plan source revision is invalid.");
        Require(IsGitCommit(plan.SourceTree), "The promotion plan source tree is invalid.");
        Require(IsExpectedCandidateTarget(plan.CandidateTarget, profile.Family),
            "The promotion plan candidate target does not match the Runtime Profile family.");
        Require(
            plan.BuildInputs is { Count: >= 1 and <= 64 } &&
            plan.BuildInputs.All(static item => IsCanonicalBuildInput(item.Key, item.Value)) &&
            HasRequiredBuildBindings(plan) &&
            IsSha256(plan.BuildInputsSha256) &&
            StringComparer.Ordinal.Equals(plan.BuildInputsSha256, Sha256(CanonicalizeBuildInputs(plan.BuildInputs))),
            "The promotion plan build-input binding is invalid.");
        Require(
            plan.Producer is not null && plan.Producer.Id == ProducerId &&
            plan.Producer.SourceRevision == plan.SourceRevision,
            "The promotion plan producer identity is invalid.");
        Require(
            IsCanonicalId(plan.SecurityPolicyId) &&
            profile.AllowedSecurityPolicyIds.Contains(plan.SecurityPolicyId, StringComparer.Ordinal) &&
            profile.SecurityPolicies.Count(item =>
                StringComparer.Ordinal.Equals(item.Id, plan.SecurityPolicyId)) == 1,
            "The promotion plan security policy is not defined exactly once by the Runtime Profile.");

        var expectedMappingKind = profile.Operations?.Jit?.SourceMappingKind ?? "not-applicable";
        RequireEqual(plan.SourceMappingKind, expectedMappingKind, "promotion plan source mapping kind");

        Require(IsImmutableReference(image.Reference), "The promotion plan image reference is not immutable.");
        Require(IsSha256(image.ImageId), "The promotion plan image ID is invalid.");
        Require(image.SizeBytes is > 0 and <= 17_179_869_184,
            "The promotion plan image size is invalid.");
        ValidateOperatorReceiptBinding(profile, plan.WineOperator, plan.SourceRevision);
        ValidateComponentIdentity(profile, plan.ComponentIdentity);
        var runtimeIdentity = plan.RuntimeIdentity
            ?? throw new BundleValidationException("The promotion plan has no runtime identity.");
        Require(
            runtimeIdentity.RuntimeCommit == profile.RuntimeCommit &&
            runtimeIdentity.JitVersion == profile.JitVersion &&
            runtimeIdentity.JitCommit == profile.JitCommit,
            "The promotion plan runtime/JIT identity does not match the Runtime Profile.");

        var performance = plan.Performance
            ?? throw new BundleValidationException("The promotion plan has no performance policy binding.");
        var provisionalPerformance = new RuntimePromotionPerformanceBinding
        {
            Result = "passed",
            PolicyId = performance.PolicyId,
            PolicyPath = performance.PolicyPath,
            PolicySha256 = performance.PolicySha256,
            EvidencePath = performance.EvidencePath,
            EvidenceSha256 = $"sha256:{new string('0', 64)}"
        };
        var provisionalReceipt = new RuntimePromotionReceiptDocument
        {
            SchemaVersion = 2,
            PlanSha256 = Sha256(planBytes),
            ProfileId = plan.ProfileId,
            MatrixTargetId = plan.MatrixTargetId,
            Platform = plan.Platform,
            Family = plan.Family,
            ResolvedVersion = plan.ResolvedVersion,
            Image = image,
            ComponentIdentity = plan.ComponentIdentity,
            RuntimeIdentity = runtimeIdentity,
            Operations = plan.Operations,
            Performance = provisionalPerformance,
            SourceRevision = plan.SourceRevision,
            Checks = []
        };
        _ = RuntimePromotionTrust.ValidateOperationBindings(profile, provisionalReceipt);
        ValidateJitLibraryPath(profile, plan.JitLibraryPath);

        Require(IsCanonicalId(performance.PolicyId), "The promotion plan performance policy ID is invalid.");
        RequireEqual(
            performance.PolicyPath,
            $"profiles/runtime-performance-policies/{performance.PolicyId}.json",
            "promotion plan performance policy path");
        RequireEqual(
            performance.EvidencePath,
            $"profiles/runtime-promotion-evidence/{profile.Id}/performance.json",
            "promotion plan performance evidence path");
        RequireEqual(
            performance.PolicySha256,
            Sha256(performancePolicyBytes),
            "promotion plan performance policy digest");
        RuntimePromotionTrust.ValidatePerformancePolicy(profile.Id, provisionalPerformance, policy);

        var canonicalPlanDigest = Sha256(planBytes);
        Require(IsSha256(canonicalPlanDigest), "The promotion plan digest is invalid.");
    }

    private static void ValidateRequestBinding(
        RuntimePromotionPlanContext context,
        RuntimeCapabilityRequestBinding binding)
    {
        if (!IsSha256(binding.ProbeArtifactRef))
            throw new BundleValidationException("The capability probe artifact reference is invalid.");
        if (context.RequiresExecutionFlow != (binding.ExecutionFlowArtifactRef is not null))
        {
            throw new BundleValidationException(
                context.RequiresExecutionFlow
                    ? "The capability request is missing its Execution Flow artifact reference."
                    : "The capability request supplied an unexpected Execution Flow artifact reference.");
        }
        if (binding.ExecutionFlowArtifactRef is not null && !IsSha256(binding.ExecutionFlowArtifactRef))
            throw new BundleValidationException("The Execution Flow artifact reference is invalid.");
        if (context.RequiresJit != (binding.MethodFilter is not null))
        {
            throw new BundleValidationException(
                context.RequiresJit
                    ? "The capability request is missing its JIT method filter."
                    : "The capability request supplied an unexpected JIT method filter.");
        }
        if (binding.MethodFilter is { Length: > 256 } ||
            binding.MethodFilter?.Any(static character => character is '\0' or '\r' or '\n') == true ||
            (binding.MethodFilter is not null && string.IsNullOrWhiteSpace(binding.MethodFilter)))
        {
            throw new BundleValidationException("The capability request JIT method filter is invalid.");
        }
    }

    internal static void ValidateRequestBinding(
        RuntimeCapabilityEvidenceDocument evidence,
        RuntimeCapabilityRequestBinding binding,
        string prefix)
    {
        RequireEqual(
            evidence.ProbeArtifact?.SourceArtifactSha256,
            binding.ProbeArtifactRef,
            $"{prefix} canonical probe artifact reference");
        if (evidence.Capability == "execution-flow")
        {
            RequireEqual(
                evidence.ProbeArtifact?.ArtifactSha256,
                binding.ExecutionFlowArtifactRef,
                $"{prefix} Execution Flow artifact reference");
        }
        else if (binding.ExecutionFlowArtifactRef is not null &&
                 evidence.ProbeArtifact?.ArtifactSha256 != binding.ProbeArtifactRef)
        {
            throw new BundleValidationException(
                $"{prefix} non-Execution Flow evidence changed the canonical probe artifact.");
        }

        var expectedMethodFilter = evidence.Capability == "jit-asm"
            ? binding.MethodFilter
            : null;
        RequireEqual(
            evidence.Invocation?.MethodFilter,
            expectedMethodFilter,
            $"{prefix} JIT method filter");
    }

    private static void RequireBoundedDocument(byte[] bytes, string description)
    {
        if (bytes.Length is < 1 or > MaximumPromotionDocumentBytes)
        {
            throw new BundleValidationException(
                $"{description} must be a 1..{MaximumPromotionDocumentBytes} byte promotion document.");
        }
    }

    private static void ValidatePreflightProfile(
        RuntimeProfileDefinition candidate,
        RuntimeProfileDefinition preflight,
        RuntimePromotionPlanPreflightProfile? binding,
        byte[] preflightProfileBytes,
        RuntimePromotionImageIdentity image,
        IReadOnlyList<string> planCapabilities)
    {
        Require(
            candidate.PromotionReceipt is null && preflight.PromotionReceipt is null,
            "Candidate and immutable preflight Runtime Profiles cannot contain a promotion receipt.");
        Require(binding is not null, "The promotion plan has no immutable preflight Runtime Profile binding.");
        RequireEqual(
            binding!.Path,
            $"profiles/runtime-promotion-plans/{candidate.Id}.profile.json",
            "promotion plan immutable preflight Runtime Profile path");
        RequireEqual(
            binding.Sha256,
            Sha256(preflightProfileBytes),
            "promotion plan immutable preflight Runtime Profile digest");
        RequireEqual(preflight.Image, image.Reference, "immutable preflight image reference");
        RequireEqual(preflight.RuntimeImageId, image.ImageId, "immutable preflight image ID");
        var expectedCandidateCapabilities = planCapabilities
            .Where(static capability => capability is not ("inspection" or "execution-flow"))
            .ToArray();
        Require(
            candidate.Capabilities.Order(StringComparer.Ordinal)
                .SequenceEqual(expectedCandidateCapabilities, StringComparer.Ordinal),
            "The blocked candidate Runtime Profile capability set is not the strict non-instrumentation subset of the promotion plan.");

        var candidateNode = JsonNode.Parse(JsonSerializer.Serialize(candidate, InputJsonOptions))
            ?? throw new BundleValidationException("The candidate Runtime Profile could not be compared.");
        var preflightNode = JsonNode.Parse(JsonSerializer.Serialize(preflight, InputJsonOptions))
            ?? throw new BundleValidationException("The immutable preflight Runtime Profile could not be compared.");
        preflightNode["image"] = candidateNode["image"]?.DeepClone();
        preflightNode["runtimeImageId"] = candidateNode["runtimeImageId"]?.DeepClone();
        preflightNode["capabilities"] = candidateNode["capabilities"]?.DeepClone();
        var profilesMatch = JsonNode.DeepEquals(candidateNode, preflightNode) ||
            IsAllowedFrameworkRangeNormalization(candidateNode.AsObject(), preflightNode.AsObject());
        Require(
            profilesMatch,
            "The immutable preflight Runtime Profile changes fields other than image identity, " +
            "framework range normalization, and plan-bound instrumentation capabilities.");
    }

    private static bool IsAllowedFrameworkRangeNormalization(
        JsonObject candidate,
        JsonObject preflight)
    {
        var runtimeVersion = OptionalString(preflight, "runtimeVersion");
        if (runtimeVersion is null ||
            !StringComparer.Ordinal.Equals(OptionalString(candidate, "runtimeVersion"), runtimeVersion) ||
            !TryGetSingleObject(candidate, "acceptedFrameworks", out var candidateFramework) ||
            !TryGetSingleObject(preflight, "acceptedFrameworks", out var preflightFramework))
        {
            return false;
        }

        var candidateExact = OptionalString(candidateFramework, "exactVersion");
        var candidateMinimum = OptionalString(candidateFramework, "minimumVersion");
        var candidateMaximum = OptionalString(candidateFramework, "maximumVersion");
        var preflightExact = OptionalString(preflightFramework, "exactVersion");
        var preflightMinimum = OptionalString(preflightFramework, "minimumVersion");
        var preflightMaximum = OptionalString(preflightFramework, "maximumVersion");

        return candidateExact is null &&
            candidateMinimum is not null &&
            candidateMaximum is not null &&
            preflightExact is not null &&
            StringComparer.Ordinal.Equals(preflightExact, runtimeVersion) &&
            preflightMinimum is null &&
            preflightMaximum is null &&
            StringComparer.Ordinal.Equals(
                OptionalString(candidateFramework, "name"),
                OptionalString(preflightFramework, "name"));
    }

    private static bool TryGetSingleObject(
        JsonObject root,
        string propertyName,
        out JsonObject value)
    {
        if (root[propertyName] is JsonArray array &&
            array.Count == 1 &&
            array[0] is JsonObject item)
        {
            value = item;
            return true;
        }

        value = null!;
        return false;
    }

    private static string? OptionalString(JsonObject root, string propertyName) =>
        root[propertyName]?.GetValue<string>();

    private static void ValidateComponentIdentity(
        RuntimeProfileDefinition profile,
        RuntimePromotionComponentIdentity? component)
    {
        Require(component is not null, "The promotion plan has no component identity.");
        Require(RuntimePromotionTrust.IsImmutableSourceUri(component!.SourceUri),
            "The promotion plan component source URI is not immutable.");
        if (profile.Family is "coreclr" or "coreclr-wine")
        {
            Require(
                component.SourceDigest is { Length: 135 } &&
                component.SourceDigest.StartsWith("sha512:", StringComparison.Ordinal) &&
                IsLowerHex(component.SourceDigest.AsSpan(7)),
                "The promotion plan CoreCLR component digest is invalid.");
            return;
        }

        Require(IsSha256(component.SourceDigest),
            "The promotion plan operator component digest is invalid.");
        if (component.SourceUri.StartsWith("docker://", StringComparison.Ordinal))
        {
            Require(component.SourceUri.EndsWith("@" + component.SourceDigest, StringComparison.Ordinal),
                "The promotion plan operator source URI and digest disagree.");
        }
    }

    private static void ValidateOperatorReceiptBinding(
        RuntimeProfileDefinition profile,
        RuntimePromotionWineOperatorBinding? binding,
        string planSourceRevision)
    {
        var requiresWineOperator = profile.Family is "coreclr-wine" or "netfx-clr-wine";
        if (!requiresWineOperator)
        {
            Require(binding is null,
                "Only Wine runtime promotion plans may bind a Wine operator receipt.");
            return;
        }

        Require(binding is not null,
            "Wine runtime promotion plans must bind a signed Wine operator receipt.");
        Require(IsGitCommit(binding!.SourceRevision) && IsGitCommit(binding.SourceTree),
            "Wine operator receipt source identity is invalid.");
        RequireEqual(binding.SourceRevision, planSourceRevision, "Wine operator receipt source revision");
        RequireEqual(
            binding.ReceiptPath,
            $"profiles/runtime-operator-receipts/wine-coreclr-{binding.SourceRevision}.json",
            "Wine operator receipt path");
        RequireEqual(
            binding.SignaturePath,
            binding.ReceiptPath + ".sig",
            "Wine operator receipt signature path");
        Require(IsSha256(binding.ReceiptSha256), "Wine operator receipt digest is invalid.");
        Require(IsSha256(binding.SignatureSha256), "Wine operator receipt signature digest is invalid.");
        RequireEqual(
            binding.KeyId,
            WineCoreClrOperatorReceiptTrust.KeyId,
            "Wine operator receipt key ID");
        Require(IsImmutableReference(binding.Reference),
            "Wine operator receipt reference is not immutable.");
        Require(IsSha256(binding.ImageId), "Wine operator receipt image ID is invalid.");
        Require(binding.SizeBytes is > 0 and <= 9_007_199_254_740_991 and <= 17_179_869_184,
            "Wine operator receipt image size is invalid.");
        Require(binding.LineageKind is "direct" or "framework-row" or "framework-parent",
            "Wine operator receipt lineage is invalid.");
        var direct = profile.Family == "coreclr-wine";
        Require(direct == (binding.LineageKind == "direct"),
            "Wine operator receipt lineage does not match the runtime family.");
        Require(
            direct
                ? binding.IntermediaryReference is null && binding.IntermediaryImageId is null && binding.IntermediarySizeBytes is null
                : IsImmutableReference(binding.IntermediaryReference!) && IsSha256(binding.IntermediaryImageId!) &&
                  binding.IntermediarySizeBytes is > 0 and <= 9_007_199_254_740_991 and <= 17_179_869_184,
            "Wine operator receipt intermediary lineage is invalid.");
    }

    internal static void ValidateJitLibraryPath(RuntimeProfileDefinition profile, string? path)
    {
        var requiresJit = profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal);
        Require(requiresJit == (path is not null),
            "The promotion plan JIT library path does not match the Runtime Profile capability set.");
        if (path is null)
            return;
        Require(path.Length is >= 2 and <= 4096 && path[0] == '/' && !path.Any(char.IsControl) &&
                !path.Contains('\\') &&
                !path.Contains("//", StringComparison.Ordinal) &&
                !path.Contains("/../", StringComparison.Ordinal) &&
                !path.EndsWith("/..", StringComparison.Ordinal) &&
                !path.Contains("/./", StringComparison.Ordinal) &&
                !path.EndsWith("/.", StringComparison.Ordinal),
            "The promotion plan JIT library path is not canonical.");
        var matchesRuntimeFamily = profile.Family switch
        {
            "coreclr" => path.EndsWith("/libclrjit.so", StringComparison.Ordinal),
            "coreclr-wine" => path.EndsWith("clrjit.dll", StringComparison.OrdinalIgnoreCase),
            "mono" => StringComparer.Ordinal.Equals(path, "/usr/bin/mono-sgen"),
            _ => false
        };
        Require(
            matchesRuntimeFamily,
            "The promotion plan JIT library path does not match the runtime platform.");
    }

    private static string Sha256(byte[] bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static byte[] CanonicalizeBuildInputs(IReadOnlyDictionary<string, string> values)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(values));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = false,
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
               }))
        {
            writer.WriteStartObject();
            foreach (var item in document.RootElement.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                writer.WriteString(item.Name, item.Value.GetString());
            }
            writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static bool IsCanonicalBuildInput(string key, string value) =>
        key.Length is > 0 and <= 128 &&
        key.All(static character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') &&
        !key.Contains("SECRET", StringComparison.Ordinal) &&
        !key.Contains("TOKEN", StringComparison.Ordinal) &&
        !key.Contains("PASSWORD", StringComparison.Ordinal) &&
        !key.Contains("PRIVATE", StringComparison.Ordinal) &&
        !key.Contains("PATH", StringComparison.Ordinal) &&
        value.Length is > 0 and <= 4096 &&
        value.All(static character => character is >= ' ' and <= '~');

    private static bool HasRequiredBuildBindings(RuntimePromotionPlanDocument plan) =>
        plan.BuildInputs.TryGetValue("IMAGE_PREFIX", out var imagePrefix) && imagePrefix.Length > 0 &&
        plan.BuildInputs.TryGetValue("RELEASE_ID", out var releaseId) && releaseId.Length > 0 &&
        plan.BuildInputs.TryGetValue("SOURCE_DATE_EPOCH", out var epoch) &&
        epoch.All(static character => char.IsAsciiDigit(character)) &&
        plan.BuildInputs.TryGetValue("RUNTIME_MATRIX_PROFILE_ID", out var profileId) &&
        StringComparer.Ordinal.Equals(profileId, plan.ProfileId) &&
        plan.BuildInputs.TryGetValue("RUNTIME_MATRIX_RUNTIME_VERSION", out var runtimeVersion) &&
        StringComparer.Ordinal.Equals(runtimeVersion, plan.ResolvedVersion) &&
        !plan.BuildInputs.ContainsKey("SOURCE_REVISION") &&
        (!plan.BuildInputs.TryGetValue("RUNTIME_MATRIX_RUNTIME_COMMIT", out var runtimeCommit) ||
         StringComparer.Ordinal.Equals(runtimeCommit, plan.RuntimeIdentity.RuntimeCommit)) &&
        (!plan.BuildInputs.TryGetValue("RUNTIME_MATRIX_JIT_COMMIT", out var jitCommit) ||
         StringComparer.Ordinal.Equals(jitCommit, plan.RuntimeIdentity.JitCommit));

    private static bool IsExpectedCandidateTarget(string? target, string? family) =>
        (target, family) switch
        {
            ("runtime-dotnet-matrix-candidate", "coreclr") => true,
            ("runtime-mono-matrix-candidate", "mono") => true,
            ("runtime-wine-dotnet-matrix-candidate", "coreclr-wine") => true,
            ("runtime-wine-framework-matrix-candidate", "netfx-clr-wine") => true,
            ("runtime-wine-framework-matrix-shared-candidate", "netfx-clr-wine") => true,
            _ => false
        };

    private static bool IsImmutableReference(string? value)
    {
        if (value is null || value.Length > 512 || value.Any(char.IsWhiteSpace))
            return false;
        var marker = value.LastIndexOf("@sha256:", StringComparison.Ordinal);
        return marker > 0 && marker + 8 + 64 == value.Length &&
               IsLowerHex(value.AsSpan(marker + 8));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        IsLowerHex(value.AsSpan(7));

    private static bool IsGitCommit(string? value) =>
        value is { Length: 40 or 64 } && IsLowerHex(value.AsSpan());

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return false;
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static bool IsCanonicalId(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');


    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new BundleValidationException(message);
    }

    private static void RequireEqual(string? actual, string? expected, string label)
    {
        if (!StringComparer.Ordinal.Equals(actual, expected))
            throw new BundleValidationException($"The {label} does not match its bound input.");
    }
}

public sealed class RuntimePromotionPlanContext
{
    private readonly RuntimePromotionPlanDocument _plan;
    private readonly string _planSha256;
    private readonly string _preflightProfileSha256;

    internal RuntimePromotionPlanContext(
        RuntimeProfileDefinition profile,
        RuntimePromotionPlanDocument plan,
        RuntimePerformancePolicyDocument performancePolicy,
        byte[] preflightProfileBytes,
        byte[] planBytes)
    {
        Profile = profile;
        _plan = plan;
        PerformancePolicy = performancePolicy;
        PreflightProfileBytes = preflightProfileBytes.ToArray();
        _planSha256 = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(planBytes))}";
        _preflightProfileSha256 =
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(preflightProfileBytes))}";
        Capabilities = plan.Capabilities.ToArray();
        EvidencePaths = Capabilities.ToDictionary(
            static capability => capability,
            capability => $"profiles/runtime-promotion-evidence/{plan.ProfileId}/{capability}.json",
            StringComparer.Ordinal);
    }

    internal RuntimeProfileDefinition Profile { get; }
    internal RuntimePerformancePolicyDocument PerformancePolicy { get; }
    public byte[] PreflightProfileBytes { get; }
    public string ProfileId => _plan.ProfileId;
    public string SourceRevision => _plan.SourceRevision;
    public string SecurityPolicyId => _plan.SecurityPolicyId;
    public string ImageReference => _plan.Image.Reference;
    public string ImageId => _plan.Image.ImageId;
    public long ImageSizeBytes => _plan.Image.SizeBytes;
    public string PlanSha256 => _planSha256;
    public string PreflightProfileSha256 => _preflightProfileSha256;
    public string SourceMappingKind => _plan.SourceMappingKind;
    public string? JitLibraryPath => _plan.JitLibraryPath;
    public IReadOnlyList<string> Capabilities { get; }
    public IReadOnlyDictionary<string, string> EvidencePaths { get; }
    public bool RequiresJit => Capabilities.Contains("jit-asm", StringComparer.Ordinal);
    public bool RequiresExecutionFlow => Capabilities.Contains("execution-flow", StringComparer.Ordinal);
    public string PerformancePolicyId => _plan.Performance.PolicyId;
    public string PerformancePolicyPath => _plan.Performance.PolicyPath;
    public string PerformancePolicySha256 => _plan.Performance.PolicySha256;
    public string PerformanceEvidencePath => _plan.Performance.EvidencePath;
    internal RuntimePromotionWineOperatorBinding? WineOperator => _plan.WineOperator;

    public RuntimeCapabilityEvidencePreflightValidationResult ValidateDocument(
        byte[] evidenceBytes,
        RuntimeCapabilityRequestBinding requestBinding)
    {
        ArgumentNullException.ThrowIfNull(evidenceBytes);
        ArgumentNullException.ThrowIfNull(requestBinding);
        if (evidenceBytes.Length is < 1 or > RuntimePromotionPlanWorkflow.MaximumPromotionDocumentBytes)
        {
            throw new BundleValidationException(
                $"Runtime '{ProfileId}' capability evidence exceeds the " +
                $"{RuntimePromotionPlanWorkflow.MaximumPromotionDocumentBytes} byte trust-boundary limit.");
        }
        var evidence = RuntimePromotionJson.Deserialize<RuntimeCapabilityEvidenceDocument>(
            evidenceBytes,
            RuntimePromotionPlanWorkflowInput.JsonOptions,
            $"Runtime '{ProfileId}' capability evidence");
        if (!Capabilities.Contains(evidence.Capability, StringComparer.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime capability evidence returned undeclared capability '{evidence.Capability}'.");
        }
        if (evidence.Producer is null ||
            !StringComparer.Ordinal.Equals(evidence.Producer.PlanSha256, _planSha256))
        {
            throw new BundleValidationException(
                "Runtime capability evidence does not bind the exact promotion plan bytes.");
        }
        if (evidence.Sandbox is null ||
            !StringComparer.Ordinal.Equals(evidence.Sandbox.SecurityPolicyId, SecurityPolicyId))
        {
            throw new BundleValidationException(
                "Runtime capability evidence does not use the promotion plan security policy.");
        }
        if (!StringComparer.Ordinal.Equals(evidence.Image?.Reference, ImageReference) ||
            !StringComparer.Ordinal.Equals(evidence.Image?.ImageId, ImageId))
        {
            throw new BundleValidationException(
                "Runtime capability evidence does not use the promotion plan immutable image.");
        }
        RuntimePromotionPlanWorkflow.ValidateRequestBinding(
            evidence,
            requestBinding,
            $"Runtime '{ProfileId}' {evidence.Capability} evidence");

        var mappingSource = "not-applicable";
        var mappingKind = "not-applicable";
        if (evidence.Capability == "jit-asm")
        {
            mappingKind = _plan.SourceMappingKind;
            mappingSource = evidence.Jit?.Mapping?.Source
                ?? throw new BundleValidationException("JIT capability evidence has no mapping source.");
        }
        var check = new RuntimePromotionCapabilityCheck
        {
            Capability = evidence.Capability,
            Result = "passed",
            NetworkDisabled = true,
            SupervisorSandbox = true,
            OutputLimitValidated = true,
            SourceMappingKind = mappingKind,
            MappingSource = mappingSource,
            EvidencePath = EvidencePaths[evidence.Capability],
            EvidenceSha256 = $"sha256:{new string('0', 64)}"
        };
        var receipt = BuildReceipt(
            [check],
            new RuntimePromotionPerformanceBinding
            {
                Result = "passed",
                PolicyId = PerformancePolicyId,
                PolicyPath = PerformancePolicyPath,
                PolicySha256 = PerformancePolicySha256,
                EvidencePath = PerformanceEvidencePath,
                EvidenceSha256 = $"sha256:{new string('0', 64)}"
            });
        var files = RuntimeCapabilityEvidenceValidation.Validate(
            evidenceBytes,
            Profile,
            receipt,
            check,
            out var probeArtifact);
        if (!StringComparer.Ordinal.Equals(probeArtifact.PlanSha256, _planSha256) ||
            !StringComparer.Ordinal.Equals(
                probeArtifact.PreflightProfileSha256,
                _preflightProfileSha256))
        {
            throw new BundleValidationException(
                "Runtime capability evidence probe artifact does not bind the exact promotion plan and immutable preflight Runtime Profile bytes.");
        }
        if (evidence.Capability == "jit-asm")
        {
            var jitLibrary = files.SingleOrDefault(static file => file.Role == "jit-library");
            if (jitLibrary is null || !StringComparer.Ordinal.Equals(jitLibrary.Path, JitLibraryPath))
            {
                throw new BundleValidationException(
                    "JIT capability evidence does not retain the promotion plan JIT library.");
            }
        }
        return new RuntimeCapabilityEvidencePreflightValidationResult(
            evidence.Capability,
            EvidencePaths[evidence.Capability],
            mappingKind,
            mappingSource,
            probeArtifact,
            files.Select(static file => new RuntimeCapabilityEvidenceImageFile(
                file.Path,
                file.Sha256,
                file.SizeBytes,
                file.Role,
                file.Format,
                file.Architecture)).ToArray());
    }

    internal RuntimePromotionReceiptDocument BuildReceipt(
        IReadOnlyList<RuntimePromotionCapabilityCheck> checks,
        RuntimePromotionPerformanceBinding performance) => new()
    {
        SchemaVersion = 2,
        PlanSha256 = _planSha256,
        ProfileId = _plan.ProfileId,
        MatrixTargetId = _plan.MatrixTargetId,
        Platform = _plan.Platform,
        Family = _plan.Family,
        ResolvedVersion = _plan.ResolvedVersion,
        Image = _plan.Image,
        WineOperator = _plan.WineOperator,
        ComponentIdentity = _plan.ComponentIdentity,
        RuntimeIdentity = _plan.RuntimeIdentity,
        Operations = _plan.Operations,
        Performance = performance,
        SourceRevision = _plan.SourceRevision,
        Checks = checks.Select(static check => (RuntimePromotionCapabilityCheck?)check).ToList()
    };

    internal InspectedImage CreateInspectedImage() => new(
        ProfileId,
        ImageReference,
        ImageId,
        "linux",
        "amd64",
        _plan.Image.SizeBytes,
        [ImageReference],
        new Dictionary<string, string>(StringComparer.Ordinal),
        null,
        null,
        ProfileId,
        null,
        ProfileId,
        null,
        null);
}

public sealed record RuntimePromotionFinalizationResult(
    string ProfileId,
    byte[] ReceiptBytes,
    string ReceiptSha256,
    IReadOnlyDictionary<string, string> CapabilityEvidenceSha256,
    string PerformanceEvidenceSha256);

public sealed record RuntimeCapabilityRequestBinding(
    string ProbeArtifactRef,
    string? ExecutionFlowArtifactRef,
    string? MethodFilter);

internal static class RuntimePromotionPlanWorkflowInput
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}

internal sealed class RuntimePromotionPlanDocument
{
    public required int SchemaVersion { get; init; }
    public required string CandidateTarget { get; init; }
    public required string ProfileId { get; init; }
    public required string ProfileSha256 { get; init; }
    public required string MatrixTargetId { get; init; }
    public required string Platform { get; init; }
    public required string Family { get; init; }
    public required string ResolvedVersion { get; init; }
    public required RuntimePromotionImageIdentity Image { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("wineOperator")]
    public RuntimePromotionWineOperatorBinding? WineOperator { get; init; }

    public required RuntimePromotionComponentIdentity ComponentIdentity { get; init; }
    public required RuntimePromotionRuntimeIdentity RuntimeIdentity { get; init; }
    public required string SourceRevision { get; init; }
    public required string SourceTree { get; init; }
    public required Dictionary<string, string> BuildInputs { get; init; }
    public required string BuildInputsSha256 { get; init; }
    public required RuntimePromotionPlanProducer Producer { get; init; }
    public required string SecurityPolicyId { get; init; }
    public required List<string> Capabilities { get; init; }
    public required string SourceMappingKind { get; init; }
    public required RuntimePromotionOperations Operations { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JitLibraryPath { get; init; }

    public required RuntimePromotionPlanPreflightProfile PreflightProfile { get; init; }
    public required RuntimePromotionPlanPerformance Performance { get; init; }
}

internal sealed class RuntimePromotionPlanPreflightProfile
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
}

internal sealed class RuntimePromotionPlanProducer
{
    public required string Id { get; init; }
    public required string SourceRevision { get; init; }
}

internal sealed class RuntimePromotionPlanPerformance
{
    public required string PolicyId { get; init; }
    public required string PolicyPath { get; init; }
    public required string PolicySha256 { get; init; }
    public required string EvidencePath { get; init; }
}

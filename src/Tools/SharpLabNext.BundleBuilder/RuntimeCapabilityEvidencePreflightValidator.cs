using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.BundleBuilder;

// Kept as an internal test helper for validating historical draft-receipt fixtures.
// Production preflight is plan-driven through RuntimePromotionPlanWorkflow and
// never accepts a caller-supplied draft receipt.
internal static class RuntimeCapabilityEvidencePreflightValidator
{
    private static readonly JsonSerializerOptions InputJsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static RuntimeCapabilityEvidencePreflightContext CreateContext(
        byte[] profileBytes,
        byte[] receiptBytes,
        string securityPolicyId)
    {
        ArgumentNullException.ThrowIfNull(profileBytes);
        ArgumentNullException.ThrowIfNull(receiptBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(securityPolicyId);

        var profile = RuntimePromotionJson.Deserialize<RuntimeProfileDefinition>(
            profileBytes,
            InputJsonOptions,
            "Runtime Profile");
        var profileFailures = RuntimeProfileValidation.ValidatePackage(
            profile,
            requireDigestPinnedImage: false);
        if (profileFailures.Count > 0)
        {
            throw new BundleValidationException(
                $"Runtime Profile '{profile.Id}' is invalid: {string.Join(" ", profileFailures)}");
        }

        var receipt = RuntimePromotionJson.Deserialize<RuntimePromotionReceiptDocument>(
            receiptBytes,
            InputJsonOptions,
            $"Runtime '{profile.Id}' draft promotion receipt");
        ValidateContext(profile, receipt, securityPolicyId);
        return new RuntimeCapabilityEvidencePreflightContext(profile, receipt, securityPolicyId);
    }

    private static void ValidateContext(
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt,
        string securityPolicyId)
    {
        Require(receipt.SchemaVersion == 2, "The draft promotion receipt must use schema version 2.");
        RequireEqual(receipt.ProfileId, profile.Id, "draft promotion receipt profile ID");
        RequireEqual(receipt.Family, profile.Family, "draft promotion receipt family");
        RequireEqual(receipt.ResolvedVersion, profile.RuntimeVersion, "draft promotion receipt runtime version");
        RequireEqual(receipt.RuntimeIdentity.RuntimeCommit, profile.RuntimeCommit, "draft promotion receipt runtime commit");
        RequireEqual(receipt.RuntimeIdentity.JitVersion, profile.JitVersion, "draft promotion receipt JIT version");
        RequireEqual(receipt.RuntimeIdentity.JitCommit, profile.JitCommit, "draft promotion receipt JIT commit");
        Require(IsImmutableReference(receipt.Image.Reference),
            "The draft promotion receipt image reference must be immutable.");
        Require(IsSha256(receipt.Image.ImageId),
            "The draft promotion receipt image ID must be a canonical SHA-256 value.");
        Require(IsGitCommit(receipt.SourceRevision),
            "The draft promotion receipt source revision must be a full lowercase Git commit.");
        Require(profile.AllowedSecurityPolicyIds.Contains(securityPolicyId, StringComparer.Ordinal),
            $"Runtime Profile '{profile.Id}' does not allow security policy '{securityPolicyId}'.");
        Require(profile.SecurityPolicies.Count(policy =>
                StringComparer.Ordinal.Equals(policy.Id, securityPolicyId)) == 1,
            $"Runtime Profile '{profile.Id}' does not define security policy '{securityPolicyId}' exactly once.");

        var declared = profile.Capabilities.Order(StringComparer.Ordinal).ToArray();
        Require(declared.Length is >= 1 and <= 4 &&
                declared.Distinct(StringComparer.Ordinal).Count() == declared.Length &&
                declared.All(static capability => capability is
                    "run" or "jit-asm" or "inspection" or "execution-flow") &&
                declared.Contains("run", StringComparer.Ordinal),
            $"Runtime Profile '{profile.Id}' has an invalid capability declaration.");
        Require(receipt.Checks is { Count: >= 1 and <= 4 } &&
                receipt.Checks.All(static check => check is not null),
            $"Runtime '{profile.Id}' draft receipt must contain one non-null check per capability.");
        var checks = receipt.Checks.Select(static check => check!).ToArray();
        var observed = checks.Select(static check => check.Capability).Order(StringComparer.Ordinal).ToArray();
        Require(declared.SequenceEqual(observed, StringComparer.Ordinal),
            $"Runtime '{profile.Id}' draft receipt checks do not exactly match the Runtime Profile capabilities.");

        foreach (var check in checks)
        {
            Require(check.Result == "passed" && check.NetworkDisabled && check.SupervisorSandbox &&
                    check.OutputLimitValidated && IsSha256(check.EvidenceSha256),
                $"Runtime '{profile.Id}' {check.Capability} draft receipt check is incomplete.");
            var expectedPath =
                $"profiles/runtime-promotion-evidence/{profile.Id}/{check.Capability}.json";
            RequireEqual(check.EvidencePath, expectedPath,
                $"Runtime '{profile.Id}' {check.Capability} evidence path");
            ValidateMapping(profile, receipt, check);
        }
    }

    private static void ValidateMapping(
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt,
        RuntimePromotionCapabilityCheck check)
    {
        if (check.Capability != "jit-asm")
        {
            Require(check.SourceMappingKind == "not-applicable" && check.MappingSource == "not-applicable",
                $"Runtime '{profile.Id}' non-JIT draft receipt check cannot claim source mapping.");
            return;
        }

        var jit = profile.Operations?.Jit
            ?? throw new BundleValidationException(
                $"Runtime '{profile.Id}' declares jit-asm without a JIT operation.");
        RequireEqual(check.SourceMappingKind, jit.SourceMappingKind,
            $"Runtime '{profile.Id}' JIT source mapping kind");
        Require(profile.Family != "netfx-clr-wine",
            $"Runtime '{profile.Id}' family cannot declare jit-asm.");
        if (check.SourceMappingKind == RuntimeJitSourceMappingKinds.LinuxProfiler)
        {
            Require(profile.Family == "coreclr" && check.MappingSource is "ordinary" or "rich" &&
                    receipt.Operations.Jit?.ProfilerPath is not null,
                $"Runtime '{profile.Id}' linux-profiler draft receipt lacks profiler-backed mapping context.");
        }
        else if (check.SourceMappingKind == RuntimeJitSourceMappingKinds.CheckedJitDebugInfo)
        {
            Require(profile.Family == "coreclr" &&
                    check.MappingSource == RuntimeJitSourceMappingKinds.CheckedJitDebugInfo &&
                    receipt.Operations.Jit?.ProfilerPath is null,
                $"Runtime '{profile.Id}' checked-JIT draft receipt lacks debug-info mapping context.");
        }
        else
        {
            Require(check.SourceMappingKind == RuntimeJitSourceMappingKinds.None &&
                    check.MappingSource is "none" or "method",
                $"Runtime '{profile.Id}' mapping-free or method-level JIT draft receipt has invalid mapping context.");
        }
    }

    private static bool IsImmutableReference(string? value)
    {
        if (value is null || value.Length > 512 || value.Any(char.IsWhiteSpace))
            return false;
        var marker = value.LastIndexOf("@sha256:", StringComparison.Ordinal);
        return marker > 0 && marker + 8 + 64 == value.Length && IsLowerHex(value.AsSpan(marker + 8));
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

internal sealed class RuntimeCapabilityEvidencePreflightContext
{
    private readonly RuntimeProfileDefinition _profile;
    private readonly RuntimePromotionReceiptDocument _receipt;
    private readonly string _securityPolicyId;
    private readonly IReadOnlyDictionary<string, RuntimePromotionCapabilityCheck> _checks;

    internal RuntimeCapabilityEvidencePreflightContext(
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt,
        string securityPolicyId)
    {
        _profile = profile;
        _receipt = receipt;
        _securityPolicyId = securityPolicyId;
        _checks = receipt.Checks.Select(static check => check!).ToDictionary(
            static check => check.Capability,
            StringComparer.Ordinal);
        Capabilities = _checks.Keys.Order(StringComparer.Ordinal).ToArray();
        EvidencePaths = _checks.ToDictionary(
            static item => item.Key,
            static item => item.Value.EvidencePath,
            StringComparer.Ordinal);
    }

    public string ProfileId => _profile.Id;
    public string SourceRevision => _receipt.SourceRevision;
    public string ImageReference => _receipt.Image.Reference;
    public string ImageId => _receipt.Image.ImageId;
    public IReadOnlyList<string> Capabilities { get; }
    public IReadOnlyDictionary<string, string> EvidencePaths { get; }
    public bool RequiresJit => _checks.ContainsKey("jit-asm");
    public bool RequiresExecutionFlow => _checks.ContainsKey("execution-flow");

    public RuntimeCapabilityEvidencePreflightValidationResult ValidateDocument(byte[] evidenceBytes)
    {
        ArgumentNullException.ThrowIfNull(evidenceBytes);
        string capability;
        try
        {
            using var document = JsonDocument.Parse(evidenceBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("capability", out var capabilityElement) ||
                capabilityElement.ValueKind != JsonValueKind.String)
            {
                throw new BundleValidationException("Runtime capability evidence has no string capability.");
            }
            capability = capabilityElement.GetString()!;
            if (!document.RootElement.TryGetProperty("sandbox", out var sandbox) ||
                sandbox.ValueKind != JsonValueKind.Object ||
                !sandbox.TryGetProperty("securityPolicyId", out var policyElement) ||
                policyElement.ValueKind != JsonValueKind.String ||
                !StringComparer.Ordinal.Equals(policyElement.GetString(), _securityPolicyId))
            {
                throw new BundleValidationException(
                    "Runtime capability evidence does not use the requested security policy.");
            }
        }
        catch (JsonException exception)
        {
            throw new BundleValidationException($"Runtime capability evidence is invalid JSON: {exception.Message}");
        }

        if (!_checks.TryGetValue(capability, out var check))
        {
            throw new BundleValidationException(
                $"Runtime capability evidence returned undeclared capability '{capability}'.");
        }
        var imageFiles = RuntimeCapabilityEvidenceValidation.Validate(
            evidenceBytes,
            _profile,
            _receipt,
            check,
            out var probeArtifact);
        return new RuntimeCapabilityEvidencePreflightValidationResult(
            capability,
            check.EvidencePath,
            check.SourceMappingKind,
            check.MappingSource,
            probeArtifact,
            imageFiles.Select(static file => new RuntimeCapabilityEvidenceImageFile(
                file.Path,
                file.Sha256,
                file.SizeBytes,
                file.Role,
                file.Format,
                file.Architecture)).ToArray());
    }
}

public sealed record RuntimeCapabilityEvidencePreflightValidationResult(
    string Capability,
    string EvidencePath,
    string SourceMappingKind,
    string MappingSource,
    RuntimeCapabilityProbeArtifactSnapshot ProbeArtifact,
    IReadOnlyList<RuntimeCapabilityEvidenceImageFile> ImageFiles);

public sealed record RuntimeCapabilityEvidenceImageFile(
    string Path,
    string Sha256,
    long SizeBytes,
    string Role,
    string Format,
    string Architecture);

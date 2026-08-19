using System.Security.Cryptography;
using System.Text.Json;
using SharpLabNext.Contracts;

namespace SharpLabNext.Artifacts.Contracts;

public static class RuntimeCapabilityProbeContract
{
    public const string ContractId = "runtime-capability-probe-v1";
    public const string ReleaseId = ContractId;
    public const string LanguageId = "csharp";
    public const string ToolchainId = "runtime-capability-probe";
    public const string CompilerVersion = "1";
    public const string EntryPoint = "SharpLabNext.RuntimeCapabilityProbe.Program.Main";
    public const string MetadataContractKey = "sharplabnext.runtime-capability-probe";
    public const string MetadataContractValue = "v1";
    public const string MetadataSourceRevisionKey = "sharplabnext.source-revision";
    public const string MetadataPromotionPlanSha256Key = "sharplabnext.promotion-plan-sha256";
    public const string MetadataPreflightProfileSha256Key = "sharplabnext.preflight-profile-sha256";

    public const string ExecutionFlowProcessorId = "artifacts-default";
    public const string ExecutionFlowProcessorVersion = "1.0.0";
    public const string ExecutionFlowTransformId = "runtime-instrumentation-v1";
    public const string ExecutionFlowProfileId = "execution-flow-v1";
    public const string InstrumentationTransformKey = "sharplabnext.instrumentation.transform";
    public const string InstrumentationProfileKey = "sharplabnext.instrumentation.profile";
    public const string InstrumentationAppliedKey = "sharplabnext.instrumentation.applied";
    public const string InstrumentationPointsKey = "sharplabnext.instrumentation.points";

    public static string ExecutionFlowOptionsDigest { get; } = ComputeOptionsDigest(
        new TransformArtifactOptions(RewriterProfileId: ExecutionFlowProfileId));

    private static string ComputeOptionsDigest(TransformArtifactOptions options)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            options,
            ContractJson.CreateCanonicalSerializerOptions());
        // Keep the contract binary-compatible with the net8 target. The
        // convenience ToHexStringLower API is only available in newer TFMs.
        return $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }
}

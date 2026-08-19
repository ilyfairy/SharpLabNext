using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLabNext.BundleBuilder;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.UnitTests;

public sealed class RuntimeJitMappingContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData("none")]
    [InlineData("method")]
    public void MappingKindNoneAcceptsOnlyTruthfulMappingFreeOrMethodAssociation(string mappingSource)
    {
        var (profileBytes, receiptBytes) = CreateDotNet6Context(mappingSource);

        var context = RuntimeCapabilityEvidencePreflightValidator.CreateContext(
            profileBytes,
            receiptBytes,
            "runtime-job-default");

        Assert.Equal("dotnet-6-linux-x64", context.ProfileId);
        Assert.True(context.RequiresJit);
    }

    [Fact]
    public void MappingKindNoneRejectsInstructionMappingClaims()
    {
        var (profileBytes, receiptBytes) = CreateDotNet6Context("ordinary");

        var exception = Assert.Throws<BundleValidationException>(() =>
            RuntimeCapabilityEvidencePreflightValidator.CreateContext(
                profileBytes,
                receiptBytes,
                "runtime-job-default"));

        Assert.Contains("mapping-free or method-level", exception.Message, StringComparison.Ordinal);
    }

    private static (byte[] Profile, byte[] Receipt) CreateDotNet6Context(string mappingSource)
    {
        var profileBytes = File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "profiles",
            "runtimes",
            "candidates",
            "dotnet-6-linux-x64.json"));
        var profile = JsonSerializer.Deserialize<RuntimeProfileDefinition>(profileBytes, Json)
            ?? throw new InvalidOperationException("The .NET 6 candidate profile is empty.");
        var receipt = new RuntimePromotionReceiptDocument
        {
            SchemaVersion = 2,
            PlanSha256 = Sha('0'),
            ProfileId = profile.Id,
            MatrixTargetId = "dotnet-6",
            Platform = "linux",
            Family = profile.Family,
            ResolvedVersion = profile.RuntimeVersion,
            Image = new RuntimePromotionImageIdentity
            {
                Reference = $"registry.example/runtime@{Sha('1')}",
                ImageId = Sha('2'),
                SizeBytes = 1
            },
            ComponentIdentity = new RuntimePromotionComponentIdentity
            {
                SourceUri = "https://example.invalid/dotnet-runtime.tar.gz",
                SourceDigest = Sha('3')
            },
            RuntimeIdentity = new RuntimePromotionRuntimeIdentity
            {
                RuntimeCommit = profile.RuntimeCommit,
                JitVersion = profile.JitVersion,
                JitCommit = profile.JitCommit
            },
            Operations = new RuntimePromotionOperations
            {
                Run = Helper(
                    profile.Operations!.Run!.ImplementationId,
                    "/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll",
                    '4'),
                Jit = Helper(
                    profile.Operations.Jit!.ImplementationId,
                    "/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll",
                    '5')
            },
            Performance = new RuntimePromotionPerformanceBinding
            {
                Result = "passed",
                PolicyId = "runtime-image-linux-x64-v1",
                PolicyPath = "profiles/runtime-performance-policies/runtime-image-linux-x64-v1.json",
                PolicySha256 = Sha('6'),
                EvidencePath = $"profiles/runtime-promotion-evidence/{profile.Id}/performance.json",
                EvidenceSha256 = Sha('7')
            },
            SourceRevision = new string('8', 40),
            Checks =
            [
                Check(profile.Id, "run", "not-applicable", "not-applicable", '9'),
                Check(profile.Id, "jit-asm", RuntimeJitSourceMappingKinds.None, mappingSource, 'a')
            ]
        };
        return (profileBytes, JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
    }

    private static RuntimePromotionOperationHelper Helper(
        string implementation,
        string path,
        char digest) =>
        new()
        {
            Implementation = implementation,
            AssemblyPath = path,
            AssemblySha256 = Sha(digest)
        };

    private static RuntimePromotionCapabilityCheck Check(
        string profileId,
        string capability,
        string sourceMappingKind,
        string mappingSource,
        char digest) =>
        new()
        {
            Capability = capability,
            Result = "passed",
            NetworkDisabled = true,
            SupervisorSandbox = true,
            OutputLimitValidated = true,
            SourceMappingKind = sourceMappingKind,
            MappingSource = mappingSource,
            EvidencePath = $"profiles/runtime-promotion-evidence/{profileId}/{capability}.json",
            EvidenceSha256 = Sha(digest)
        };

    private static string Sha(char value) => $"sha256:{new string(value, 64)}";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}

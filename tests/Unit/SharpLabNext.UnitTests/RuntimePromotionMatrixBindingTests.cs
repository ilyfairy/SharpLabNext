using System.Text.Json;
using SharpLabNext.BundleBuilder;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.UnitTests;

public sealed class RuntimePromotionMatrixBindingTests
{
    private const string ProfileId = "dotnet-10-linux-x64";
    private const string ReceiptPath = "profiles/runtime-promotion-receipts/dotnet-10-linux-x64.json";
    private const string ReceiptDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void VerifiedMatrixBindingMatchesActiveProfileAndCapturedReceipt()
    {
        var profile = CreateProfile();
        var snapshot = CreateSnapshot();

        RuntimePromotionMatrixBinding.Validate(
            CreateMatrix(ReceiptPath, ReceiptDigest, "verified"),
            [profile],
            [snapshot]);
    }

    [Fact]
    public void MatrixBindingRejectsStateOrReceiptDrift()
    {
        var profile = CreateProfile();
        var snapshot = CreateSnapshot();

        var stateException = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionMatrixBinding.Validate(
                CreateMatrix(ReceiptPath, ReceiptDigest, "blocked"),
                [profile],
                [snapshot]));
        Assert.Contains("must be 'verified'", stateException.Message, StringComparison.Ordinal);

        var digestException = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionMatrixBinding.Validate(
                CreateMatrix(ReceiptPath, "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "verified"),
                [profile],
                [snapshot]));
        Assert.Contains("does not match its active profile", digestException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatrixBindingRejectsMissingPlatformBinding()
    {
        var exception = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionMatrixBinding.Validate(
                JsonSerializer.SerializeToUtf8Bytes(new { coreClr = Array.Empty<object>() }),
                [CreateProfile()],
                [CreateSnapshot()]));

        Assert.Contains("no matching platform binding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncGateReadsOnlyTheCanonicalMatrixPath()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"sharplabnext-matrix-binding-{Guid.NewGuid():N}");
        try
        {
            var matrixPath = Path.Combine(root, "profiles", "runtime-matrix.json");
            Directory.CreateDirectory(Path.GetDirectoryName(matrixPath)!);
            await File.WriteAllBytesAsync(
                matrixPath,
                CreateMatrix(ReceiptPath, ReceiptDigest, "verified"),
                TestContext.Current.CancellationToken);

            await RuntimePromotionMatrixBinding.ValidateAsync(
                root,
                [CreateProfile()],
                [CreateSnapshot()],
                TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static RuntimeProfileDefinition CreateProfile() => new()
    {
        Id = ProfileId,
        Family = "coreclr",
        PromotionReceipt = new RuntimePromotionReceiptReference
        {
            Path = ReceiptPath,
            Sha256 = ReceiptDigest
        }
    };

    private static RuntimePromotionTrustSnapshot CreateSnapshot() => new(
        ProfileId,
        new string('c', 40),
        "sha256:" + new string('d', 64),
        "sha256:" + new string('0', 64),
        "registry.example/runtime@sha256:" + new string('e', 64),
        "sha256:" + new string('f', 64),
        1,
        new RuntimePromotionFileSnapshot(ReceiptPath, ReceiptDigest),
        [],
        new RuntimePromotionFileSnapshot(
            "profiles/runtime-performance-policies/test.json",
            "sha256:" + new string('1', 64)),
        new RuntimePromotionMeasurementHelperSnapshot(
            "sharplabnext-runtime-cgroup-sidecar-v1",
            "registry.example/runtime-supervisor@sha256:" + new string('2', 64),
            "sha256:" + new string('3', 64),
            1,
            "/usr/local/bin/sharplabnext-runtime-measurement",
            new string('c', 40),
            RuntimeMeasurementHelperContract.ContentSha256),
        []);

    private static byte[] CreateMatrix(string path, string digest, string state) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            coreClr = new[]
            {
                new
                {
                    id = "dotnet-10",
                    linuxCapability = new
                    {
                        promotionState = state,
                        promotionReceipt = new { path, sha256 = digest }
                    }
                }
            }
        });
}

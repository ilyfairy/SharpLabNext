using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Configuration;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.RuntimeSupervisor;

public sealed class RuntimePromotionPreflightOptions
{
    public const string SectionName = "RuntimePromotionPreflight";
    private const long MaximumProfileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions ProfileJson = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public bool Enabled { get; set; }
    public string? PlanSha256 { get; set; }
    public string? SourceRevision { get; set; }
    public string? ProfilePath { get; set; }
    public string? ProfileSha256 { get; set; }

    private RuntimeProfileOptions? Profile { get; set; }

    public static RuntimePromotionPreflightOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new RuntimePromotionPreflightOptions();
        configuration.GetSection(SectionName).Bind(options);
        if (!options.Enabled)
        {
            if (options.PlanSha256 is not null || options.SourceRevision is not null ||
                options.ProfilePath is not null ||
                options.ProfileSha256 is not null)
            {
                throw new InvalidOperationException(
                    "Disabled runtime promotion preflight configuration cannot retain trusted inputs.");
            }
            return options;
        }

        if (!RuntimeProfileValidation.IsSha256(options.PlanSha256) ||
            !RuntimeProfileValidation.IsSha256(options.ProfileSha256))
        {
            throw new InvalidOperationException(
                "Runtime promotion preflight plan/profile digests must be canonical SHA-256 values.");
        }
        if (options.SourceRevision is null || options.SourceRevision.Length is not (40 or 64) ||
            options.SourceRevision.Any(static character =>
                !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "Runtime promotion preflight source revision must be a full lowercase Git commit.");
        }
        if (string.IsNullOrWhiteSpace(options.ProfilePath) ||
            !Path.IsPathFullyQualified(options.ProfilePath))
        {
            throw new InvalidOperationException(
                "Runtime promotion preflight ProfilePath must be an absolute local path.");
        }

        var path = Path.GetFullPath(options.ProfilePath);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 1 or > MaximumProfileBytes)
        {
            throw new InvalidOperationException(
                "Runtime promotion preflight profile must be a bounded regular non-link file.");
        }
        byte[] bytes;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   64 * 1024,
                   FileOptions.SequentialScan))
        {
            bytes = new byte[checked((int)info.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1 || stream.Length != info.Length)
                throw new InvalidOperationException("Runtime promotion preflight profile changed while loading.");
        }
        var observedSha256 = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(options.ProfileSha256!),
                System.Text.Encoding.ASCII.GetBytes(observedSha256)))
        {
            throw new InvalidOperationException(
                "Runtime promotion preflight profile digest does not match the local startup binding.");
        }

        RuntimeProfileOptions profile;
        try
        {
            profile = JsonSerializer.Deserialize<RuntimeProfileOptions>(bytes, ProfileJson)
                ?? throw new JsonException("The profile document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Runtime promotion preflight profile is invalid JSON: {exception.Message}",
                exception);
        }
        var failures = RuntimeProfileValidation.ValidatePackage(
            profile,
            requireDigestPinnedImage: true);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Runtime promotion preflight profile '{profile.Id}' is invalid: " +
                string.Join(" ", failures));
        }
        if (profile.PromotionReceipt is not null ||
            profile.AllowedSecurityPolicyIds.Count != 1 ||
            profile.SecurityPolicies.Count != 1 ||
            !StringComparer.Ordinal.Equals(
                profile.AllowedSecurityPolicyIds[0],
                profile.SecurityPolicies[0].Id))
        {
            throw new InvalidOperationException(
                "Runtime promotion preflight requires exactly one embedded security policy and no receipt.");
        }

        options.ProfilePath = path;
        options.Profile = profile;
        return options;
    }

    public void ApplyTo(RuntimeSupervisorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enabled)
            return;
        var profile = Profile
            ?? throw new InvalidOperationException("Runtime promotion preflight profile was not loaded.");
        var policy = profile.SecurityPolicies.Single();
        options.Profiles = [profile];
        options.SecurityPolicies =
        [
            new RuntimeSecurityPolicyOptions
            {
                Id = policy.Id,
                MemoryBytes = policy.MemoryBytes,
                NanoCpus = policy.NanoCpus,
                PidsLimit = policy.PidsLimit,
                MaximumDurationSeconds = policy.MaximumDurationSeconds,
                MaximumArtifactBytes = policy.MaximumArtifactBytes,
                MaximumOutputBytes = policy.MaximumOutputBytes,
                TmpfsBytes = policy.TmpfsBytes
            }
        ];
        options.RequireDigestPinnedImages = true;
        options.SessionReuseEnabled = false;
        options.PromotionPreflightPlanSha256 = PlanSha256;
        options.PromotionPreflightProfileSha256 = ProfileSha256;
        options.PromotionPreflightSourceRevision = SourceRevision;
        options.ResourceScope = $"promotion-{PlanSha256![7..23]}";
    }
}

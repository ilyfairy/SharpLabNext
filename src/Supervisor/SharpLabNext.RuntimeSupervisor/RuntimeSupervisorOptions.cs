using Microsoft.Extensions.Options;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.RuntimeSupervisor;

public sealed class RuntimeSupervisorOptions
{
    public const string SectionName = "RuntimeSupervisor";

    public string DockerSocketPath { get; set; } = "/var/run/docker.sock";

    public string DockerApiVersion { get; set; } = "v1.47";

    public string ArtifactStoreBaseAddress { get; set; } = "http://artifact-store:8080";

    public string ContainerLabel { get; set; } = "com.sharplabnext.runtime-job";

    public string ResourceScope { get; set; } = "default";

    public int ArtifactLeaseSeconds { get; set; } = 300;

    public int ReaperIntervalSeconds { get; set; } = 30;

    public int StaleContainerSeconds { get; set; } = 900;

    public bool SessionReuseEnabled { get; set; } = true;

    public int SessionMaximumAgeSeconds { get; set; } = 600;

    public bool RequireDigestPinnedImages { get; set; } = true;

    public string? PromotionPreflightPlanSha256 { get; set; }

    public string? PromotionPreflightProfileSha256 { get; set; }

    public string? PromotionPreflightSourceRevision { get; set; }

    public RuntimeSandboxOptions Sandbox { get; set; } = new();

    public List<RuntimeProfileOptions> Profiles { get; set; } = [];

    public List<RuntimeSecurityPolicyOptions> SecurityPolicies { get; set; } = [];

    public RuntimeProfileOptions GetProfile(string id) =>
        Profiles.SingleOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Runtime profile '{id}' is not installed.");

    public RuntimeSecurityPolicyOptions GetSecurityPolicy(string id) =>
        SecurityPolicies.SingleOrDefault(policy => string.Equals(policy.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Runtime security policy '{id}' is not installed.");
}

public sealed class RuntimeProfileOptions : RuntimeProfileDefinition
{
}

public sealed class RuntimeSecurityPolicyOptions : RuntimeSecurityPolicyDefinition
{
}

public sealed class RuntimeSupervisorProfileOverlayOptions
{
    public const string SectionName = "RuntimeSupervisorProfileOverlay";

    public bool Enabled { get; set; }

    public List<RuntimeProfileOptions> Profiles { get; set; } = [];

    public List<RuntimeSecurityPolicyOptions> SecurityPolicies { get; set; } = [];

    public void ApplyTo(RuntimeSupervisorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enabled)
            return;
        options.Profiles = Profiles;
        options.SecurityPolicies = SecurityPolicies;
    }
}

public sealed class RuntimeSupervisorOptionsValidator : IValidateOptions<RuntimeSupervisorOptions>
{
    public ValidateOptionsResult Validate(string? name, RuntimeSupervisorOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.DockerSocketPath) ||
            (options.DockerSocketPath[0] != '/' && !Path.IsPathFullyQualified(options.DockerSocketPath)))
        {
            failures.Add("RuntimeSupervisor:DockerSocketPath must be an absolute path.");
        }

        if (!options.DockerApiVersion.StartsWith('v') || options.DockerApiVersion.Contains('/'))
        {
            failures.Add("RuntimeSupervisor:DockerApiVersion must use a value such as 'v1.47'.");
        }

        if (!Uri.TryCreate(options.ArtifactStoreBaseAddress, UriKind.Absolute, out var artifactStoreUri) ||
            artifactStoreUri.Scheme is not ("http" or "https"))
        {
            failures.Add("RuntimeSupervisor:ArtifactStoreBaseAddress must be an absolute HTTP URI.");
        }

        if (string.IsNullOrWhiteSpace(options.ResourceScope) ||
            options.ResourceScope.Length > 128 ||
            options.ResourceScope.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
        {
            failures.Add("RuntimeSupervisor:ResourceScope must be a stable label value of at most 128 characters.");
        }

        ValidatePositive(options.ArtifactLeaseSeconds, nameof(options.ArtifactLeaseSeconds), failures);
        ValidatePositive(options.ReaperIntervalSeconds, nameof(options.ReaperIntervalSeconds), failures);
        ValidatePositive(options.StaleContainerSeconds, nameof(options.StaleContainerSeconds), failures);
        ValidatePositive(options.SessionMaximumAgeSeconds, nameof(options.SessionMaximumAgeSeconds), failures);
        ValidateDistinctIds(options.Profiles.Select(static profile => profile.Id), "runtime profile", failures);
        ValidateDistinctIds(options.SecurityPolicies.Select(static policy => policy.Id), "security policy", failures);
        var promotionBindings = new object?[]
        {
            options.PromotionPreflightPlanSha256,
            options.PromotionPreflightProfileSha256,
            options.PromotionPreflightSourceRevision
        };
        if (promotionBindings.Any(static value => value is null) &&
            promotionBindings.Any(static value => value is not null))
        {
            failures.Add(
                "Runtime promotion preflight plan/profile digests and source revision must be configured together.");
        }
        if (options.PromotionPreflightPlanSha256 is not null &&
            (!RuntimeProfileValidation.IsSha256(options.PromotionPreflightPlanSha256) ||
             !RuntimeProfileValidation.IsSha256(options.PromotionPreflightProfileSha256) ||
             !IsGitCommit(options.PromotionPreflightSourceRevision) ||
             options.Profiles.Count != 1 || !options.RequireDigestPinnedImages ||
             options.SessionReuseEnabled))
        {
            failures.Add(
                "Runtime promotion preflight requires canonical digests, one immutable profile, and disabled session reuse.");
        }
        failures.AddRange(RuntimeSandboxPolicy.ValidateConfiguration(options.Sandbox));

        if (options.Profiles.Count == 0)
        {
            failures.Add("At least one runtime profile must be configured.");
        }

        if (options.SecurityPolicies.Count == 0)
        {
            failures.Add("At least one runtime security policy must be configured.");
        }

        foreach (var profile in options.Profiles)
        {
            failures.AddRange(RuntimeProfileValidation.Validate(profile, options.RequireDigestPinnedImages));
            foreach (var policyId in profile.AllowedSecurityPolicyIds)
            {
                if (!options.SecurityPolicies.Any(policy => string.Equals(policy.Id, policyId, StringComparison.Ordinal)))
                    failures.Add($"Runtime profile '{profile.Id}' allows missing security policy '{policyId}'.");
            }
        }

        foreach (var policy in options.SecurityPolicies)
        {
            failures.AddRange(RuntimeProfileValidation.Validate(policy));
        }

        var maximumJobDuration = options.SecurityPolicies.Count == 0
            ? 0
            : options.SecurityPolicies.Max(static policy => policy.MaximumDurationSeconds);
        if (options.SessionReuseEnabled &&
            options.SessionMaximumAgeSeconds + maximumJobDuration + options.ReaperIntervalSeconds >=
            options.StaleContainerSeconds)
        {
            failures.Add(
                "RuntimeSupervisor:SessionMaximumAgeSeconds must leave enough time for one job and one reaper interval before StaleContainerSeconds.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsGitCommit(string? value) => value is { Length: 40 or 64 } &&
        value.All(static character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static void ValidateDistinctIds(IEnumerable<string> ids, string description, List<string> failures)
    {
        var duplicates = ids
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(static id => id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key);
        failures.AddRange(duplicates.Select(id => $"Duplicate {description} ID '{id}'."));
    }

    private static void ValidateRequired(string? value, string description, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
        {
            failures.Add($"The {description} must be non-empty and cannot contain NUL characters.");
        }
    }

    private static void ValidatePositive(long value, string description, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{description} must be positive.");
        }
    }

}

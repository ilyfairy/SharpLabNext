using Microsoft.Extensions.Options;

namespace SharpLabNext.Observability;

public sealed class SharpLabNextObservabilityOptions
{
    public const string SectionName = "Observability";

    public string? OtlpEndpoint { get; set; }

    public string? DeploymentEnvironment { get; set; }
}

public sealed class SharpLabNextObservabilityOptionsValidator : IValidateOptions<SharpLabNextObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, SharpLabNextObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.OtlpEndpoint is not null &&
            !TryParseOtlpEndpoint(options.OtlpEndpoint, out _))
        {
            failures.Add(
                "Observability:OtlpEndpoint must be an absolute HTTP(S) URI without credentials, query, or fragment.");
        }

        if (options.DeploymentEnvironment is not null &&
            !SharpLabNextObservabilityExtensions.IsStableIdentity(options.DeploymentEnvironment))
        {
            failures.Add(
                "Observability:DeploymentEnvironment must be a stable label value of at most 128 characters.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static bool TryParseOtlpEndpoint(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }
}

using System.Collections.ObjectModel;
using SharpLabNext.Catalog;

namespace SharpLabNext.Gateway;

public sealed class LanguageSessionGatewayOptions
{
    public const string SectionName = "LanguageSessions";

    public int MaxSessions { get; init; } = 128;

    public int MaxMessageBytes { get; init; } = 1024 * 1024;

    public int MaxWorkspaceFiles { get; init; } = 32;

    public int MaxFileSourceUtf8Bytes { get; init; } = 512 * 1024;

    public int MaxTotalSourceUtf8Bytes { get; init; } = 1024 * 1024;

    public TimeSpan MaximumSessionLifetime { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ReapInterval { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (MaxSessions <= 0)
            throw new InvalidOperationException($"{SectionName}:MaxSessions must be positive.");
        if (MaxMessageBytes is < 1024 or > 16 * 1024 * 1024)
            throw new InvalidOperationException($"{SectionName}:MaxMessageBytes must be between 1 KiB and 16 MiB.");
        if (MaxWorkspaceFiles is < 1 or > 1024)
            throw new InvalidOperationException($"{SectionName}:MaxWorkspaceFiles must be between 1 and 1024.");
        ValidateByteLimit(MaxFileSourceUtf8Bytes, nameof(MaxFileSourceUtf8Bytes));
        ValidateByteLimit(MaxTotalSourceUtf8Bytes, nameof(MaxTotalSourceUtf8Bytes));
        if (MaxFileSourceUtf8Bytes > MaxTotalSourceUtf8Bytes)
            throw new InvalidOperationException($"{SectionName}:MaxFileSourceUtf8Bytes cannot exceed MaxTotalSourceUtf8Bytes.");
        ValidateDuration(MaximumSessionLifetime, nameof(MaximumSessionLifetime));
        ValidateDuration(ConnectTimeout, nameof(ConnectTimeout));
        ValidateDuration(CloseTimeout, nameof(CloseTimeout));
        ValidateDuration(KeepAliveInterval, nameof(KeepAliveInterval));
        ValidateDuration(ReapInterval, nameof(ReapInterval));
    }

    private static void ValidateDuration(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
            throw new InvalidOperationException($"{SectionName}:{name} must be positive.");
    }

    private static void ValidateByteLimit(int value, string name)
    {
        if (value is < 1024 or > 64 * 1024 * 1024)
            throw new InvalidOperationException($"{SectionName}:{name} must be between 1 KiB and 64 MiB.");
    }
}

public sealed record LanguageWorkerEndpoint(string WorkerId, Uri BaseAddress, string ExpectedReleaseId, string? ExpectedWorkerImageId, string? ServiceToken, IReadOnlyDictionary<string, string>? ExpectedReferenceSetDigests = null);

public sealed class LanguageWorkerEndpointRegistry
{
    private readonly ReadOnlyDictionary<string, LanguageWorkerEndpoint> _endpoints;

    public LanguageWorkerEndpointRegistry(IEnumerable<LanguageWorkerEndpoint> endpoints)
    {
        var map = endpoints.ToDictionary(static endpoint => endpoint.WorkerId, StringComparer.Ordinal);
        _endpoints = new ReadOnlyDictionary<string, LanguageWorkerEndpoint>(map);
    }

    public bool TryGet(string workerId, out LanguageWorkerEndpoint? endpoint) =>
        _endpoints.TryGetValue(workerId, out endpoint);

    public IReadOnlyCollection<LanguageWorkerEndpoint> Endpoints => _endpoints.Values;

    public static LanguageWorkerEndpointRegistry FromConfiguration(IConfiguration configuration, string releaseId, IEnumerable<string> catalogWorkerIds, string? defaultServiceToken = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentNullException.ThrowIfNull(catalogWorkerIds);

        var allowedWorkerIds = catalogWorkerIds.ToHashSet(StringComparer.Ordinal);
        var endpoints = new List<LanguageWorkerEndpoint>();
        foreach (var child in configuration.GetSection("Services:LanguageWorkers").GetChildren())
        {
            var workerId = child.Key;
            if (!allowedWorkerIds.Contains(workerId))
            {
                throw new InvalidOperationException($"Services:LanguageWorkers:{workerId} does not match a workerId in the active catalog.");
            }
            endpoints.Add(new LanguageWorkerEndpoint(workerId, ParseBaseAddress(child["BaseAddress"], $"Services:LanguageWorkers:{workerId}:BaseAddress"), releaseId, NullIfWhiteSpace(child["ExpectedWorkerImageId"]), ReadServiceToken(child, workerId) ?? defaultServiceToken));
        }
        return new LanguageWorkerEndpointRegistry(endpoints);
    }

    public static LanguageWorkerEndpointRegistry FromConfiguration(IConfiguration configuration, string releaseId, IEnumerable<ToolchainManifest> catalogToolchains, IReadOnlyDictionary<string, string> expectedReferenceSetDigests, string? defaultServiceToken = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentNullException.ThrowIfNull(catalogToolchains);
        ArgumentNullException.ThrowIfNull(expectedReferenceSetDigests);

        var toolchains = catalogToolchains.ToArray();
        var allowedWorkerIds = toolchains.Select(static item => item.WorkerId).ToHashSet(StringComparer.Ordinal);
        var endpoints = new Dictionary<string, LanguageWorkerEndpoint>(StringComparer.Ordinal);
        foreach (var child in configuration.GetSection("Services:LanguageWorkers").GetChildren())
        {
            var workerId = child.Key;
            if (!allowedWorkerIds.Contains(workerId))
            {
                throw new InvalidOperationException($"Services:LanguageWorkers:{workerId} does not match a workerId in the active catalog.");
            }

            var baseAddress = ParseBaseAddress(child["BaseAddress"], $"Services:LanguageWorkers:{workerId}:BaseAddress");
            var expectedReferences = toolchains.Where(toolchain => string.Equals(toolchain.WorkerId, workerId, StringComparison.Ordinal)).SelectMany(static toolchain => toolchain.AllowedReferenceSetIds).Distinct(StringComparer.Ordinal).ToDictionary(static id => id, id => expectedReferenceSetDigests.TryGetValue(id, out var digest) ? digest : throw new InvalidOperationException($"Catalog reference set '{id}' has no release-lock identity."), StringComparer.Ordinal);
            endpoints[workerId] = new LanguageWorkerEndpoint(workerId, baseAddress, releaseId, NullIfWhiteSpace(child["ExpectedWorkerImageId"]), ReadServiceToken(child, workerId) ?? defaultServiceToken, expectedReferences);
        }
        return new LanguageWorkerEndpointRegistry(endpoints.Values);
    }

    private static Uri ParseBaseAddress(string? value, string configurationKey)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"{configurationKey} must be an absolute HTTP(S) service URI without credentials, query, or fragment.");
        }

        return uri;
    }

    private static string? ReadServiceToken(IConfigurationSection section, string workerId)
    {
        var tokenFile = NullIfWhiteSpace(section["ServiceTokenFile"]);
        var token = NullIfWhiteSpace(section["ServiceToken"]);
        if (tokenFile is not null)
        {
            var fullPath = Path.GetFullPath(tokenFile);
            if (!File.Exists(fullPath))
                throw new InvalidOperationException($"The service token file for language worker '{workerId}' does not exist.");
            token = NullIfWhiteSpace(File.ReadAllText(fullPath));
        }

        if (token is { Length: > 8192 })
            throw new InvalidOperationException($"The service token for language worker '{workerId}' is too large.");
        return token;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

using System.Collections.ObjectModel;

namespace SharpLabNext.Gateway;

public sealed record ArtifactWorkerEndpoint(string WorkerId, Uri BaseAddress, string ExpectedReleaseId, string? ExpectedWorkerImageId, string? ServiceToken);

public sealed class ArtifactWorkerEndpointRegistry
{
    private readonly ReadOnlyDictionary<string, ArtifactWorkerEndpoint> _endpoints;

    public ArtifactWorkerEndpointRegistry(IEnumerable<ArtifactWorkerEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var map = endpoints.ToDictionary(static endpoint => endpoint.WorkerId, StringComparer.Ordinal);
        _endpoints = new ReadOnlyDictionary<string, ArtifactWorkerEndpoint>(map);
    }

    public bool TryGet(string workerId, out ArtifactWorkerEndpoint? endpoint) =>
        _endpoints.TryGetValue(workerId, out endpoint);

    public IReadOnlyCollection<ArtifactWorkerEndpoint> Endpoints => _endpoints.Values;

    public static ArtifactWorkerEndpointRegistry FromConfiguration(IConfiguration configuration, string releaseId, IEnumerable<string> catalogWorkerIds, string? defaultServiceToken = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentNullException.ThrowIfNull(catalogWorkerIds);
        var allowedWorkerIds = catalogWorkerIds.ToHashSet(StringComparer.Ordinal);
        var endpoints = new List<ArtifactWorkerEndpoint>();
        foreach (var child in configuration.GetSection("Services:ArtifactWorkers").GetChildren())
        {
            var workerId = child.Key;
            if (!allowedWorkerIds.Contains(workerId))
            {
                throw new InvalidOperationException($"Services:ArtifactWorkers:{workerId} does not match a workerId in the active catalog.");
            }
            endpoints.Add(new ArtifactWorkerEndpoint(workerId, ParseBaseAddress(child["BaseAddress"], $"Services:ArtifactWorkers:{workerId}:BaseAddress"), releaseId, NullIfWhiteSpace(child["ExpectedWorkerImageId"]), ReadServiceToken(child, workerId) ?? defaultServiceToken));
        }
        return new ArtifactWorkerEndpointRegistry(endpoints);
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
                throw new InvalidOperationException($"The service token file for artifact worker '{workerId}' does not exist.");
            token = NullIfWhiteSpace(File.ReadAllText(fullPath));
        }
        if (token is { Length: > 8192 })
            throw new InvalidOperationException($"The service token for artifact worker '{workerId}' is too large.");
        return token;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

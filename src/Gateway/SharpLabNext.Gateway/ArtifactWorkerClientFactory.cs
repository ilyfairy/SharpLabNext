using System.Net.Http.Headers;

namespace SharpLabNext.Gateway;

public interface IArtifactWorkerClientFactory
{
    IArtifactWorkerClient Create(string workerId);
}

public sealed class ArtifactWorkerClientFactory(IHttpClientFactory httpClientFactory, ArtifactWorkerEndpointRegistry endpoints, ArtifactPipelineOptions options) : IArtifactWorkerClientFactory
{
    public IArtifactWorkerClient Create(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (!endpoints.TryGet(workerId, out var endpoint) || endpoint is null)
            throw new ArtifactWorkerEndpointUnavailableException(workerId);
        var client = httpClientFactory.CreateClient(nameof(ArtifactWorkerClientFactory));
        client.BaseAddress = endpoint.BaseAddress;
        client.Timeout = Timeout.InfiniteTimeSpan;
        if (endpoint.ServiceToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ServiceToken);
        return new ArtifactWorkerClient(client, options, new ArtifactWorkerClientSettings(endpoint.WorkerId, endpoint.ExpectedReleaseId, endpoint.ExpectedWorkerImageId));
    }
}

public sealed class ArtifactWorkerEndpointUnavailableException(string workerId) : Exception($"Artifact worker '{workerId}' is not installed.")
{
    public string WorkerId { get; } = workerId;
}

using System.Net.Http.Headers;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.Gateway;

public interface IToolchainWorkerClientFactory
{
    IToolchainWorkerClient Create(string workerId);
}

public sealed class ToolchainWorkerClientFactory(
    IHttpClientFactory httpClientFactory,
    LanguageWorkerEndpointRegistry endpoints) : IToolchainWorkerClientFactory
{
    public IToolchainWorkerClient Create(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (!endpoints.TryGet(workerId, out var endpoint) || endpoint is null)
            throw new ToolchainWorkerEndpointUnavailableException(workerId);

        var httpClient = httpClientFactory.CreateClient(nameof(ToolchainWorkerClientFactory));
        httpClient.BaseAddress = endpoint.BaseAddress;
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        if (endpoint.ServiceToken is not null)
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ServiceToken);
        return new ToolchainWorkerClient(
            httpClient,
            new ToolchainWorkerClientSettings(
                endpoint.WorkerId,
                endpoint.ExpectedReleaseId,
                endpoint.ExpectedWorkerImageId,
                endpoint.ExpectedReferenceSetDigests));
    }
}

public sealed class ToolchainWorkerEndpointUnavailableException(string workerId)
    : Exception($"Toolchain worker '{workerId}' is not installed.")
{
    public string WorkerId { get; } = workerId;
}

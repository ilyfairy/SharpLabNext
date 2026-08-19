using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactStore.Client;

public interface IArtifactStoreClient
{
    Task<PutContentResponse> PutContentAsync(
        ContentRef contentRef,
        Stream content,
        long? declaredSize = null,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default);

    Task<ArtifactContentResponse> OpenContentReadAsync(
        ContentRef contentRef,
        CancellationToken cancellationToken = default);

    Task<PutArtifactResponse> PutArtifactAsync(
        ArtifactManifest manifest,
        IReadOnlyList<ArtifactFileUpload> files,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default);

    Task<ArtifactBundleDescriptor?> GetArtifactAsync(
        ArtifactRef artifactRef,
        CancellationToken cancellationToken = default);

    Task<ArtifactContentResponse> OpenArtifactFileReadAsync(
        ArtifactRef artifactRef,
        string path,
        CancellationToken cancellationToken = default);

    Task<ArtifactLeaseResponse> AcquireLeaseAsync(
        ArtifactRef artifactRef,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task<ArtifactLeaseResponse> RenewLeaseAsync(
        string leaseToken,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task ReleaseLeaseAsync(string leaseToken, CancellationToken cancellationToken = default);

    Task<GarbageCollectionResponse> CollectGarbageAsync(
        int maxArtifacts = 1000,
        int maxContents = 5000,
        CancellationToken cancellationToken = default);
}

public sealed record ArtifactFileUpload(string Path, Stream Content, long? DeclaredSize = null);

public sealed class ArtifactContentResponse : IAsyncDisposable, IDisposable
{
    private readonly HttpResponseMessage _response;

    internal ArtifactContentResponse(HttpResponseMessage response, Stream content)
    {
        _response = response;
        Content = content;
        Length = response.Content.Headers.ContentLength;
        ETag = response.Headers.ETag?.Tag;
    }

    public Stream Content { get; }

    public long? Length { get; }

    public string? ETag { get; }

    public void Dispose()
    {
        Content.Dispose();
        _response.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
    }
}

public sealed class ArtifactStoreClient : IArtifactStoreClient
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();
    private readonly HttpClient _httpClient;

    public ArtifactStoreClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<PutContentResponse> PutContentAsync(
        ContentRef contentRef,
        Stream content,
        long? declaredSize = null,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var digest = ArtifactStoreProtocol.GetDigest(contentRef);
        var uri = BuildUri($"{ArtifactStoreProtocol.ApiPrefix}/contents/sha256/{digest}", timeToLive);
        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        var streamContent = new StreamContent(content);
        if (declaredSize is not null)
        {
            if (declaredSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(declaredSize));
            }

            streamContent.Headers.ContentLength = declaredSize;
        }

        request.Content = streamContent;
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredJsonAsync<PutContentResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<ArtifactContentResponse> OpenContentReadAsync(
        ContentRef contentRef,
        CancellationToken cancellationToken = default)
    {
        var digest = ArtifactStoreProtocol.GetDigest(contentRef);
        return OpenReadAsync($"{ArtifactStoreProtocol.ApiPrefix}/contents/sha256/{digest}", cancellationToken);
    }

    public async Task<PutArtifactResponse> PutArtifactAsync(
        ArtifactManifest manifest,
        IReadOnlyList<ArtifactFileUpload> files,
        TimeSpan? timeToLive = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);
        ArtifactIdentity.Validate(manifest);

        var normalizedUploads = files.Select(file => file with { Path = ArtifactPath.Normalize(file.Path) }).ToArray();
        _ = ArtifactPath.NormalizeDistinct(normalizedUploads.Select(file => file.Path));
        var expectedPaths = manifest.Files.Select(file => ArtifactPath.Normalize(file.Path)).Order(StringComparer.Ordinal).ToArray();
        var actualPaths = normalizedUploads.Select(file => file.Path).Order(StringComparer.Ordinal).ToArray();
        if (!expectedPaths.SequenceEqual(actualPaths, StringComparer.Ordinal))
        {
            throw new ArgumentException("Uploads must contain exactly one stream for every manifest file.", nameof(files));
        }

        using var form = new MultipartFormDataContent();
        form.Add(
            new StringContent(JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8, "application/json"),
            ArtifactStoreProtocol.ManifestPartName);
        foreach (var upload in normalizedUploads)
        {
            ArgumentNullException.ThrowIfNull(upload.Content);
            var streamContent = new StreamContent(upload.Content);
            if (upload.DeclaredSize is not null)
            {
                if (upload.DeclaredSize < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(files), "Declared sizes cannot be negative.");
                }

                streamContent.Headers.ContentLength = upload.DeclaredSize;
            }

            form.Add(streamContent, ArtifactStoreProtocol.FilesPartName, upload.Path);
        }

        var digest = ArtifactStoreProtocol.GetDigest(manifest.ArtifactId);
        var uri = BuildUri($"{ArtifactStoreProtocol.ApiPrefix}/artifacts/sha256/{digest}", timeToLive);
        using var response = await _httpClient.PutAsync(uri, form, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredJsonAsync<PutArtifactResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtifactBundleDescriptor?> GetArtifactAsync(
        ArtifactRef artifactRef,
        CancellationToken cancellationToken = default)
    {
        var digest = ArtifactStoreProtocol.GetDigest(artifactRef);
        using var response = await _httpClient.GetAsync(
            $"{ArtifactStoreProtocol.ApiPrefix}/artifacts/sha256/{digest}",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredJsonAsync<ArtifactBundleDescriptor>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<ArtifactContentResponse> OpenArtifactFileReadAsync(
        ArtifactRef artifactRef,
        string path,
        CancellationToken cancellationToken = default)
    {
        var digest = ArtifactStoreProtocol.GetDigest(artifactRef);
        var normalizedPath = ArtifactPath.Normalize(path);
        var escapedPath = string.Join('/', normalizedPath.Split('/').Select(Uri.EscapeDataString));
        return OpenReadAsync(
            $"{ArtifactStoreProtocol.ApiPrefix}/artifacts/sha256/{digest}/files/{escapedPath}",
            cancellationToken);
    }

    public async Task<ArtifactLeaseResponse> AcquireLeaseAsync(
        ArtifactRef artifactRef,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var digest = ArtifactStoreProtocol.GetDigest(artifactRef);
        var request = new ArtifactLeaseRequest(owner, ToDurationSeconds(duration));
        using var response = await _httpClient.PostAsJsonAsync(
            $"{ArtifactStoreProtocol.ApiPrefix}/artifacts/sha256/{digest}/leases",
            request,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredJsonAsync<ArtifactLeaseResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtifactLeaseResponse> RenewLeaseAsync(
        string leaseToken,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseToken(leaseToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{ArtifactStoreProtocol.ApiPrefix}/leases/{Uri.EscapeDataString(leaseToken)}")
        {
            Content = JsonContent.Create(new ArtifactLeaseRenewalRequest(ToDurationSeconds(duration)), options: JsonOptions)
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredJsonAsync<ArtifactLeaseResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseLeaseAsync(string leaseToken, CancellationToken cancellationToken = default)
    {
        ValidateLeaseToken(leaseToken);
        using var response = await _httpClient.DeleteAsync(
            $"{ArtifactStoreProtocol.ApiPrefix}/leases/{Uri.EscapeDataString(leaseToken)}",
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GarbageCollectionResponse> CollectGarbageAsync(
        int maxArtifacts = 1000,
        int maxContents = 5000,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"{ArtifactStoreProtocol.ApiPrefix}/maintenance/collect",
            new GarbageCollectionRequest(maxArtifacts, maxContents),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredJsonAsync<GarbageCollectionResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArtifactContentResponse> OpenReadAsync(string uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ArtifactContentResponse(response, stream);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static string BuildUri(string path, TimeSpan? timeToLive)
    {
        if (timeToLive is null)
        {
            return path;
        }

        return $"{path}?TtlSeconds={ToDurationSeconds(timeToLive.Value)}";
    }

    private static int ToDurationSeconds(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration.TotalSeconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return checked((int)Math.Ceiling(duration.TotalSeconds));
    }

    private static void ValidateLeaseToken(string leaseToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (!leaseToken.StartsWith("lease_", StringComparison.Ordinal) || leaseToken.Length > 128)
        {
            throw new ArgumentException("The lease token is malformed.", nameof(leaseToken));
        }
    }

    private static async Task<T> ReadRequiredJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
        ?? throw new HttpRequestException("Artifact Store returned an empty JSON response.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new ArtifactStoreHttpException(response.StatusCode, body);
    }
}

public sealed class ArtifactStoreHttpException(System.Net.HttpStatusCode statusCode, string responseBody)
    : HttpRequestException($"Artifact Store returned HTTP {(int)statusCode} ({statusCode}).")
{
    public System.Net.HttpStatusCode StatusCodeValue { get; } = statusCode;

    public string ResponseBody { get; } = responseBody;
}

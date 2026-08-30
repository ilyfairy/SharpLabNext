using System.Net;

namespace SharpLabNext.BundleBuilder;

public interface IExternalSourceMaterialFetcher
{
    Task<ExternalSourceMaterial> FetchAsync(Uri sourceUri, CancellationToken cancellationToken);
}

public sealed class ExternalSourceMaterial : IAsyncDisposable
{
    private readonly IDisposable? _owner;

    public ExternalSourceMaterial(Uri finalUri, long? contentLength, Stream content, IDisposable? owner = null)
    {
        FinalUri = finalUri ?? throw new ArgumentNullException(nameof(finalUri));
        ContentLength = contentLength;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        _owner = owner;
    }

    public Uri FinalUri { get; }

    public long? ContentLength { get; }

    public Stream Content { get; }

    public ValueTask DisposeAsync()
    {
        Content.Dispose();
        _owner?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class HttpClientExternalSourceMaterialFetcher : IExternalSourceMaterialFetcher
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public async Task<ExternalSourceMaterial> FetchAsync(Uri sourceUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        if (!string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new BundleValidationException("External source material must use HTTPS.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new BundleValidationException($"External source material request failed with HTTP {(int)response.StatusCode}.");
        }

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            response.Dispose();
            throw new BundleValidationException("External source material redirected away from HTTPS.");
        }

        try
        {
            var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new ExternalSourceMaterial(finalUri, response.Content.Headers.ContentLength, content, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}

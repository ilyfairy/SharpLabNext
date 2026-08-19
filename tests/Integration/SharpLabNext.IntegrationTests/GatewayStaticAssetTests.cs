using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using SharpLabNext.Gateway;

namespace SharpLabNext.IntegrationTests;

public sealed class GatewayStaticAssetTests : IClassFixture<StaticAssetGatewayFactory>
{
    private readonly HttpClient _client;
    private readonly StaticAssetGatewayFactory _factory;

    public GatewayStaticAssetTests(StaticAssetGatewayFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task IndexAndHashedAssetsPreferBrotliAndPreserveMetadata()
    {
        using var index = await GetAsync("/", "br, zstd");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("br", Assert.Single(index.Content.Headers.ContentEncoding));
        Assert.Equal("text/html", index.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Accept-Encoding", Assert.Single(index.Headers.Vary));
        Assert.Equal("no-cache", index.Headers.CacheControl?.ToString());
        Assert.Equal(
            "brotli index",
            await index.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Encoding.UTF8.GetByteCount("brotli index"), index.Content.Headers.ContentLength);

        using var asset = await GetAsync(
            "/assets/app-12345678.js",
            "br;q=0.8, zstd;q=0.8, identity;q=0.8");
        Assert.Equal("br", Assert.Single(asset.Content.Headers.ContentEncoding));
        Assert.Equal("text/javascript", asset.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task BrotliFallbackHonorsZeroQualityAndMissingZstdVariant()
    {
        using var rejectedZstd = await GetAsync(
            "/assets/app-12345678.js",
            "zstd;q=0, br;q=0.7, identity;q=0.5");
        Assert.Equal("br", Assert.Single(rejectedZstd.Content.Headers.ContentEncoding));
        Assert.Equal(
            "brotli asset",
            await rejectedZstd.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var missingZstd = await GetAsync("/assets/br-only-12345678.js", "zstd, br");
        Assert.Equal("br", Assert.Single(missingZstd.Content.Headers.ContentEncoding));
        Assert.Equal(
            "brotli only",
            await missingZstd.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GzipFallbackIsNegotiatedDirectlyByGateway()
    {
        using var preferred = await GetAsync(
            "/assets/app-12345678.js",
            "zstd;q=0, br;q=0, gzip;q=0.8, identity;q=0.5");
        Assert.Equal(HttpStatusCode.OK, preferred.StatusCode);
        Assert.Equal("gzip", Assert.Single(preferred.Content.Headers.ContentEncoding));
        Assert.Equal("Accept-Encoding", Assert.Single(preferred.Headers.Vary));
        Assert.Equal(
            "gzip asset",
            await preferred.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var onlyAvailable = await GetAsync(
            "/assets/gzip-only-12345678.js",
            "zstd, br, gzip");
        Assert.Equal(HttpStatusCode.OK, onlyAvailable.StatusCode);
        Assert.Equal("gzip", Assert.Single(onlyAvailable.Content.Headers.ContentEncoding));
        Assert.Equal(
            "gzip only",
            await onlyAvailable.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsupportedOrUnavailableEncodingsUseIdentity()
    {
        using var unsupported = await GetAsync("/assets/identity-only-12345678.js", "compress");
        Assert.Empty(unsupported.Content.Headers.ContentEncoding);
        Assert.Equal(
            "identity only",
            await unsupported.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var unavailable = await GetAsync("/assets/identity-only-12345678.js", "zstd, br");
        Assert.Empty(unavailable.Content.Headers.ContentEncoding);
        Assert.Equal("Accept-Encoding", Assert.Single(unavailable.Headers.Vary));
    }

    [Fact]
    public async Task RejectsRequestWhenEveryRepresentationHasZeroQuality()
    {
        using var response = await GetAsync(
            "/assets/app-12345678.js",
            "zstd;q=0, br;q=0, gzip;q=0, identity;q=0");

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Equal("Accept-Encoding", Assert.Single(response.Headers.Vary));
    }

    [Fact]
    public async Task HeadNegotiatesWithoutSendingAResponseBody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "/assets/app-12345678.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "zstd");
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("zstd", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(Encoding.UTF8.GetByteCount("zstd asset"), response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SpaFallbackUsesTheNegotiatedIndexRepresentation()
    {
        using var response = await GetAsync("/workbench/source", "zstd, br");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("br", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "brotli index",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IdentityRepresentationSupportsRangesAndConditionalRequests()
    {
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/app-12345678.js");
        rangeRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
        using var rangeResponse = await _client.SendAsync(
            rangeRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal(
            "iden",
            await rangeResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("bytes 0-3/14", rangeResponse.Content.Headers.ContentRange?.ToString());

        using var initial = await GetAsync("/assets/app-12345678.js", "zstd");
        Assert.NotNull(initial.Headers.ETag);
        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/app-12345678.js");
        conditionalRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "zstd");
        conditionalRequest.Headers.IfNoneMatch.Add(initial.Headers.ETag);
        using var conditionalResponse = await _client.SendAsync(
            conditionalRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, conditionalResponse.StatusCode);
        Assert.Equal("zstd", Assert.Single(conditionalResponse.Content.Headers.ContentEncoding));
    }

    [Fact]
    public async Task ApiResponsesAreNotReplacedByStaticCompressedFiles()
    {
        using var response = await GetAsync("/api/v1/catalog", "zstd");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task EmptyAcceptEncodingUsesIdentityAndEncodedTraversalCannotEscapeRoot()
    {
        var server = new PrecompressedStaticAssetServer([_factory.WebRoot]);
        using var emptyEncodingRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/assets/app-12345678.js");
        emptyEncodingRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "");
        using var identityResponse = await _client.SendAsync(
            emptyEncodingRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, identityResponse.StatusCode);
        Assert.Empty(identityResponse.Content.Headers.ContentEncoding);

        var outsideName = $"outside-{Guid.NewGuid():N}.js";
        var outsidePath = Path.Combine(Path.GetDirectoryName(_factory.WebRoot)!, outsideName);
        await File.WriteAllTextAsync(
            outsidePath,
            "must not be served",
            TestContext.Current.CancellationToken);
        try
        {
            var traversalContext = new DefaultHttpContext();
            traversalContext.Request.Method = HttpMethods.Get;
            traversalContext.Request.Path = $"/%2e%2e/{outsideName}";
            traversalContext.Response.Body = new MemoryStream();

            Assert.False(await server.TryServeRequestAsync(traversalContext));
            Assert.Equal(0, traversalContext.Response.Body.Length);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}

public sealed class StaticAssetGatewayFactory : WebApplicationFactory<Program>
{
    private readonly string _webRoot = Path.Combine(
        Path.GetTempPath(),
        "SharpLabNext-StaticAssets",
        Guid.NewGuid().ToString("N"));

    public StaticAssetGatewayFactory()
    {
        Write("index.html", "identity index");
        Write("index.html.br", "brotli index");
        Write("index.html.zst", "zstd index");
        Write("assets/app-12345678.js", "identity asset");
        Write("assets/app-12345678.js.br", "brotli asset");
        Write("assets/app-12345678.js.gz", "gzip asset");
        Write("assets/app-12345678.js.zst", "zstd asset");
        Write("assets/br-only-12345678.js", "identity fallback");
        Write("assets/br-only-12345678.js.br", "brotli only");
        Write("assets/identity-only-12345678.js", "identity only");
        Write("assets/gzip-only-12345678.js", "identity gzip fallback");
        Write("assets/gzip-only-12345678.js.gz", "gzip only");
        Write("api/v1/catalog", "static api");
        Write("api/v1/catalog.zst", "compressed static api");
    }

    internal string WebRoot => _webRoot;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseWebRoot(_webRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_webRoot))
            Directory.Delete(_webRoot, recursive: true);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_webRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

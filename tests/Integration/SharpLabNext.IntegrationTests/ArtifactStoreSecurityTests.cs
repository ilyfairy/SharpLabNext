using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.IntegrationTests;

[Collection<ArtifactStoreProcessTestGroup>]
public sealed class ArtifactStoreSecurityTests
{
    [Fact]
    public async Task InternalEndpointsRequireSharedBearerTokenWhileHealthRemainsAnonymous()
    {
        const string token = "artifact-store-internal-service-token-for-tests";
        await using var server = await ArtifactStoreProcess.StartAsync(
            TestContext.Current.CancellationToken,
            internalServiceToken: token);
        using var anonymousClient = new HttpClient
        {
            BaseAddress = server.HttpClient.BaseAddress,
            Timeout = server.HttpClient.Timeout
        };

        using var health = await anonymousClient.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var unauthorized = await anonymousClient.GetAsync(
            "/api/v1/artifacts/status",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Contains("Bearer", unauthorized.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        using var wrongRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/artifacts/status");
        wrongRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string('x', token.Length));
        using var wrong = await anonymousClient.SendAsync(wrongRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/artifacts/status");
        authorizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var authorized = await anonymousClient.SendAsync(
            authorizedRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }

    [Fact]
    public async Task DigestMismatchAndTraversalManifestAreRejectedWithoutPublishing()
    {
        await using var server = await ArtifactStoreProcess.StartAsync(TestContext.Current.CancellationToken);
        var bytes = Encoding.UTF8.GetBytes("untrusted bytes");
        var wrongDigest = new string('a', ArtifactStoreProtocol.Sha256HexLength);
        using (var put = new HttpRequestMessage(
            HttpMethod.Put,
            $"/internal/v1/contents/sha256/{wrongDigest}?TtlSeconds=60")
        {
            Content = new ByteArrayContent(bytes)
        })
        using (var response = await server.HttpClient.SendAsync(put, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var safeManifest = ArtifactStoreTestData.CreateManifest(("app.dll", bytes, "primary-assembly"));
        var unsafeManifest = safeManifest with
        {
            ArtifactId = new ArtifactRef($"sha256:{new string('0', ArtifactStoreProtocol.Sha256HexLength)}"),
            EntryAssembly = "../app.dll",
            Files = [safeManifest.Files[0] with { Path = "../app.dll" }]
        };
        using var form = new MultipartFormDataContent();
        form.Add(
            new StringContent(
                JsonSerializer.Serialize(unsafeManifest, ContractJson.CreateSerializerOptions()),
                Encoding.UTF8,
                "application/json"),
            ArtifactStoreProtocol.ManifestPartName);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, ArtifactStoreProtocol.FilesPartName, "../app.dll");
        using var traversalResponse = await server.HttpClient.PutAsync(
            $"/internal/v1/artifacts/sha256/{new string('0', ArtifactStoreProtocol.Sha256HexLength)}",
            form,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, traversalResponse.StatusCode);
        Assert.False(File.Exists(Path.Combine(server.RootPath, "app.dll")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(server.RootPath, "contents"), "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(server.RootPath, "tmp")));
    }

    [Fact]
    public async Task LegacyLowerCamelMultipartNamesAreRejected()
    {
        await using var server = await ArtifactStoreProcess.StartAsync(TestContext.Current.CancellationToken);
        var bytes = Encoding.UTF8.GetBytes("artifact bytes");
        var manifest = ArtifactStoreTestData.CreateManifest(("app.dll", bytes, "primary-assembly"));
        using var form = new MultipartFormDataContent();
        form.Add(
            new StringContent(
                JsonSerializer.Serialize(manifest, ContractJson.CreateSerializerOptions()),
                Encoding.UTF8,
                "application/json"),
            "manifest");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "files", "app.dll");

        using var response = await server.HttpClient.PutAsync(
            $"/internal/v1/artifacts/sha256/{ArtifactStoreProtocol.GetDigest(manifest.ArtifactId)}",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CorruptedOrSymlinkedCasContentIsNeverReturned()
    {
        await using var server = await ArtifactStoreProcess.StartAsync(TestContext.Current.CancellationToken);
        var bytes = Encoding.UTF8.GetBytes("trusted bytes");
        var contentRef = ContentIdentity.Compute(bytes);
        _ = await server.Client.PutContentAsync(
            contentRef,
            new MemoryStream(bytes, writable: false),
            bytes.LongLength,
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);
        var digest = ArtifactStoreProtocol.GetDigest(contentRef);
        var casPath = Path.Combine(server.RootPath, "contents", "sha256", digest[..2], digest);
        await File.WriteAllBytesAsync(casPath, Encoding.UTF8.GetBytes("tampered"), TestContext.Current.CancellationToken);

        using (var corrupted = await server.HttpClient.GetAsync(
            $"/internal/v1/contents/sha256/{digest}",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, corrupted.StatusCode);
            var responseBody = await corrupted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(server.RootPath, responseBody, StringComparison.OrdinalIgnoreCase);
        }

        File.Delete(casPath);
        var target = Path.Combine(server.RootPath, "untrusted-target.bin");
        await File.WriteAllBytesAsync(target, bytes, TestContext.Current.CancellationToken);
        try
        {
            _ = File.CreateSymbolicLink(casPath, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        using var symlinked = await server.HttpClient.GetAsync(
            $"/internal/v1/contents/sha256/{digest}",
            TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.OK, symlinked.StatusCode);
    }
}

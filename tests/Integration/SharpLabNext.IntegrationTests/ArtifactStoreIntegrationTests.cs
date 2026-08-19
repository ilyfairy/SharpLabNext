using System.Net;
using System.Text;
using SharpLabNext.ArtifactStore.Client;

namespace SharpLabNext.IntegrationTests;

[Collection<ArtifactStoreProcessTestGroup>]
public sealed class ArtifactStoreIntegrationTests
{
    [Fact]
    public async Task ArtifactSurvivesStoreRestart()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "SharpLabNext-ArtifactStoreTests",
            Guid.NewGuid().ToString("N"));
        var assembly = Encoding.UTF8.GetBytes("persistent assembly");
        var manifest = ArtifactStoreTestData.CreateManifest(("app.dll", assembly, "primary-assembly"));
        await using (var first = await ArtifactStoreProcess.StartAsync(
            TestContext.Current.CancellationToken,
            rootPath,
            deleteRootOnDispose: false))
        {
            _ = await first.Client.PutArtifactAsync(
                manifest,
                [new ArtifactFileUpload("app.dll", new MemoryStream(assembly, writable: false), assembly.LongLength)],
                TimeSpan.FromMinutes(1),
                TestContext.Current.CancellationToken);
        }

        await using var second = await ArtifactStoreProcess.StartAsync(
            TestContext.Current.CancellationToken,
            rootPath);
        var descriptor = await second.Client.GetArtifactAsync(manifest.ArtifactId, TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        await using var content = await second.Client.OpenArtifactFileReadAsync(
            manifest.ArtifactId,
            "app.dll",
            TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await content.Content.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(assembly, copy.ToArray());
    }

    [Fact]
    public async Task ArtifactRoundTripIsContentAddressedAndIdempotent()
    {
        await using var server = await ArtifactStoreProcess.StartAsync(TestContext.Current.CancellationToken);
        var assembly = Encoding.UTF8.GetBytes("managed assembly bytes");
        var symbols = Encoding.UTF8.GetBytes("portable pdb bytes");
        var manifest = ArtifactStoreTestData.CreateManifest(
            ("app.dll", assembly, "primary-assembly"),
            ("symbols/app.pdb", symbols, "portable-pdb"));

        var first = await server.Client.PutArtifactAsync(
            manifest,
            [
                new ArtifactFileUpload("app.dll", new MemoryStream(assembly, writable: false), assembly.LongLength),
                new ArtifactFileUpload("symbols/app.pdb", new MemoryStream(symbols, writable: false), symbols.LongLength)
            ],
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);
        var second = await server.Client.PutArtifactAsync(
            manifest,
            [
                new ArtifactFileUpload("app.dll", new MemoryStream(assembly, writable: false), assembly.LongLength),
                new ArtifactFileUpload("symbols/app.pdb", new MemoryStream(symbols, writable: false), symbols.LongLength)
            ],
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(manifest.ArtifactId, first.ArtifactRef);
        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        var descriptor = await server.Client.GetArtifactAsync(manifest.ArtifactId, TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        Assert.Equal(2, descriptor.Entries.Count);

        await using (var content = await server.Client.OpenArtifactFileReadAsync(
            manifest.ArtifactId,
            "symbols/app.pdb",
            TestContext.Current.CancellationToken))
        {
            using var copy = new MemoryStream();
            await content.Content.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal(symbols, copy.ToArray());
        }

        var contentRef = ContentIdentity.Compute(assembly);
        await using (var content = await server.Client.OpenContentReadAsync(contentRef, TestContext.Current.CancellationToken))
        {
            using var copy = new MemoryStream();
            await content.Content.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal(assembly, copy.ToArray());
            Assert.Equal($"\"{contentRef.Value}\"", content.ETag);
        }

        var lease = await server.Client.AcquireLeaseAsync(
            manifest.ArtifactId,
            "integration-test",
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);
        var renewed = await server.Client.RenewLeaseAsync(
            lease.LeaseToken,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.True(renewed.ExpiresAt > lease.ExpiresAt);
        await server.Client.ReleaseLeaseAsync(lease.LeaseToken, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(server.RootPath, "metadata", "artifacts.db")));
        Assert.DoesNotContain(server.RootPath, System.Text.Json.JsonSerializer.Serialize(descriptor), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeaseProtectsExpiredArtifactUntilRelease()
    {
        await using var server = await ArtifactStoreProcess.StartAsync(TestContext.Current.CancellationToken);
        var assembly = Encoding.UTF8.GetBytes("short lived assembly");
        var manifest = ArtifactStoreTestData.CreateManifest(("app.dll", assembly, "primary-assembly"));
        _ = await server.Client.PutArtifactAsync(
            manifest,
            [new ArtifactFileUpload("app.dll", new MemoryStream(assembly, writable: false), assembly.LongLength)],
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        var lease = await server.Client.AcquireLeaseAsync(
            manifest.ArtifactId,
            "ttl-test",
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
        var protectedCollection = await server.Client.CollectGarbageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, protectedCollection.ArtifactsDeleted);
        Assert.NotNull(await server.Client.GetArtifactAsync(manifest.ArtifactId, TestContext.Current.CancellationToken));

        await server.Client.ReleaseLeaseAsync(lease.LeaseToken, TestContext.Current.CancellationToken);
        var finalCollection = await server.Client.CollectGarbageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, finalCollection.ArtifactsDeleted);
        Assert.Equal(1, finalCollection.ContentsDeleted);
        Assert.Null(await server.Client.GetArtifactAsync(manifest.ArtifactId, TestContext.Current.CancellationToken));

        var digest = ArtifactStoreProtocol.GetDigest(ContentIdentity.Compute(assembly));
        using var response = await server.HttpClient.GetAsync(
            $"/internal/v1/contents/sha256/{digest}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

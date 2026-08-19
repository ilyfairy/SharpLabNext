using SharpLabNext.ArtifactProcessing;
using SharpLabNext.ArtifactProcessing.Protocol;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker;

namespace SharpLabNext.ArtifactWorker.Tests;

internal static class TestSettings
{
    public static ArtifactWorkerSettings Create(
        string root,
        ArtifactProcessorLimits? limits = null,
        string? processorPath = null) => new(
            new ArtifactWorkerIdentity(
                "test-release",
                "sha256:test-worker",
                "artifacts-default",
                ProcessorProtocol.IlSpyVersion,
                ProcessorProtocol.IlVerificationVersion),
            limits ?? ArtifactProcessorLimits.Default,
            "http://artifact-store.test",
            processorPath ?? typeof(ProcessorEngine).Assembly.Location,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            root,
            new Dictionary<string, ArtifactReferenceSet>(StringComparer.Ordinal)
            {
                ["net10-ref"] = new(
                    "net10-ref",
                    [Path.GetDirectoryName(typeof(object).Assembly.Location)!],
                    "System.Private.CoreLib")
            },
            new HashSet<string>(["default"], StringComparer.Ordinal));

    public static ArtifactStoreClient CreateUnusedStoreClient() => new(new HttpClient(new EmptyHandler())
    {
        BaseAddress = new Uri("http://artifact-store.test")
    });

    public static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpLabNext-ArtifactWorkerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static void DeleteRoot(string root)
    {
        TemporaryArtifactDirectory.Delete(root);
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}

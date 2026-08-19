using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Artifacts.ConstGenerics.Protocol;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics.Tests;

internal static class ConstGenericsTestInfrastructure
{
    public static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    public static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "SharpLabNext.Tests", $"const-ilspy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteRoot(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static ArtifactWorkerCapabilityManifest CapabilityManifest() => new()
    {
        SchemaVersion = 1,
        WorkerId = "artifacts-const-generics",
        ProtocolVersion = "1.0",
        Capabilities = ["il", "decompiled-csharp", "il-verify"],
        AcceptedArtifactFormats = ["dotnet-managed-pe-v1"],
        ProducedArtifactFormats = ["il-text-v1", "decompiled-csharp-v1", "il-verification-v1"],
        TransformIds = [],
        RenderOutputIds = ["il", "decompiled-csharp"],
        VerificationProfileIds = ["il-verify"],
        Limits = new ArtifactWorkerLimits(
            64 * 1024 * 1024,
            8 * 1024 * 1024,
            2,
            15_000,
            256,
            16)
    };

    public static ConstGenericsArtifactWorkerSettings Settings(string root)
    {
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?
            .Name ?? "Debug";
        var buildOutput = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "SharpLabNext.Worker.Artifacts.ConstGenerics.Processor",
            "bin",
            configuration,
            "net8.0",
            "SharpLabNext.Worker.Artifacts.ConstGenerics.Processor.dll"));
        var processorPath = File.Exists(buildOutput)
            ? buildOutput
            : throw new FileNotFoundException("The ConstGenerics processor test build output is unavailable.", buildOutput);
        return new ConstGenericsArtifactWorkerSettings(
            "test-release",
            $"sha256:{new string('a', ArtifactStoreProtocol.Sha256HexLength)}",
            "http://artifact-store.test",
            root,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            processorPath,
            RuntimeEnvironment.GetRuntimeDirectory(),
            RuntimeEnvironment.GetRuntimeDirectory(),
            "9.0.0-constgenerics.1.23470.1",
            "System.Private.CoreLib",
            32,
            32L * 1024 * 1024,
            16L * 1024 * 1024,
            512L * 1024 * 1024,
            64 * 1024,
            TimeSpan.FromHours(1));
    }

    public static ArtifactStoreClient CreateClient(HttpMessageHandler handler) => new(new HttpClient(handler)
    {
        BaseAddress = new Uri("http://artifact-store.test")
    });

    public static RenderArtifactRequest RenderRequest(ArtifactRef artifactRef, string outputId) => new(
        $"request-{outputId}",
        $"key-{outputId}",
        "pipeline-const-generics",
        artifactRef,
        "artifacts-const-generics",
        outputId,
        new RenderArtifactOptions(
            IncludeSequencePoints: false,
            IncludeCompilerGeneratedMembers: true,
            MaxCharacters: 1_000_000),
        DateTimeOffset.UtcNow.AddSeconds(30));

    public static VerifyArtifactRequest VerifyRequest(ArtifactRef artifactRef) => new(
        "request-verify",
        "key-verify",
        "pipeline-const-generics",
        artifactRef,
        "artifacts-const-generics",
        new VerifyArtifactOptions("il-verify", IncludeMetadataTokens: true, MaxFindings: 1_000),
        DateTimeOffset.UtcNow.AddSeconds(30));
}

internal sealed class ConstGenericsArtifactStoreHandler : HttpMessageHandler
{
    private readonly byte[] _servedAssembly;
    private readonly ArtifactBundleDescriptor _bundle;
    private readonly Dictionary<ContentRef, byte[]> _uploadedContent = [];
    private int _leaseAcquisitionCount;
    private int _leaseReleaseCount;
    private int _fileDownloadCount;

    public ConstGenericsArtifactStoreHandler(
        Func<ArtifactManifest, ArtifactManifest>? mutateManifest = null,
        bool corruptContent = false)
    {
        var expectedAssembly = File.ReadAllBytes(
            typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location);
        _servedAssembly = expectedAssembly.ToArray();
        if (corruptContent)
            _servedAssembly[^1] ^= 0xff;
        var contentRef = ContentIdentity.Compute(expectedAssembly);
        var placeholder = new ArtifactRef($"sha256:{new string('0', ArtifactStoreProtocol.Sha256HexLength)}");
        var manifest = new ArtifactManifest(
            ArtifactStoreProtocol.ArtifactManifestVersion,
            placeholder,
            new ArtifactProducer(
                "test-release",
                "csharp",
                "roslyn-const-generics",
                "4.8.0",
                "bcd209abd947ac1bc71ef1ee29bd8a02d8e78ffc",
                $"sha256:{new string('b', ArtifactStoreProtocol.Sha256HexLength)}"),
            "const-generics-ref",
            "net9.0",
            "dotnet-managed-pe-v1",
            new ArtifactRuntimeRequirement(
                "coreclr-const-generics",
                [new FrameworkRequirement("Microsoft.NETCore.App", "9.0.0-constgenerics.1.23470.1")],
                "anycpu",
                [ConstGenericsProcessorProtocol.RuntimeFeatureTag]),
            [ConstGenericsProcessorProtocol.MetadataFeatureTag],
            BuildOutputKind.Library,
            "app.dll",
            null,
            [new ArtifactFileDescriptor("primary-assembly", "app.dll", expectedAssembly.LongLength, contentRef.Value)],
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["compatibilityGroup"] = ConstGenericsProcessorProtocol.CompatibilityGroup
            });
        manifest = mutateManifest?.Invoke(manifest) ?? manifest;
        manifest = ArtifactIdentity.WithComputedId(manifest);
        _bundle = new ArtifactBundleDescriptor(
            manifest,
            [new ArtifactBundleEntry(
                "app.dll",
                expectedAssembly.LongLength,
                contentRef.Value,
                "primary-assembly",
                contentRef)]);
    }

    public ArtifactRef ArtifactRef => _bundle.Manifest.ArtifactId;

    public ArtifactBundleDescriptor Bundle => _bundle;

    public int LeaseAcquisitionCount => Volatile.Read(ref _leaseAcquisitionCount);

    public int LeaseReleaseCount => Volatile.Read(ref _leaseReleaseCount);

    public int FileDownloadCount => Volatile.Read(ref _fileDownloadCount);

    public byte[] GetUploadedContent(ContentRef contentRef) => _uploadedContent[contentRef];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var artifactPath = $"{ArtifactStoreProtocol.ApiPrefix}/artifacts/sha256/{ArtifactStoreProtocol.GetDigest(ArtifactRef)}";
        if (request.Method == HttpMethod.Get && path == artifactPath)
            return Json(_bundle);
        if (request.Method == HttpMethod.Post && path == $"{artifactPath}/leases")
        {
            Interlocked.Increment(ref _leaseAcquisitionCount);
            return Json(new ArtifactLeaseResponse(
                "lease_const",
                ArtifactRef,
                "artifacts-const-generics:test",
                DateTimeOffset.UtcNow.AddMinutes(1)));
        }
        if (request.Method == HttpMethod.Get && path == $"{artifactPath}/files/app.dll")
        {
            Interlocked.Increment(ref _fileDownloadCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_servedAssembly)
            };
        }
        if (request.Method == HttpMethod.Delete &&
            path == $"{ArtifactStoreProtocol.ApiPrefix}/leases/lease_const")
        {
            Interlocked.Increment(ref _leaseReleaseCount);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        var contentPrefix = $"{ArtifactStoreProtocol.ApiPrefix}/contents/sha256/";
        if (request.Method == HttpMethod.Put && path.StartsWith(contentPrefix, StringComparison.Ordinal))
        {
            var bytes = await (request.Content ?? throw new InvalidOperationException("Content is required."))
                .ReadAsByteArrayAsync(cancellationToken);
            var contentRef = ArtifactStoreProtocol.ContentRefFromDigest(path[contentPrefix.Length..]);
            if (ContentIdentity.Compute(bytes) != contentRef)
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            _uploadedContent.Add(contentRef, bytes);
            return Json(new PutContentResponse(
                contentRef,
                bytes.LongLength,
                DateTimeOffset.UtcNow.AddHours(1),
                false));
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value, options: ConstGenericsTestInfrastructure.JsonOptions)
    };
}

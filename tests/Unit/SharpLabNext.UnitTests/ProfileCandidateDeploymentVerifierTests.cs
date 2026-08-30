using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.ProfileUpdater;

namespace SharpLabNext.UnitTests;

public sealed class ProfileCandidateDeploymentVerifierTests
{
    [Fact]
    public async Task VerifiesGatewayWorkerCompilerAndRuntimeIdentityClosure()
    {
        using var fixture = await CandidateFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient(fixture.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var verifier = new ProfileCandidateDeploymentVerifier(http);

        var result = await verifier.VerifyAsync(fixture.Options, TestContext.Current.CancellationToken);

        Assert.Equal("candidate-test", result.ReleaseId);
        Assert.Equal(14, result.WorkersVerified);
        Assert.Equal(5, result.RuntimesVerified);
    }

    [Fact]
    public async Task RejectsLegacyCamelCaseServiceResponses()
    {
        using var fixture = await CandidateFixture.CreateAsync(TestContext.Current.CancellationToken);
        var responses = fixture.CreateResponses();
        responses[fixture.SystemUri] = responses[fixture.SystemUri].Replace("\"Id\"", "\"id\"", StringComparison.Ordinal).Replace("\"ReleaseId\"", "\"releaseId\"", StringComparison.Ordinal);
        using var http = new HttpClient(fixture.CreateHandler(responses)) { Timeout = Timeout.InfiniteTimeSpan };
        var verifier = new ProfileCandidateDeploymentVerifier(http);

        var exception = await Assert.ThrowsAsync<ProfileUpdateValidationException>(() => verifier.VerifyAsync(fixture.Options, TestContext.Current.CancellationToken));

        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("compiler")]
    [InlineData("netfx-compiler")]
    [InlineData("worker-image")]
    [InlineData("artifact-worker-image")]
    [InlineData("reference-set")]
    [InlineData("netfx-reference-set")]
    [InlineData("reference-set-content")]
    [InlineData("reference-source-uri")]
    [InlineData("reference-source-digest")]
    [InlineData("netfx30-reference-source-digest")]
    [InlineData("jsharp-reference-source-digest")]
    [InlineData("runtime")]
    public async Task RejectsDeployedIdentityMismatch(string mismatch)
    {
        using var fixture = await CandidateFixture.CreateAsync(TestContext.Current.CancellationToken);
        var responses = fixture.CreateResponses();
        switch (mismatch)
        {
            case "compiler":
                responses[fixture.WorkerUri("roslyn-stable")] = responses[fixture.WorkerUri("roslyn-stable")].Replace("\"compilerVersion\":\"5.6.0\"", "\"compilerVersion\":\"5.5.0\"", StringComparison.Ordinal);
                break;
            case "netfx-compiler":
                responses[fixture.WorkerUri("roslyn-stable-netfx48")] =
                    responses[fixture.WorkerUri("roslyn-stable-netfx48")].Replace("\"compilerVersion\":\"5.6.0\"", "\"compilerVersion\":\"5.5.0\"", StringComparison.Ordinal);
                break;
            case "worker-image":
                responses[fixture.WorkerUri("fsharp-stable")] = responses[fixture.WorkerUri("fsharp-stable")].Replace(fixture.ImageId("worker-fsharp"), ImmutableImageId("wrong-worker"), StringComparison.Ordinal);
                break;
            case "artifact-worker-image":
                responses[fixture.WorkerUri("artifacts-const-generics")] =
                    responses[fixture.WorkerUri("artifacts-const-generics")].Replace(fixture.ImageId("worker-artifacts-const-generics"), ImmutableImageId("wrong-artifact-worker"), StringComparison.Ordinal);
                break;
            case "reference-set":
                responses[fixture.WorkerUri("roslyn-stable")] = responses[fixture.WorkerUri("roslyn-stable")].Replace(fixture.ReferenceSetDigest("net10-ref"), "sha512-wrong-reference-set", StringComparison.Ordinal);
                break;
            case "netfx-reference-set":
                responses[fixture.WorkerUri("roslyn-stable-netfx48")] =
                    responses[fixture.WorkerUri("roslyn-stable-netfx48")].Replace(fixture.ReferenceSetDigest("netfx48-managed-ref"), "sha512-wrong-netfx-reference-set", StringComparison.Ordinal);
                break;
            case "reference-set-content":
                responses[fixture.WorkerUri("fsharp-stable")] = responses[fixture.WorkerUri("fsharp-stable")].Replace($"sha256:{new string('f', 64)}", $"sha256:{new string('e', 64)}", StringComparison.Ordinal);
                break;
            case "reference-source-uri":
                responses[fixture.WorkerUri("roslyn-const-generics")] = responses[fixture.WorkerUri("roslyn-const-generics")].Replace(
                        $"\"SourceUri\":\"{fixture.ReferenceSetSourceUri("const-generics-ref")}\"",
                        "\"SourceUri\":\"https://example.test/wrong-source.tar.gz\"",
                        StringComparison.Ordinal);
                break;
            case "reference-source-digest":
                responses[fixture.WorkerUri("roslyn-const-generics")] = responses[fixture.WorkerUri("roslyn-const-generics")].Replace($"\"SourceArchiveDigest\":\"{fixture.ReferenceSetDigest("const-generics-ref")}\"", $"\"SourceArchiveDigest\":\"sha256:{new string('0', 64)}\"", StringComparison.Ordinal);
                break;
            case "netfx30-reference-source-digest":
                responses[fixture.WorkerUri("roslyn-stable-netfx48")] =
                    responses[fixture.WorkerUri("roslyn-stable-netfx48")].Replace("sha512:335bc1db148c258d05757352507e248e3d38693a9620e3d429e5147da0a8540e49570df45c63bd203ee652e068fa29d25cb8262efa0c9126f777df18110c1fc8", $"sha512:{new string('0', 128)}", StringComparison.Ordinal);
                break;
            case "jsharp-reference-source-digest":
                responses[fixture.WorkerUri("vjc-jsharp20")] = responses[fixture.WorkerUri("vjc-jsharp20")].Replace($"\"SourceArchiveDigest\":\"{fixture.ReferenceSetDigest("jsharp20-ref")}\"", $"\"SourceArchiveDigest\":\"sha256:{new string('0', 64)}\"", StringComparison.Ordinal);
                break;
            case "runtime":
                responses[fixture.RuntimeStatusUri] = responses[fixture.RuntimeStatusUri].Replace("\"RuntimeVersion\":\"10.0.9\"", "\"RuntimeVersion\":\"10.0.8\"", StringComparison.Ordinal);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mismatch));
        }
        using var http = new HttpClient(fixture.CreateHandler(responses)) { Timeout = Timeout.InfiniteTimeSpan };
        var verifier = new ProfileCandidateDeploymentVerifier(http);

        var exception = await Assert.ThrowsAsync<ProfileUpdateValidationException>(() => verifier.VerifyAsync(fixture.Options, TestContext.Current.CancellationToken));

        Assert.Contains("identity mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string ImmutableImageId(string id) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))}";

    private sealed class CandidateFixture : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly JsonSerializerOptions WireJsonOptions = ContractJson.CreateSerializerOptions();
        private static readonly string[] BundleImageIds =
        [
            "gateway",
            "runtime-supervisor",
            "worker-roslyn-stable",
            "worker-roslyn-netfx48",
            "worker-roslyn-main",
            "worker-roslyn-const-generics",
            "worker-fsharp",
            "worker-gsharp",
            "worker-peachpie",
            "worker-cppcli",
            "worker-jsharp",
            "worker-il",
            "worker-minilang",
            "worker-artifacts-default",
            "worker-artifacts-const-generics",
            "worker-artifacts-il-assembler",
            "dotnet-10-linux-x64",
            "dotnet-11-preview-linux-x64",
            "const-generics-linux-x64",
            "wine-netfx48-linux-x64",
            "wine-jsharp20-linux-x64"
        ];
        private readonly ReleaseLockDocument releaseLock;
        private readonly CatalogDocument catalog;
        private readonly CandidateValidationEndpoints endpoints;
        private readonly Dictionary<string, string> images;

        private CandidateFixture(string root, ReleaseLockDocument releaseLock, CatalogDocument catalog, CandidateValidationEndpoints endpoints, Dictionary<string, string> images, ProfileCandidateVerificationOptions options)
        {
            Root = root;
            this.releaseLock = releaseLock;
            this.catalog = catalog;
            this.endpoints = endpoints;
            this.images = images;
            Options = options;
        }

        public string Root { get; }
        public ProfileCandidateVerificationOptions Options { get; }
        public Uri SystemUri => Endpoint(endpoints.Gateway, "/api/v1/system");
        public Uri RuntimeStatusUri => Endpoint(endpoints.Services["runtime-supervisor"], "/api/v1/runtime/status");

        public static async Task<CandidateFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.CandidateVerifier.{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var repositoryRoot = FindRepositoryRoot();
            var runtimeDirectory = Path.Combine(root, "profiles", "runtimes");
            Directory.CreateDirectory(runtimeDirectory);
            File.Copy(Path.Combine(repositoryRoot, "profiles", "runtimes", "const-generics-linux-x64.json"), Path.Combine(runtimeDirectory, "const-generics-linux-x64.json"));
            var template = await CatalogLoader.LoadCatalogAsync(Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"), cancellationToken);
            var releaseLock = CreateLock();
            template = RestrictSelectableRuntimesToLock(template, releaseLock);
            const string candidateDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var material = await CandidateReleaseMaterializer.WriteAsync(root, template, releaseLock, candidateDigest, cancellationToken);
            var catalog = await CatalogLoader.LoadCatalogAsync(material.CatalogPath, cancellationToken);
            var endpoints = CandidateReleaseMaterializer.CreateValidationEndpoints(candidateDigest);
            var imageIds = BundleImageIds.ToDictionary(static id => id, ImmutableImageId, StringComparer.Ordinal);
            var bundlePath = Path.Combine(root, "bundle.json");
            await File.WriteAllTextAsync(
                bundlePath,
                JsonSerializer.Serialize(new
                {
                    releaseId = releaseLock.ReleaseId,
                    source = new { revision = "candidate-source" },
                    images = imageIds.Select(pair => new
                    {
                        id = pair.Key,
                        imageId = pair.Value,
                        runtimeCommit = releaseLock.Components.TryGetValue(pair.Key, out var component) && component.Kind == "runtime"
                            ? component.Commit : null,
                        jitCommit = releaseLock.Components.TryGetValue(pair.Key, out component) && component.Kind == "runtime"
                            ? component.JitCommit : null
                    })
                }, JsonOptions),
                cancellationToken);
            return new CandidateFixture(
                root,
                releaseLock,
                catalog,
                endpoints,
                imageIds,
                new ProfileCandidateVerificationOptions
                {
                    LockPath = material.LockPath,
                    CatalogPath = material.CatalogPath,
                    EndpointsPath = material.ValidationEndpointsPath,
                    BundlePath = bundlePath,
                    Timeout = TimeSpan.FromSeconds(2)
                });
        }

        public Dictionary<Uri, string> CreateResponses()
        {
            var responses = new Dictionary<Uri, string>
            {
                [Endpoint(endpoints.Gateway, "/api/v1/system")] = Serialize(new ServiceIdentity("gateway", ServiceKind.Gateway, releaseLock.ReleaseId, ProtocolVersion.WorkerV1, [], "ready")),
                [Endpoint(endpoints.Gateway, "/api/v1/catalog")] = Serialize(catalog),
                [WorkerUri("roslyn-stable")] = Serialize(Worker(
                    "roslyn-stable",
                    "worker-roslyn-stable",
                    new Dictionary<string, string>
                    {
                        ["compilerVersion"] = "5.6.0"
                    })),
                [WorkerUri("roslyn-stable-netfx48")] = Serialize(Worker(
                    "roslyn-stable-netfx48",
                    "worker-roslyn-netfx48",
                    new Dictionary<string, string>
                    {
                        ["compilerVersion"] = "5.6.0"
                    })),
                [WorkerUri("roslyn-main")] = Serialize(Worker(
                    "roslyn-main",
                    "worker-roslyn-main",
                    new Dictionary<string, string>
                    {
                        ["compilerVersion"] = "5.10.0",
                        ["compilerCommit"] = new string('a', 40)
                    })),
                [WorkerUri("roslyn-const-generics")] = Serialize(Worker(
                    "roslyn-const-generics",
                    "worker-roslyn-const-generics",
                    new Dictionary<string, string>
                    {
                        ["compilerVersion"] = "4.8.0",
                        ["compilerCommit"] = new string('b', 40)
                    })),
                [WorkerUri("fsharp-stable")] = Serialize(Worker(
                    "fsharp-stable",
                    "worker-fsharp",
                    new Dictionary<string, string>
                    {
                        ["compilerVersion"] = "43.12.204",
                        ["fsharpCoreVersion"] = "10.1.204"
                    })),
                [WorkerUri("gsharp-stable")] = Serialize(Worker("gsharp-stable", "worker-gsharp", new Dictionary<string, string>())),
                [WorkerUri("peachpie-stable")] = Serialize(Worker(
                    "peachpie-stable",
                    "worker-peachpie",
                    new Dictionary<string, string>
                    {
                        ["compilerVersion"] = "1.1.13",
                        ["compilerCommit"] = "608bf30cf3f43f97e32825076a2cfdaa25043e50"
                    })),
                [WorkerUri("msvc-cppcli-netfx48")] = Serialize(Worker("msvc-cppcli-netfx48", "worker-cppcli", new Dictionary<string, string>())),
                [WorkerUri("vjc-jsharp20")] = Serialize(Worker(
                    "vjc-jsharp20",
                    "worker-jsharp",
                    new Dictionary<string, string>
                    {
                        ["compilerVersion"] = "2.0.50727.937"
                    })),
                [WorkerUri("mobius-ilasm-stable")] = Serialize(Worker(
                    "mobius-ilasm-stable",
                    "worker-il",
                    new Dictionary<string, string>
                    {
                        ["compilerVersion"] = "0.1.0"
                    })),
                [WorkerUri("minilang-stable")] = Serialize(Worker("minilang-stable", "worker-minilang", new Dictionary<string, string>())),
                [WorkerUri("artifacts-default")] = Serialize(Worker(
                    "artifacts-default",
                    "worker-artifacts-default",
                    new Dictionary<string, string>
                    {
                        ["ilspyVersion"] = "10.1.0.8386",
                        ["ilVerificationVersion"] = "10.0.9"
                    },
                    ServiceKind.ArtifactWorker,
                    WorkerKind.ArtifactProcessor)),
                [WorkerUri("artifacts-const-generics")] = Serialize(Worker("artifacts-const-generics", "worker-artifacts-const-generics", new Dictionary<string, string>(), ServiceKind.ArtifactWorker, WorkerKind.ArtifactProcessor)),
                [WorkerUri("il-assembler")] = Serialize(Worker("il-assembler", "worker-artifacts-il-assembler", new Dictionary<string, string>(), ServiceKind.ArtifactWorker, WorkerKind.ArtifactProcessor))
            };
            var runtimeProfiles = catalog.Runtimes.Where(static runtime => runtime.Availability.IsSelectable).Select(runtime =>
                {
                    var component = releaseLock.Components[runtime.Id];
                    var isOperatorRuntime = runtime.Family is "netfx-clr-wine" or "mono";
                    var runtimeCommit = isOperatorRuntime ? "not-applicable" : component.Commit!;
                    var jitVersion = isOperatorRuntime ? "not-applicable" : runtime.ResolvedVersion;
                    var jitCommit = isOperatorRuntime ? "not-applicable" : component.JitCommit!;
                    return new
                    {
                        runtime.Id,
                        runtimeVersion = runtime.ResolvedVersion,
                        runtimeCommit,
                        jitVersion,
                        jitCommit,
                        runtimeImageId = images[runtime.Id],
                        image = images[runtime.Id],
                        runtime.Rid,
                        runtime.Architecture,
                        capabilities = runtime.Capabilities
                    };
                }).ToArray();
            responses[RuntimeStatusUri] = Serialize(new
            {
                service = new ServiceIdentity("runtime-supervisor", ServiceKind.RuntimeSupervisor, releaseLock.ReleaseId, ProtocolVersion.WorkerV1, [], "ready"),
                profiles = runtimeProfiles
            });
            return responses;
        }

        public StubHttpHandler CreateHandler(Dictionary<Uri, string>? responses = null) =>
            new StubHttpHandler(responses ?? CreateResponses());

        public Uri WorkerUri(string id) => Endpoint(endpoints.Services[id], "/api/v1/worker/describe");
        public string ImageId(string id) => images[id];
        public string ReferenceSetDigest(string id) => catalog.ReferenceSets.Single(item => item.Id == id).Digest;
        public string ReferenceSetSourceUri(string id) => releaseLock.Components[id].SourceUri!;

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private WorkerDescriptor Worker(string profileId, string imageId, IReadOnlyDictionary<string, string> identity, ServiceKind serviceKind = ServiceKind.ToolchainWorker, WorkerKind workerKind = WorkerKind.Toolchain) =>
            new(
                new ServiceIdentity(profileId, serviceKind, releaseLock.ReleaseId, ProtocolVersion.WorkerV1, [], "ready"),
                $"{profileId}-instance",
                workerKind,
                images[imageId],
                ProtocolVersion.WorkerV1,
                [ProtocolVersion.WorkerV1],
                [],
                [profileId],
                DateTimeOffset.UnixEpoch,
                identity,
                serviceKind == ServiceKind.ToolchainWorker
                    ? CreateReferenceSetAttestations(profileId) : null);

        private ReferenceSetAttestation[] CreateReferenceSetAttestations(string profileId)
        {
            var toolchain = catalog.Toolchains.Single(item => item.Id == profileId);
            return toolchain.AllowedReferenceSetIds.Select(referenceSetId =>
            {
                var manifest = catalog.ReferenceSets.Single(item => item.Id == referenceSetId);
                var component = releaseLock.Components[referenceSetId];
                var package = string.IsNullOrWhiteSpace(component.Package) ? null : component.Package;
                var isOperatorImage = component.SourceUri?.StartsWith("docker://", StringComparison.Ordinal) == true;
                if (string.Equals(referenceSetId, "netfx30-managed-ref", StringComparison.Ordinal))
                {
                    return new ReferenceSetAttestation(referenceSetId, manifest.TargetFramework, manifest.Digest, $"sha256:{new string('f', 64)}", NetFx30CompositeProvenance());
                }
                return new ReferenceSetAttestation(referenceSetId, manifest.TargetFramework, manifest.Digest, $"sha256:{new string('f', 64)}", new ReferenceSetProvenance(isOperatorImage ? "operator-image" : package is null ? "source-build" : "nuget-package", component.ResolvedVersion, package, component.SourceUri, isOperatorImage ? null : component.Commit, isOperatorImage ? component.Digest : package is null ? component.Digest : $"sha512:{component.Sha512}"));
            }).ToArray();
        }

        private static ReferenceSetProvenance NetFx30CompositeProvenance() => new(
            "nuget-package-composition",
            "net30-union-v1",
            Sources:
            [
                new ReferenceSetProvenanceSource(
                    "base",
                    "all",
                    "Microsoft.NETFramework.ReferenceAssemblies.net20",
                    "1.0.3",
                    "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net20/1.0.3/microsoft.netframework.referenceassemblies.net20.1.0.3.nupkg",
                    "sha512:335bc1db148c258d05757352507e248e3d38693a9620e3d429e5147da0a8540e49570df45c63bd203ee652e068fa29d25cb8262efa0c9126f777df18110c1fc8",
                    "sha512-M1vB2xSMJY0FdXNSUH4kjj04aTqWIOPUKeUUfaCoVA5JVw30XGO9ID7mUuBo+inSXLgmLvoMkSb3d98YEQwfyA=="),
                new ReferenceSetProvenanceSource(
                    "extension",
                    "assembly-version:3.0.0.0",
                    "Microsoft.NETFramework.ReferenceAssemblies.net35",
                    "1.0.3",
                    "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net35/1.0.3/microsoft.netframework.referenceassemblies.net35.1.0.3.nupkg",
                    "sha512:974538a5f8e787cd2af679cc4b2ea1f4e69a2edf76f3d428da53b361aa0d5f0cf8041520c7515e400fc16f3de1735f8252f0e9ce21bbecef22d4367a6d720af8",
                    "sha512-l0U4pfjnh80q9nnMSy6h9OaaLt9289Qo2lOzYaoNXwz4BBUgx1FeQA/Bbz3hc1+CUvDpziG77O8i1DZ6bXIK+A==")
            ]);

        private static ReleaseLockDocument CreateLock() => new()
        {
            SchemaVersion = 1,
            ReleaseId = "candidate-test",
            ResolvedAt = DateTimeOffset.UnixEpoch,
            Components = new Dictionary<string, LockedComponent>(StringComparer.Ordinal)
            {
                ["roslyn-stable"] = Package("toolchain", "5.6.0", "Microsoft.CodeAnalysis.CSharp"),
                ["roslyn-stable-netfx48"] = Package("toolchain", "5.6.0", "Microsoft.CodeAnalysis.CSharp"),
                ["roslyn-main"] = new()
                {
                    Kind = "toolchain",
                    ResolvedVersion = "5.10.0",
                    Commit = new string('a', 40),
                    SourceUri = $"https://example.test/roslyn/{new string('a', 40)}.tar.gz",
                    Digest = $"sha256:{new string('b', 64)}"
                },
                ["roslyn-const-generics"] = new()
                {
                    Kind = "toolchain",
                    ResolvedVersion = "4.8.0-const-generics.bcd209abd947",
                    Commit = new string('b', 40)
                },
                ["fsharp-stable"] = Package("toolchain", "43.12.204", "FSharp.Compiler.Service"),
                ["fsharp-core"] = Package("runtime-dependency", "10.1.204", "FSharp.Core"),
                ["gsharp-source"] = new()
                {
                    Kind = "source",
                    ResolvedVersion = "0.3.33",
                    Commit = "aaf35bb8d5e1e8704e982ad0ab95263451bd2d3d",
                    Digest = "sha256:f52d21ef09b198bad69b7ac8dd5f6d2eaa91216b80bfc22e9610a1fef28f06d4",
                    SourceUri = "https://codeload.github.com/DavidObando/gsharp/tar.gz/aaf35bb8d5e1e8704e982ad0ab95263451bd2d3d"
                },
                ["gsharp-stable"] = new()
                {
                    Kind = "toolchain",
                    ResolvedVersion = "0.3.33",
                    Commit = "aaf35bb8d5e1e8704e982ad0ab95263451bd2d3d",
                    Digest = "sha256:f52d21ef09b198bad69b7ac8dd5f6d2eaa91216b80bfc22e9610a1fef28f06d4",
                    SourceUri = "https://github.com/DavidObando/gsharp/tree/aaf35bb8d5e1e8704e982ad0ab95263451bd2d3d"
                },
                ["gsharp-legacy-0.3.8-source"] = new()
                {
                    Kind = "source",
                    ResolvedVersion = "0.3.8",
                    Commit = "723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01",
                    Digest = "sha256:d01510636cb7a4598f76fb01c8d2cf59898def757fd536049a92c359cd9c71fb",
                    SourceUri = "https://codeload.github.com/DavidObando/gsharp/tar.gz/723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01"
                },
                ["gsharp-legacy-0.3.8"] = new()
                {
                    Kind = "toolchain",
                    ResolvedVersion = "0.3.8",
                    Commit = "723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01",
                    Digest = "sha256:d01510636cb7a4598f76fb01c8d2cf59898def757fd536049a92c359cd9c71fb",
                    SourceUri = "https://github.com/DavidObando/gsharp/tree/723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01"
                },
                ["peachpie-stable"] = Package("toolchain", "1.1.13", "Peachpie.CodeAnalysis") with
                {
                    Commit = "608bf30cf3f43f97e32825076a2cfdaa25043e50"
                },
                ["peachpie-runtime"] = Package("runtime-dependency", "1.1.13", "Peachpie.Runtime") with
                {
                    Commit = "608bf30cf3f43f97e32825076a2cfdaa25043e50"
                },
                ["peachpie-library"] = Package("runtime-dependency", "1.1.13", "Peachpie.Library") with
                {
                    Commit = "608bf30cf3f43f97e32825076a2cfdaa25043e50"
                },
                ["msvc-cppcli-netfx48"] = OperatorImageComponent("toolchain", "19.51.36248"),
                ["jsharp20"] = JSharpOperatorComponent("operator-image", "2.0.50727.937-clr2-x64"),
                ["vjc-jsharp20"] = new()
                {
                    Kind = "toolchain",
                    ResolvedVersion = "2.0.50727.937"
                },
                ["jsharp20-ref"] = new()
                {
                    Kind = "reference-set",
                    ResolvedVersion = "2.0.50727.937",
                    Digest = "sha256:25288dc53b3190f14a65ebf96258601a262ebd4a8fa68e4881c897258b122013",
                    SourceUri = JSharpOperatorSourceUri
                },
                ["mobius-ilasm-stable"] = Package("toolchain", "0.1.0", "Mobius.ILasm"),
                ["minilang-stable"] = new()
                {
                    Kind = "toolchain",
                    ResolvedVersion = "1.0.0"
                },
                ["ilspy"] = Package("artifact-processor", "10.1.0.8386", "ICSharpCode.Decompiler"),
                ["dotnet-ilverify"] = Package("artifact-processor", "10.0.9", "Microsoft.ILVerification"),
                ["artifacts-default"] = new()
                {
                    Kind = "artifact-processor",
                    ResolvedVersion = "ilspy/10.1.0.8386+ilverify/10.0.9"
                },
                ["artifacts-const-generics"] = new()
                {
                    Kind = "artifact-processor",
                    ResolvedVersion = $"{new string('d', 12)}-{new string('c', 12)}-ctarg-v1",
                    Commit = new string('d', 40)
                },
                ["const-generics-ilspy-source"] = new()
                {
                    Kind = "source",
                    ResolvedVersion = new string('d', 40),
                    Commit = new string('d', 40),
                    Digest = $"sha256:{new string('e', 64)}",
                    SourceUri = $"https://example.test/ilspy/{new string('d', 40)}.tar.gz"
                },
                ["const-generics-runtime-source"] = new()
                {
                    Kind = "source",
                    ResolvedVersion = new string('c', 40),
                    Commit = new string('c', 40),
                    Digest = $"sha256:{new string('f', 64)}",
                    SourceUri = $"https://example.test/runtime/{new string('c', 40)}.tar.gz"
                },
                ["dotnet-10-linux-x64"] = Runtime("10.0.9"),
                ["dotnet-11-preview-linux-x64"] = Runtime("11.0.0-preview.5"),
                ["const-generics-linux-x64"] = new()
                {
                    Kind = "runtime",
                    ResolvedVersion = "9.0.0-constgenerics.1.23470.1",
                    Commit = new string('c', 40),
                    JitCommit = new string('c', 40)
                },
                ["wine-netfx48-linux-x64"] = OperatorImageComponent("runtime", "wine-9.0+netfx48"),
                ["wine-jsharp20-linux-x64"] = JSharpOperatorComponent("runtime", "wine-9.0+clr2+jsharp-2.0.50727.937"),
                ["net10-ref"] = Package("reference-set", "10.0.9", "Microsoft.NETCore.App.Ref"),
                ["net11-preview-ref"] = Package("reference-set", "11.0.0-preview.5", "Microsoft.NETCore.App.Ref"),
                ["netfx20-managed-ref"] = FrameworkPackage("net20"),
                ["netfx30-managed-ref"] = new()
                {
                    Kind = "reference-set",
                    ResolvedVersion = "net30-union-v1",
                    Digest = "sha256:d61880a865bf41757cd61d1006f72aade7fcf574a369a7c7189aea0d60579b96"
                },
                ["netfx35-managed-ref"] = FrameworkPackage("net35"),
                ["netfx40-managed-ref"] = FrameworkPackage("net40"),
                ["netfx45-managed-ref"] = FrameworkPackage("net45"),
                ["netfx451-managed-ref"] = FrameworkPackage("net451"),
                ["netfx452-managed-ref"] = FrameworkPackage("net452"),
                ["netfx46-managed-ref"] = FrameworkPackage("net46"),
                ["netfx461-managed-ref"] = FrameworkPackage("net461"),
                ["netfx462-managed-ref"] = FrameworkPackage("net462"),
                ["netfx47-managed-ref"] = FrameworkPackage("net47"),
                ["netfx471-managed-ref"] = FrameworkPackage("net471"),
                ["netfx472-managed-ref"] = FrameworkPackage("net472"),
                ["netfx48-managed-ref"] = Package("reference-set", "1.0.3", "Microsoft.NETFramework.ReferenceAssemblies.net48"),
                ["const-generics-ref"] = new()
                {
                    Kind = "reference-set",
                    ResolvedVersion = "9.0.0-constgenerics.1.23470.1",
                    Commit = new string('c', 40),
                    Digest = "sha256:00f0f9fcfc083e931004ceaa914633990ad7e389ce8d21012b97af5844f501b4",
                    SourceUri = "https://example.test/runtime/const-generics-source.tar.gz"
                },
                ["netfx48-ref"] = OperatorImageComponent("reference-set", "4.8")
            }
        };

        private static LockedComponent OperatorImageComponent(string kind, string version)
        {
            const string digest = "sha256:463e30099e98f760e5f67cbe5aedeae5679f3fa4d3d1e9f9fee5232a5c06e743";
            return new LockedComponent
            {
                Kind = kind,
                ResolvedVersion = version,
                Digest = digest,
                SourceUri = $"docker://codex/msvc-wine@{digest}"
            };
        }

        private const string JSharpOperatorDigest = "sha256:61ac7f65c64101a912fca1fee14128241daa0e2d1869ce641f2192fa0a2555f6";
        private const string JSharpOperatorSourceUri = "docker://sharplabnext/operator-jsharp20@sha256:61ac7f65c64101a912fca1fee14128241daa0e2d1869ce641f2192fa0a2555f6";

        private static LockedComponent JSharpOperatorComponent(string kind, string version) => new()
        {
            Kind = kind,
            ResolvedVersion = version,
            Digest = JSharpOperatorDigest,
            SourceUri = JSharpOperatorSourceUri
        };

        private static LockedComponent Package(string kind, string version, string package) => new()
        {
            Kind = kind,
            ResolvedVersion = version,
            Package = package,
            SourceUri = $"https://example.test/{package}/{version}.nupkg",
            PackageContentHash = $"sha512-{package}-{version}",
            Sha512 = new string('d', 128)
        };

        private static LockedComponent FrameworkPackage(string targetFramework) => Package("reference-set", "1.0.3", $"Microsoft.NETFramework.ReferenceAssemblies.{targetFramework}");

        private static LockedComponent Runtime(string version) => new()
        {
            Kind = "runtime",
            ResolvedVersion = version,
            Commit = version.StartsWith("10.", StringComparison.Ordinal)
                ? "901ca941248413c79832d2fdbd709da0c4386353" : "f7b4c5716faaee8fb8a289aed29118cad955c45f",
            JitCommit = version.StartsWith("10.", StringComparison.Ordinal)
                ? "901ca941248413c79832d2fdbd709da0c4386353" : "f7b4c5716faaee8fb8a289aed29118cad955c45f",
            SourceUri = $"https://example.test/runtime/{version}.tar.gz",
            Sha512 = new string('e', 128)
        };

        private static CatalogDocument RestrictSelectableRuntimesToLock(CatalogDocument catalog, ReleaseLockDocument releaseLock)
        {
            var referenceSetIds = catalog.ReferenceSets.Where(referenceSet => HasComponent(releaseLock, referenceSet.Id, "reference-set") || IsResolvedBySyntheticChannel(referenceSet.Id)).Select(static referenceSet => referenceSet.Id).ToHashSet(StringComparer.Ordinal);
            return catalog with
            {
                Toolchains = catalog.Toolchains.Select(toolchain => toolchain with
                    {
                        AllowedReferenceSetIds = toolchain.AllowedReferenceSetIds.Where(referenceSetIds.Contains).ToArray()
                    })
                    .ToArray(),
                ReferenceSets = catalog.ReferenceSets.Where(referenceSet => referenceSetIds.Contains(referenceSet.Id)).ToArray(),
                Runtimes = catalog.Runtimes.Select(runtime => HasComponent(releaseLock, runtime.Id, "runtime")
                        ? runtime : runtime with
                        {
                            Availability = new ComponentAvailability
                            {
                                Installed = false,
                                Health = "unavailable",
                                Reason = "Not represented by this synthetic release lock."
                            }
                        })
                    .ToArray(),
                Compatibility = catalog.Compatibility.Where(rule => rule.Kind != CompatibilityRuleKind.ToolchainReferenceSet || referenceSetIds.Contains(rule.ToId)).ToArray(),
                Presets = catalog.Presets.Where(preset => referenceSetIds.Contains(preset.ReferenceSetId)).ToArray()
            };
        }

        private static bool HasComponent(ReleaseLockDocument releaseLock, string id, string kind) =>
            releaseLock.Components.TryGetValue(id, out var component) &&
            string.Equals(component.Kind, kind, StringComparison.Ordinal);

        private static bool IsResolvedBySyntheticChannel(string referenceSetId) =>
            referenceSetId is "net10-ref" or "net11-preview-ref";

        private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, WireJsonOptions);

        private static Uri Endpoint(string baseAddress, string path) => new(new Uri(baseAddress), path);

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new InvalidOperationException("Repository root was not found.");
        }
    }

    private sealed class StubHttpHandler(IReadOnlyDictionary<Uri, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri is not null && responses.TryGetValue(request.RequestUri, out var response))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

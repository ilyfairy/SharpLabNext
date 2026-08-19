using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.IntegrationTests;

public sealed class ReferenceSetAttestationReaderTests
{
    [Fact]
    public async Task ValidManifestIsLoadedAndVerified()
    {
        using var fixture = await ReferenceSetFixture.CreateAsync(
            writeManifest: true,
            TestContext.Current.CancellationToken);

        var attestation = ReferenceSetAttestationReader.LoadAndVerify(
            fixture.Root,
            ReferenceSetFixture.ReferenceSetId,
            ReferenceSetFixture.TargetFramework,
            ReferenceSetFixture.ResolvedVersion,
            expectedDigest: ReferenceSetFixture.LockedDigest,
            requireManifest: true);

        Assert.Equal(ReferenceSetFixture.ReferenceSetId, attestation.Id);
        Assert.Equal(ReferenceSetFixture.LockedDigest, attestation.Digest);
        Assert.Equal(ReferenceSetFixture.ResolvedVersion, attestation.Provenance.ResolvedVersion);
    }

    [Fact]
    public async Task ValidCompositeManifestIsLoadedAndVerified()
    {
        using var fixture = await CompositeReferenceSetFixture.CreateAsync(
            mutation: null,
            TestContext.Current.CancellationToken);

        var attestation = ReferenceSetAttestationReader.LoadAndVerify(
            fixture.Root,
            CompositeReferenceSetFixture.ReferenceSetId,
            CompositeReferenceSetFixture.TargetFramework,
            CompositeReferenceSetFixture.ResolvedVersion,
            expectedDigest: CompositeReferenceSetFixture.LockedDigest,
            requireManifest: true);

        Assert.Equal(CompositeReferenceSetFixture.LockedDigest, attestation.Digest);
        var sources = Assert.IsAssignableFrom<IReadOnlyList<SharpLabNext.Contracts.ReferenceSetProvenanceSource>>(
            attestation.Provenance.Sources);
        Assert.Collection(
            sources,
            source =>
            {
                Assert.Equal("base", source.Role);
                Assert.Equal("all", source.Selection);
            },
            source =>
            {
                Assert.Equal("extension", source.Role);
                Assert.Equal("assembly-version:3.0.0.0", source.Selection);
            });
    }

    [Theory]
    [InlineData(0, "role", "extension")]
    [InlineData(1, "selection", "assembly-version:3.5.0.0")]
    public async Task CompositeRoleOrSelectionTamperingIsRejected(
        int sourceIndex,
        string propertyName,
        string replacement)
    {
        using var fixture = await CompositeReferenceSetFixture.CreateAsync(
            document => GetCompositeSources(document)[sourceIndex]![propertyName] = replacement,
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidDataException>(() => ReferenceSetAttestationReader.LoadAndVerify(
            fixture.Root,
            CompositeReferenceSetFixture.ReferenceSetId,
            CompositeReferenceSetFixture.TargetFramework,
            CompositeReferenceSetFixture.ResolvedVersion,
            expectedDigest: CompositeReferenceSetFixture.LockedDigest,
            requireManifest: true));

        Assert.Contains("source provenance is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompositeSourceDigestTamperingIsRejected()
    {
        using var fixture = await CompositeReferenceSetFixture.CreateAsync(
            document => GetCompositeSources(document)[0]!["sourceArchiveDigest"] =
                $"sha512:{new string('0', 128)}",
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidDataException>(() => ReferenceSetAttestationReader.LoadAndVerify(
            fixture.Root,
            CompositeReferenceSetFixture.ReferenceSetId,
            CompositeReferenceSetFixture.TargetFramework,
            CompositeReferenceSetFixture.ResolvedVersion,
            expectedDigest: CompositeReferenceSetFixture.LockedDigest,
            requireManifest: true));

        Assert.Contains("source identity digest", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompositeIdentityDigestTamperingIsRejectedAfterConfigurationMatch()
    {
        var tamperedDigest = $"sha256:{new string('0', 64)}";
        using var fixture = await CompositeReferenceSetFixture.CreateAsync(
            document => document["referenceSet"]!["digest"] = tamperedDigest,
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidDataException>(() => ReferenceSetAttestationReader.LoadAndVerify(
            fixture.Root,
            CompositeReferenceSetFixture.ReferenceSetId,
            CompositeReferenceSetFixture.TargetFramework,
            CompositeReferenceSetFixture.ResolvedVersion,
            expectedDigest: tamperedDigest,
            requireManifest: true));

        Assert.Contains("source identity digest", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModifiedAssemblyIsRejected()
    {
        using var fixture = await ReferenceSetFixture.CreateAsync(
            writeManifest: true,
            TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            fixture.AssemblyPath,
            "modified",
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidDataException>(() => ReferenceSetAttestationReader.LoadAndVerify(
            fixture.Root,
            ReferenceSetFixture.ReferenceSetId,
            ReferenceSetFixture.TargetFramework,
            ReferenceSetFixture.ResolvedVersion,
            expectedDigest: ReferenceSetFixture.LockedDigest,
            requireManifest: true));

        Assert.Contains("modified assembly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtraAssemblyIsRejected()
    {
        using var fixture = await ReferenceSetFixture.CreateAsync(
            writeManifest: true,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Root, "Extra.dll"),
            [4, 5, 6],
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidDataException>(() => ReferenceSetAttestationReader.LoadAndVerify(
            fixture.Root,
            ReferenceSetFixture.ReferenceSetId,
            ReferenceSetFixture.TargetFramework,
            ReferenceSetFixture.ResolvedVersion,
            expectedDigest: ReferenceSetFixture.LockedDigest,
            requireManifest: true));

        Assert.Contains("file set", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiredManifestCannotBeMissing()
    {
        using var fixture = await ReferenceSetFixture.CreateAsync(
            writeManifest: false,
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidDataException>(() => ReferenceSetAttestationReader.LoadAndVerify(
            fixture.Root,
            ReferenceSetFixture.ReferenceSetId,
            ReferenceSetFixture.TargetFramework,
            ReferenceSetFixture.ResolvedVersion,
            expectedDigest: ReferenceSetFixture.LockedDigest,
            requireManifest: true));

        Assert.Contains("manifest is missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestDigestMustMatchConfiguredDigest()
    {
        using var fixture = await ReferenceSetFixture.CreateAsync(
            writeManifest: true,
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidDataException>(() => ReferenceSetAttestationReader.LoadAndVerify(
            fixture.Root,
            ReferenceSetFixture.ReferenceSetId,
            ReferenceSetFixture.TargetFramework,
            ReferenceSetFixture.ResolvedVersion,
            expectedDigest: "sha512-wrong-reference-package",
            requireManifest: true));

        Assert.Contains("digest does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryPackageManifestCannotDeclareCompositeSources()
    {
        using var fixture = await ReferenceSetFixture.CreateAsync(
            writeManifest: true,
            TestContext.Current.CancellationToken);
        var manifestPath = Path.Combine(fixture.Root, ReferenceSetAttestationReader.ManifestFileName);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(
            manifestPath,
            TestContext.Current.CancellationToken))!.AsObject();
        document["referenceSet"]!["provenance"]!["sources"] = new JsonArray();
        await File.WriteAllTextAsync(
            manifestPath,
            document.ToJsonString(),
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidDataException>(() => ReferenceSetAttestationReader.LoadAndVerify(
            fixture.Root,
            ReferenceSetFixture.ReferenceSetId,
            ReferenceSetFixture.TargetFramework,
            ReferenceSetFixture.ResolvedVersion,
            expectedDigest: ReferenceSetFixture.LockedDigest,
            requireManifest: true));

        Assert.Contains("not valid for its kind", exception.Message, StringComparison.Ordinal);
    }

    private static JsonArray GetCompositeSources(JsonObject document) =>
        document["referenceSet"]!["provenance"]!["sources"]!.AsArray();

    private sealed class ReferenceSetFixture : IDisposable
    {
        public const string ReferenceSetId = "test-reference-set";
        public const string TargetFramework = "net10.0";
        public const string ResolvedVersion = "10.0.9";
        public const string LockedDigest = "sha512-test-reference-package";
        private const string AssemblyFileName = "System.Runtime.dll";

        private ReferenceSetFixture(string root)
        {
            Root = root;
            AssemblyPath = Path.Combine(root, AssemblyFileName);
        }

        public string Root { get; }
        public string AssemblyPath { get; }

        public static async Task<ReferenceSetFixture> CreateAsync(
            bool writeManifest,
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"SharpLabNext.ReferenceSetAttestation.{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var fixture = new ReferenceSetFixture(root);
            byte[] assemblyBytes = [1, 2, 3];
            await File.WriteAllBytesAsync(fixture.AssemblyPath, assemblyBytes, cancellationToken);
            if (writeManifest)
                await WriteManifestAsync(root, assemblyBytes, cancellationToken);
            return fixture;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static async Task WriteManifestAsync(
            string root,
            byte[] assemblyBytes,
            CancellationToken cancellationToken)
        {
            var fileDigest = Sha256(assemblyBytes);
            var canonical = $"{fileDigest}  {assemblyBytes.LongLength}  {AssemblyFileName}\n";
            var contentDigest = Sha256(Encoding.UTF8.GetBytes(canonical));
            var document = new
            {
                schemaVersion = 1,
                referenceSet = new
                {
                    id = ReferenceSetId,
                    targetFramework = TargetFramework,
                    digest = LockedDigest,
                    contentDigest,
                    provenance = new
                    {
                        kind = "nuget-package",
                        resolvedVersion = ResolvedVersion,
                        package = "Microsoft.NETCore.App.Ref",
                        sourceUri = "https://example.test/microsoft.netcore.app.ref.10.0.9.nupkg",
                        sourceArchiveDigest = $"sha512:{new string('b', 128)}"
                    }
                },
                files = new[]
                {
                    new
                    {
                        path = AssemblyFileName,
                        size = assemblyBytes.LongLength,
                        digest = fileDigest
                    }
                }
            };
            await File.WriteAllTextAsync(
                Path.Combine(root, ReferenceSetAttestationReader.ManifestFileName),
                JsonSerializer.Serialize(document),
                cancellationToken);
        }

        private static string Sha256(byte[] value) =>
            $"sha256:{Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant()}";
    }

    private sealed class CompositeReferenceSetFixture : IDisposable
    {
        public const string ReferenceSetId = "netfx30-managed-ref";
        public const string TargetFramework = "net30";
        public const string ResolvedVersion = "net30-union-v1";
        public const string LockedDigest =
            "sha256:d61880a865bf41757cd61d1006f72aade7fcf574a369a7c7189aea0d60579b96";
        private const string AssemblyFileName = "mscorlib.dll";

        private CompositeReferenceSetFixture(string root) => Root = root;

        public string Root { get; }

        public static async Task<CompositeReferenceSetFixture> CreateAsync(
            Action<JsonObject>? mutation,
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"SharpLabNext.CompositeReferenceSetAttestation.{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var fixture = new CompositeReferenceSetFixture(root);
            byte[] assemblyBytes = [1, 2, 3];
            await File.WriteAllBytesAsync(
                Path.Combine(root, AssemblyFileName),
                assemblyBytes,
                cancellationToken);

            var fileDigest = Sha256(assemblyBytes);
            var contentDigest = Sha256(Encoding.UTF8.GetBytes(
                $"{fileDigest}  {assemblyBytes.LongLength}  {AssemblyFileName}\n"));
            var document = CreateDocument(contentDigest, fileDigest, assemblyBytes.LongLength);
            mutation?.Invoke(document);
            await File.WriteAllTextAsync(
                Path.Combine(root, ReferenceSetAttestationReader.ManifestFileName),
                document.ToJsonString(),
                cancellationToken);
            return fixture;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static JsonObject CreateDocument(string contentDigest, string fileDigest, long fileSize) => new()
        {
            ["schemaVersion"] = 1,
            ["referenceSet"] = new JsonObject
            {
                ["id"] = ReferenceSetId,
                ["targetFramework"] = TargetFramework,
                ["digest"] = LockedDigest,
                ["contentDigest"] = contentDigest,
                ["provenance"] = new JsonObject
                {
                    ["kind"] = "nuget-package-composition",
                    ["resolvedVersion"] = ResolvedVersion,
                    ["sources"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "base",
                            ["selection"] = "all",
                            ["package"] = "Microsoft.NETFramework.ReferenceAssemblies.net20",
                            ["resolvedVersion"] = "1.0.3",
                            ["sourceUri"] = "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net20/1.0.3/microsoft.netframework.referenceassemblies.net20.1.0.3.nupkg",
                            ["sourceArchiveDigest"] = "sha512:335bc1db148c258d05757352507e248e3d38693a9620e3d429e5147da0a8540e49570df45c63bd203ee652e068fa29d25cb8262efa0c9126f777df18110c1fc8",
                            ["packageContentHash"] = "sha512-M1vB2xSMJY0FdXNSUH4kjj04aTqWIOPUKeUUfaCoVA5JVw30XGO9ID7mUuBo+inSXLgmLvoMkSb3d98YEQwfyA=="
                        },
                        new JsonObject
                        {
                            ["role"] = "extension",
                            ["selection"] = "assembly-version:3.0.0.0",
                            ["package"] = "Microsoft.NETFramework.ReferenceAssemblies.net35",
                            ["resolvedVersion"] = "1.0.3",
                            ["sourceUri"] = "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net35/1.0.3/microsoft.netframework.referenceassemblies.net35.1.0.3.nupkg",
                            ["sourceArchiveDigest"] = "sha512:974538a5f8e787cd2af679cc4b2ea1f4e69a2edf76f3d428da53b361aa0d5f0cf8041520c7515e400fc16f3de1735f8252f0e9ce21bbecef22d4367a6d720af8",
                            ["packageContentHash"] = "sha512-l0U4pfjnh80q9nnMSy6h9OaaLt9289Qo2lOzYaoNXwz4BBUgx1FeQA/Bbz3hc1+CUvDpziG77O8i1DZ6bXIK+A=="
                        }
                    }
                }
            },
            ["files"] = new JsonArray
            {
                new JsonObject
                {
                    ["path"] = AssemblyFileName,
                    ["size"] = fileSize,
                    ["digest"] = fileDigest
                }
            }
        };

        private static string Sha256(byte[] value) =>
            $"sha256:{Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant()}";
    }
}

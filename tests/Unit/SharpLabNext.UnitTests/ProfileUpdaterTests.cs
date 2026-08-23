using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.ProfileUpdater;
using YamlDotNet.RepresentationModel;

namespace SharpLabNext.UnitTests;

public sealed class ProfileUpdaterTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ActiveRuntimeProfileIds =
    [
        "const-generics-linux-x64",
        "dotnet-10-linux-x64",
        "dotnet-11-preview-linux-x64",
        "wine-jsharp20-linux-x64",
        "wine-netfx48-linux-x64"
    ];

    [Fact]
    public async Task CandidateWorkspaceInitializesSubmodulesAfterCreatingWorktree()
    {
        var runner = new RecordingCommandRunner();
        var manager = new GitProfileCandidateWorkspaceManager(runner);
        var root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.Source.{Guid.NewGuid():N}");
        var workspace = Path.Combine(Path.GetTempPath(), $"SharpLabNext.Candidate.{Guid.NewGuid():N}");

        await manager.PrepareAsync(root, workspace, TestContext.Current.CancellationToken);

        Assert.Collection(
            runner.Commands,
            command =>
            {
                Assert.Equal("git", command.FileName);
                Assert.Equal(root, command.WorkingDirectory);
                Assert.Equal(["worktree", "add", "--detach", workspace, "HEAD"], command.Arguments);
            },
            command =>
            {
                Assert.Equal(workspace, command.WorkingDirectory);
                Assert.Equal(["submodule", "sync", "--recursive"], command.Arguments);
            },
            command =>
            {
                Assert.Equal(workspace, command.WorkingDirectory);
                Assert.Equal(
                    ["submodule", "update", "--init", "--recursive", "--checkout", "--force"],
                    command.Arguments);
            });
    }

    [Fact]
    public async Task CandidateWorkspaceRefreshesSubmodulesWhenWorktreeAlreadyExists()
    {
        var runner = new RecordingCommandRunner();
        var manager = new GitProfileCandidateWorkspaceManager(runner);
        var root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.Source.{Guid.NewGuid():N}");
        var workspace = Path.Combine(Path.GetTempPath(), $"SharpLabNext.Candidate.{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".git"),
            "gitdir: test",
            TestContext.Current.CancellationToken);
        try
        {
            await manager.PrepareAsync(root, workspace, TestContext.Current.CancellationToken);

            Assert.Collection(
                runner.Commands,
                command => Assert.Equal(["submodule", "sync", "--recursive"], command.Arguments),
                command => Assert.Equal(
                    ["submodule", "update", "--init", "--recursive", "--checkout", "--force"],
                    command.Arguments));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task CandidateWorkspaceFailsClosedWhenSubmoduleCheckoutFails()
    {
        var runner = new RecordingCommandRunner
        {
            FailurePredicate = static command => command.Arguments.SequenceEqual(
                ["submodule", "update", "--init", "--recursive", "--checkout", "--force"])
        };
        var manager = new GitProfileCandidateWorkspaceManager(runner);
        var root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.Source.{Guid.NewGuid():N}");
        var workspace = Path.Combine(Path.GetTempPath(), $"SharpLabNext.Candidate.{Guid.NewGuid():N}");

        var exception = await Assert.ThrowsAsync<ProfileUpdateValidationException>(() =>
            manager.PrepareAsync(root, workspace, TestContext.Current.CancellationToken));

        Assert.Contains("submodule checkout failed with exit code 17", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfficialSourceReadsRuntimeAndJitCommitFromVerifiedArchiveVersion()
    {
        const string commit = "901ca941248413c79832d2fdbd709da0c4386353";
        var archive = CreateRuntimeArchive(commit, "10.0.9");
        var sha512 = Convert.ToHexStringLower(SHA512.HashData(archive));
        using var http = new HttpClient(new DelegateHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/dotnet/release-metadata/10.0/releases.json" => JsonResponse(CreateReleaseMetadata("10.0.9", sha512)),
            "/runtime.tar.gz" => BytesResponse(archive),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var source = new OfficialProfileSourceClient(http);

        var result = await source.ResolveDotNetChannelAsync("10.0", TestContext.Current.CancellationToken);

        Assert.Equal(commit, result.RuntimeCommit);
        Assert.Equal(commit, result.JitCommit);
    }

    [Fact]
    public async Task OfficialSourceRejectsRuntimeVersionFromArchiveWithWrongChecksum()
    {
        var archive = CreateRuntimeArchive(new string('a', 40), "10.0.9");
        using var http = new HttpClient(new DelegateHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/dotnet/release-metadata/10.0/releases.json" => JsonResponse(CreateReleaseMetadata("10.0.9", new string('0', 128))),
            "/runtime.tar.gz" => BytesResponse(archive),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var source = new OfficialProfileSourceClient(http);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolveDotNetChannelAsync(
            "10.0",
            TestContext.Current.CancellationToken));

        Assert.Contains("SHA-512 mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfficialSourceResolvesGSharpVersionTagToExactCommitAndArchiveDigest()
    {
        const string commit = "723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01";
        var archive = Encoding.UTF8.GetBytes("gsharp-source-archive");
        using var http = new HttpClient(new DelegateHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/DavidObando/gsharp/commits/v0.3.8" => JsonResponse($$"""{"sha":"{{commit}}"}"""),
            $"/DavidObando/gsharp/archive/{commit}.tar.gz" => BytesResponse(archive),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var source = new OfficialProfileSourceClient(http);

        var result = await source.ResolveGitCommitAsync(
            "DavidObando",
            "gsharp",
            "v0.3.8",
            TestContext.Current.CancellationToken);

        Assert.Equal("0.3.8", result.ProductVersion);
        Assert.Equal(commit, result.Commit);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(archive)), result.ArchiveSha256);
    }

    [Fact]
    public async Task ResolverUsesChannelManifestsAsReleaseInputAuthority()
    {
        using var channels = new TempChannelDirectory(
            runtimeYaml: """
                id: custom-runtime
                kind: runtime-channel
                source:
                  type: dotnet-release-metadata
                  channel: "10.0"
                  policy: latest-release
                sdkComponentId: custom-sdk
                referenceSet:
                  id: custom-ref
                  package: Custom.ReferencePack
                platform:
                  os: linux
                  libc: glibc
                  architecture: x64
                update:
                  pollInterval: 12h
                  autoPromoteAfterTests: false
                  retainLastKnownGood: true
                """,
            toolchainsYaml: """
                channels:
                  - id: custom-compiler
                    kind: toolchain
                    source: { type: nuget, package: Custom.Compiler, policy: exact, version: 2.3.4 }
                derivedComponents:
                  - id: custom-framework-compiler
                    kind: toolchain
                    versionTemplate: "{custom-compiler}"
                update:
                  pollInterval: 12h
                  autoPromoteAfterTests: false
                """);
        var updater = new ReleaseLockUpdater(new FakeProfileSourceClient(), channels.Root);
        var current = new ReleaseLockDocument
        {
            SchemaVersion = 1,
            ReleaseId = "development",
            ResolvedAt = DateTimeOffset.UnixEpoch,
            Components = new Dictionary<string, LockedComponent>()
        };

        var result = await updater.ResolveAsync(current, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("10.0.9", result.Candidate.Components["custom-runtime"].ResolvedVersion);
        Assert.Equal("10.0.301", result.Candidate.Components["custom-sdk"].ResolvedVersion);
        Assert.Equal("Custom.ReferencePack", result.Candidate.Components["custom-ref"].Package);
        Assert.Equal("2.3.4", result.Candidate.Components["custom-compiler"].ResolvedVersion);
        var customCompiler = result.Candidate.Components["custom-compiler"];
        var customFrameworkCompiler = result.Candidate.Components["custom-framework-compiler"];
        Assert.Equal(customCompiler with { PatchDigest = null, ImageId = null }, customFrameworkCompiler);
        Assert.DoesNotContain("roslyn-stable", result.Candidate.Components);
        Assert.DoesNotContain("peachpie-stable", result.Candidate.Components);
    }

    [Theory]
    [InlineData("runtime", "{compiler}")]
    [InlineData("toolchain", "prefix-{compiler}")]
    [InlineData("toolchain", "{compiler}-{other}")]
    [InlineData("toolchain", "{processor}")]
    public async Task ResolverRejectsInvalidDerivedToolchainContract(
        string kind,
        string versionTemplate)
    {
        using var channels = new TempChannelDirectory(
            toolchainsYaml: $$"""
                channels:
                  - id: compiler
                    kind: toolchain
                    source: { type: nuget, package: Compiler, policy: latest-stable }
                  - id: other
                    kind: toolchain
                    source: { type: nuget, package: Other.Compiler, policy: latest-stable }
                  - id: processor
                    kind: artifact-processor
                    source: { type: nuget, package: Processor, policy: latest-stable }
                derivedComponents:
                  - id: derived-compiler
                    kind: {{kind}}
                    versionTemplate: "{{versionTemplate}}"
                update:
                  pollInterval: 6h
                  autoPromoteAfterTests: false
                """);
        var updater = new ReleaseLockUpdater(new FakeProfileSourceClient(), channels.Root);
        var current = new ReleaseLockDocument
        {
            SchemaVersion = 1,
            ReleaseId = "development",
            ResolvedAt = DateTimeOffset.UnixEpoch,
            Components = new Dictionary<string, LockedComponent>()
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => updater.ResolveAsync(
            current,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("derived", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CandidateIdentityClosureRejectsDerivedToolchainInputMismatch()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);
        var components = releaseLock.Components.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        components["roslyn-stable-netfx48"] = components["roslyn-stable-netfx48"] with
        {
            SourceUri = "https://example.test/wrong-roslyn-package.nupkg"
        };

        var exception = Assert.Throws<ProfileUpdateValidationException>(() =>
            CandidateReleaseMaterializer.ValidateIdentityClosure(
                releaseLock with { Components = components },
                catalog));

        Assert.Contains("complete input identity", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateIdentityClosureRejectsLegacyGSharpComponentWithWrongKind()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);
        catalog = CandidateReleaseMaterializer.CreateCatalog(
            catalog,
            releaseLock,
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var components = releaseLock.Components.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        components["gsharp-legacy-0.3.8"] = components["gsharp-legacy-0.3.8"] with
        {
            Kind = "source"
        };

        var exception = Assert.Throws<ProfileUpdateValidationException>(() =>
            CandidateReleaseMaterializer.ValidateIdentityClosure(
                releaseLock with { Components = components },
                catalog));

        Assert.Contains("gsharp-legacy-0.3.8", exception.Message, StringComparison.Ordinal);
        Assert.Contains("must have kind 'toolchain'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateIdentityClosureRejectsPayloadIdentityWhenMatrixRuntimeBecomesSelectable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);
        catalog = CandidateReleaseMaterializer.CreateCatalog(
            catalog,
            releaseLock,
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        const string runtimeId = "dotnet-5-linux-x64";
        var matrixRuntime = catalog.Runtimes.Single(runtime => runtime.Id == runtimeId);
        var candidateCatalog = catalog with
        {
            Runtimes = catalog.Runtimes
                .Select(runtime => runtime.Id == runtimeId
                    ? runtime with
                    {
                        Availability = new ComponentAvailability
                        {
                            Installed = true,
                            Health = "healthy"
                        }
                    }
                    : runtime)
                .ToArray()
        };
        var components = releaseLock.Components.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        components[runtimeId] = new LockedComponent
        {
            Kind = "runtime",
            ResolvedVersion = matrixRuntime.ResolvedVersion,
            Commit = $"payload-sha512:{new string('a', 128)}",
            JitCommit = $"payload-sha512:{new string('b', 128)}",
            Sha512 = new string('c', 128),
            SourceUri = "https://example.test/dotnet-5-runtime.tar.gz"
        };

        var exception = Assert.Throws<ProfileUpdateValidationException>(() =>
            CandidateReleaseMaterializer.ValidateIdentityClosure(
                releaseLock with { Components = components },
                candidateCatalog));

        Assert.Contains("payload-sha512", exception.Message, StringComparison.Ordinal);
        Assert.Contains(runtimeId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateIdentityClosureRejectsSelectableMatrixRuntimeWithoutLockComponent()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);
        catalog = CandidateReleaseMaterializer.CreateCatalog(
            catalog,
            releaseLock,
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        const string runtimeId = "dotnet-5-linux-x64";
        var candidateCatalog = catalog with
        {
            Runtimes = catalog.Runtimes
                .Select(runtime => runtime.Id == runtimeId
                    ? runtime with
                    {
                        Availability = new ComponentAvailability
                        {
                            Installed = true,
                            Health = "healthy"
                        }
                    }
                    : runtime)
                .ToArray()
        };

        var components = releaseLock.Components.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        Assert.True(components.Remove(runtimeId));

        var exception = Assert.Throws<ProfileUpdateValidationException>(() =>
            CandidateReleaseMaterializer.ValidateIdentityClosure(
                releaseLock with { Components = components },
                candidateCatalog));

        Assert.Contains("no corresponding lock component", exception.Message, StringComparison.Ordinal);
        Assert.Contains(runtimeId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateIdentityClosureAcceptsSelectableMonoWithOperatorDigest()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);
        catalog = CandidateReleaseMaterializer.CreateCatalog(
            catalog,
            releaseLock,
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        const string runtimeId = "mono-6.12-linux-x64";
        var mono = catalog.Runtimes.Single(runtime => runtime.Id == runtimeId);
        var candidateCatalog = catalog with
        {
            Runtimes = catalog.Runtimes
                .Select(runtime => runtime.Id == runtimeId
                    ? runtime with
                    {
                        RuntimeCommit = "not-applicable",
                        JitVersion = "not-applicable",
                        JitCommit = "not-applicable",
                        Availability = new ComponentAvailability
                        {
                            Installed = true,
                            Health = "healthy"
                        }
                    }
                    : runtime)
                .ToArray()
        };
        var components = releaseLock.Components.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        components[runtimeId] = new LockedComponent
        {
            Kind = "runtime",
            ResolvedVersion = mono.ResolvedVersion,
            Digest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            SourceUri = "docker://example.test/mono@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };

        CandidateReleaseMaterializer.ValidateIdentityClosure(
            releaseLock with { Components = components },
            candidateCatalog);
    }

    [Fact]
    public async Task CandidateIdentityClosureRejectsOperatorCoreClrCommitClaims()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);
        catalog = CandidateReleaseMaterializer.CreateCatalog(
            catalog,
            releaseLock,
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        const string runtimeId = "mono-6.12-linux-x64";
        var candidateCatalog = catalog with
        {
            Runtimes = catalog.Runtimes
                .Select(runtime => runtime.Id == runtimeId
                    ? runtime with { RuntimeCommit = new string('a', 40) }
                    : runtime)
                .ToArray()
        };

        var exception = Assert.Throws<ProfileUpdateValidationException>(() =>
            CandidateReleaseMaterializer.ValidateIdentityClosure(releaseLock, candidateCatalog));

        Assert.Contains("not-applicable", exception.Message, StringComparison.Ordinal);
        Assert.Contains(runtimeId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateIdentityClosureRejectsSelectableMonoWithoutOperatorDigest()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);
        catalog = CandidateReleaseMaterializer.CreateCatalog(
            catalog,
            releaseLock,
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        const string runtimeId = "mono-6.12-linux-x64";
        var mono = catalog.Runtimes.Single(runtime => runtime.Id == runtimeId);
        var candidateCatalog = catalog with
        {
            Runtimes = catalog.Runtimes
                .Select(runtime => runtime.Id == runtimeId
                    ? runtime with
                    {
                        RuntimeCommit = null,
                        JitCommit = null,
                        Availability = new ComponentAvailability
                        {
                            Installed = true,
                            Health = "healthy"
                        }
                    }
                    : runtime)
                .ToArray()
        };
        var components = releaseLock.Components.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        components[runtimeId] = new LockedComponent
        {
            Kind = "runtime",
            ResolvedVersion = mono.ResolvedVersion,
            SourceUri = "docker://example.test/mono"
        };

        var exception = Assert.Throws<ProfileUpdateValidationException>(() =>
            CandidateReleaseMaterializer.ValidateIdentityClosure(
                releaseLock with { Components = components },
                candidateCatalog));

        Assert.Contains("immutable sha256 lock digest", exception.Message, StringComparison.Ordinal);
        Assert.Contains(runtimeId, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("source: { type: arbitrary-url, package: Compiler, policy: latest-stable }")]
    [InlineData("source: { type: nuget, package: Compiler, policy: floating }")]
    [InlineData("source: { type: nuget, package: Compiler, policy: latest-stable, downloadUrl: 'http://unsafe.test/compiler' }")]
    [InlineData("source: { type: nuget, package: Compiler, package: Other.Compiler, policy: latest-stable }")]
    public async Task ResolverRejectsUnsupportedManifestSourcePolicy(string source)
    {
        using var channels = new TempChannelDirectory(
            toolchainsYaml: $$"""
                channels:
                  - id: compiler
                    kind: toolchain
                    {{source}}
                update:
                  pollInterval: 6h
                  autoPromoteAfterTests: false
                """);
        var updater = new ReleaseLockUpdater(new FakeProfileSourceClient(), channels.Root);
        var current = new ReleaseLockDocument
        {
            SchemaVersion = 1,
            ReleaseId = "development",
            ResolvedAt = DateTimeOffset.UnixEpoch,
            Components = new Dictionary<string, LockedComponent>()
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => updater.ResolveAsync(
            current,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolverRejectsDuplicateManifestOutputIds()
    {
        using var channels = new TempChannelDirectory(toolchainsYaml: """
            channels:
              - id: runtime
                kind: toolchain
                source: { type: nuget, package: Compiler, policy: latest-stable }
            update:
              pollInterval: 6h
              autoPromoteAfterTests: false
            """);
        var updater = new ReleaseLockUpdater(new FakeProfileSourceClient(), channels.Root);
        var current = new ReleaseLockDocument
        {
            SchemaVersion = 1,
            ReleaseId = "development",
            ResolvedAt = DateTimeOffset.UnixEpoch,
            Components = new Dictionary<string, LockedComponent>()
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => updater.ResolveAsync(
            current,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("duplicated", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolverProducesAnExactCandidateWithoutMutatingTheInputLock()
    {
        var current = new ReleaseLockDocument
        {
            SchemaVersion = 1,
            ReleaseId = "development",
            ResolvedAt = DateTimeOffset.UnixEpoch,
            Components = new Dictionary<string, LockedComponent>
            {
                ["dotnet-11-preview-linux-x64"] = new()
                {
                    Kind = "runtime",
                    ResolvedVersion = "11.0.0-preview.4"
                },
                ["frontend-react"] = new()
                {
                    Kind = "frontend",
                    ResolvedVersion = "19.2.7"
                },
                ["const-generics-versiontools"] = new()
                {
                    Kind = "build-dependency",
                    ResolvedVersion = "8.0.0-beta.23516.4",
                    Package = "Microsoft.DotNet.VersionTools.Tasks",
                    SourceUri = "https://example.test/microsoft.dotnet.versiontools.tasks.nupkg",
                    Digest = $"sha256:{new string('d', 64)}"
                },
                ["roslyn-const-generics"] = new()
                {
                    Kind = "toolchain",
                    ResolvedVersion = "4.8.0-const-generics.bcd209abd947",
                    PatchDigest = $"sha256:{new string('e', 64)}"
                }
            }
        };
        var updater = new ReleaseLockUpdater(
            new FakeProfileSourceClient(),
            Path.Combine(FindRepositoryRoot(), "profiles", "channels"));

        var result = await updater.ResolveAsync(
            current,
            "candidate-1",
            TestContext.Current.CancellationToken);

        Assert.Equal("11.0.0-preview.5", result.Candidate.Components["dotnet-11-preview-linux-x64"].ResolvedVersion);
        Assert.Equal(
            "f7b4c5716faaee8fb8a289aed29118cad955c45f",
            result.Candidate.Components["dotnet-11-preview-linux-x64"].Commit);
        Assert.Equal(
            "f7b4c5716faaee8fb8a289aed29118cad955c45f",
            result.Candidate.Components["dotnet-11-preview-linux-x64"].JitCommit);
        Assert.Equal(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            result.Candidate.Components["roslyn-main"].Commit);
        Assert.Equal("5.10.0", result.Candidate.Components["roslyn-main"].ResolvedVersion);
        Assert.Equal(
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            result.Candidate.Components["roslyn-main"].Digest);
        Assert.Equal(
            "https://github.com/dotnet/roslyn/archive/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tar.gz",
            result.Candidate.Components["roslyn-main"].SourceUri);
        Assert.Equal("Microsoft.NETCore.App.Ref", result.Candidate.Components["net10-ref"].Package);
        Assert.Equal("0.1.0", result.Candidate.Components["mobius-ilasm-stable"].ResolvedVersion);
        Assert.Equal("Mobius.ILasm", result.Candidate.Components["mobius-ilasm-stable"].Package);
        Assert.Equal("10.1.204", result.Candidate.Components["fsharp-core"].ResolvedVersion);
        Assert.Equal("1.1.13", result.Candidate.Components["peachpie-stable"].ResolvedVersion);
        Assert.Equal(
            "608bf30cf3f43f97e32825076a2cfdaa25043e50",
            result.Candidate.Components["peachpie-stable"].Commit);
        Assert.Equal("Peachpie.Runtime", result.Candidate.Components["peachpie-runtime"].Package);
        Assert.Equal("Peachpie.Library", result.Candidate.Components["peachpie-library"].Package);
        Assert.Equal("Microsoft.ILVerification", result.Candidate.Components["dotnet-ilverify"].Package);
        Assert.Equal("0.3.33", result.Candidate.Components["gsharp-stable"].ResolvedVersion);
        Assert.Equal(
            "aaf35bb8d5e1e8704e982ad0ab95263451bd2d3d",
            result.Candidate.Components["gsharp-stable"].Commit);
        Assert.Equal(
            "sha256:f52d21ef09b198bad69b7ac8dd5f6d2eaa91216b80bfc22e9610a1fef28f06d4",
            result.Candidate.Components["gsharp-source"].Digest);
        Assert.DoesNotContain("frontend-react", result.Candidate.Components);
        Assert.Equal(
            "8.0.0-beta.23516.4",
            result.Candidate.Components["const-generics-versiontools"].ResolvedVersion);
        Assert.Equal(
            $"sha256:{new string('d', 64)}",
            result.Candidate.Components["const-generics-versiontools"].Digest);
        Assert.Null(result.Candidate.Components["roslyn-const-generics"].PatchDigest);
        Assert.Equal("11.0.0-preview.4", current.Components["dotnet-11-preview-linux-x64"].ResolvedVersion);
        Assert.Contains(result.Changes, static change => change.ComponentId == "dotnet-11-preview-linux-x64");
        Assert.Contains(result.Changes, static change =>
            change.ComponentId == "frontend-react" && change.NewVersion is null);
        Assert.Contains(result.Changes, static change => change.ComponentId == "roslyn-const-generics");
    }

    [Fact]
    public async Task ResolveStoresCandidateByContentDigestAndWritesReceipt()
    {
        using var repository = new TempRepository();
        var runner = new RecordingCommandRunner();
        var workflow = repository.CreateWorkflow(runner);

        var result = await workflow.ResolveAsync(
            "candidate-1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            result.CandidateDigest,
            Digest(await File.ReadAllBytesAsync(result.CandidatePath, TestContext.Current.CancellationToken)));
        Assert.EndsWith(
            Path.Combine(result.CandidateDigest[7..], "lock.json"),
            result.CandidatePath,
            StringComparison.Ordinal);
        Assert.Equal(repository.ActiveDigest, result.Receipt.SourceDigest);
        Assert.Equal("candidate-1", result.Receipt.ReleaseId);
        Assert.Equal(ProfileUpdateStage.Resolve, Assert.Single(result.Receipt.Stages).Stage);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(result.CandidatePath)!, "receipt.json")));
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(result.CandidatePath)!, "workspace")));
        Assert.True(File.Exists(Path.Combine(
            Path.GetDirectoryName(result.CandidatePath)!,
            "workspace",
            "profiles",
            "versions.props")));
        var workspaceRoot = Path.Combine(Path.GetDirectoryName(result.CandidatePath)!, "workspace");
        var generatedJsonPaths = new List<string>
        {
            result.CandidatePath,
            Path.Combine(Path.GetDirectoryName(result.CandidatePath)!, "receipt.json"),
            Path.Combine(workspaceRoot, "profiles", "catalog", "catalog.json"),
            Path.Combine(repository.StateRoot, "state.json"),
            repository.PublicStatusPath
        };
        generatedJsonPaths.AddRange(Directory.EnumerateFiles(
            Path.Combine(workspaceRoot, "profiles", "runtimes"),
            "*.json",
            SearchOption.TopDirectoryOnly));
        foreach (var generatedJsonPath in generatedJsonPaths)
            await AssertUtf8NoBomLfAsync(generatedJsonPath);
        var resolvedLock = await CatalogLoader.LoadReleaseLockAsync(
            result.CandidatePath,
            TestContext.Current.CancellationToken);
        var materializedCatalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(workspaceRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            resolvedLock.Components["roslyn-stable-netfx48"].ResolvedVersion,
            materializedCatalog.Toolchains.Single(static item =>
                item.Id == "roslyn-stable-netfx48").ResolvedVersion);
        Assert.Equal(
            $"Roslyn Stable {resolvedLock.Components["roslyn-stable-netfx48"].ResolvedVersion} / .NET Framework",
            materializedCatalog.Toolchains.Single(static item =>
                item.Id == "roslyn-stable-netfx48").DisplayName);
        Assert.Equal(
            resolvedLock.Components["gsharp-stable"].ResolvedVersion,
            materializedCatalog.Toolchains.Single(static item =>
                item.Id == "gsharp-stable").DisplayName);
        Assert.Equal(
            resolvedLock.Components["roslyn-stable"] with { PatchDigest = null, ImageId = null },
            resolvedLock.Components["roslyn-stable-netfx48"]);
        var frameworkReferenceSetIds = materializedCatalog.Toolchains
            .Single(static item => item.Id == "roslyn-stable-netfx48")
            .AllowedReferenceSetIds;
        Assert.Equal(14, frameworkReferenceSetIds.Count);
        foreach (var referenceSetId in frameworkReferenceSetIds)
        {
            Assert.Equal(
                ReferenceSetIdentityResolver.ResolveLockedDigest(
                    resolvedLock.Components[referenceSetId],
                    referenceSetId),
                materializedCatalog.ReferenceSets.Single(item => item.Id == referenceSetId).Digest);
        }
        Assert.Equal(
            ".NET 10",
            materializedCatalog.ReferenceSets.Single(static item => item.Id == "net10-ref").DisplayName);
        Assert.Equal(
            ".NET Main",
            materializedCatalog.ReferenceSets.Single(static item => item.Id == "net11-preview-ref").DisplayName);
        Assert.Equal(
            "Const Generics",
            materializedCatalog.ReferenceSets.Single(static item => item.Id == "const-generics-ref").DisplayName);
        Assert.Equal(
            ".NET Framework 4.8",
            materializedCatalog.ReferenceSets.Single(static item => item.Id == "netfx48-ref").DisplayName);
        Assert.Equal(
            ".NET 10",
            materializedCatalog.Runtimes.Single(static item => item.Id == "dotnet-10-linux-x64").DisplayName);
        Assert.Equal(
            ".NET Main",
            materializedCatalog.Runtimes.Single(static item => item.Id == "dotnet-11-preview-linux-x64").DisplayName);
        var versions = XDocument.Load(Path.Combine(workspaceRoot, "profiles", "versions.props"));
        var properties = versions.Root!.Element("PropertyGroup")!.Elements()
            .ToDictionary(element => element.Name.LocalName, element => element.Value, StringComparer.Ordinal);
        Assert.Equal(
            resolvedLock.Components["const-generics-ilspy-source"].Commit,
            properties["ConstGenericsIlSpyCommit"]);
        Assert.Equal(
            resolvedLock.Components["const-generics-runtime-source"].Commit,
            properties["ConstGenericsRuntimeCommit"]);
        Assert.Equal(
            resolvedLock.Components["const-generics-ref"].ResolvedVersion,
            properties["ConstGenericsReferenceVersion"]);
        Assert.Equal(resolvedLock.Components["peachpie-stable"].ResolvedVersion, properties["PeachPieVersion"]);
        Assert.Equal(resolvedLock.Components["peachpie-stable"].Commit, properties["PeachPieCommit"]);
        Assert.Equal("$(ConstGenericsIlSpyCommit)", properties["ConstGenericsIlSpyProcessorVersion"]);
        var artifactProcessorVersion = resolvedLock.Components["artifacts-const-generics"].ResolvedVersion;
        var processorVersionPrefix =
            $"{resolvedLock.Components["const-generics-ilspy-source"].Commit![..12]}-" +
            $"{resolvedLock.Components["const-generics-runtime-source"].Commit![..12]}-";
        Assert.StartsWith(processorVersionPrefix, artifactProcessorVersion, StringComparison.Ordinal);
        Assert.Equal(
            $"$(ConstGenericsRuntimeCommit)+{artifactProcessorVersion[processorVersionPrefix.Length..]}",
            properties["ConstGenericsVerificationProcessorVersion"]);
        await using var runtimeProfileStream = File.OpenRead(Path.Combine(
            workspaceRoot,
            "profiles",
            "runtimes",
            "const-generics-linux-x64.json"));
        using var runtimeProfile = await JsonDocument.ParseAsync(
            runtimeProfileStream,
            cancellationToken: TestContext.Current.CancellationToken);
        var runtime = resolvedLock.Components["const-generics-linux-x64"];
        var expectedRuntimeImage = $"sharplabnext/runtime-const-generics:{result.Receipt.ReleaseId}";
        Assert.Equal(runtime.ResolvedVersion, runtimeProfile.RootElement.GetProperty("runtimeVersion").GetString());
        Assert.Equal(runtime.Commit, runtimeProfile.RootElement.GetProperty("runtimeCommit").GetString());
        Assert.Equal(runtime.ResolvedVersion, runtimeProfile.RootElement.GetProperty("jitVersion").GetString());
        Assert.Equal(runtime.JitCommit, runtimeProfile.RootElement.GetProperty("jitCommit").GetString());
        Assert.Equal(expectedRuntimeImage, runtimeProfile.RootElement.GetProperty("image").GetString());
        Assert.Equal(expectedRuntimeImage, runtimeProfile.RootElement.GetProperty("runtimeImageId").GetString());
        foreach (var runtimeProfileId in ActiveRuntimeProfileIds)
        {
            using var activeProfile = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(workspaceRoot, "profiles", "runtimes", $"{runtimeProfileId}.json"),
                TestContext.Current.CancellationToken));
            var component = resolvedLock.Components[runtimeProfileId];
            var root = activeProfile.RootElement;
            Assert.Equal(runtimeProfileId, root.GetProperty("id").GetString());
            Assert.Equal(component.ResolvedVersion, root.GetProperty("runtimeVersion").GetString());
            Assert.EndsWith($":{result.Receipt.ReleaseId}", root.GetProperty("image").GetString(), StringComparison.Ordinal);
            Assert.Equal(
                root.GetProperty("image").GetString(),
                root.GetProperty("runtimeImageId").GetString());
            if (component.Commit is not null)
                Assert.Equal(component.Commit, root.GetProperty("runtimeCommit").GetString());
            if (component.JitCommit is not null)
            {
                Assert.Equal(component.ResolvedVersion, root.GetProperty("jitVersion").GetString());
                Assert.Equal(component.JitCommit, root.GetProperty("jitCommit").GetString());
            }
        }
        var validationCompose = await File.ReadAllTextAsync(
            Path.Combine(
                Path.GetDirectoryName(result.CandidatePath)!,
                "workspace",
                "artifacts",
                "profile-candidate",
                "compose.validation.yaml"),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            1,
            validationCompose.Split("  runtime-supervisor:", StringSplitOptions.None).Length - 1);
        Assert.Contains(
            $"RuntimeSupervisor__ResourceScope: \"{result.CandidateDigest}\"",
            validationCompose,
            StringComparison.Ordinal);
        Assert.Contains("  worker-roslyn-netfx48:", validationCompose, StringComparison.Ordinal);
        Assert.Contains("  worker-jsharp:", validationCompose, StringComparison.Ordinal);
        var validationEndpoints = JsonSerializer.Deserialize<CandidateValidationEndpoints>(
            await File.ReadAllTextAsync(
                Path.Combine(
                    Path.GetDirectoryName(result.CandidatePath)!,
                    "workspace",
                    "artifacts",
                    "profile-candidate",
                    "endpoints.json"),
                TestContext.Current.CancellationToken),
            WebJsonOptions);
        Assert.NotNull(validationEndpoints);
        Assert.Contains("roslyn-stable-netfx48", validationEndpoints.Services.Keys);
        Assert.Contains("vjc-jsharp20", validationEndpoints.Services.Keys);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ResolvePublishesSanitizedOperationalStatus()
    {
        using var repository = new TempRepository();
        var workflow = repository.CreateWorkflow(new RecordingCommandRunner());

        var candidate = await workflow.ResolveAsync(
            "candidate-public",
            cancellationToken: TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(
            repository.PublicStatusPath,
            TestContext.Current.CancellationToken);
        var status = JsonSerializer.Deserialize<ProfileUpdateStatusDocument>(
            json,
            WebJsonOptions);
        Assert.NotNull(status);
        Assert.Equal(ProfileUpdateStatusKind.CandidateInProgress, status.Status);
        Assert.True(status.Checked);
        Assert.True(status.UpdateAvailable);
        Assert.Equal("development", status.Active.ReleaseId);
        Assert.Equal(repository.ActiveDigest, status.Active.LockDigest);
        Assert.Equal("development", status.LastKnownGood?.ReleaseId);
        Assert.Equal(repository.ActiveDigest, status.LastKnownGood?.LockDigest);
        Assert.Equal("candidate-public", status.Candidate?.ReleaseId);
        Assert.Equal(candidate.CandidateDigest, status.Candidate?.LockDigest);
        Assert.Equal(ProfileUpdatePublicStage.Resolve, status.LastStage.Stage);
        Assert.Equal(ProfileUpdatePublicStageOutcome.Succeeded, status.LastStage.Outcome);
        Assert.Null(status.LastStage.Error);
        Assert.DoesNotContain(repository.Root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commands", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arguments", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspacePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidatePath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildUsesRuntimeArgumentsFromCandidateLock()
    {
        using var repository = new TempRepository();
        var runner = new RecordingCommandRunner();
        var workflow = repository.CreateWorkflow(runner);
        var candidate = await workflow.ResolveAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await workflow.BuildAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            TestContext.Current.CancellationToken);

        Assert.Equal(9, runner.Commands.Count);
        Assert.Contains("eng/verify-ilsense-inputs.cs", runner.Commands[0].Arguments);
        Assert.Contains("--verify-restore", runner.Commands[0].Arguments);
        Assert.Equal(["run", "eng/verify-buildkit.cs"], runner.Commands[1].Arguments);
        Assert.Contains("--force-evaluate", runner.Commands[2].Arguments);
        Assert.Equal(["restore", "SharpLabNext.slnx", "--locked-mode"], runner.Commands[3].Arguments);
        var bake = Assert.Single(runner.Commands, static command => command.FileName == "docker");
        Assert.DoesNotContain("--load", bake.Arguments);
        Assert.NotNull(bake.Environment);
        var resolvedLock = await CatalogLoader.LoadReleaseLockAsync(
            candidate.CandidatePath,
            TestContext.Current.CancellationToken);
        Assert.Equal("10.0.9", bake.Environment["DOTNET10_RUNTIME_VERSION"]);
        Assert.Equal("901ca941248413c79832d2fdbd709da0c4386353", bake.Environment["DOTNET10_RUNTIME_COMMIT"]);
        Assert.Equal("901ca941248413c79832d2fdbd709da0c4386353", bake.Environment["DOTNET10_JIT_COMMIT"]);
        Assert.Equal("https://example.test/dotnet-10.0.9.tar.gz", bake.Environment["DOTNET10_RUNTIME_URL"]);
        Assert.Equal(new string('a', 128), bake.Environment["DOTNET10_RUNTIME_SHA512"]);
        Assert.Equal("11.0.0-preview.5", bake.Environment["DOTNET11_RUNTIME_VERSION"]);
        Assert.Equal("f7b4c5716faaee8fb8a289aed29118cad955c45f", bake.Environment["DOTNET11_RUNTIME_COMMIT"]);
        Assert.Equal("f7b4c5716faaee8fb8a289aed29118cad955c45f", bake.Environment["DOTNET11_JIT_COMMIT"]);
        Assert.Equal("https://example.test/dotnet-11.0.0-preview.5.tar.gz", bake.Environment["DOTNET11_RUNTIME_URL"]);
        Assert.Equal(new string('a', 128), bake.Environment["DOTNET11_RUNTIME_SHA512"]);
        Assert.Equal(
            resolvedLock.Components["jit-profiler-clr-samples"].Commit,
            bake.Environment["JIT_PROFILER_CLR_SAMPLES_COMMIT"]);
        Assert.Equal(
            resolvedLock.Components["jit-profiler-clr-samples"].SourceUri,
            bake.Environment["JIT_PROFILER_CLR_SAMPLES_SOURCE_URI"]);
        Assert.Equal(
            resolvedLock.Components["jit-profiler-runtime-headers"].Commit,
            bake.Environment["JIT_PROFILER_RUNTIME_HEADERS_COMMIT"]);
        Assert.Equal(
            resolvedLock.Components["jit-profiler-runtime-headers"].SourceUri,
            bake.Environment["JIT_PROFILER_RUNTIME_HEADERS_SOURCE_URI"]);
        Assert.Equal("5.6.0", bake.Environment["ROSLYN_STABLE_VERSION"]);
        Assert.Equal("5.10.0", bake.Environment["ROSLYN_MAIN_VERSION"]);
        Assert.Equal(new string('a', 40), bake.Environment["ROSLYN_MAIN_COMMIT"]);
        Assert.Equal("43.12.204", bake.Environment["FSHARP_COMPILER_SERVICE_VERSION"]);
        Assert.Equal("10.1.204", bake.Environment["FSHARP_CORE_VERSION"]);
        Assert.Equal("0.3.33", bake.Environment["GSHARP_VERSION"]);
        Assert.Equal(
            "aaf35bb8d5e1e8704e982ad0ab95263451bd2d3d",
            bake.Environment["GSHARP_COMMIT"]);
        Assert.Equal(
            "f52d21ef09b198bad69b7ac8dd5f6d2eaa91216b80bfc22e9610a1fef28f06d4",
            bake.Environment["GSHARP_ARCHIVE_SHA256"]);
        Assert.Equal("0.1.0", bake.Environment["ILSENSE_VERSION"]);
        Assert.Equal(
            "a2253dd77d052e02f654908e5ecb60b6602be782",
            bake.Environment["ILSENSE_COMMIT"]);
        Assert.Equal(
            "5c42956f5a423f44ed1e22526ed11d7a30af127be5f90d0700c06bb13db9ca00",
            bake.Environment["ILSENSE_ARCHIVE_SHA256"]);
        Assert.Equal("1.1.13", bake.Environment["PEACHPIE_CODEANALYSIS_VERSION"]);
        Assert.Equal("1.1.13", bake.Environment["PEACHPIE_RUNTIME_VERSION"]);
        Assert.Equal("1.1.13", bake.Environment["PEACHPIE_LIBRARY_VERSION"]);
        Assert.Equal(
            "608bf30cf3f43f97e32825076a2cfdaa25043e50",
            bake.Environment["PEACHPIE_COMMIT"]);
        Assert.EndsWith(
            "/608bf30cf3f43f97e32825076a2cfdaa25043e50/LICENSE.txt",
            bake.Environment["PEACHPIE_LICENSE_URL"],
            StringComparison.Ordinal);
        Assert.Equal(
            "sha512-Q1XzhqGM3cR1FW5hWh7JIfjCCNtmNM1u0HW1nM0UCyl4X5MM7cM9dxeBjmWETIumKpP/8yj19WXF0wRmfQgaew==",
            bake.Environment["PEACHPIE_CODEANALYSIS_PACKAGE_CONTENT_HASH"]);
        Assert.Equal(new string('e', 128), bake.Environment["PEACHPIE_RUNTIME_SHA512"]);
        var cppCliOperator = resolvedLock.Components["msvc-cppcli-private-image"];
        var cppCliPreparedBase = resolvedLock.Components["msvc-cppcli-prepared-base"];
        Assert.Equal(cppCliOperator.ResolvedVersion, bake.Environment["CPPCLI_PRIVATE_IMAGE_VERSION"]);
        Assert.Equal(cppCliOperator.Digest, bake.Environment["CPPCLI_PRIVATE_IMAGE_DIGEST"]);
        Assert.Equal(cppCliOperator.SourceUri, bake.Environment["CPPCLI_PRIVATE_IMAGE_SOURCE_URI"]);
        Assert.Equal(
            cppCliPreparedBase.SourceUri!["docker://".Length..],
            bake.Environment["CPPCLI_PREPARED_BASE_IMAGE"]);
        Assert.Equal(
            cppCliPreparedBase.ResolvedVersion,
            bake.Environment["CPPCLI_PREPARED_BASE_VERSION"]);
        Assert.Equal(cppCliPreparedBase.Digest, bake.Environment["CPPCLI_PREPARED_BASE_DIGEST"]);
        Assert.Equal(
            cppCliPreparedBase.SourceUri,
            bake.Environment["CPPCLI_PREPARED_BASE_SOURCE_URI"]);
        var jsharpOperator = resolvedLock.Components["jsharp20"];
        var jsharpPreparedBase = resolvedLock.Components["jsharp20-prepared-base"];
        var jsharpCompiler = resolvedLock.Components["vjc-jsharp20"];
        var jsharpReference = resolvedLock.Components["jsharp20-ref"];
        var jsharpRuntime = resolvedLock.Components["wine-jsharp20-linux-x64"];
        Assert.Equal(
            jsharpOperator.SourceUri!["docker://".Length..],
            bake.Environment["JSHARP_TOOLCHAIN_IMAGE"]);
        Assert.Equal(jsharpOperator.ResolvedVersion, bake.Environment["JSHARP_TOOLCHAIN_VERSION"]);
        Assert.Equal(jsharpCompiler.ResolvedVersion, bake.Environment["JSHARP_COMPILER_VERSION"]);
        Assert.Equal(jsharpOperator.Digest, bake.Environment["JSHARP_TOOLCHAIN_DIGEST"]);
        Assert.Equal(jsharpOperator.SourceUri, bake.Environment["JSHARP_TOOLCHAIN_SOURCE_URI"]);
        Assert.Equal(
            jsharpPreparedBase.SourceUri!["docker://".Length..],
            bake.Environment["JSHARP_WINE_BASE_IMAGE"]);
        Assert.Equal(
            jsharpPreparedBase.ResolvedVersion,
            bake.Environment["JSHARP_WINE_BASE_VERSION"]);
        Assert.Equal(jsharpPreparedBase.Digest, bake.Environment["JSHARP_WINE_BASE_DIGEST"]);
        Assert.Equal(jsharpPreparedBase.SourceUri, bake.Environment["JSHARP_WINE_BASE_SOURCE_URI"]);
        Assert.Equal(jsharpReference.ResolvedVersion, bake.Environment["JSHARP_REFERENCE_VERSION"]);
        Assert.Equal(jsharpReference.Digest, bake.Environment["JSHARP_REFERENCE_DIGEST"]);
        Assert.Equal(jsharpReference.SourceUri, bake.Environment["JSHARP_REFERENCE_SOURCE_URI"]);
        Assert.Equal(jsharpRuntime.ResolvedVersion, bake.Environment["WINE_JSHARP20_RUNTIME_VERSION"]);
        Assert.Equal(jsharpRuntime.Digest, bake.Environment["WINE_JSHARP20_RUNTIME_DIGEST"]);
        Assert.Equal(jsharpRuntime.SourceUri, bake.Environment["WINE_JSHARP20_RUNTIME_SOURCE_URI"]);
        Assert.Equal("10.1.0.8386", bake.Environment["ILSPY_VERSION"]);
        Assert.Equal("10.0.9", bake.Environment["ILVERIFICATION_VERSION"]);
        Assert.Equal("10.0.9", bake.Environment["NET10_REFERENCE_PACK_VERSION"]);
        Assert.Equal(new string('d', 128), bake.Environment["NET10_REFERENCE_SHA512"]);
        Assert.Equal("sha512-test-content-hash", bake.Environment["NET10_REFERENCE_PACKAGE_CONTENT_HASH"]);
        Assert.Equal("11.0.0-preview.5", bake.Environment["NET11_REFERENCE_VERSION"]);
        Assert.Equal(new string('d', 128), bake.Environment["NET11_REFERENCE_SHA512"]);
        Assert.Equal("sha512-test-content-hash", bake.Environment["NET11_REFERENCE_PACKAGE_CONTENT_HASH"]);
        foreach (var (referenceSetId, versionVariable, prefix) in new (string ReferenceSetId, string VersionVariable, string Prefix)[]
        {
            ("netcoreapp2.0-ref", "NETCOREAPP20_REFERENCE_VERSION", "NETCOREAPP20"),
            ("netcoreapp2.1-ref", "NETCOREAPP21_REFERENCE_VERSION", "NETCOREAPP21"),
            ("netcoreapp2.2-ref", "NETCOREAPP22_REFERENCE_VERSION", "NETCOREAPP22"),
            ("netcoreapp3.0-ref", "NETCOREAPP30_REFERENCE_VERSION", "NETCOREAPP30"),
            ("netcoreapp3.1-ref", "NETCOREAPP31_REFERENCE_VERSION", "NETCOREAPP31"),
            ("net5-ref", "NET5_REFERENCE_VERSION", "NET5"),
            ("net6-ref", "NET6_REFERENCE_VERSION", "NET6"),
            ("net7-ref", "NET7_REFERENCE_VERSION", "NET7"),
            ("net8-ref", "NET8_REFERENCE_VERSION", "NET8"),
            ("net9-ref", "NET9_REFERENCE_VERSION", "NET9"),
            ("net10-ref", "NET10_REFERENCE_PACK_VERSION", "NET10"),
            ("net11-preview-ref", "NET11_REFERENCE_VERSION", "NET11")
        })
        {
            var component = resolvedLock.Components[referenceSetId];
            Assert.Equal(component.ResolvedVersion, bake.Environment[versionVariable]);
            Assert.Equal(component.SourceUri, bake.Environment[$"{prefix}_REFERENCE_SOURCE_URI"]);
            Assert.Equal(component.Sha512, bake.Environment[$"{prefix}_REFERENCE_SHA512"]);
            Assert.Equal(
                component.PackageContentHash,
                bake.Environment[$"{prefix}_REFERENCE_PACKAGE_CONTENT_HASH"]);
        }
        var netfx48ManagedReference = resolvedLock.Components["netfx48-managed-ref"];
        Assert.Equal(
            netfx48ManagedReference.ResolvedVersion,
            bake.Environment["NETFX48_MANAGED_REFERENCE_VERSION"]);
        Assert.Equal(
            netfx48ManagedReference.SourceUri,
            bake.Environment["NETFX48_MANAGED_REFERENCE_URL"]);
        Assert.Equal(
            netfx48ManagedReference.SourceUri,
            bake.Environment["NETFX48_MANAGED_REFERENCE_SOURCE_URI"]);
        Assert.Equal(
            netfx48ManagedReference.Sha512,
            bake.Environment["NETFX48_MANAGED_REFERENCE_SHA512"]);
        Assert.Equal(
            netfx48ManagedReference.PackageContentHash,
            bake.Environment["NETFX48_MANAGED_REFERENCE_PACKAGE_CONTENT_HASH"]);
        foreach (var (referenceSetId, variable) in new (string ReferenceSetId, string Variable)[]
        {
            ("netfx20-managed-ref", "NETFX20_MANAGED_REFERENCE_DIGEST"),
            ("netfx30-managed-ref", "NETFX30_MANAGED_REFERENCE_DIGEST"),
            ("netfx35-managed-ref", "NETFX35_MANAGED_REFERENCE_DIGEST"),
            ("netfx40-managed-ref", "NETFX40_MANAGED_REFERENCE_DIGEST"),
            ("netfx45-managed-ref", "NETFX45_MANAGED_REFERENCE_DIGEST"),
            ("netfx451-managed-ref", "NETFX451_MANAGED_REFERENCE_DIGEST"),
            ("netfx452-managed-ref", "NETFX452_MANAGED_REFERENCE_DIGEST"),
            ("netfx46-managed-ref", "NETFX46_MANAGED_REFERENCE_DIGEST"),
            ("netfx461-managed-ref", "NETFX461_MANAGED_REFERENCE_DIGEST"),
            ("netfx462-managed-ref", "NETFX462_MANAGED_REFERENCE_DIGEST"),
            ("netfx47-managed-ref", "NETFX47_MANAGED_REFERENCE_DIGEST"),
            ("netfx471-managed-ref", "NETFX471_MANAGED_REFERENCE_DIGEST"),
            ("netfx472-managed-ref", "NETFX472_MANAGED_REFERENCE_DIGEST"),
            ("netfx48-managed-ref", "NETFX48_MANAGED_REFERENCE_DIGEST")
        })
        {
            var component = resolvedLock.Components[referenceSetId];
            var prefix = variable[..^"_DIGEST".Length];
            Assert.Equal(
                ReferenceSetIdentityResolver.ResolveLockedDigest(
                    component,
                    referenceSetId),
                bake.Environment[variable]);
            Assert.Equal(component.ResolvedVersion, bake.Environment[$"{prefix}_VERSION"]);
            if (component.SourceUri is null)
                Assert.DoesNotContain($"{prefix}_SOURCE_URI", bake.Environment.Keys);
            else
                Assert.Equal(component.SourceUri, bake.Environment[$"{prefix}_SOURCE_URI"]);
        }
        var constGenericsReference = resolvedLock.Components["const-generics-ref"];
        Assert.Equal(
            constGenericsReference.ResolvedVersion,
            bake.Environment["CONST_GENERICS_REFERENCE_VERSION"]);
        Assert.Equal(constGenericsReference.Digest, bake.Environment["CONST_GENERICS_REFERENCE_DIGEST"]);
        var constGenericsRuntimeSource = resolvedLock.Components["const-generics-runtime-source"];
        Assert.Equal(
            constGenericsRuntimeSource.Commit,
            bake.Environment["CONST_GENERICS_RUNTIME_COMMIT"]);
        Assert.Equal(
            constGenericsRuntimeSource.Digest![7..],
            bake.Environment["CONST_GENERICS_RUNTIME_ARCHIVE_SHA256"]);
        var versionTools = resolvedLock.Components["const-generics-versiontools"];
        Assert.Equal(versionTools.ResolvedVersion, bake.Environment["CONST_GENERICS_VERSIONTOOLS_VERSION"]);
        Assert.Equal(
            versionTools.Digest![7..],
            bake.Environment["CONST_GENERICS_VERSIONTOOLS_PACKAGE_SHA256"]);
        var constGenericsRoslyn = resolvedLock.Components["roslyn-const-generics"];
        Assert.Equal(
            constGenericsRoslyn.Commit,
            bake.Environment["CONST_GENERICS_ROSLYN_COMMIT"]);
        Assert.Equal(
            constGenericsRoslyn.ResolvedVersion.Split("-const-generics.", StringSplitOptions.None)[0],
            bake.Environment["CONST_GENERICS_ROSLYN_VERSION"]);
        Assert.Equal(
            constGenericsRoslyn.ResolvedVersion,
            bake.Environment["CONST_GENERICS_ROSLYN_COMPONENT_VERSION"]);
        Assert.Equal(
            resolvedLock.Components["artifacts-const-generics"].Commit,
            bake.Environment["CONST_GENERICS_ILSPY_COMMIT"]);
        Assert.Equal(
            resolvedLock.Components["artifacts-default"].ResolvedVersion,
            bake.Environment["ARTIFACTS_DEFAULT_VERSION"]);
        Assert.Equal(
            resolvedLock.Components["artifacts-const-generics"].ResolvedVersion,
            bake.Environment["ARTIFACTS_CONST_GENERICS_VERSION"]);
        Assert.Equal(
            resolvedLock.Components["il-assembler"].ResolvedVersion,
            bake.Environment["IL_ASSEMBLER_VERSION"]);
        Assert.Equal(
            resolvedLock.Components["minilang-stable"].ResolvedVersion,
            bake.Environment["MINILANG_VERSION"]);
        Assert.Equal("sharplabnext", bake.Environment["IMAGE_PREFIX"]);
        Assert.StartsWith(
            "mcr.microsoft.com/dotnet/sdk:",
            bake.Environment["BASE_DOTNET_SDK_IMAGE"],
            StringComparison.Ordinal);
        Assert.Equal(candidate.Receipt.ReleaseId, bake.Environment["RELEASE_ID"]);
        Assert.StartsWith("candidate-", bake.Environment["SOURCE_REVISION"], StringComparison.Ordinal);
        Assert.Equal(SourceDateEpochResolver.DevelopmentFallbackUnixSeconds, bake.Environment["SOURCE_DATE_EPOCH"]);
        Assert.Equal(ProfileUpdateStageStatus.Succeeded, result.Stage.Status);
    }

    [Fact]
    public async Task BakeEnvironmentCoversEveryDeclaredBakeVariable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var environment = await BakeEnvironmentResolver.CreateAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            Path.Combine(repositoryRoot, "profiles", "base-images.json"),
            "test-source-revision",
            "1700000000",
            cancellationToken: TestContext.Current.CancellationToken);
        var bake = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "eng", "bake.hcl"),
            TestContext.Current.CancellationToken);
        var candidateBake = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "eng", "bake.runtime-candidates.hcl"),
            TestContext.Current.CancellationToken);
        var declaredVariables = Regex.Matches(
                bake,
                "(?m)^variable \\\"(?<name>[A-Z0-9_]+)\\\" \\{$",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(declaredVariables);
        var candidateOnlyVariables = Regex.Matches(
                candidateBake,
                "(?m)^variable \\\"(?<name>RUNTIME_MATRIX_[A-Z0-9_]+)\\\" \\{$",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(candidateOnlyVariables);
        Assert.DoesNotContain(declaredVariables, static name =>
            name.StartsWith("RUNTIME_MATRIX_", StringComparison.Ordinal));
        Assert.Empty(candidateOnlyVariables.Intersect(environment.Keys, StringComparer.Ordinal));
        Assert.Empty(declaredVariables.Except(environment.Keys, StringComparer.Ordinal));
        Assert.All(environment.Values, static value => Assert.False(string.IsNullOrWhiteSpace(value)));
    }

    [Fact]
    public async Task BakeEnvironmentDerivesTheSourceControlledWineUserspaceIdentity()
    {
        var repositoryRoot = FindRepositoryRoot();
        var environment = await BakeEnvironmentResolver.CreateAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            Path.Combine(repositoryRoot, "profiles", "base-images.json"),
            "test-source-revision",
            "1700000000",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "wine-9.0~repack-4build3+xvfb-2:21.1.12-1ubuntu1.6",
            environment["WINE_CORECLR_USERSPACE_VERSION"]);
        Assert.Equal(
            "sha256:4ecfff207a9b13eb6492aa3f9ca01d2d3dd1b713837ef2917524eaf23fa55981",
            environment["WINE_CORECLR_USERSPACE_DIGEST"]);
        Assert.Equal(
            "https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/",
            environment["WINE_CORECLR_USERSPACE_SOURCE_URI"]);

        var bake = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "eng", "bake.hcl"),
            TestContext.Current.CancellationToken);
        var wineOperator = ExtractNamedBlock(bake, "target", "operator-wine-coreclr");
        Assert.Contains(
            "\"org.opencontainers.image.version\" = \"wine-9.0-noble-amd64\"",
            wineOperator,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BakeEnvironmentMarksTheStandaloneWineOperatorAsDevelopmentOnly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var environment = await BakeEnvironmentResolver.CreateAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            Path.Combine(repositoryRoot, "profiles", "base-images.json"),
            "test-source-revision",
            "1700000000",
            cancellationToken: TestContext.Current.CancellationToken);

        // Only build-wine-coreclr-operator.mjs may promote this tuple after it
        // has verified a clean committed source context.
        Assert.Equal("working-tree-development", environment["OPERATOR_SOURCE_CONTEXT"]);
        Assert.Equal("false", environment["OPERATOR_PROMOTION_ELIGIBLE"]);
        Assert.Equal("true", environment["OPERATOR_DEVELOPMENT_ONLY"]);
    }

    [Fact]
    public async Task BakeEnvironmentRejectsWineUserspaceWithAnUnexpectedKind()
    {
        var repositoryRoot = FindRepositoryRoot();
        var lockDocument = JsonSerializer.Deserialize<ReleaseLockDocument>(
            await File.ReadAllTextAsync(
                Path.Combine(repositoryRoot, "profiles", "lock.json"),
                TestContext.Current.CancellationToken),
            WebJsonOptions)
            ?? throw new InvalidOperationException("Repository release lock is invalid.");
        var components = lockDocument.Components.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        components["wine-coreclr-userspace"] = components["wine-coreclr-userspace"] with
        {
            Kind = "operator-image"
        };
        var temporaryLock = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                temporaryLock,
                JsonSerializer.Serialize(lockDocument with { Components = components }, WebJsonOptions),
                TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<BakeEnvironmentValidationException>(() =>
                BakeEnvironmentResolver.CreateAsync(
                    temporaryLock,
                    Path.Combine(repositoryRoot, "profiles", "base-images.json"),
                    "test-source-revision",
                    "1700000000",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("wine-coreclr-userspace.kind", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temporaryLock);
        }
    }

    [Fact]
    public async Task RoslynCoreClrReferenceSetsAreBoundToEveryBuildArgumentAndUseNetStandardRuntimeApi()
    {
        var repositoryRoot = FindRepositoryRoot();
        var bake = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "eng", "bake.hcl"),
            TestContext.Current.CancellationToken);
        var stableTarget = ExtractNamedBlock(bake, "target", "service-with-roslyn-coreclr-reference-sets");
        var mainTarget = ExtractNamedBlock(bake, "target", "worker-roslyn-main");
        var workerDockerfile = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "deploy", "docker", "Dockerfile.worker"),
            TestContext.Current.CancellationToken);
        var mainDockerfile = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "deploy", "docker", "Dockerfile.worker-roslyn-main"),
            TestContext.Current.CancellationToken);

        foreach (var (versionVariable, sourceVariable, prefix, optionPrefix) in new[]
                 {
                     ("NETCOREAPP20_REFERENCE_VERSION", "NETCOREAPP20_REFERENCE_SOURCE_URI", "NETCOREAPP20", "netcoreapp20"),
                     ("NETCOREAPP21_REFERENCE_VERSION", "NETCOREAPP21_REFERENCE_SOURCE_URI", "NETCOREAPP21", "netcoreapp21"),
                     ("NETCOREAPP22_REFERENCE_VERSION", "NETCOREAPP22_REFERENCE_SOURCE_URI", "NETCOREAPP22", "netcoreapp22"),
                     ("NETCOREAPP30_REFERENCE_VERSION", "NETCOREAPP30_REFERENCE_SOURCE_URI", "NETCOREAPP30", "netcoreapp30"),
                     ("NETCOREAPP31_REFERENCE_VERSION", "NETCOREAPP31_REFERENCE_SOURCE_URI", "NETCOREAPP31", "netcoreapp31"),
                     ("NET5_REFERENCE_VERSION", "NET5_REFERENCE_SOURCE_URI", "NET5", "net5"),
                     ("NET6_REFERENCE_VERSION", "NET6_REFERENCE_SOURCE_URI", "NET6", "net6"),
                     ("NET7_REFERENCE_VERSION", "NET7_REFERENCE_SOURCE_URI", "NET7", "net7"),
                     ("NET8_REFERENCE_VERSION", "NET8_REFERENCE_SOURCE_URI", "NET8", "net8"),
                     ("NET9_REFERENCE_VERSION", "NET9_REFERENCE_SOURCE_URI", "NET9", "net9"),
                     ("NET10_REFERENCE_PACK_VERSION", "NET10_REFERENCE_URL", "NET10", "net10"),
                     ("NET11_REFERENCE_VERSION", "NET11_REFERENCE_URL", "NET11", "net11")
                 })
        {
            foreach (var target in new[] { stableTarget, mainTarget })
            {
                Assert.Contains($"{versionVariable} = required({versionVariable})", target, StringComparison.Ordinal);
                Assert.Contains($"{sourceVariable} = required({sourceVariable})", target, StringComparison.Ordinal);
                Assert.Contains(
                    $"{prefix}_REFERENCE_SHA512 = required({prefix}_REFERENCE_SHA512)",
                    target,
                    StringComparison.Ordinal);
                Assert.Contains(
                    $"{prefix}_REFERENCE_PACKAGE_CONTENT_HASH = required({prefix}_REFERENCE_PACKAGE_CONTENT_HASH)",
                    target,
                    StringComparison.Ordinal);
            }

            foreach (var dockerfile in new[] { workerDockerfile, mainDockerfile })
            {
                Assert.Contains($"ARG {versionVariable}", dockerfile, StringComparison.Ordinal);
                Assert.Contains($"ARG {sourceVariable}", dockerfile, StringComparison.Ordinal);
                Assert.Contains($"ARG {prefix}_REFERENCE_SHA512", dockerfile, StringComparison.Ordinal);
                Assert.Contains($"ARG {prefix}_REFERENCE_PACKAGE_CONTENT_HASH", dockerfile, StringComparison.Ordinal);
                Assert.Contains($"--{optionPrefix}-version", dockerfile, StringComparison.Ordinal);
                Assert.Contains($"--{optionPrefix}-url", dockerfile, StringComparison.Ordinal);
                Assert.Contains($"--{optionPrefix}-sha512", dockerfile, StringComparison.Ordinal);
                Assert.Contains($"--{optionPrefix}-content-hash", dockerfile, StringComparison.Ordinal);
            }
        }

        foreach (var dockerfile in new[] { workerDockerfile, mainDockerfile })
        {
            Assert.Contains("src/RuntimeApi/SharpLabNext.Runtime/SharpLabNext.Runtime.csproj", dockerfile, StringComparison.Ordinal);
            Assert.Contains("--framework netstandard2.1", dockerfile, StringComparison.Ordinal);
            Assert.Contains("/app/sharplab-runtime-netstandard21/SharpLab.Runtime.dll", dockerfile, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DevelopmentComposeConsumesPrebuiltRoslynCoreClrImagesWithoutRemotePulls()
    {
        var repositoryRoot = FindRepositoryRoot();
        var compose = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "deploy", "compose.dev.yaml"),
            TestContext.Current.CancellationToken);

        foreach (var (service, nextService) in new[]
                 {
                     ("worker-roslyn-stable", "worker-roslyn-netfx48"),
                     ("worker-roslyn-main", "worker-roslyn-const-generics")
                 })
        {
            var start = compose.IndexOf($"\n  {service}:\n", StringComparison.Ordinal);
            var end = compose.IndexOf($"\n  {nextService}:\n", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, $"Could not locate {service} in development Compose.");
            var block = compose[start..end];
            Assert.DoesNotContain("    build:\n", block, StringComparison.Ordinal);
            Assert.Contains("    pull_policy: never\n", block, StringComparison.Ordinal);
        }

        var composeValidator = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "eng", "validate-compose.mjs"),
            TestContext.Current.CancellationToken);
        Assert.Contains(
            ".filter(key => /^ReferenceSets__/.test(key))",
            composeValidator,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "/^ReferenceSets__.+__Path$/.test(key)",
            composeValidator,
            StringComparison.Ordinal);
        Assert.Contains(
            "must use pull_policy=never for its prebuilt development image",
            composeValidator,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BakeEnvironmentRejectsMissingCoreClrReferenceSet()
    {
        var repositoryRoot = FindRepositoryRoot();
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);
        var components = releaseLock.Components.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        Assert.True(components.Remove("netcoreapp2.0-ref"));

        var exception = Assert.Throws<BakeEnvironmentValidationException>(() =>
            BakeEnvironmentResolver.Create(
                releaseLock with { Components = components },
                Path.Combine(repositoryRoot, "profiles", "base-images.json"),
                "test-source-revision",
                "1700000000"));

        Assert.Contains("netcoreapp2.0-ref", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CppCliProductTargetsConsumePreparedBaseWithoutAliasingRawOperator()
    {
        var repositoryRoot = FindRepositoryRoot();
        var bake = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "eng", "bake.hcl"),
            TestContext.Current.CancellationToken);
        var runtimeTarget = ExtractNamedBlock(bake, "target", "runtime-wine-netfx48");
        var workerTarget = ExtractNamedBlock(bake, "target", "worker-cppcli");

        Assert.Contains(
            "\"cppcli-prepared-base-context\" = \"docker-image://${required(CPPCLI_PREPARED_BASE_IMAGE)}\"",
            runtimeTarget,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"cppcli-prepared-base\" = \"docker-image://${required(CPPCLI_PREPARED_BASE_IMAGE)}\"",
            workerTarget,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CPPCLI_TOOLCHAIN_IMAGE", runtimeTarget, StringComparison.Ordinal);
        Assert.DoesNotContain("CPPCLI_TOOLCHAIN_IMAGE", workerTarget, StringComparison.Ordinal);

        var runtimeDockerfile = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "deploy", "docker", "Dockerfile.runtime-wine-netfx48"),
            TestContext.Current.CancellationToken);
        var workerDockerfile = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "deploy", "docker", "Dockerfile.worker-cppcli"),
            TestContext.Current.CancellationToken);
        Assert.Contains("FROM cppcli-prepared-base-context AS wine-source", runtimeDockerfile, StringComparison.Ordinal);
        Assert.Contains("/usr/local/bin/sharplabnext-service", runtimeDockerfile, StringComparison.Ordinal);
        Assert.Contains("FROM cppcli-prepared-base AS final", workerDockerfile, StringComparison.Ordinal);
        Assert.Contains("rm -rf /app /usr/share/dotnet", workerDockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("CPPCLI_TOOLCHAIN_IMAGE", runtimeDockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("CPPCLI_TOOLCHAIN_IMAGE", workerDockerfile, StringComparison.Ordinal);

        var deployment = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "deploy", "images.json"),
            TestContext.Current.CancellationToken))!;
        var deploymentImages = deployment["images"]!.AsArray();
        foreach (var imageId in new[] { "worker-cppcli", "wine-netfx48-linux-x64" })
        {
            var image = deploymentImages.Single(node =>
                string.Equals(node!["id"]!.GetValue<string>(), imageId, StringComparison.Ordinal))!;
            var componentIds = image["lockComponentIds"]!.AsArray()
                .Select(static node => node!.GetValue<string>());
            Assert.Contains("msvc-cppcli-private-image", componentIds, StringComparer.Ordinal);
            Assert.Contains("msvc-cppcli-prepared-base", componentIds, StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task MatrixBakeTargetsCloseRuntimeComponentAndControlImageIdentity()
    {
        var repositoryRoot = FindRepositoryRoot();
        var bake = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "eng", "bake.runtime-candidates.hcl"),
            TestContext.Current.CancellationToken);

        // Candidate metadata is retained for diagnostics, but promotion uses
        // the profile-ID component labels consumed by BundleBuilder.
        Assert.Contains(
            "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.version",
            bake,
            StringComparison.Ordinal);
        Assert.Contains(
            "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.source-uri",
            bake,
            StringComparison.Ordinal);
        Assert.Contains(
            "io.sharplabnext.runtime.commit",
            bake,
            StringComparison.Ordinal);
        Assert.Contains(
            "io.sharplabnext.jit.commit",
            bake,
            StringComparison.Ordinal);

        var monoTarget = ExtractNamedBlock(bake, "target", "runtime-mono-matrix-candidate");
        Assert.Contains(
            "RUNTIME_COMPONENT_DIGEST = RUNTIME_MATRIX_RUNTIME_DIGEST",
            monoTarget,
            StringComparison.Ordinal);
        Assert.Contains(
            "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.digest",
            monoTarget,
            StringComparison.Ordinal);
        Assert.Contains(
            "io.sharplabnext.control-image",
            monoTarget,
            StringComparison.Ordinal);
        Assert.Contains(
            "io.sharplabnext.operator-image.mono",
            monoTarget,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "io.sharplabnext.base-image.mono",
            monoTarget,
            StringComparison.Ordinal);

        var wineTarget = ExtractNamedBlock(bake, "target", "runtime-wine-dotnet-matrix-candidate");
        Assert.Contains(
            "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.commit",
            wineTarget,
            StringComparison.Ordinal);
        Assert.Contains(
            "io.sharplabnext.control-image",
            wineTarget,
            StringComparison.Ordinal);
        Assert.Contains(
            "io.sharplabnext.operator-image.wine",
            wineTarget,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "io.sharplabnext.base-image.wine",
            wineTarget,
            StringComparison.Ordinal);

        var combinedTarget = ExtractNamedBlock(bake, "target", "runtime-mono-wine-matrix-candidate");
        Assert.Contains(
            "dockerfile = \"deploy/docker/Dockerfile.runtime-mono-wine-matrix\"",
            combinedTarget,
            StringComparison.Ordinal);
        Assert.Contains(
            "MONO_WINE_IMAGE = RUNTIME_MATRIX_MONO_WINE_IMAGE",
            combinedTarget,
            StringComparison.Ordinal);
        Assert.Contains(
            "io.sharplabnext.runtime.matrix.profile-group",
            combinedTarget,
            StringComparison.Ordinal);
        Assert.Contains(
            "io.sharplabnext.operator-image.mono-wine",
            combinedTarget,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "runtime-${required(RUNTIME_MATRIX_PROFILE_ID)}:candidate",
            combinedTarget,
            StringComparison.Ordinal);

        var combinedDockerfile = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "deploy", "docker", "Dockerfile.runtime-mono-wine-matrix"),
            TestContext.Current.CancellationToken);
        foreach (var marker in new[]
        {
            "FROM ${MONO_WINE_IMAGE} AS runtime-source",
            "FROM runtime-source AS runtime-base",
            "FROM runtime-base AS preflight",
            "FROM runtime-base AS final",
            "test -x /usr/bin/mono-sgen",
            "test -x /usr/lib/wine/wine64",
            "test -d /opt/wine-netfx-clr2/drive_c/windows/Microsoft.NET/Framework64/v2.0.50727",
            "test -d /opt/wine-netfx-clr4/drive_c/windows/Microsoft.NET/Framework64/v4.0.30319",
            "COPY --from=control-image /usr/share/dotnet/ /usr/share/dotnet/",
            "COPY --from=publish /legacy-jit-helper/ /opt/sharplabnext/",
            "COPY --from=publish /target-runtime-runner/ /opt/sharplabnext/",
            "/usr/share/dotnet/dotnet --info",
            "test -s /opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe",
            "target_frames=\"$(/usr/bin/mono",
            "clr2_frames=\"$(WINEPREFIX=/opt/wine-netfx-clr2 /usr/lib/wine/wine64",
            "clr4_frames=\"$(WINEPREFIX=/opt/wine-netfx-clr4 /usr/lib/wine/wine64",
        })
        {
            Assert.Contains(marker, combinedDockerfile, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(
            "COPY --from=runtime-source /usr/ /usr/",
            combinedDockerfile,
            StringComparison.Ordinal);
        Assert.DoesNotContain("cmp --silent", combinedDockerfile, StringComparison.Ordinal);

        var wineDockerfile = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "deploy", "docker", "Dockerfile.runtime-wine-dotnet-matrix"),
            TestContext.Current.CancellationToken);
        Assert.Contains(
            "--output /legacy-jit-helper",
            wineDockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "COPY --from=publish /legacy-jit-helper/ /opt/sharplabnext/",
            wineDockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "test -s /opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll",
            wineDockerfile,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BundleEntrypointsUseSharedBakeEnvironmentTool()
    {
        var repositoryRoot = FindRepositoryRoot();
        foreach (var script in new[] { "bundle.ps1", "bundle.sh" })
        {
            var content = await File.ReadAllTextAsync(
                Path.Combine(repositoryRoot, "eng", script),
                TestContext.Current.CancellationToken);
            Assert.Contains("run-with-bake-environment.cs", content, StringComparison.Ordinal);
            Assert.Contains("--repository-root", content, StringComparison.Ordinal);
            Assert.Contains("--allow-uncommitted-source-for-development", content, StringComparison.Ordinal);
            Assert.DoesNotContain("--load", content, StringComparison.Ordinal);
            Assert.DoesNotContain("read-lock-field.cs", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ComposeE2eWorkflowUsesSharedBakeEnvironmentTool()
    {
        var repositoryRoot = FindRepositoryRoot();
        var content = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"),
            TestContext.Current.CancellationToken);
        var yaml = new YamlStream();
        yaml.Load(new StringReader(content));
        Assert.Single(yaml.Documents);

        var buildStepStart = content.IndexOf(
            "- name: Build and load locked Linux images",
            StringComparison.Ordinal);
        var buildStepEnd = content.IndexOf(
            "- name: Generate immutable local deployment metadata",
            StringComparison.Ordinal);
        Assert.True(buildStepStart >= 0);
        Assert.True(buildStepEnd > buildStepStart);
        var buildStep = content[buildStepStart..buildStepEnd];

        Assert.Contains("run-with-bake-environment.cs", buildStep, StringComparison.Ordinal);
        Assert.Contains("--lock profiles/lock.json", buildStep, StringComparison.Ordinal);
        Assert.Contains("--base-images profiles/base-images.json", buildStep, StringComparison.Ordinal);
        Assert.Contains("--source-revision \"$SOURCE_REVISION\"", buildStep, StringComparison.Ordinal);
        Assert.Contains("--repository-root \"$GITHUB_WORKSPACE\"", buildStep, StringComparison.Ordinal);
        Assert.Contains("--image-prefix \"$IMAGE_PREFIX\"", buildStep, StringComparison.Ordinal);
        Assert.Contains("--allow-uncommitted-source-for-development", buildStep, StringComparison.Ordinal);
        Assert.DoesNotContain("--load", buildStep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BakeDefinitionUsesDeterministicDockerExporter()
    {
        var repositoryRoot = FindRepositoryRoot();
        var content = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "eng", "bake.hcl"),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            "output = [\"type=docker,rewrite-timestamp=true,unpack=false\"]",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "SOURCE_DATE_EPOCH = unix_seconds(required(SOURCE_DATE_EPOCH))",
            content,
            StringComparison.Ordinal);
        Assert.DoesNotContain("unpack=true", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerBuildsUseDeterministicCompilerOutputsWithoutPortablePdbs()
    {
        var repositoryRoot = FindRepositoryRoot();
        var content = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "Directory.Build.props"),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            "Condition=\"'$(DOTNET_RUNNING_IN_CONTAINER)' == 'true'\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains("<ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>", content, StringComparison.Ordinal);
        Assert.Contains("<DeterministicSourcePaths>true</DeterministicSourcePaths>", content, StringComparison.Ordinal);
        Assert.Contains("<UseSharedCompilation>false</UseSharedCompilation>", content, StringComparison.Ordinal);
        Assert.Contains("<ConcurrentBuild>false</ConcurrentBuild>", content, StringComparison.Ordinal);
        Assert.Contains("<PathMap>$(MSBuildThisFileDirectory)=/_/</PathMap>", content, StringComparison.Ordinal);
        Assert.Contains("<DebugSymbols>false</DebugSymbols>", content, StringComparison.Ordinal);
        Assert.Contains("<DebugType>None</DebugType>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceImagesOnlyCarryReferenceSetsWhenTheirRuntimeContractUsesThem()
    {
        var repositoryRoot = FindRepositoryRoot();
        var bake = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "eng", "bake.hcl"),
            TestContext.Current.CancellationToken);
        var dockerfile = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "deploy", "docker", "Dockerfile.worker"),
            TestContext.Current.CancellationToken);

        Assert.Contains("target \"service-with-reference-sets\"", bake, StringComparison.Ordinal);
        Assert.Contains("target \"service-with-roslyn-coreclr-reference-sets\"", bake, StringComparison.Ordinal);
        Assert.Contains("target \"service-with-framework-reference-sets\"", bake, StringComparison.Ordinal);
        Assert.Contains("target = \"final-without-reference-sets\"", bake, StringComparison.Ordinal);
        Assert.Contains("target = \"final-with-reference-sets\"", bake, StringComparison.Ordinal);
        Assert.Contains("target = \"final-with-framework-reference-sets\"", bake, StringComparison.Ordinal);
        Assert.Contains(
            "target = \"final-with-framework-and-jsharp-reference-sets\"",
            bake,
            StringComparison.Ordinal);
        foreach (var target in new[]
                 {
                     "worker-fsharp",
                     "worker-il",
                     "worker-minilang"
                 })
        {
            Assert.Matches(
                Regex.Escape($"target \"{target}\" {{") +
                @"\s+inherits = \[""service-with-reference-sets""\]",
                bake);
        }
        Assert.Matches(
            Regex.Escape("target \"worker-roslyn-stable\" {") +
            @"\s+inherits = \[""service-with-roslyn-coreclr-reference-sets""\]",
            bake);
        Assert.Matches(
            Regex.Escape("target \"worker-artifacts-default\" {") +
            @"\s+inherits = \[""service-with-framework-reference-sets""\]",
            bake);
        Assert.Contains(
            "\"jsharp-reference-source\" = \"target:worker-jsharp\"",
            bake,
            StringComparison.Ordinal);
        foreach (var target in new[] { "artifact-store", "runtime-supervisor", "worker-artifacts-il-assembler" })
        {
            Assert.Matches(
                Regex.Escape($"target \"{target}\" {{") + @"\s+inherits = \[""service""\]",
                bake);
        }

        Assert.Contains("FROM publish AS reference-sets", dockerfile, StringComparison.Ordinal);
        Assert.Contains("FROM publish AS framework-reference-sets", dockerfile, StringComparison.Ordinal);
        Assert.Contains(
            "COPY eng/materialize-framework-reference-sets.cs /tools/materialize-framework-reference-sets.cs",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet run materialize-framework-reference-sets.cs",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains("FROM final-base AS final-without-reference-sets", dockerfile, StringComparison.Ordinal);
        Assert.Contains("FROM final-base AS final-with-reference-sets", dockerfile, StringComparison.Ordinal);
        Assert.Contains(
            "FROM final-with-reference-sets AS final-with-framework-reference-sets",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "FROM final-with-framework-reference-sets AS final-with-framework-and-jsharp-reference-sets",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "COPY --from=reference-sets --chown=1654:1654 /reference-sets/ /reference-sets/",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "COPY --from=framework-reference-sets --chown=1654:1654",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "/reference-sets/ /reference-sets/",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "COPY --from=jsharp-reference-source --chown=1654:1654",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "/reference-sets/jsharp20-ref/ /reference-sets/jsharp20-ref/",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "test \"${reference_content_digest}\" = \"${JSHARP_REFERENCE_DIGEST}\"",
            dockerfile,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceDateEpochUsesSourceRevisionForReleaseAndHeadForDevelopment()
    {
        var reader = new RecordingSourceDateEpochReader("001700000000");

        var releaseEpoch = await SourceDateEpochResolver.ResolveAsync(
            ".",
            "verified-revision",
            allowUncommittedSourceForDevelopment: false,
            reader,
            TestContext.Current.CancellationToken);
        var developmentEpoch = await SourceDateEpochResolver.ResolveAsync(
            ".",
            "local-uncommitted",
            allowUncommittedSourceForDevelopment: true,
            reader,
            TestContext.Current.CancellationToken);

        Assert.Equal("1700000000", releaseEpoch);
        Assert.Equal("1700000000", developmentEpoch);
        Assert.Equal(["verified-revision", "HEAD"], reader.Revisions);
    }

    [Fact]
    public async Task SourceDateEpochFallbackIsDevelopmentOnly()
    {
        var reader = new RecordingSourceDateEpochReader(epoch: null);

        var developmentEpoch = await SourceDateEpochResolver.ResolveAsync(
            ".",
            "local-uncommitted",
            allowUncommittedSourceForDevelopment: true,
            reader,
            TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<BakeEnvironmentValidationException>(() =>
            SourceDateEpochResolver.ResolveAsync(
                ".",
                "verified-revision",
                allowUncommittedSourceForDevelopment: false,
                reader,
                TestContext.Current.CancellationToken));

        Assert.Equal(SourceDateEpochResolver.DevelopmentFallbackUnixSeconds, developmentEpoch);
        Assert.Contains("verified source revision", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("seconds")]
    [InlineData("9223372036854775808")]
    public void SourceDateEpochRejectsInvalidUnixSeconds(string? value)
    {
        var exception = Assert.Throws<BakeEnvironmentValidationException>(() =>
            SourceDateEpochResolver.Validate(value));

        Assert.Contains("Unix timestamp", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BakeEnvironmentRejectsMutableBaseImageReference()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "profiles", "base-images.json"),
            TestContext.Current.CancellationToken))!;
        manifest["images"]![0]!["reference"] = "node:24.18.0-bookworm-slim";
        var temporaryManifest = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                temporaryManifest,
                manifest.ToJsonString(),
                TestContext.Current.CancellationToken);
            var exception = await Assert.ThrowsAsync<BakeEnvironmentValidationException>(() =>
                BakeEnvironmentResolver.CreateAsync(
                    Path.Combine(repositoryRoot, "profiles", "lock.json"),
                    temporaryManifest,
                    "test-source-revision",
                    "1700000000",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("repository[:tag]@sha256", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temporaryManifest);
        }
    }

    [Theory]
    [InlineData("sharplabnext/operator-jsharp20:latest", null)]
    [InlineData(
        "docker://sharplabnext/operator-jsharp20@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task BakeEnvironmentRejectsMutableOrMismatchedJSharpOperatorImage(
        string sourceUri,
        string? digest)
    {
        var repositoryRoot = FindRepositoryRoot();
        var lockDocument = JsonSerializer.Deserialize<ReleaseLockDocument>(
            await File.ReadAllTextAsync(
                Path.Combine(repositoryRoot, "profiles", "lock.json"),
                TestContext.Current.CancellationToken),
            WebJsonOptions)
            ?? throw new InvalidOperationException("Repository release lock is invalid.");
        var components = lockDocument.Components.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        components["jsharp20"] = components["jsharp20"] with
        {
            SourceUri = sourceUri,
            Digest = digest ?? components["jsharp20"].Digest
        };
        var temporaryLock = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                temporaryLock,
                JsonSerializer.Serialize(
                    lockDocument with { Components = components },
                    WebJsonOptions),
                TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<BakeEnvironmentValidationException>(() =>
                BakeEnvironmentResolver.CreateAsync(
                    temporaryLock,
                    Path.Combine(repositoryRoot, "profiles", "base-images.json"),
                    "test-source-revision",
                    "1700000000",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("jsharp20.sourceUri", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temporaryLock);
        }
    }

    [Theory]
    [InlineData("sharplabnext/worker-cppcli:latest", null)]
    [InlineData(
        "docker://localhost:5000/sharplabnext/msvc-cppcli-prepared-base@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task BakeEnvironmentRejectsMutableOrMismatchedCppCliPreparedBaseImage(
        string sourceUri,
        string? digest)
    {
        var repositoryRoot = FindRepositoryRoot();
        var lockDocument = JsonSerializer.Deserialize<ReleaseLockDocument>(
            await File.ReadAllTextAsync(
                Path.Combine(repositoryRoot, "profiles", "lock.json"),
                TestContext.Current.CancellationToken),
            WebJsonOptions)
            ?? throw new InvalidOperationException("Repository release lock is invalid.");
        var components = lockDocument.Components.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        components["msvc-cppcli-prepared-base"] = components["msvc-cppcli-prepared-base"] with
        {
            SourceUri = sourceUri,
            Digest = digest ?? components["msvc-cppcli-prepared-base"].Digest
        };
        var temporaryLock = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                temporaryLock,
                JsonSerializer.Serialize(
                    lockDocument with { Components = components },
                    WebJsonOptions),
                TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<BakeEnvironmentValidationException>(() =>
                BakeEnvironmentResolver.CreateAsync(
                    temporaryLock,
                    Path.Combine(repositoryRoot, "profiles", "base-images.json"),
                    "test-source-revision",
                    "1700000000",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("msvc-cppcli-prepared-base.sourceUri", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temporaryLock);
        }
    }

    [Theory]
    [InlineData("sharplabnext/runtime-wine-jsharp20:latest", null)]
    [InlineData(
        "docker://sharplabnext/runtime-wine-jsharp20@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task BakeEnvironmentRejectsMutableOrMismatchedJSharpPreparedBaseImage(
        string sourceUri,
        string? digest)
    {
        var repositoryRoot = FindRepositoryRoot();
        var lockDocument = JsonSerializer.Deserialize<ReleaseLockDocument>(
            await File.ReadAllTextAsync(
                Path.Combine(repositoryRoot, "profiles", "lock.json"),
                TestContext.Current.CancellationToken),
            WebJsonOptions)
            ?? throw new InvalidOperationException("Repository release lock is invalid.");
        var components = lockDocument.Components.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        components["jsharp20-prepared-base"] = components["jsharp20-prepared-base"] with
        {
            SourceUri = sourceUri,
            Digest = digest ?? components["jsharp20-prepared-base"].Digest
        };
        var temporaryLock = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                temporaryLock,
                JsonSerializer.Serialize(
                    lockDocument with { Components = components },
                    WebJsonOptions),
                TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<BakeEnvironmentValidationException>(() =>
                BakeEnvironmentResolver.CreateAsync(
                    temporaryLock,
                    Path.Combine(repositoryRoot, "profiles", "base-images.json"),
                    "test-source-revision",
                    "1700000000",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("jsharp20-prepared-base.sourceUri", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temporaryLock);
        }
    }

    [Fact]
    public async Task BuildFailureIsRecordedWithoutChangingApprovedLock()
    {
        using var repository = new TempRepository();
        var runner = new RecordingCommandRunner { FailureFileName = "docker" };
        var workflow = repository.CreateWorkflow(runner);
        var candidate = await workflow.ResolveAsync(cancellationToken: TestContext.Current.CancellationToken);
        var approvedBefore = await File.ReadAllBytesAsync(
            repository.LockPath,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ProfileUpdateCommandFailedException>(() => workflow.BuildAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            TestContext.Current.CancellationToken));

        Assert.Equal(
            approvedBefore,
            await File.ReadAllBytesAsync(repository.LockPath, TestContext.Current.CancellationToken));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(candidate.CandidatePath)!, "receipt.json"),
            TestContext.Current.CancellationToken));
        var stages = receipt.RootElement.GetProperty("stages");
        Assert.Equal("failed", stages[stages.GetArrayLength() - 1].GetProperty("status").GetString());
        Assert.Equal("build", stages[stages.GetArrayLength() - 1].GetProperty("stage").GetString());
        var publicJson = await File.ReadAllTextAsync(
            repository.PublicStatusPath,
            TestContext.Current.CancellationToken);
        using var publicStatus = JsonDocument.Parse(publicJson);
        Assert.Equal("candidate-failed", publicStatus.RootElement.GetProperty("status").GetString());
        var error = publicStatus.RootElement.GetProperty("lastStage").GetProperty("error");
        Assert.Equal("profile-update.build-failed", error.GetProperty("code").GetString());
        Assert.DoesNotContain("docker", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(repository.Root, publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commands", publicJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestRejectsCandidateWhenApprovedSourceDigestChanged()
    {
        using var repository = new TempRepository();
        var runner = new RecordingCommandRunner();
        var workflow = repository.CreateWorkflow(runner);
        var candidate = await workflow.ResolveAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.BuildAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            TestContext.Current.CancellationToken);
        var commandCountBeforeTest = runner.Commands.Count;
        await File.AppendAllTextAsync(
            repository.LockPath,
            Environment.NewLine,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ProfileUpdateValidationException>(() => workflow.TestAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            ProfileUpdateTestScope.Full,
            TestContext.Current.CancellationToken));

        Assert.Contains("does not match active lock digest", exception.Message, StringComparison.Ordinal);
        Assert.Equal(commandCountBeforeTest, runner.Commands.Count);
    }

    [Fact]
    public async Task FullTestRunsAllQualityGatesAgainstCandidateLock()
    {
        using var repository = new TempRepository();
        var runner = new RecordingCommandRunner();
        var workflow = repository.CreateWorkflow(runner);
        var candidate = await workflow.ResolveAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.BuildAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            TestContext.Current.CancellationToken);
        runner.Commands.Clear();

        var result = await workflow.TestAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            ProfileUpdateTestScope.Full,
            TestContext.Current.CancellationToken);

        Assert.Equal(15, runner.Commands.Count);
        Assert.Contains(runner.Commands, static command => command.Arguments.Contains("SharpLabNext.slnx"));
        var schema = Assert.Single(runner.Commands, static command =>
            command.Arguments.Contains("eng/validate-schemas.mjs"));
        Assert.Contains(Path.Combine(schema.WorkingDirectory, "profiles", "lock.json"), schema.Arguments);
        Assert.Contains(runner.Commands, static command => command.Arguments.Contains("eng/validate-compose.mjs"));
        var compatibility = Assert.Single(runner.Commands, static command =>
            command.Arguments.Contains("src/Tools/SharpLabNext.CompatibilityCli"));
        Assert.Contains(Path.Combine(compatibility.WorkingDirectory, "profiles", "lock.json"), compatibility.Arguments);
        var bundle = Assert.Single(runner.Commands, static command =>
            command.Arguments.Contains("src/Tools/SharpLabNext.BundleBuilder"));
        Assert.Contains("--metadata-only", bundle.Arguments);
        Assert.Contains("--allow-uncommitted-source-for-development", bundle.Arguments);
        var composeUp = Assert.Single(runner.Commands, static command =>
            command.FileName == "docker" && command.Arguments.Contains("up"));
        Assert.Contains("--pull", composeUp.Arguments);
        Assert.Contains("never", composeUp.Arguments);
        var verifier = Assert.Single(runner.Commands, static command =>
            command.Arguments.Contains("eng/verify-profile-candidate.cs"));
        Assert.Contains("--bundle", verifier.Arguments);
        Assert.Contains(runner.Commands, static command =>
            command.Arguments.Contains("eng/smoke/gateway-compose.cs") && command.Arguments.Contains("--full"));
        var performance = Assert.Single(runner.Commands, static command =>
            command.Arguments.Contains("eng/performance/gateway-performance.cs"));
        Assert.Contains("--base-address", performance.Arguments);
        Assert.Contains("--thresholds", performance.Arguments);
        Assert.Contains("eng/performance/thresholds.v1.json", performance.Arguments);
        Assert.Contains("--output", performance.Arguments);
        Assert.Contains(performance.Arguments, static argument =>
            argument.EndsWith("performance-report-1.json", StringComparison.Ordinal));
        Assert.Contains(runner.Commands, static command =>
            command.Arguments.Contains("eng/smoke/gateway-compose.cs") && command.Arguments.Contains("--security"));
        Assert.Contains(runner.Commands, static command => command.Arguments.Contains("eng/smoke/runtime-failures.cs"));
        Assert.Contains(runner.Commands, static command => command.Arguments.Contains("test:e2e"));
        var cleanup = Assert.Single(runner.Commands, static command => command.AlwaysRun);
        Assert.Contains("down", cleanup.Arguments);
        Assert.Contains("--volumes", cleanup.Arguments);
        var candidateWorkspace = runner.Commands[0].WorkingDirectory;
        Assert.EndsWith("workspace", candidateWorkspace, StringComparison.Ordinal);
        Assert.All(runner.Commands, command => Assert.Equal(candidateWorkspace, command.WorkingDirectory));
        Assert.Equal(ProfileUpdateTestScope.Full, result.Stage.TestScope);
    }

    [Fact]
    public async Task FullTestRunsComposeCleanupWhenIdentityVerificationFails()
    {
        using var repository = new TempRepository();
        var runner = new RecordingCommandRunner();
        var workflow = repository.CreateWorkflow(runner);
        var candidate = await workflow.ResolveAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.BuildAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            TestContext.Current.CancellationToken);
        runner.Commands.Clear();
        runner.FailurePredicate = static command => command.Arguments.Contains("eng/verify-profile-candidate.cs");

        await Assert.ThrowsAsync<ProfileUpdateCommandFailedException>(() => workflow.TestAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            ProfileUpdateTestScope.Full,
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain(runner.Commands, static command => command.Arguments.Contains("--full"));
        var cleanup = Assert.Single(runner.Commands, static command => command.AlwaysRun);
        Assert.Contains("down", cleanup.Arguments);
        Assert.Equal(cleanup, runner.Commands[^1]);
    }

    [Fact]
    public async Task FullTestRunsComposeCleanupWhenPerformanceGateFails()
    {
        using var repository = new TempRepository();
        var runner = new RecordingCommandRunner();
        var workflow = repository.CreateWorkflow(runner);
        var candidate = await workflow.ResolveAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.BuildAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            TestContext.Current.CancellationToken);
        runner.Commands.Clear();
        runner.FailurePredicate = static command =>
            command.Arguments.Contains("eng/performance/gateway-performance.cs");

        await Assert.ThrowsAsync<ProfileUpdateCommandFailedException>(() => workflow.TestAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            ProfileUpdateTestScope.Full,
            TestContext.Current.CancellationToken));

        Assert.Contains(runner.Commands, static command =>
            command.Arguments.Contains("eng/performance/gateway-performance.cs"));
        Assert.DoesNotContain(runner.Commands, static command =>
            command.Arguments.Contains("eng/smoke/gateway-compose.cs") && command.Arguments.Contains("--security"));
        var cleanup = Assert.Single(runner.Commands, static command => command.AlwaysRun);
        Assert.Contains("down", cleanup.Arguments);
        Assert.Equal(cleanup, runner.Commands[^1]);
    }

    [Fact]
    public void ProfileUpdateAutomationHasIndependentAlwaysDockerCleanup()
    {
        var workflowPath = Path.Combine(
            FindRepositoryRoot(),
            ".github",
            "workflows",
            "profile-update.yml");
        var workflow = File.ReadAllText(workflowPath);
        var fullGate = workflow.IndexOf("- name: Run full candidate gate", StringComparison.Ordinal);
        var cleanup = workflow.IndexOf("- name: Cleanup candidate Docker resources", StringComparison.Ordinal);
        var promote = workflow.IndexOf("- name: Promote candidate material locally", StringComparison.Ordinal);

        Assert.True(fullGate >= 0);
        Assert.True(cleanup > fullGate);
        Assert.True(promote > cleanup);
        var cleanupStep = workflow[cleanup..promote];
        Assert.Contains("if: always()", cleanupStep, StringComparison.Ordinal);
        Assert.Contains("jq -er '.candidateDigest'", cleanupStep, StringComparison.Ordinal);
        Assert.Contains("^sha256:([0-9a-f]{64})$", cleanupStep, StringComparison.Ordinal);
        Assert.Contains(
            "candidate_project=\"sln-profile-${digest_hex:0:12}-1\"",
            cleanupStep,
            StringComparison.Ordinal);
        Assert.Contains("com.docker.compose.project", cleanupStep, StringComparison.Ordinal);
        Assert.Contains(
            "label=com.docker.compose.project=$candidate_project",
            cleanupStep,
            StringComparison.Ordinal);
        Assert.Contains("com.sharplabnext.runtime-job=true", cleanupStep, StringComparison.Ordinal);
        Assert.Contains("com.sharplabnext.runtime-job=workspace", cleanupStep, StringComparison.Ordinal);
        Assert.True(
            cleanupStep.Split(
                "label=com.sharplabnext.release-id=$candidate_release_id",
                StringSplitOptions.None).Length >= 5,
            "Every runtime cleanup and verification query must include the candidate release label.");
        Assert.True(
            cleanupStep.Split(
                "label=com.sharplabnext.resource-scope=$candidate_digest",
                StringSplitOptions.None).Length >= 5,
            "Every runtime cleanup and verification query must include the candidate resource scope.");
        Assert.DoesNotContain("grep '^sln-profile-'", cleanupStep, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--filter label=com.sharplabnext.runtime-job=true\n          )",
            cleanupStep,
            StringComparison.Ordinal);
        Assert.Contains("docker container rm --force", cleanupStep, StringComparison.Ordinal);
        Assert.Contains("docker volume rm", cleanupStep, StringComparison.Ordinal);
        Assert.Contains("Candidate Docker cleanup left labeled resources behind", cleanupStep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestRejectsCandidateWhenCatalogIdentityWasTampered()
    {
        using var repository = new TempRepository();
        var runner = new RecordingCommandRunner();
        var workflow = repository.CreateWorkflow(runner);
        var candidate = await workflow.ResolveAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.BuildAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            TestContext.Current.CancellationToken);
        var commandCount = runner.Commands.Count;
        var catalogPath = Path.Combine(
            repository.Root,
            candidate.Receipt.WorkspacePath,
            "profiles",
            "catalog",
            "catalog.json");
        var catalogText = await File.ReadAllTextAsync(catalogPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            catalogPath,
            catalogText.Replace("Roslyn Stable 5.6.0", "Roslyn Stable 5.6.1", StringComparison.Ordinal)
                .Replace("\"resolvedVersion\": \"5.6.0\"", "\"resolvedVersion\": \"5.6.1\"", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ProfileUpdateValidationException>(() => workflow.TestAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            ProfileUpdateTestScope.Full,
            TestContext.Current.CancellationToken));

        Assert.Contains("Candidate identity mismatch", exception.Message, StringComparison.Ordinal);
        Assert.Equal(commandCount, runner.Commands.Count);
    }

    [Theory]
    [InlineData("versions")]
    [InlineData("runtime-profile")]
    [InlineData("package-lock")]
    public async Task TestRejectsCandidateWhenBuiltMaterialWasTampered(string material)
    {
        using var repository = new TempRepository();
        var runner = new RecordingCommandRunner();
        var workflow = repository.CreateWorkflow(runner);
        var candidate = await workflow.ResolveAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.BuildAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            TestContext.Current.CancellationToken);
        var commandCount = runner.Commands.Count;
        var workspace = Path.Combine(repository.Root, candidate.Receipt.WorkspacePath);
        var path = material switch
        {
            "versions" => Path.Combine(workspace, "profiles", "versions.props"),
            "runtime-profile" => Path.Combine(
                workspace,
                "profiles",
                "runtimes",
                "dotnet-10-linux-x64.json"),
            _ => Path.Combine(workspace, "packages.lock.json")
        };
        await File.AppendAllTextAsync(
            path,
            material == "runtime-profile" ? " \n" : "\n<!-- tampered -->\n",
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ProfileUpdateValidationException>(() => workflow.TestAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            ProfileUpdateTestScope.Full,
            TestContext.Current.CancellationToken));

        Assert.Contains("material digest mismatch", exception.Message, StringComparison.Ordinal);
        Assert.Equal(commandCount, runner.Commands.Count);
    }

    [Fact]
    public async Task BuildRejectsCandidateWithAChangedActiveRuntimeProfileSet()
    {
        using var repository = new TempRepository();
        var runner = new RecordingCommandRunner();
        var workflow = repository.CreateWorkflow(runner);
        var candidate = await workflow.ResolveAsync(cancellationToken: TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(
            repository.Root,
            candidate.Receipt.WorkspacePath,
            "profiles",
            "runtimes",
            "dotnet-10-linux-x64.json"));

        var exception = await Assert.ThrowsAsync<ProfileUpdateValidationException>(() => workflow.BuildAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            TestContext.Current.CancellationToken));

        Assert.Contains("runtime profile set", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task PromoteRequiresFullTestsAndPreservesHistoryBeforeReplacingLock()
    {
        using var repository = new TempRepository();
        var previousRuntimeProfiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var runtimeProfileId in ActiveRuntimeProfileIds)
        {
            previousRuntimeProfiles[runtimeProfileId] = await File.ReadAllBytesAsync(
                Path.Combine(repository.Root, "profiles", "runtimes", $"{runtimeProfileId}.json"),
                TestContext.Current.CancellationToken);
        }
        var runner = new RecordingCommandRunner();
        var workflow = repository.CreateWorkflow(runner);
        var candidate = await workflow.ResolveAsync(
            "candidate-1",
            cancellationToken: TestContext.Current.CancellationToken);
        var candidateWorkspace = Path.Combine(repository.Root, candidate.Receipt.WorkspacePath);
        var candidateCatalogPath = Path.Combine(candidateWorkspace, "profiles", "catalog", "catalog.json");
        var candidateVersionsPath = Path.Combine(candidateWorkspace, "profiles", "versions.props");
        var candidateRuntimeProfilePaths = ActiveRuntimeProfileIds.ToDictionary(
            static id => id,
            id => Path.Combine(candidateWorkspace, "profiles", "runtimes", $"{id}.json"),
            StringComparer.Ordinal);
        var candidatePackageLockPath = Path.Combine(candidateWorkspace, "packages.lock.json");
        var candidateNamedPackageLockPath = Path.Combine(
            candidateWorkspace,
            "src",
            "Workers",
            "IL",
            "SharpLabNext.Worker.IL",
            "packages.EleCho.ILSense.lock.json");
        var candidateCatalog = await File.ReadAllTextAsync(candidateCatalogPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            candidateCatalogPath,
            candidateCatalog.Replace("20260712.4-dev", "candidate-promotion", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(candidateVersionsPath, "<!-- candidate -->\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(candidatePackageLockPath, "{\"candidate\":true}\n", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(candidateNamedPackageLockPath)!);
        await File.WriteAllTextAsync(
            candidateNamedPackageLockPath,
            "{\"namedCandidate\":true}\n",
            TestContext.Current.CancellationToken);
        await workflow.BuildAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            TestContext.Current.CancellationToken);
        await workflow.TestAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            ProfileUpdateTestScope.Affected,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ProfileUpdateValidationException>(() => workflow.PromoteAsync(
            null,
            candidate.CandidateDigest,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            repository.ActiveDigest,
            Digest(await File.ReadAllBytesAsync(repository.LockPath, TestContext.Current.CancellationToken)));

        await workflow.TestAsync(
            null,
            candidate.CandidateDigest,
            "Release",
            ProfileUpdateTestScope.Full,
            TestContext.Current.CancellationToken);
        var promoted = await workflow.PromoteAsync(
            null,
            candidate.CandidateDigest,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            candidate.CandidateDigest,
            Digest(await File.ReadAllBytesAsync(repository.LockPath, TestContext.Current.CancellationToken)));
        Assert.Equal(ProfileUpdateStage.Promote, promoted.Stage.Stage);
        var previousHistory = Path.Combine(
            repository.StateRoot,
            "history",
            repository.ActiveDigest[7..],
            "lock.json");
        Assert.True(File.Exists(previousHistory));
        Assert.Equal(
            repository.ActiveDigest,
            Digest(await File.ReadAllBytesAsync(previousHistory, TestContext.Current.CancellationToken)));
        Assert.True(File.Exists(Path.Combine(repository.StateRoot, "last-known-good", "previous.lock.json")));
        Assert.Equal(
            candidate.CandidateDigest,
            Digest(await File.ReadAllBytesAsync(
                Path.Combine(repository.StateRoot, "last-known-good", "lock.json"),
                TestContext.Current.CancellationToken)));
        Assert.Equal(
            await File.ReadAllBytesAsync(candidateCatalogPath, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(
                Path.Combine(repository.Root, "profiles", "catalog", "catalog.json"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            await File.ReadAllBytesAsync(candidateVersionsPath, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(
                Path.Combine(repository.Root, "profiles", "versions.props"),
                TestContext.Current.CancellationToken));
        foreach (var runtimeProfileId in ActiveRuntimeProfileIds)
        {
            var candidateRuntimeProfile = await File.ReadAllBytesAsync(
                candidateRuntimeProfilePaths[runtimeProfileId],
                TestContext.Current.CancellationToken);
            Assert.Equal(
                candidateRuntimeProfile,
                await File.ReadAllBytesAsync(
                    Path.Combine(repository.Root, "profiles", "runtimes", $"{runtimeProfileId}.json"),
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                previousRuntimeProfiles[runtimeProfileId],
                await File.ReadAllBytesAsync(
                    Path.Combine(
                        repository.StateRoot,
                        "history",
                        repository.ActiveDigest[7..],
                        "runtimes",
                        $"{runtimeProfileId}.json"),
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                candidateRuntimeProfile,
                await File.ReadAllBytesAsync(
                    Path.Combine(
                        repository.StateRoot,
                        "history",
                        candidate.CandidateDigest[7..],
                        "runtimes",
                        $"{runtimeProfileId}.json"),
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                candidateRuntimeProfile,
                await File.ReadAllBytesAsync(
                    Path.Combine(
                        repository.StateRoot,
                        "last-known-good",
                        "material",
                        "runtimes",
                        $"{runtimeProfileId}.json"),
                    TestContext.Current.CancellationToken));
        }
        Assert.Equal(
            await File.ReadAllBytesAsync(candidatePackageLockPath, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(
                Path.Combine(repository.Root, "packages.lock.json"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            await File.ReadAllBytesAsync(candidateNamedPackageLockPath, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(
                Path.Combine(
                    repository.Root,
                    "src",
                    "Workers",
                    "IL",
                    "SharpLabNext.Worker.IL",
                    "packages.EleCho.ILSense.lock.json"),
                TestContext.Current.CancellationToken));
        var publicStatus = JsonSerializer.Deserialize<ProfileUpdateStatusDocument>(
            await File.ReadAllTextAsync(repository.PublicStatusPath, TestContext.Current.CancellationToken),
            WebJsonOptions);
        Assert.NotNull(publicStatus);
        Assert.Equal(ProfileUpdateStatusKind.CandidateApproved, publicStatus.Status);
        Assert.False(publicStatus.UpdateAvailable);
        Assert.Equal("candidate-1", publicStatus.Active.ReleaseId);
        Assert.Equal(candidate.CandidateDigest, publicStatus.Active.LockDigest);
        Assert.Equal(publicStatus.Active, publicStatus.LastKnownGood);
    }

    [Fact]
    public void ParserSupportsSubcommandsAliasesAndGatedLegacyApply()
    {
        Assert.Equal(
            ProfileUpdaterCommandKind.Check,
            ProfileUpdaterCommand.Parse(["--check", "--fail-on-change"]).Kind);
        Assert.Equal(
            ProfileUpdaterCommandKind.Test,
            ProfileUpdaterCommand.Parse(["test", "--test-scope", "affected"]).Kind);
        Assert.Equal(
            ProfileUpdaterCommandKind.Resolve,
            ProfileUpdaterCommand.Parse(["--output", "candidate.json"]).Kind);
        Assert.Equal(
            ProfileUpdaterCommandKind.Pipeline,
            ProfileUpdaterCommand.Parse(["--apply"]).Kind);
    }

    private static string Digest(byte[] bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    private static async Task AssertUtf8NoBomLfAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.NotEmpty(bytes);
        Assert.False(bytes.AsSpan().StartsWith("\uFEFF"u8));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(bytes);
    }

    private static byte[] CreateRuntimeArchive(string commit, string version)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: false))
        using (var versionData = new MemoryStream(Encoding.UTF8.GetBytes($"{commit}\n{version}\n")))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, ".version")
            {
                DataStream = versionData
            });
        }
        return output.ToArray();
    }

    private static string CreateReleaseMetadata(string version, string runtimeSha512) => $$"""
        {
          "latest-release": "{{version}}",
          "releases": [
            {
              "release-version": "{{version}}",
              "release-date": "2026-06-09",
              "runtime": {
                "version": "{{version}}",
                "files": [
                  {
                    "rid": "linux-x64",
                    "name": "dotnet-runtime-{{version}}-linux-x64.tar.gz",
                    "url": "https://example.test/runtime.tar.gz",
                    "hash": "{{runtimeSha512}}"
                  }
                ]
              },
              "sdk": {
                "version": "10.0.301",
                "files": [
                  {
                    "rid": "linux-x64",
                    "name": "dotnet-sdk-10.0.301-linux-x64.tar.gz",
                    "url": "https://example.test/sdk.tar.gz",
                    "hash": "{{new string('f', 128)}}"
                  }
                ]
              }
            }
          ]
        }
        """;

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private sealed class RecordingCommandRunner : IProfileUpdateCommandRunner
    {
        public List<ProfileUpdateExternalCommand> Commands { get; } = [];
        public string? FailureFileName { get; init; }
        public Func<ProfileUpdateExternalCommand, bool>? FailurePredicate { get; set; }

        public Task<ProfileUpdateCommandResult> RunAsync(
            ProfileUpdateExternalCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(new ProfileUpdateCommandResult(
                string.Equals(command.FileName, FailureFileName, StringComparison.Ordinal) ||
                FailurePredicate?.Invoke(command) == true
                    ? 17
                    : 0));
        }
    }

    private sealed class DelegateHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }

    private sealed class TempRepository : IDisposable
    {
        private static readonly JsonSerializerOptions LockJsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        public TempRepository()
        {
            Root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.ProfileUpdater.Tests.{Guid.NewGuid():N}");
            LockPath = Path.Combine(Root, "profiles", "lock.json");
            Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
            var sourceRepository = FindRepositoryRoot();
            Directory.CreateDirectory(Path.Combine(Root, "profiles", "catalog"));
            Directory.CreateDirectory(Path.Combine(Root, "profiles", "channels"));
            Directory.CreateDirectory(Path.Combine(Root, "profiles", "runtimes"));
            File.Copy(
                Path.Combine(sourceRepository, "profiles", "catalog", "catalog.json"),
                Path.Combine(Root, "profiles", "catalog", "catalog.json"));
            File.Copy(
                Path.Combine(sourceRepository, "profiles", "versions.props"),
                Path.Combine(Root, "profiles", "versions.props"));
            File.Copy(
                Path.Combine(sourceRepository, "profiles", "base-images.json"),
                Path.Combine(Root, "profiles", "base-images.json"));
            foreach (var channelPath in Directory.EnumerateFiles(
                         Path.Combine(sourceRepository, "profiles", "channels"),
                         "*.yaml"))
            {
                File.Copy(
                    channelPath,
                    Path.Combine(Root, "profiles", "channels", Path.GetFileName(channelPath)));
            }
            foreach (var runtimeProfileId in ActiveRuntimeProfileIds)
            {
                var runtimeProfilePath = Path.Combine(
                    Root,
                    "profiles",
                    "runtimes",
                    $"{runtimeProfileId}.json");
                File.Copy(
                    Path.Combine(sourceRepository, "profiles", "runtimes", $"{runtimeProfileId}.json"),
                    runtimeProfilePath);
                NormalizeDevelopmentRuntimeProfileImage(runtimeProfilePath, runtimeProfileId);
            }
            File.WriteAllText(Path.Combine(Root, "packages.lock.json"), "{}\n");
            var sourceLock = JsonSerializer.Deserialize<ReleaseLockDocument>(
                File.ReadAllText(Path.Combine(sourceRepository, "profiles", "lock.json")),
                WebJsonOptions)
                ?? throw new InvalidOperationException("Repository release lock is invalid.");
            var document = new ReleaseLockDocument
            {
                SchemaVersion = 1,
                ReleaseId = "development",
                ResolvedAt = DateTimeOffset.UnixEpoch,
                Components = new Dictionary<string, LockedComponent>
                {
                    ["wine-coreclr-userspace"] = sourceLock.Components["wine-coreclr-userspace"],
                    ["jit-profiler-clr-samples"] = sourceLock.Components["jit-profiler-clr-samples"],
                    ["jit-profiler-runtime-headers"] = sourceLock.Components["jit-profiler-runtime-headers"],
                    ["msvc-wine-source"] = sourceLock.Components["msvc-wine-source"],
                    ["msvc-cppcli-private-image"] = sourceLock.Components["msvc-cppcli-private-image"],
                    ["msvc-cppcli-prepared-base"] = sourceLock.Components["msvc-cppcli-prepared-base"],
                    ["msvc-cppcli-netfx48"] = sourceLock.Components["msvc-cppcli-netfx48"],
                    ["netfx48-ref"] = sourceLock.Components["netfx48-ref"],
                    ["netfx48-managed-ref"] = sourceLock.Components["netfx48-managed-ref"],
                    ["wine-netfx48-linux-x64"] = sourceLock.Components["wine-netfx48-linux-x64"],
                    ["jsharp20"] = sourceLock.Components["jsharp20"],
                    ["jsharp20-prepared-base"] = sourceLock.Components["jsharp20-prepared-base"],
                    ["vjc-jsharp20"] = sourceLock.Components["vjc-jsharp20"],
                    ["jsharp20-ref"] = sourceLock.Components["jsharp20-ref"],
                    ["wine-jsharp20-linux-x64"] = sourceLock.Components["wine-jsharp20-linux-x64"],
                    ["artifacts-jsil"] = sourceLock.Components["artifacts-jsil"],
                    ["jsil-source"] = sourceLock.Components["jsil-source"],
                    ["jsil-meta-source"] = sourceLock.Components["jsil-meta-source"],
                    ["jsil-ilspy-source"] = sourceLock.Components["jsil-ilspy-source"],
                    ["jsil-nrefactory-source"] = sourceLock.Components["jsil-nrefactory-source"],
                    ["jsil-cecil-source"] = sourceLock.Components["jsil-cecil-source"],
                    ["roslyn-const-generics"] = new()
                    {
                        Kind = "toolchain",
                        ResolvedVersion = "4.8.0-const-generics.bcd209abd947",
                        Commit = "bcd209abd947ac1bc71ef1ee29bd8a02d8e78ffc",
                        Digest = "sha256:e43c77373cc7dc07a58a73516fce5512f441b5435e7d17d0e18af41942dc7487",
                        SourceUri = "https://github.com/hez2010/roslyn/tree/bcd209abd947ac1bc71ef1ee29bd8a02d8e78ffc"
                    },
                    ["const-generics-roslyn-source"] = new()
                    {
                        Kind = "source",
                        ResolvedVersion = "bcd209abd947ac1bc71ef1ee29bd8a02d8e78ffc",
                        Commit = "bcd209abd947ac1bc71ef1ee29bd8a02d8e78ffc",
                        Digest = "sha256:e43c77373cc7dc07a58a73516fce5512f441b5435e7d17d0e18af41942dc7487",
                        SourceUri = "https://codeload.github.com/hez2010/roslyn/tar.gz/bcd209abd947ac1bc71ef1ee29bd8a02d8e78ffc"
                    },
                    ["const-generics-runtime-source"] = new()
                    {
                        Kind = "source",
                        ResolvedVersion = "79f7f1408b2c811904c983419b45139e654f1e46",
                        Commit = "79f7f1408b2c811904c983419b45139e654f1e46",
                        Digest = "sha256:00f0f9fcfc083e931004ceaa914633990ad7e389ce8d21012b97af5844f501b4",
                        SourceUri = "https://codeload.github.com/hez2010/runtime/tar.gz/79f7f1408b2c811904c983419b45139e654f1e46"
                    },
                    ["const-generics-linux-x64"] = new()
                    {
                        Kind = "runtime",
                        ResolvedVersion = "9.0.0-constgenerics.1.23470.1",
                        Commit = "79f7f1408b2c811904c983419b45139e654f1e46",
                        JitCommit = "79f7f1408b2c811904c983419b45139e654f1e46",
                        SourceUri = "https://github.com/hez2010/runtime/tree/79f7f1408b2c811904c983419b45139e654f1e46"
                    },
                    ["minilang-stable"] = new()
                    {
                        Kind = "toolchain",
                        ResolvedVersion = "1.0.0"
                    },
                    ["gsharp-source"] = new()
                    {
                        Kind = "source",
                        ResolvedVersion = "0.3.8",
                        Commit = "723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01",
                        Digest = "sha256:d01510636cb7a4598f76fb01c8d2cf59898def757fd536049a92c359cd9c71fb",
                        SourceUri = "https://codeload.github.com/DavidObando/gsharp/tar.gz/723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01"
                    },
                    ["gsharp-stable"] = new()
                    {
                        Kind = "toolchain",
                        ResolvedVersion = "0.3.8",
                        Commit = "723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01",
                        Digest = "sha256:d01510636cb7a4598f76fb01c8d2cf59898def757fd536049a92c359cd9c71fb",
                        SourceUri = "https://github.com/DavidObando/gsharp/tree/723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01"
                    },
                    ["gsharp-legacy-0.3.8-source"] = sourceLock.Components["gsharp-legacy-0.3.8-source"],
                    ["gsharp-legacy-0.3.8"] = sourceLock.Components["gsharp-legacy-0.3.8"],
                    ["ilsense"] = sourceLock.Components["ilsense"],
                    ["ilsense-source"] = sourceLock.Components["ilsense-source"],
                    ["const-generics-ref"] = new()
                    {
                        Kind = "reference-set",
                        ResolvedVersion = "9.0.0-constgenerics.1.23470.1",
                        Commit = "79f7f1408b2c811904c983419b45139e654f1e46",
                        Digest = "sha256:00f0f9fcfc083e931004ceaa914633990ad7e389ce8d21012b97af5844f501b4",
                        SourceUri = "https://codeload.github.com/hez2010/runtime/tar.gz/79f7f1408b2c811904c983419b45139e654f1e46"
                    },
                    ["const-generics-ilspy-source"] = new()
                    {
                        Kind = "source",
                        ResolvedVersion = "a2042c704a935a5402c6d700626e52702866ed6d",
                        Commit = "a2042c704a935a5402c6d700626e52702866ed6d",
                        Digest = "sha256:2b4f80b014d2bdacb71031c42cf3df6c9fef8194f5b4cc2a39c473f2c9f44b7e",
                        SourceUri = "https://codeload.github.com/ilyfairy/ILSpy/tar.gz/a2042c704a935a5402c6d700626e52702866ed6d"
                    },
                    ["artifacts-const-generics"] = new()
                    {
                        Kind = "artifact-processor",
                        ResolvedVersion = "a2042c704a93-79f7f1408b2c-ctarg-v1",
                        Commit = "a2042c704a935a5402c6d700626e52702866ed6d",
                        Digest = "sha256:2b4f80b014d2bdacb71031c42cf3df6c9fef8194f5b4cc2a39c473f2c9f44b7e",
                        SourceUri = "https://github.com/ilyfairy/ILSpy/tree/a2042c704a935a5402c6d700626e52702866ed6d"
                    },
                    ["il-assembler"] = new()
                    {
                        Kind = "artifact-processor",
                        ResolvedVersion = "0.1.0"
                    },
                    ["const-generics-versiontools"] = new()
                    {
                        Kind = "build-dependency",
                        ResolvedVersion = "8.0.0-beta.23516.4",
                        Package = "Microsoft.DotNet.VersionTools.Tasks",
                        SourceUri = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/flat2/microsoft.dotnet.versiontools.tasks/8.0.0-beta.23516.4/microsoft.dotnet.versiontools.tasks.8.0.0-beta.23516.4.nupkg",
                        Digest = "sha256:ad0a9c0ef28dd49bd2bfd7eb1be7ec355bd11a9671c0e2d8f1c08016b56be1bf"
                    }
                }
            };
            var documentComponents = Assert.IsType<Dictionary<string, LockedComponent>>(document.Components);
            foreach (var (componentId, component) in sourceLock.Components.Where(static pair =>
                         pair.Key.StartsWith("netfx", StringComparison.Ordinal) &&
                         pair.Key.EndsWith("-managed-ref", StringComparison.Ordinal)))
            {
                documentComponents.TryAdd(componentId, component);
            }
            foreach (var componentId in new[]
                     {
                         "netcoreapp2.0-ref",
                         "netcoreapp2.1-ref",
                         "netcoreapp2.2-ref",
                         "netcoreapp3.0-ref",
                         "netcoreapp3.1-ref",
                         "net5-ref",
                         "net6-ref",
                         "net7-ref",
                         "net8-ref",
                         "net9-ref",
                         "net10-ref",
                         "net11-preview-ref"
                     })
            {
                documentComponents.TryAdd(componentId, sourceLock.Components[componentId]);
            }
            File.WriteAllText(
                LockPath,
                JsonSerializer.Serialize(document, LockJsonOptions) + Environment.NewLine);
            var catalogPath = Path.Combine(Root, "profiles", "catalog", "catalog.json");
            var template = JsonSerializer.Deserialize<CatalogDocument>(
                File.ReadAllText(catalogPath),
                WebJsonOptions)
                ?? throw new InvalidOperationException("Repository catalog is invalid.");
            File.WriteAllText(
                catalogPath,
                JsonSerializer.Serialize(
                    RestrictSelectableRuntimesToLock(template, document),
                    LockJsonOptions) + Environment.NewLine);
            ActiveDigest = Digest(File.ReadAllBytes(LockPath));
        }

        public string Root { get; }
        public string LockPath { get; }
        public string ActiveDigest { get; }
        public string StateRoot => Path.Combine(Root, "artifacts", "profile-updater");
        public string PublicStatusPath => Path.Combine(StateRoot, "status.public.json");

        public ProfileUpdateWorkflow CreateWorkflow(IProfileUpdateCommandRunner runner) =>
            new(
                Root,
                LockPath,
                StateRoot,
                new FakeProfileSourceClient(),
                runner,
                workspaceManager: new CopyProfileCandidateWorkspaceManager());

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }

        private static CatalogDocument RestrictSelectableRuntimesToLock(
            CatalogDocument catalog,
            ReleaseLockDocument releaseLock)
        {
            var referenceSetIds = catalog.ReferenceSets
                .Where(referenceSet => HasComponent(releaseLock, referenceSet.Id, "reference-set") ||
                                       IsResolvedBySyntheticChannel(referenceSet.Id))
                .Select(static referenceSet => referenceSet.Id)
                .ToHashSet(StringComparer.Ordinal);
            return catalog with
            {
                Toolchains = catalog.Toolchains
                    .Select(toolchain => toolchain with
                    {
                        AllowedReferenceSetIds = toolchain.AllowedReferenceSetIds
                            .Where(referenceSetIds.Contains)
                            .ToArray()
                    })
                    .ToArray(),
                ReferenceSets = catalog.ReferenceSets
                    .Where(referenceSet => referenceSetIds.Contains(referenceSet.Id))
                    .ToArray(),
                Runtimes = catalog.Runtimes
                    .Select(runtime => HasComponent(releaseLock, runtime.Id, "runtime")
                        ? runtime
                        : runtime with
                        {
                            Availability = new ComponentAvailability
                            {
                                Installed = false,
                                Health = "unavailable",
                                Reason = "Not represented by this synthetic release lock."
                            }
                        })
                    .ToArray(),
                Compatibility = catalog.Compatibility
                    .Where(rule => rule.Kind != CompatibilityRuleKind.ToolchainReferenceSet ||
                                   referenceSetIds.Contains(rule.ToId))
                    .ToArray(),
                Presets = catalog.Presets
                    .Where(preset => referenceSetIds.Contains(preset.ReferenceSetId))
                    .ToArray()
            };
        }

        private static bool HasComponent(
            ReleaseLockDocument releaseLock,
            string id,
            string kind) =>
            releaseLock.Components.TryGetValue(id, out var component) &&
            string.Equals(component.Kind, kind, StringComparison.Ordinal);

        private static bool IsResolvedBySyntheticChannel(string referenceSetId) =>
            referenceSetId is "net10-ref" or "net11-preview-ref";

        private static void NormalizeDevelopmentRuntimeProfileImage(string path, string runtimeProfileId)
        {
            var profile = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException($"Runtime profile '{runtimeProfileId}' is invalid.");
            var image = profile["image"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"Runtime profile '{runtimeProfileId}' has no image.");
            if (image.Contains('@'))
                profile["image"] = $"{image[..image.IndexOf('@')]}:development";
            File.WriteAllText(path, profile.ToJsonString() + "\n");
        }
    }

    private sealed class FakeProfileSourceClient : IProfileSourceClient
    {
        public Task<DotNetChannelResolution> ResolveDotNetChannelAsync(
            string channel,
            CancellationToken cancellationToken = default)
        {
            var version = channel == "10.0" ? "10.0.9" : "11.0.0-preview.5";
            var sdk = channel == "10.0" ? "10.0.301" : "11.0.100-preview.5";
            return Task.FromResult(new DotNetChannelResolution(
                channel,
                version,
                channel == "10.0" ? "901ca941248413c79832d2fdbd709da0c4386353" : "f7b4c5716faaee8fb8a289aed29118cad955c45f",
                channel == "10.0" ? "901ca941248413c79832d2fdbd709da0c4386353" : "f7b4c5716faaee8fb8a289aed29118cad955c45f",
                new Uri($"https://example.test/dotnet-{version}.tar.gz"),
                new string('a', 128),
                sdk,
                new Uri($"https://example.test/sdk-{sdk}.tar.gz"),
                new string('b', 128),
                new DateOnly(2026, 7, 11)));
        }

        public Task<NuGetPackageResolution> ResolveLatestStablePackageAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Package(packageId, packageId switch
            {
                "Microsoft.CodeAnalysis.CSharp" => "5.6.0",
                "FSharp.Compiler.Service" => "43.12.204",
                "FSharp.Core" => "10.1.204",
                "ICSharpCode.Decompiler" => "10.1.0.8386",
                "Microsoft.ILVerification" => "10.0.9",
                _ => "1.0.0"
            }));

        public Task<NuGetPackageResolution> ResolveExactPackageAsync(
            string packageId,
            string version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Package(packageId, version));

        public Task<GitCommitResolution> ResolveGitCommitAsync(
            string owner,
            string repository,
            string branch,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(repository, "gsharp", StringComparison.Ordinal))
            {
                Assert.Equal("v0.3.33", branch);
                const string commit = "aaf35bb8d5e1e8704e982ad0ab95263451bd2d3d";
                return Task.FromResult(new GitCommitResolution(
                    commit,
                    new Uri("https://github.com/DavidObando/gsharp"),
                    new Uri($"https://github.com/DavidObando/gsharp/archive/{commit}.tar.gz"),
                    "f52d21ef09b198bad69b7ac8dd5f6d2eaa91216b80bfc22e9610a1fef28f06d4",
                    "0.3.33"));
            }

            return Task.FromResult(new GitCommitResolution(
                new string('a', 40),
                new Uri("https://github.com/dotnet/roslyn"),
                new Uri($"https://github.com/dotnet/roslyn/archive/{new string('a', 40)}.tar.gz"),
                new string('c', 64),
                "5.10.0"));
        }

        private static NuGetPackageResolution Package(string packageId, string version) =>
            new(
                packageId,
                version,
                new Uri($"https://example.test/{packageId}/{version}.nupkg"),
                packageId switch
                {
                    "Peachpie.CodeAnalysis" =>
                        "sha512-Q1XzhqGM3cR1FW5hWh7JIfjCCNtmNM1u0HW1nM0UCyl4X5MM7cM9dxeBjmWETIumKpP/8yj19WXF0wRmfQgaew==",
                    "Peachpie.Runtime" =>
                        "sha512-ZltR4twzl0KMPAa91xiqwqFZMnvj8NPB3EIt24sWztTbstWekrUxjhAsFtCQdn7xdZceSoG4Sg+zHOopVOjMxA==",
                    "Peachpie.Library" =>
                        "sha512-5BA1KJ2M0zsqTRrqHL7zbT1zpEDI2mILkxkmGXiQm3G0i+GkPv50/Kt1mQqHHLCTx5I7r67Ny6oTgn4TarP8pg==",
                    _ => "sha512-test-content-hash"
                },
                new string(packageId == "Microsoft.NETCore.App.Ref" ? 'd' : 'e', 128));
    }

    private sealed class CopyProfileCandidateWorkspaceManager : IProfileCandidateWorkspaceManager
    {
        public Task PrepareAsync(
            string repositoryRoot,
            string workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            foreach (var source in Directory.EnumerateFiles(repositoryRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(repositoryRoot, source);
                if (relative.Split(Path.DirectorySeparatorChar).Any(static segment => segment == "artifacts"))
                    continue;
                var destination = Path.Combine(workspaceRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSourceDateEpochReader(string? epoch) : ISourceDateEpochReader
    {
        public List<string> Revisions { get; } = [];

        public Task<string?> ReadAsync(
            string repositoryRoot,
            string revision,
            CancellationToken cancellationToken = default)
        {
            Revisions.Add(revision);
            return Task.FromResult(epoch);
        }
    }

    private sealed class TempChannelDirectory : IDisposable
    {
        private const string DefaultRuntimeYaml = """
            id: runtime
            kind: runtime-channel
            source:
              type: dotnet-release-metadata
              channel: "10.0"
              policy: latest-release
            referenceSet:
              id: reference
              package: Microsoft.NETCore.App.Ref
            platform:
              os: linux
              libc: glibc
              architecture: x64
            update:
              pollInterval: 6h
              autoPromoteAfterTests: false
              retainLastKnownGood: true
            """;

        public TempChannelDirectory(string? runtimeYaml = null, string? toolchainsYaml = null)
        {
            Root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.Channels.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "runtime.yaml"), runtimeYaml ?? DefaultRuntimeYaml);
            File.WriteAllText(
                Path.Combine(Root, "toolchains.yaml"),
                toolchainsYaml ?? """
                    channels:
                      - id: compiler
                        kind: toolchain
                        source: { type: nuget, package: Compiler, policy: latest-stable }
                    update:
                      pollInterval: 6h
                      autoPromoteAfterTests: false
                    """);
        }

        public string Root { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static string ExtractNamedBlock(string source, string kind, string name)
    {
        var marker = $"{kind} \"{name}\"";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Bake source does not contain {marker}.");
        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"Bake block {marker} has no opening brace.");
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return source[start..(index + 1)];
                    break;
            }
        }

        throw new InvalidOperationException($"Bake block {marker} is not closed.");
    }

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

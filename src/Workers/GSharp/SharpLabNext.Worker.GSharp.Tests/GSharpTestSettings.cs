using Microsoft.Extensions.Logging.Abstractions;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Testing;
using System.Text.Json;

namespace SharpLabNext.Worker.GSharp.Tests;

internal static class GSharpTestSettings
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string CompilerVersion => StableToolchain.CompilerVersion;

    public static string CompilerCommit => StableToolchain.CompilerCommit;

    public static string CompilerPath => StableToolchain.CompilerAssemblyPath;

    public static string LanguageServerPath => StableToolchain.LanguageServerAssemblyPath;

    public static GSharpToolchainProfile StableToolchain { get; } = CreateToolchain(GSharpToolchain.ToolchainId);

    public static GSharpToolchainProfile LegacyToolchain { get; } = CreateToolchain(GSharpToolchain.LegacyToolchainId);

    public static string CreateRoot()
    {
        EnsureToolsExist();
        var root = Path.Combine(Path.GetTempPath(), "SharpLabNext-GSharpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static GSharpWorkerSettings CreateSettings(string root) => new(
        new GSharpWorkerIdentity("test-release", $"sha256:{new string('a', 64)}"),
        new GSharpProcessLimits(1024 * 1024, 512L * 1024 * 1024, TimeSpan.FromMinutes(5)),
        Path.Combine(root, "work"),
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        new Dictionary<string, GSharpToolchainProfile>(StringComparer.Ordinal)
        {
            [StableToolchain.ToolchainId] = StableToolchain,
            [LegacyToolchain.ToolchainId] = LegacyToolchain
        },
        ReferenceSets());

    public static GSharpBuildService CreateBuildService(string root, out GSharpCompilerProcess compiler)
    {
        var settings = CreateSettings(root);
        var manifest = LoadManifest();
        var referenceSets = new GSharpReferenceSetProvider(settings.ReferenceSets.Where(static item => item.Id == "net10-ref").ToArray(), requireAttestation: false);
        compiler = new GSharpCompilerProcess(settings, manifest, NullLogger<GSharpCompilerProcess>.Instance);
        return new GSharpBuildService(referenceSets, compiler, settings, manifest);
    }

    public static LanguageWorkerCapabilityManifest LoadManifest() =>
        LanguageWorkerCapabilityManifestSerializer.Load(Path.Combine(RepositoryRoot, "src", "Workers", "GSharp", "SharpLabNext.Worker.GSharp", "language-worker.json"));

    public static BuildRequest CreateRequest(BuildTarget target, string source, BuildOutputKind outputKind = BuildOutputKind.Console, string toolchainId = GSharpToolchain.ToolchainId) =>
        CreateRequest(target, [new WorkspaceFile("Program.gs", 1, source)], ["Program.gs"], outputKind, toolchainId);

    public static BuildRequest CreateRequest(BuildTarget target, IReadOnlyList<WorkspaceFile> files, IReadOnlyList<string> sourceOrder, BuildOutputKind outputKind = BuildOutputKind.Console, string toolchainId = GSharpToolchain.ToolchainId)
    {
        var toolchain = toolchainId == StableToolchain.ToolchainId
            ? StableToolchain : toolchainId == LegacyToolchain.ToolchainId
                ? LegacyToolchain : throw new ArgumentOutOfRangeException(nameof(toolchainId));
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, outputKind, AllowUnsafe: false, EmitPortablePdb: true, NullableContextMode.Disable, LanguageVersion: toolchain.CompilerVersion);
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 7, 3, GSharpToolchain.LanguageId, files, files[0].Path, sourceOrder, "net10-ref", options);
        return new BuildRequest($"request-{Guid.NewGuid():N}", $"idempotency-{Guid.NewGuid():N}", "pipeline-gsharp-test", toolchainId, workspace.ReferenceSetId, workspace, DateTimeOffset.UtcNow.AddSeconds(30), options, target);
    }

    public static IReadOnlyDictionary<string, string?> WebHostConfiguration(string root) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GSharp:ReleaseId"] = "content",
            ["GSharp:WorkerImageId"] = $"sha256:{new string('0', 64)}",
            ["GSharp:DotNetHostPath"] = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [$"GSharp:Toolchains:{StableToolchain.ToolchainId}:CompilerVersion"] = StableToolchain.CompilerVersion,
            [$"GSharp:Toolchains:{StableToolchain.ToolchainId}:CompilerCommit"] = StableToolchain.CompilerCommit,
            [$"GSharp:Toolchains:{StableToolchain.ToolchainId}:CompilerAssemblyPath"] = StableToolchain.CompilerAssemblyPath,
            [$"GSharp:Toolchains:{StableToolchain.ToolchainId}:LanguageServerAssemblyPath"] = StableToolchain.LanguageServerAssemblyPath,
            [$"GSharp:Toolchains:{LegacyToolchain.ToolchainId}:CompilerVersion"] = LegacyToolchain.CompilerVersion,
            [$"GSharp:Toolchains:{LegacyToolchain.ToolchainId}:CompilerCommit"] = LegacyToolchain.CompilerCommit,
            [$"GSharp:Toolchains:{LegacyToolchain.ToolchainId}:CompilerAssemblyPath"] = LegacyToolchain.CompilerAssemblyPath,
            [$"GSharp:Toolchains:{LegacyToolchain.ToolchainId}:LanguageServerAssemblyPath"] = LegacyToolchain.LanguageServerAssemblyPath,
            ["GSharp:WorkRoot"] = Path.Combine(root, "web-work"),
            ["GSharp:MaximumProcessOutputBytes"] = (1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["GSharp:MaximumProcessWorkingSetBytes"] = (512L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ReferenceSets:net10-ref:Path"] = TestReferenceSets.Net10.Path,
            ["ReferenceSets:net10-ref:TargetFramework"] = "net10.0",
            ["ReferenceSets:net10-ref:FrameworkVersion"] = TestReferenceSets.Net10.Version,
            ["ReferenceSets:net10-ref:Digest"] = TestReferenceSets.Net10.Digest
        };

    public static void DeleteRoot(string root)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static IReadOnlyList<GSharpReferenceSetDefinition> ReferenceSets() =>
    [
        new("net10-ref", TestReferenceSets.Net10.Path, "net10.0", TestReferenceSets.Net10.Version, TestReferenceSets.Net10.Digest, null)
    ];

    private static void EnsureToolsExist()
    {
        foreach (var toolchain in new[] { StableToolchain, LegacyToolchain })
        {
            if (!File.Exists(toolchain.CompilerAssemblyPath) || !File.Exists(toolchain.LanguageServerAssemblyPath))
            {
                throw new InvalidOperationException($"The fixed G# v{toolchain.CompilerVersion} compiler and language server must be built under " + $"artifacts/source-cache/gsharp-v{toolchain.CompilerVersion}/out/bin/Release before running G# worker tests.");
            }
        }
    }

    private static GSharpToolchainProfile CreateToolchain(string toolchainId)
    {
        var version = LockedToolchainProperty(toolchainId, "resolvedVersion");
        var sourceRoot = Path.Combine(RepositoryRoot, "artifacts", "source-cache", $"gsharp-v{version}", "out", "bin", "Release");
        return new GSharpToolchainProfile(toolchainId, version, LockedToolchainProperty(toolchainId, "commit"), Path.Combine(sourceRoot, "Compiler", "gsc.dll"), Path.Combine(sourceRoot, "LanguageServer", "GSharp.LanguageServer.dll"));
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
        throw new InvalidOperationException("The SharpLabNext repository root could not be located.");
    }

    private static string LockedToolchainProperty(string toolchainId, string propertyName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "profiles", "lock.json")));
        var component = document.RootElement.GetProperty("components").GetProperty(toolchainId);
        return component.GetProperty(propertyName).GetString() ?? throw new InvalidDataException($"profiles/lock.json {toolchainId}.{propertyName} is missing.");
    }
}

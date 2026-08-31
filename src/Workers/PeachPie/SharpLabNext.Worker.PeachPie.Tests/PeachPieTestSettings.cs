using System.Globalization;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Testing;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.PeachPie.Tests;

internal static class PeachPieTestSettings
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string WorkerAssemblyPath => typeof(PeachPieToolchain).Assembly.Location;

    public static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpLabNext-PeachPieTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static PeachPieWorkerSettings CreateSettings(string root, bool isolated) => new(
        new PeachPieWorkerIdentity("test-release", PeachPieToolchain.CompilerVersion, PeachPieToolchain.CompilerCommit, $"sha256:{new string('a', 64)}"),
        CompilerProcessIsolationOptions.Default with { Enabled = isolated, MaximumConcurrentProcesses = 4, MaximumResponseBytes = 64 * 1024 * 1024 },
        Path.Combine(root, "work"),
        Path.Combine(AppContext.BaseDirectory, PeachPieToolchain.RuntimeAssemblyName),
        Path.Combine(AppContext.BaseDirectory, PeachPieToolchain.LibraryAssemblyName),
        GetMonoUnixNativeLibraryPath(),
        ReferenceSets());

    public static LanguageWorkerCapabilityManifest LoadManifest()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "language-worker.json");
        var path = File.Exists(outputPath)
            ? outputPath : Path.Combine(RepositoryRoot, "src", "Workers", "PeachPie", "SharpLabNext.Worker.PeachPie", "language-worker.json");
        return LanguageWorkerCapabilityManifestSerializer.Load(path);
    }

    public static PeachPieBuildService CreateBuildService(string root)
    {
        var settings = CreateSettings(root, isolated: false);
        return CreateBuildService(settings);
    }

    public static PeachPieBuildService CreateBuildService(PeachPieWorkerSettings settings)
    {
        var manifest = LoadManifest();
        var referenceSets = new PeachPieReferenceSetProvider(settings.ReferenceSets, requireAttestation: false);
        return new PeachPieBuildService(referenceSets, new PeachPieCompiler(referenceSets, settings, manifest), new UnexpectedCompilerProcessRunner(), settings, manifest);
    }

    public static CompilerProcessRunner CreateCompilerProcessRunner(PeachPieWorkerSettings settings) => new(
        settings.BuildProcess,
        new CompilerProcessCommand(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [WorkerAssemblyPath, PeachPieCompilerChild.ChildArgument],
            AppContext.BaseDirectory,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DOTNET_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            }));

    public static BuildRequest CreateRequest(BuildTarget target, string source, string path = "Program.php") =>
        CreateRequest(target, [new WorkspaceFile(path, 1, source)], [path], path);

    public static BuildRequest CreateRequest(BuildTarget target, IReadOnlyList<WorkspaceFile> files, IReadOnlyList<string> sourceOrder, string activeFile)
    {
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, BuildOutputKind.Console, AllowUnsafe: false, EmitPortablePdb: true, NullableContextMode.Disable, LanguageVersion: "8.5");
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 7, 3, PeachPieToolchain.LanguageId, files, activeFile, sourceOrder, "net10-ref", options);
        return new BuildRequest($"request-{Guid.NewGuid():N}", $"idempotency-{Guid.NewGuid():N}", "pipeline-peachpie-test", PeachPieToolchain.ToolchainId, workspace.ReferenceSetId, workspace, DateTimeOffset.UtcNow.AddSeconds(60), options, target);
    }

    public static IReadOnlyDictionary<string, string?> WebHostConfiguration(string root) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PeachPie:ReleaseId"] = "content",
            ["PeachPie:CompilerVersion"] = PeachPieToolchain.CompilerVersion,
            ["PeachPie:CompilerCommit"] = PeachPieToolchain.CompilerCommit,
            ["PeachPie:WorkerImageId"] = $"sha256:{new string('0', 64)}",
            ["PeachPie:WorkRoot"] = Path.Combine(root, "web-work"),
            ["PeachPie:BuildProcess:Enabled"] = "true",
            ["PeachPie:BuildProcess:MaximumConcurrentProcesses"] = "4",
            ["PeachPie:BuildProcess:MaximumWorkingSetBytes"] = (512L * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            ["PeachPie:BuildProcess:MaximumRequestBytes"] = (2 * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            ["PeachPie:BuildProcess:MaximumResponseBytes"] = (64 * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            ["PeachPie:BuildProcess:MaximumStandardErrorBytes"] = (64 * 1024).ToString(CultureInfo.InvariantCulture),
            ["PeachPie:BuildProcess:MemoryPollIntervalMilliseconds"] = "25",
            ["ReferenceSets:net10-ref:Path"] = GetReferencePath(),
            ["ReferenceSets:net10-ref:TargetFramework"] = "net10.0",
            ["ReferenceSets:net10-ref:FrameworkVersion"] = TestReferenceSets.Net10.Version,
            ["ReferenceSets:net10-ref:Digest"] = TestReferenceSets.Net10.Digest
        };

    public static void ApplyProcessEnvironment(string root)
    {
        foreach (var (key, value) in WebHostConfiguration(root))
            Environment.SetEnvironmentVariable(key.Replace(":", "__", StringComparison.Ordinal), value);
    }

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
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static IReadOnlyList<PeachPieReferenceSetDefinition> ReferenceSets() =>
    [
        new("net10-ref", GetReferencePath(), "net10.0", TestReferenceSets.Net10.Version, TestReferenceSets.Net10.Digest)
    ];

    private static string GetMonoUnixNativeLibraryPath() => Path.Combine(AppContext.BaseDirectory, "runtimes", PeachPieToolchain.NativeRuntimeIdentifier, "native", PeachPieToolchain.MonoUnixNativeLibraryName);

    private static string GetReferencePath() => TestReferenceSets.Net10.Path;

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

    private sealed class UnexpectedCompilerProcessRunner : ICompilerProcessRunner
    {
        public Task<TResponse> RunAsync<TRequest, TResponse>(string childArgument, TRequest request, TimeSpan timeout, CancellationToken cancellationToken)
            where TRequest : class
            where TResponse : class =>
            throw new InvalidOperationException("The in-process test unexpectedly invoked compiler isolation.");
    }
}

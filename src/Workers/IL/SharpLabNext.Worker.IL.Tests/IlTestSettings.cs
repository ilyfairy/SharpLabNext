using SharpLabNext.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Worker.IL.Compiler;

namespace SharpLabNext.Worker.IL.Tests;

internal static class IlTestSettings
{
    public static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpLabNext", "il-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        CreateReferenceSet(root, "net10-ref");
        CreateReferenceSet(root, "net11-preview-ref");
        return root;
    }

    public static IlWorkerSettings Create(string root) => new(
        new IlWorkerIdentity("test-release", "mobius-ilasm-stable", IlCompilerProtocol.PackageVersion, null, $"sha256:{new string('a', 64)}"),
        IlCompilationLimits.Default with { MaxBuildMilliseconds = 20_000, MaxConcurrentBuilds = 2 },
        IlLspLimits.Default with { DiagnosticsDebounceMilliseconds = 1 },
        new IlDevelopmentArtifactEnvelopeOptions(true, 16 * 1024 * 1024),
        new ArtifactBundlePublishingOptions(new Uri("http://artifact-store:8080"), TimeSpan.FromHours(1)),
        Path.Combine(root, "work"),
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        typeof(IlCompilerProtocol).Assembly.Location,
        [
            new IlReferenceSetDefinition("net10-ref", Path.Combine(root, "reference-sets", "net10-ref"), "net10.0", "10.0.9"),
            new IlReferenceSetDefinition("net11-preview-ref", Path.Combine(root, "reference-sets", "net11-preview-ref"), "net11.0", "11.0.0-preview.5.26302.115")
        ]);

    public static BuildRequest CreateRequest(BuildTarget target, IReadOnlyList<WorkspaceFile> files, IReadOnlyList<string> sourceOrder, BuildOutputKind outputKind = BuildOutputKind.Console, string referenceSetId = "net10-ref")
    {
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, outputKind, AllowUnsafe: false, EmitPortablePdb: true, NullableContextMode.Disable, LanguageVersion: "ecma-335");
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 1, 2, "il", files, files[0].Path, sourceOrder, referenceSetId, options);
        return new BuildRequest($"request-{Guid.NewGuid():N}", $"idempotency-{Guid.NewGuid():N}", "pipeline-test", "mobius-ilasm-stable", referenceSetId, workspace, DateTimeOffset.UtcNow.AddSeconds(30), options, target);
    }

    public static IReadOnlyList<WorkspaceFile> ValidMultiFileWorkspace() =>
    [
        new WorkspaceFile("Program.il", 1, """
            .assembly SharpLabNextMulti {}
            .module SharpLabNextMulti.dll
            .class public auto ansi Program extends [System.Runtime]System.Object
            {
              .method public hidebysig static void Main() cil managed
              {
                .entrypoint
                .maxstack 0
                call void Helper::Ping()
                ret
              }
            }
            """),
        new WorkspaceFile("Helper.il", 1, """
            .class public auto ansi abstract sealed Helper extends [System.Runtime]System.Object
            {
              .method public hidebysig static void Ping() cil managed
              {
                .maxstack 0
                ret
              }
            }
            """)
    ];

    public static void DeleteRoot(string root)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void CreateReferenceSet(string root, string id)
    {
        var referencePath = Path.Combine(root, "reference-sets", id);
        Directory.CreateDirectory(referencePath);
        var frameworkReferences = FindNet10ReferencePack();
        foreach (var fileName in new[]
                 {
                     "System.Console.dll",
                     "System.Collections.dll",
                     "System.Runtime.dll",
                     "netstandard.dll"
                 })
        {
            File.Copy(Path.Combine(frameworkReferences, fileName), Path.Combine(referencePath, fileName));
        }
        File.Copy(typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location, Path.Combine(referencePath, "SharpLab.Runtime.dll"));
    }

    private static string FindNet10ReferencePack()
    {
        var runtimeDirectory = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent?.FullName ?? throw new InvalidOperationException("The dotnet installation root could not be located.");
        var packRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        var candidates = Directory.Exists(packRoot)
            ? Directory.EnumerateDirectories(packRoot).Select(static versionRoot => Path.Combine(versionRoot, "ref", "net10.0")).Where(Directory.Exists).OrderByDescending(static path => path, StringComparer.Ordinal).ToArray() : [];
        return candidates.FirstOrDefault() ?? throw new InvalidOperationException("A .NET 10 reference pack is required to run the IL worker tests.");
    }
}

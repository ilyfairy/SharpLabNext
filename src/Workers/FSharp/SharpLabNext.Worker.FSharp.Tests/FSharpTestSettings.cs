using SharpLabNext.Worker.FSharp.Compiler;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.FSharp.Tests;

internal static class FSharpTestSettings
{
    public const string Net11PreviewVersion = "11.0.0-preview.5.26302.115";

    public static FSharpWorkerSettings Create(string workRoot, FSharpAstLimits? astLimits = null) => new(
        new FSharpWorkerIdentity(
            "test-release",
            "fsharp-stable",
            FSharpCompilerFacade.CompilerVersion,
            FSharpCompilerFacade.FSharpCorePackageVersion,
            null,
            $"sha256:{new string('a', 64)}"),
        FSharpCompilationLimits.Default,
        astLimits ?? FSharpAstLimits.Default,
        FSharpLspLimits.Default,
        CompilerProcessIsolationOptions.Default with { Enabled = false },
        new FSharpDevelopmentArtifactEnvelopeOptions(true, 8 * 1024 * 1024),
        new ArtifactBundlePublishingOptions(
            new Uri("http://artifact-store:8080"),
            TimeSpan.FromHours(1)),
        workRoot,
        [
            new FSharpReferenceSetDefinition("net10-ref", GetNet10ReferencePath(), "net10.0", "10.0.9"),
            new FSharpReferenceSetDefinition(
                "net11-preview-ref",
                GetNet11PreviewReferencePath(),
                "net11.0",
                Net11PreviewVersion)
        ]);

    public static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpLabNext-FSharpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    public static string GetNet10ReferencePath()
        => GetReferencePath("SHARPLABNEXT_NET10_REF_PATH", "10.0.9", "net10.0");

    public static string GetNet11PreviewReferencePath()
        => GetReferencePath("SHARPLABNEXT_NET11_REF_PATH", Net11PreviewVersion, "net11.0");

    private static string GetReferencePath(string environmentVariable, string version, string targetFramework)
    {
        var explicitPath = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath))
            return explicitPath;
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            "/usr/share/dotnet",
            "/usr/local/share/dotnet"
        };
        foreach (var root in roots.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            var candidate = Path.Combine(root!, "packs", "Microsoft.NETCore.App.Ref", version, "ref", targetFramework);
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException($"The .NET {version} reference pack was not found.");
    }
}

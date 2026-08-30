using SharpLabNext.Worker.FSharp.Compiler;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Testing;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.FSharp.Tests;

internal static class FSharpTestSettings
{
    public static string Net10Version => TestReferenceSets.Net10.Version;

    public static string Net11PreviewVersion => TestReferenceSets.Net11.Version;

    public static FSharpWorkerSettings Create(string workRoot, FSharpAstLimits? astLimits = null) => new(
        new FSharpWorkerIdentity("test-release", "fsharp-stable", FSharpCompilerFacade.CompilerVersion, FSharpCompilerFacade.FSharpCorePackageVersion, null, $"sha256:{new string('a', 64)}"),
        FSharpCompilationLimits.Default,
        astLimits ?? FSharpAstLimits.Default,
        FSharpLspLimits.Default,
        CompilerProcessIsolationOptions.Default with { Enabled = false },
        new FSharpDevelopmentArtifactEnvelopeOptions(true, 8 * 1024 * 1024),
        new ArtifactBundlePublishingOptions(new Uri("http://artifact-store:8080"), TimeSpan.FromHours(1)),
        workRoot,
        [
            new FSharpReferenceSetDefinition("net10-ref", GetNet10ReferencePath(), "net10.0", Net10Version),
            new FSharpReferenceSetDefinition("net11-preview-ref", GetNet11PreviewReferencePath(), "net11.0", Net11PreviewVersion)
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

    public static string GetNet10ReferencePath() => TestReferenceSets.Net10.Path;

    public static string GetNet11PreviewReferencePath() => TestReferenceSets.Net11.Path;
}

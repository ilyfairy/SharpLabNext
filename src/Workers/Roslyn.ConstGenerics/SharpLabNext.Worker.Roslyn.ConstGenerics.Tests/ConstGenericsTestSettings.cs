using SharpLabNext.Worker.Roslyn;

namespace SharpLabNext.Worker.Roslyn.ConstGenerics.Tests;

internal static class ConstGenericsTestSettings
{
    public static string CompilerVersion => CSharpBuildService.GetLoadedCompilerVersion();

    public static string ExpectedCompilerCommit => RequiredSourceBuildEnvironment("RoslynWorker__CompilerCommit");

    public static string ReferenceVersion => IsSourceBuild
        ? RequiredSourceBuildEnvironment("ReferenceSets__const-generics-ref__FrameworkVersion") : "0.0.0-test";

#if CONST_GENERICS_SOURCE_BUILD
    public static bool IsSourceBuild => true;
#else
    public static bool IsSourceBuild => false;
#endif

    public static RoslynWorkerIdentity CreateIdentity() => new("development", "roslyn-const-generics", CompilerVersion, IsSourceBuild ? ExpectedCompilerCommit : null, "test-worker-image")
    {
        SupportedLanguageIds = ["csharp"],
        ArtifactRuntimeFamily = "coreclr-const-generics",
        RequiredRuntimeFeatureTags = ["runtime.const-generics.v1"],
        MetadataFeatureTags = ["metadata.const-generics.v1"],
        CompatibilityGroup = "const-generics-bcaed316"
    };

    public static string GetReferencePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("SHARPLABNEXT_CONST_GENERICS_REF_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath))
            return explicitPath;

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            "/usr/share/dotnet",
            "/usr/local/share/dotnet"
        };
        foreach (var root in roots.Where(static root => !string.IsNullOrWhiteSpace(root)))
        {
            var packs = Path.Combine(root!, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packs))
                continue;

            var candidate = Directory.EnumerateDirectories(packs).OrderDescending(StringComparer.Ordinal).Select(path => Path.Combine(path, "ref", "net8.0")).FirstOrDefault(Directory.Exists);
            if (candidate is not null)
                return candidate;
        }

        throw new InvalidOperationException("The ConstGenerics reference set was not found. Set SHARPLABNEXT_CONST_GENERICS_REF_PATH explicitly.");
    }

    private static string RequiredSourceBuildEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Source-build tests require the lock-derived environment value '{name}'.");
}

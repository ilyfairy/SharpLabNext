using System.Runtime.InteropServices;
using SharpLabNext.Contracts;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.SampleLanguage.Worker;

internal static class MiniLanguageReferenceSetAttestations
{
    public static IReadOnlyList<ReferenceSetAttestation> Load(
        IConfiguration configuration,
        IHostEnvironment environment,
        IReadOnlyList<string> referenceSetIds)
    {
        var requireManifest = environment.IsProduction() ||
            configuration.GetValue("ReferenceSetAttestation:Required", false);
        return referenceSetIds.Select(id =>
        {
            var section = configuration.GetSection($"ReferenceSets:{id}");
            var targetFramework = Required(section["TargetFramework"], $"ReferenceSets:{id}:TargetFramework");
            var resolvedVersion = Required(section["FrameworkVersion"], $"ReferenceSets:{id}:FrameworkVersion");
            var configuredPath = Required(section["Path"], $"ReferenceSets:{id}:Path");
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
            if (!requireManifest && !Directory.Exists(root))
                root = FindInstalledReferencePack(resolvedVersion, targetFramework);
            return ReferenceSetAttestationReader.LoadAndVerify(
                root,
                id,
                targetFramework,
                resolvedVersion,
                section["Digest"],
                requireManifest,
                section["AttestationPath"]);
        }).ToArray();
    }

    private static string FindInstalledReferencePack(string version, string targetFramework)
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtime.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("The dotnet installation root could not be resolved.");
        var path = Path.Combine(
            dotnetRoot,
            "packs",
            "Microsoft.NETCore.App.Ref",
            version,
            "ref",
            targetFramework);
        if (!Directory.Exists(path))
            throw new InvalidOperationException($"Reference pack '{version}/{targetFramework}' is not installed.");
        return path;
    }

    private static string Required(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Configuration value '{key}' is required.");
}

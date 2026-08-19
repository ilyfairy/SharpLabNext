using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace SharpLabNext.RuntimeProtocol;

internal sealed class RuntimeArtifactLoadContext(
    string entryAssemblyPath,
    Assembly sharedAssembly) : AssemblyLoadContext(isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);
    private readonly string _artifactDirectory = Path.GetDirectoryName(entryAssemblyPath)
        ?? throw new ArgumentException("The entry assembly has no parent directory.", nameof(entryAssemblyPath));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, sharedAssembly.GetName().Name, StringComparison.Ordinal))
            return sharedAssembly;

        var path = _resolver.ResolveAssemblyToPath(assemblyName) ?? ProbeManagedAssembly(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) ??
            ProbeUnmanagedLibrary(_artifactDirectory, RuntimeInformation.RuntimeIdentifier, unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }

    private string? ProbeManagedAssembly(AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name))
            return null;

        var candidate = Path.Combine(_artifactDirectory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    internal static string? ProbeUnmanagedLibrary(
        string artifactDirectory,
        string runtimeIdentifier,
        string unmanagedDllName)
    {
        if (!IsSimplePathSegment(runtimeIdentifier) || !IsSimplePathSegment(unmanagedDllName))
            return null;

        var names = CandidateLibraryNames(unmanagedDllName);
        var directories = new[]
        {
            Path.Combine(artifactDirectory, "runtimes", runtimeIdentifier, "native"),
            Path.Combine(artifactDirectory, "runtimes", runtimeIdentifier),
            artifactDirectory
        };
        foreach (var directory in directories)
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> CandidateLibraryNames(string name)
    {
        if (name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return [name];
        }

        return name.StartsWith("lib", StringComparison.Ordinal)
            ? [name, $"{name}.so"]
            : [name, $"lib{name}.so", $"{name}.so"];
    }

    private static bool IsSimplePathSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !Path.IsPathFullyQualified(value) &&
        value.IndexOf(Path.DirectorySeparatorChar) < 0 &&
        value.IndexOf(Path.AltDirectorySeparatorChar) < 0 &&
        value is not "." and not "..";
}

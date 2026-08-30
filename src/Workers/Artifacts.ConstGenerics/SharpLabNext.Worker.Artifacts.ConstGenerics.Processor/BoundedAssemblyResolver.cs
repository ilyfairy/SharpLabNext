using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.Metadata;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics.Processing;

internal sealed class BoundedAssemblyResolver : IAssemblyResolver, IDisposable
{
    private const int MaximumIndexedAssemblies = 4_096;
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MetadataFile> _cache = new(StringComparer.OrdinalIgnoreCase);

    public BoundedAssemblyResolver(string inputRoot, IReadOnlyList<string> referenceRoots)
    {
        Index(inputRoot, SearchOption.AllDirectories, replaceExisting: true);
        foreach (var root in referenceRoots)
            Index(root, SearchOption.TopDirectoryOnly, replaceExisting: false);
    }

    public MetadataFile? Resolve(IAssemblyReference reference) => ResolveName(reference.Name);

    public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));

    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) => ResolveName(Path.GetFileNameWithoutExtension(moduleName));

    public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));

    internal IReadOnlyDictionary<string, string> Paths => _paths;

    public void Dispose()
    {
        foreach (var module in _cache.Values)
        {
            if (module is IDisposable disposable)
                disposable.Dispose();
        }
        _cache.Clear();
    }

    private MetadataFile? ResolveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', '\0']) >= 0)
            return null;
        if (_cache.TryGetValue(name, out var cached))
            return cached;
        if (!_paths.TryGetValue(name, out var path))
            return null;
        var module = new PEFile(path, PEStreamOptions.PrefetchEntireImage);
        _cache.Add(name, module);
        return module;
    }

    private void Index(string root, SearchOption searchOption, bool replaceExisting)
    {
        var normalizedRoot = Path.GetFullPath(root);
        foreach (var path in Directory.EnumerateFiles(normalizedRoot, "*", searchOption).Where(static path => Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe" or ".winmd"))
        {
            if (_paths.Count >= MaximumIndexedAssemblies)
                throw new ProcessorLimitExceededException();
            var name = Path.GetFileNameWithoutExtension(path);
            if (replaceExisting)
                _paths[name] = path;
            else
                _paths.TryAdd(name, path);
        }
    }
}

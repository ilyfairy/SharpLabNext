using System.Text;

namespace SharpLabNext.Worker.FSharp;

internal sealed class TemporaryFSharpWorkspace : IAsyncDisposable
{
    private TemporaryFSharpWorkspace(string root, IReadOnlyDictionary<string, string> paths)
    {
        Root = root;
        Paths = paths;
    }

    public string Root { get; }
    public IReadOnlyDictionary<string, string> Paths { get; }

    public Task WriteAsync(string relativePath, string text, CancellationToken cancellationToken)
    {
        if (!Paths.TryGetValue(relativePath, out var fullPath))
            throw new FSharpBuildRequestValidationException($"Workspace file '{relativePath}' is not part of this session.");
        return File.WriteAllTextAsync(fullPath, text, new UTF8Encoding(false), cancellationToken);
    }

    public static async Task<TemporaryFSharpWorkspace> CreateAsync(string configuredRoot, IReadOnlyList<ValidatedFSharpWorkspaceFile> files, CancellationToken cancellationToken)
    {
        var baseRoot = Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(baseRoot);
        var root = Path.Combine(baseRoot, $"job-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var file in files)
            {
                var fullPath = Resolve(root, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, file.Text, new UTF8Encoding(false), cancellationToken);
                paths.Add(file.Path, fullPath);
            }
            return new TemporaryFSharpWorkspace(root, paths);
        }
        catch
        {
            Delete(root);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Delete(Root);
        return ValueTask.CompletedTask;
    }

    internal static string Resolve(string root, string relativePath)
    {
        var normalized = FSharpWorkspaceValidator.NormalizeRelativePath(relativePath);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(rootWithSeparator, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new FSharpBuildRequestValidationException("Workspace path escaped the worker root.");
        return result;
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

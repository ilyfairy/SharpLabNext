using System.Security.Cryptography;

namespace SharpLabNext.ArtifactStore;

internal static class StorageSafety
{
    public static string ResolveRoot(string contentRoot, string configuredRoot)
    {
        var root = Path.GetFullPath(configuredRoot, contentRoot);
        EnsureDirectoryTreeHasNoLinks(root);
        Directory.CreateDirectory(root);
        EnsureDirectoryTreeHasNoLinks(root);
        return root;
    }

    public static void EnsureDirectoryTreeHasNoLinks(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var existing = fullPath;
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null || parent == existing)
            {
                return;
            }

            existing = parent;
        }

        var current = new DirectoryInfo(existing);
        while (current is not null)
        {
            EnsureNotLink(current);
            current = current.Parent;
        }
    }

    public static void EnsureNotLink(FileSystemInfo info)
    {
        info.Refresh();
        if (!info.Exists)
        {
            return;
        }

        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArtifactValidationException($"Symbolic links are not allowed in Artifact Store paths: '{info.Name}'.");
        }
    }

    public static async Task<(long Size, string Digest)> HashFileAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var size = stream.Length;
        stream.Position = 0;
        return (size, Convert.ToHexStringLower(hash));
    }

    public static string ToDatabaseRelativePath(string root, string fullPath)
    {
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relativePath))
        {
            throw new ArtifactValidationException("A storage path escaped the configured root.");
        }

        return relativePath.Replace('\\', '/');
    }

    public static string FromDatabaseRelativePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\') || relativePath[0] == '/' || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArtifactCorruptedException("Artifact Store metadata contains an invalid relative path.");
        }

        var fullPath = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), root);
        var relativeCheck = Path.GetRelativePath(root, fullPath);
        if (relativeCheck.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relativeCheck))
        {
            throw new ArtifactCorruptedException("Artifact Store metadata escaped the configured root.");
        }

        return fullPath;
    }
}

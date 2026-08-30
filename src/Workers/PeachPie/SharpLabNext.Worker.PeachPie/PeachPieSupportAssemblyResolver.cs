using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace SharpLabNext.Worker.PeachPie;

public sealed record PeachPieSupportFile(string Role, string Path, byte[] Content);

public static class PeachPieSupportAssemblyResolver
{
    public static async Task<IReadOnlyList<PeachPieSupportFile>> ResolveAsync(PeachPieWorkerSettings settings, LoadedPeachPieReferenceSet referenceSet, int maximumSupportBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSupportBytes);
        var frameworkAssemblies = referenceSet.ReferenceAssemblyPaths.Select(ReadAssemblyName).Where(static name => name is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = LocalAssemblyCandidates().Select(path => (Path: path, Name: ReadAssemblyName(path))).Where(static item => item.Name is not null).GroupBy(static item => item.Name!, StringComparer.OrdinalIgnoreCase).ToDictionary(static group => group.Key, static group => group.First().Path, StringComparer.OrdinalIgnoreCase);
        var roots = new[] { settings.RuntimeAssemblyPath, settings.LibraryAssemblyPath };
        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(roots);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pending.Dequeue();
            var name = ReadAssemblyName(path) ?? throw new PeachPieCompilerFailureException($"Support assembly '{Path.GetFileName(path)}' is not a managed assembly.");
            if (!selected.TryAdd(name, path))
                continue;
            foreach (var dependency in ReadAssemblyReferences(path))
            {
                if (frameworkAssemblies.Contains(dependency) || selected.ContainsKey(dependency))
                    continue;
                if (candidates.TryGetValue(dependency, out var dependencyPath))
                    pending.Enqueue(dependencyPath);
                else
                    throw new PeachPieCompilerFailureException($"Support assembly dependency '{dependency}' is unavailable.");
            }
        }

        var rootNames = roots.Select(ReadAssemblyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = selected.OrderByDescending(pair => rootNames.Contains(pair.Key)).ThenBy(static pair => Path.GetFileName(pair.Value), StringComparer.Ordinal).Select(static pair => pair.Value).ToArray();
        var result = new List<PeachPieSupportFile>(ordered.Length + 1);
        var remainingBytes = maximumSupportBytes;
        foreach (var path in ordered)
        {
            var content = await ReadBoundedAsync(path, remainingBytes, cancellationToken).ConfigureAwait(false);
            remainingBytes -= content.Length;
            result.Add(new PeachPieSupportFile("support-assembly", Path.GetFileName(path), content));
        }
        var nativeAsset = await ReadLinuxX64NativeAssetAsync(settings.MonoUnixNativeLibraryPath, remainingBytes, cancellationToken).ConfigureAwait(false);
        result.Add(nativeAsset);
        return result;
    }

    private static async Task<PeachPieSupportFile> ReadLinuxX64NativeAssetAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var expectedSuffix = Path.DirectorySeparatorChar +
            PeachPieToolchain.MonoUnixNativePackagePath.Replace('/', Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.EndsWith(expectedSuffix, comparison))
        {
            throw new PeachPieCompilerFailureException("The pinned Mono.Unix native support asset has an invalid source path.");
        }

        var content = await ReadBoundedAsync(fullPath, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (!IsLinuxX64Elf(content))
        {
            throw new PeachPieCompilerFailureException("The pinned Mono.Unix native support asset is not a Linux x64 shared library.");
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!StringComparer.Ordinal.Equals(digest, PeachPieToolchain.MonoUnixNativeSha256))
        {
            throw new PeachPieCompilerFailureException("The pinned Mono.Unix native support asset does not match its reviewed SHA-256 identity.");
        }

        return new PeachPieSupportFile("native-library", PeachPieToolchain.MonoUnixNativeArtifactPath, content);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        long length;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                throw new FileNotFoundException();
            length = file.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PeachPieCompilerFailureException($"Support file '{Path.GetFileName(path)}' is unavailable.");
        }
        if (length <= 0)
        {
            throw new PeachPieCompilerFailureException($"Support file '{Path.GetFileName(path)}' is empty.");
        }
        if (length > maximumBytes)
        {
            throw new PeachPieBuildOutputLimitExceededException("The PeachPie runtime support closure exceeds the artifact limit.");
        }

        byte[] content;
        try
        {
            content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PeachPieCompilerFailureException($"Support file '{Path.GetFileName(path)}' is unavailable.");
        }
        if (content.LongLength != length)
        {
            throw new PeachPieCompilerFailureException($"Support file '{Path.GetFileName(path)}' changed while it was being read.");
        }
        return content;
    }

    private static bool IsLinuxX64Elf(ReadOnlySpan<byte> content) =>
        content.Length >= 20 &&
        content[0] == 0x7f &&
        content[1] == (byte)'E' &&
        content[2] == (byte)'L' &&
        content[3] == (byte)'F' &&
        content[4] == 2 &&
        content[5] == 1 &&
        content[6] == 1 &&
        content[16] == 3 &&
        content[17] == 0 &&
        content[18] == 0x3e &&
        content[19] == 0;

    private static IEnumerable<string> LocalAssemblyCandidates()
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer);
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (paths.Add(path))
                yield return path;
        }
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
            yield break;
        foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (paths.Add(path))
                yield return path;
        }
    }

    private static string? ReadAssemblyName(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
                return null;
            var metadata = pe.GetMetadataReader();
            return metadata.IsAssembly
                ? metadata.GetString(metadata.GetAssemblyDefinition().Name) : null;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[] ReadAssemblyReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
    }
}

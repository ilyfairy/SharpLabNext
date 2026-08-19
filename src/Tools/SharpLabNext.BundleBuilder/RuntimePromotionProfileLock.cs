using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SharpLabNext.BundleBuilder;

/// <summary>
/// Cross-process advisory lock for one runtime promotion profile.
/// </summary>
/// <remarks>
/// The handle remains open for the complete producer transaction. FileStream.Lock
/// is released by the operating system when a process exits, so an interrupted
/// producer cannot leave a stale PID marker that blocks future work.
/// </remarks>
public sealed class RuntimePromotionProfileLock : IDisposable
{
    private const string LockDirectory = "artifacts/runtime-matrix-promotion/profile-locks";
    private readonly FileStream _stream;
    private bool _released;

    private RuntimePromotionProfileLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    public string Path { get; }

    public static async Task<RuntimePromotionProfileLock> AcquireAsync(
        string repositoryRoot,
        string profileId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The profile lock timeout must be positive.");
        if (!IsCanonicalProfileId(profileId))
            throw new ArgumentException("The runtime profile ID is not canonical.", nameof(profileId));

        var root = System.IO.Path.GetFullPath(repositoryRoot)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var lockDirectory = System.IO.Path.Combine(
            root,
            LockDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
        EnsureNoReparsePoints(root, lockDirectory);
        Directory.CreateDirectory(lockDirectory);
        EnsureNoReparsePoints(root, lockDirectory);
        var key = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(profileId)));
        var lockPath = System.IO.Path.Combine(lockDirectory, $"{key}.lock");
        EnsureNoReparsePoints(root, lockPath);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Runtime promotion profile locks require Windows or Linux advisory file locks.");
        }

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                if (stream.Length == 0)
                {
                    stream.WriteByte(0);
                    stream.Flush(flushToDisk: true);
                }
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                    stream.Lock(0, 1);
                return new RuntimePromotionProfileLock(stream, lockPath);
            }
            catch (IOException) when (stream is not null)
            {
                stream.Dispose();
                if (stopwatch.Elapsed >= timeout)
                {
                    throw new TimeoutException(
                        $"Another promotion already holds the runtime profile lock for '{profileId}'.");
                }
            }

            var remaining = timeout - stopwatch.Elapsed;
            var delay = remaining < TimeSpan.FromMilliseconds(100)
                ? remaining
                : TimeSpan.FromMilliseconds(100);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_released)
            return;
        _released = true;
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                _stream.Unlock(0, 1);
        }
        finally
        {
            _stream.Dispose();
        }
    }

    private static bool IsCanonicalProfileId(string value) =>
        value.Length is > 0 and <= 128 &&
        value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var relative = System.IO.Path.GetRelativePath(root, path);
        if (System.IO.Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{System.IO.Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The promotion profile lock path escapes the repository root.");
        }

        var segments = relative == "."
            ? Array.Empty<string>()
            : relative.Split(
                [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        if (Directory.Exists(current) &&
            (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The repository root cannot be a reparse point.");
        }
        foreach (var segment in segments)
        {
            current = System.IO.Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"The promotion profile lock path contains a reparse point '{current}'.");
            }
        }
    }
}

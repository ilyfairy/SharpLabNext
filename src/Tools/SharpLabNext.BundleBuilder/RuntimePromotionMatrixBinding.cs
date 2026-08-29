using System.Text.Json;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.BundleBuilder;

/// <summary>
/// Closes the small piece of matrix state that BundleBuilder consumes directly.
/// Full receipt/evidence validation remains in <see cref="RuntimePromotionTrust"/>
/// and the shared schema validator; this gate only proves that an active profile
/// is represented by the same verified matrix platform binding.
/// </summary>
internal static class RuntimePromotionMatrixBinding
{
    private const long MaximumMatrixBytes = 16L * 1024 * 1024;
    internal const string MatrixRelativePath = "profiles/runtime-matrix.json";

    public static async Task ValidateAsync(
        string repositoryRoot,
        IReadOnlyList<RuntimeProfileDefinition> activeProfiles,
        IReadOnlyList<RuntimePromotionTrustSnapshot> promotionTrust,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(activeProfiles);
        ArgumentNullException.ThrowIfNull(promotionTrust);

        if (!activeProfiles.Any(static profile => profile.PromotionReceipt is not null))
            return;

        var bytes = await ReadMatrixAsync(repositoryRoot, cancellationToken);
        Validate(bytes, activeProfiles, promotionTrust);
    }

    internal static void Validate(
        byte[] matrixBytes,
        IReadOnlyList<RuntimeProfileDefinition> activeProfiles,
        IReadOnlyList<RuntimePromotionTrustSnapshot> promotionTrust)
    {
        ArgumentNullException.ThrowIfNull(matrixBytes);
        ArgumentNullException.ThrowIfNull(activeProfiles);
        ArgumentNullException.ThrowIfNull(promotionTrust);

        var promotionProfiles = activeProfiles
            .Where(static profile => profile.PromotionReceipt is not null)
            .OrderBy(static profile => profile.Id, StringComparer.Ordinal)
            .ToArray();
        if (promotionProfiles.Length == 0)
            return;

        var snapshots = new Dictionary<string, RuntimePromotionTrustSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in promotionTrust)
        {
            if (!snapshots.TryAdd(snapshot.RuntimeId, snapshot))
            {
                throw new BundleValidationException(
                    $"Runtime promotion matrix binding has duplicate trust snapshot '{snapshot.RuntimeId}'.");
            }
        }

        using var document = ParseMatrix(matrixBytes);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new BundleValidationException(
                $"{MatrixRelativePath} must contain a JSON object.");
        }

        foreach (var profile in promotionProfiles)
        {
            if (!snapshots.TryGetValue(profile.Id, out var snapshot))
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' has no matching promotion trust snapshot.");
            }

            var capability = FindCapability(document.RootElement, profile.Id, profile.Family);
            if (capability is null)
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' has no matching platform binding in {MatrixRelativePath}.");
            }

            RequireString(capability.Value, "promotionState", "verified", profile.Id);
            if (!capability.Value.TryGetProperty("promotionReceipt", out var matrixReceipt) ||
                matrixReceipt.ValueKind != JsonValueKind.Object)
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' matrix binding has no promotionReceipt object.");
            }

            var activeReceipt = profile.PromotionReceipt
                ?? throw new BundleValidationException(
                    $"Runtime '{profile.Id}' is missing its active promotion receipt.");
            var matrixPath = RequiredString(matrixReceipt, "path", profile.Id);
            var matrixDigest = RequiredString(matrixReceipt, "sha256", profile.Id);
            if (!StringComparer.Ordinal.Equals(matrixPath, activeReceipt.Path) ||
                !StringComparer.Ordinal.Equals(matrixDigest, activeReceipt.Sha256))
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' matrix promotionReceipt does not match its active profile.");
            }

            if (!StringComparer.Ordinal.Equals(matrixPath, snapshot.Receipt.RelativePath) ||
                !StringComparer.Ordinal.Equals(matrixDigest, snapshot.Receipt.Sha256))
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' matrix promotionReceipt does not match the captured receipt.");
            }
        }
    }

    private static JsonElement? FindCapability(
        JsonElement matrix,
        string profileId,
        string family)
    {
        var matches = new List<JsonElement>(capacity: 1);
        if (family is "coreclr" or "coreclr-wine" &&
            matrix.TryGetProperty("coreClr", out var coreClr) &&
            coreClr.ValueKind == JsonValueKind.Array)
        {
            foreach (var target in coreClr.EnumerateArray())
            {
                if (target.ValueKind != JsonValueKind.Object ||
                    !TryString(target, "id", out var id))
                    continue;

                var (suffix, property) = family == "coreclr"
                    ? (id + "-linux-x64", "linuxCapability")
                    : ("wine-" + id + "-linux-x64", "wineCapability");
                if (StringComparer.Ordinal.Equals(profileId, suffix) &&
                    target.TryGetProperty(property, out var capability))
                {
                    matches.Add(capability);
                }
            }
        }
        else if (family == "mono" &&
                 matrix.TryGetProperty("mono", out var mono) &&
                 mono.ValueKind == JsonValueKind.Object &&
                 TryString(mono, "id", out var monoId) &&
                 StringComparer.Ordinal.Equals(profileId, monoId) &&
                 mono.TryGetProperty("capability", out var monoCapability))
        {
            matches.Add(monoCapability);
        }
        else if (family == "netfx-clr-wine" &&
                 matrix.TryGetProperty("framework", out var framework) &&
                 framework.ValueKind == JsonValueKind.Object &&
                 framework.TryGetProperty("targets", out var targets) &&
                 targets.ValueKind == JsonValueKind.Array)
        {
            foreach (var target in targets.EnumerateArray())
            {
                if (target.ValueKind != JsonValueKind.Object ||
                    !TryString(target, "id", out var id))
                    continue;
                if (StringComparer.Ordinal.Equals(profileId, "wine-" + id + "-linux-x64") &&
                    target.TryGetProperty("capability", out var capability))
                {
                    matches.Add(capability);
                }
            }
        }

        if (matches.Count > 1)
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' has duplicate platform bindings in {MatrixRelativePath}.");
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    internal static JsonDocument ParseMatrix(byte[] matrixBytes)
    {
        try
        {
            return JsonDocument.Parse(matrixBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException exception)
        {
            throw new BundleValidationException(
                $"{MatrixRelativePath} is invalid JSON: {exception.Message}");
        }
    }

    private static string RequiredString(JsonElement objectElement, string property, string profileId)
    {
        if (!TryString(objectElement, property, out var value))
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' matrix promotionReceipt.{property} must be a non-empty string.");
        }
        return value;
    }

    private static void RequireString(
        JsonElement objectElement,
        string property,
        string expected,
        string profileId)
    {
        var actual = RequiredString(objectElement, property, profileId);
        if (!StringComparer.Ordinal.Equals(actual, expected))
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' matrix {property} must be '{expected}'.");
        }
    }

    private static bool TryString(JsonElement objectElement, string property, out string value)
    {
        if (objectElement.ValueKind == JsonValueKind.Object &&
            objectElement.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(element.GetString()))
        {
            value = element.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    internal static async Task<byte[]> ReadMatrixAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(
            root,
            MatrixRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureContained(root, path);
        EnsureNoReparsePoints(root, path, includeLeaf: false);

        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length is < 1 or > MaximumMatrixBytes)
        {
            throw new BundleValidationException(
                $"{MatrixRelativePath} must be a bounded regular file.");
        }

        byte[] bytes;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (stream.ReadByte() != -1 || stream.Length != info.Length)
            {
                throw new BundleValidationException(
                    $"{MatrixRelativePath} changed while it was being read.");
            }
        }
        return bytes;
    }

    private static void EnsureContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new BundleValidationException(
                $"{MatrixRelativePath} escapes the repository root.");
        }
    }

    private static void EnsureNoReparsePoints(string root, string path, bool includeLeaf)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var count = includeLeaf ? segments.Length : Math.Max(0, segments.Length - 1);
        var current = root;
        for (var index = 0; index < count; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new BundleValidationException(
                    $"{MatrixRelativePath} path contains a reparse point.");
            }
        }
    }
}

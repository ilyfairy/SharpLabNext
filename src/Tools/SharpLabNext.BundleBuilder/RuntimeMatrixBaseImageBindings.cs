using System.Text.Json;

namespace SharpLabNext.BundleBuilder;

internal sealed class RuntimeMatrixBaseImageBindings
{
    internal const string LinuxRuntimeBaseImageId = "dotnet-runtime-deps";

    private RuntimeMatrixBaseImageBindings(IReadOnlyDictionary<string, string> linuxRuntimeBaseImages)
    {
        LinuxRuntimeBaseImages = linuxRuntimeBaseImages;
    }

    public IReadOnlyDictionary<string, string> LinuxRuntimeBaseImages { get; }

    public static async Task<RuntimeMatrixBaseImageBindings> LoadAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var bytes = await RuntimePromotionMatrixBinding.ReadMatrixAsync(repositoryRoot, cancellationToken);
        return Parse(bytes);
    }

    internal static RuntimeMatrixBaseImageBindings Parse(byte[] matrixBytes)
    {
        ArgumentNullException.ThrowIfNull(matrixBytes);
        using var document = RuntimePromotionMatrixBinding.ParseMatrix(matrixBytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schemaVersion) || schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out var schemaVersionValue) || schemaVersionValue != 1)
        {
            throw new BundleValidationException($"{RuntimePromotionMatrixBinding.MatrixRelativePath} has an unsupported schema version.");
        }
        if (!root.TryGetProperty("coreClr", out var coreClr) || coreClr.ValueKind != JsonValueKind.Array || coreClr.GetArrayLength() == 0)
        {
            throw new BundleValidationException($"{RuntimePromotionMatrixBinding.MatrixRelativePath} must contain CoreCLR runtime rows.");
        }

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in coreClr.EnumerateArray())
        {
            var id = RequiredString(row, "id");
            var baseImage = RequiredString(row, "linuxBaseImage");
            _ = ReleaseBundleBuilder.BaseImageDigest(baseImage);
            var runtimeId = id + "-linux-x64";
            if (!bindings.TryAdd(runtimeId, baseImage))
            {
                throw new BundleValidationException($"{RuntimePromotionMatrixBinding.MatrixRelativePath} has duplicate Linux runtime row '{runtimeId}'.");
            }
        }
        return new RuntimeMatrixBaseImageBindings(bindings);
    }

    private static string RequiredString(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new BundleValidationException($"{RuntimePromotionMatrixBinding.MatrixRelativePath} CoreCLR row has no valid '{property}'.");
        }
        return element.GetString()!;
    }
}

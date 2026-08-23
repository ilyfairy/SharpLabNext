using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;

namespace SharpLabNext.BundleBuilder;

public interface IDockerCli
{
    Task<DockerImageInspection> InspectImageAsync(string reference, CancellationToken cancellationToken);

    Task<DockerImageFileInspection> InspectImageFileAsync(
        string imageId,
        string absolutePath,
        long maximumBytes,
        CancellationToken cancellationToken);

    Task<DockerImageFileInspection> CopyImageFileAsync(
        string imageId,
        string absolutePath,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This Docker client does not support copying image files.");

    Task SaveImagesAsync(
        IReadOnlyList<string> references,
        string outputPath,
        CancellationToken cancellationToken);
}

public sealed record DockerImageInspection(
    string ImageId,
    string OperatingSystem,
    string Architecture,
    long SizeBytes,
    IReadOnlyList<string> RepoDigests,
    IReadOnlyDictionary<string, string> Labels);

public sealed record DockerImageFileInspection(
    string Sha256,
    long Length);

public sealed class DockerCli(string command) : IDockerCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DockerImageInspection> InspectImageAsync(
        string reference,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(["image", "inspect", reference], cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1)
        {
            throw new BundleValidationException($"Docker returned an invalid inspection result for '{reference}'.");
        }

        var image = document.RootElement[0];
        var id = RequiredString(image, "Id");
        var operatingSystem = RequiredString(image, "Os");
        var architecture = RequiredString(image, "Architecture");
        var sizeBytes = RequiredPositiveInt64(image, "Size");
        var repoDigests = image.TryGetProperty("RepoDigests", out var repoDigestValue) &&
                          repoDigestValue.ValueKind == JsonValueKind.Array
            ? repoDigestValue.EnumerateArray()
                .Select(static item => item.GetString())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (image.TryGetProperty("Config", out var config) &&
            config.ValueKind == JsonValueKind.Object &&
            config.TryGetProperty("Labels", out var labelValue) &&
            labelValue.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labelValue.EnumerateObject())
            {
                if (label.Value.ValueKind == JsonValueKind.String)
                {
                    labels[label.Name] = label.Value.GetString()!;
                }
            }
        }

        return new DockerImageInspection(id, operatingSystem, architecture, sizeBytes, repoDigests, labels);
    }

    public async Task<DockerImageFileInspection> InspectImageFileAsync(
        string imageId,
        string absolutePath,
        long maximumBytes,
        CancellationToken cancellationToken) =>
        await ReadImageFileAsync(imageId, absolutePath, null, maximumBytes, cancellationToken);

    public async Task<DockerImageFileInspection> CopyImageFileAsync(
        string imageId,
        string absolutePath,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent) || File.Exists(destination) || Directory.Exists(destination))
        {
            throw new BundleValidationException(
                "Image file copy destination must be a new file in an existing directory.");
        }

        return await ReadImageFileAsync(
            imageId,
            absolutePath,
            destination,
            maximumBytes,
            cancellationToken);
    }

    private async Task<DockerImageFileInspection> ReadImageFileAsync(
        string imageId,
        string absolutePath,
        string? destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalImageId(imageId))
        {
            throw new BundleValidationException(
                "Image file inspection requires a captured sha256 image ID, not a mutable reference.");
        }
        if (!IsSafeAbsoluteContainerPath(absolutePath))
        {
            throw new BundleValidationException(
                $"Image file path '{absolutePath}' must be a canonical absolute container path.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        var create = await RunAsync(
            ["container", "create", "--entrypoint", "/bin/true", imageId],
            cancellationToken);
        var containerId = create.StandardOutput.Trim();
        if (!IsContainerId(containerId))
        {
            throw new BundleValidationException(
                $"Docker returned an invalid temporary container ID for image '{imageId}'.");
        }

        Exception? inspectionFailure = null;
        try
        {
            return await InspectContainerFileAsync(
                containerId,
                absolutePath,
                destinationPath,
                maximumBytes,
                cancellationToken);
        }
        catch (Exception exception)
        {
            inspectionFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                _ = await RunAsync(
                    ["container", "rm", "--force", containerId],
                    CancellationToken.None);
            }
            catch when (inspectionFailure is not null)
            {
                // Preserve the primary validation failure. A successful inspection still
                // fails if Docker cannot remove its temporary container.
            }
        }
    }

    public async Task SaveImagesAsync(
        IReadOnlyList<string> references,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            throw new ArgumentException("At least one image reference is required.", nameof(references));
        }

        var arguments = new List<string> { "image", "save", "--output", outputPath };
        arguments.AddRange(references);
        _ = await RunAsync(arguments, cancellationToken);
    }

    private async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(arguments);
        using var process = Process.Start(startInfo)
            ?? throw new BundleValidationException($"Could not start '{command}'.");
        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            TryKill(process);
            await WaitForExitQuietlyAsync(process);
            await DrainQuietlyAsync(outputTask, errorTask);
            throw;
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var publicError = error.Length > 4096 ? error[..4096] : error;
            throw new BundleValidationException(
                $"Docker command failed with exit code {process.ExitCode}: {publicError.Trim()}");
        }

        return new ProcessResult(output, error);
    }

    private async Task<DockerImageFileInspection> InspectContainerFileAsync(
        string containerId,
        string absolutePath,
        string? destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(
            ["container", "cp", $"{containerId}:{absolutePath}", "-"]);
        using var process = Process.Start(startInfo)
            ?? throw new BundleValidationException($"Could not start '{command}'.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        DockerImageFileInspection? inspection = null;
        Exception? readFailure = null;
        try
        {
            using var reader = new TarReader(process.StandardOutput.BaseStream, leaveOpen: true);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                {
                    continue;
                }
                if (inspection is not null)
                {
                    throw new BundleValidationException(
                        $"Image path '{absolutePath}' produced more than one archive entry.");
                }
                if (entry.EntryType is not (TarEntryType.RegularFile or
                    TarEntryType.V7RegularFile or
                    TarEntryType.ContiguousFile) || entry.DataStream is null)
                {
                    throw new BundleValidationException(
                        $"Image path '{absolutePath}' must resolve to one regular, non-link file.");
                }

                inspection = await ComputeDigestAsync(
                    entry.DataStream,
                    absolutePath,
                    destinationPath,
                    maximumBytes,
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            readFailure = exception;
            TryKill(process);
        }

        await process.WaitForExitAsync(CancellationToken.None);
        var error = await errorTask;
        if (readFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(readFailure).Throw();
        }
        if (process.ExitCode != 0)
        {
            var publicError = error.Length > 4096 ? error[..4096] : error;
            throw new BundleValidationException(
                $"Docker command failed with exit code {process.ExitCode}: {publicError.Trim()}");
        }

        return inspection ?? throw new BundleValidationException(
            $"Image path '{absolutePath}' did not produce a regular file.");
    }

    private static async Task<DockerImageFileInspection> ComputeDigestAsync(
        Stream input,
        string path,
        string? destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = destinationPath is null
            ? null
            : new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        long length = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                length = checked(length + read);
                if (length > maximumBytes)
                {
                    throw new BundleValidationException(
                        $"Image file '{path}' exceeds the {maximumBytes}-byte validation limit.");
                }
                hash.AppendData(buffer.AsSpan(0, read));
                if (output is not null)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (length == 0)
            {
                throw new BundleValidationException($"Image file '{path}' is empty.");
            }

            return new DockerImageFileInspection(
                $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}",
                length);
        }
        catch
        {
            if (destinationPath is not null)
            {
                try { File.Delete(destinationPath); } catch (IOException) { }
            }
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static bool IsCanonicalImageId(string value) =>
        value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToArray().All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsContainerId(string value) =>
        value.Length == 64 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeAbsoluteContainerPath(string value)
    {
        if (value.Length is 0 or > 4096 ||
            value[0] != '/' ||
            value.Contains('\0') ||
            value.Contains('\\') ||
            value.Contains("//", StringComparison.Ordinal) ||
            value.Length > 1 && value[^1] == '/')
        {
            return false;
        }

        return value[1..].Split('/', StringSplitOptions.None)
            .All(static segment => segment is not "." and not ".." &&
                segment.Length > 0 &&
                segment.All(static character => !char.IsControl(character)));
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            OperationCanceledException)
        {
        }
    }

    private static async Task DrainQuietlyAsync(params Task<string>[] streams)
    {
        try
        {
            await Task.WhenAll(streams);
        }
        catch (Exception exception) when (exception is
            IOException or
            ObjectDisposedException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string RequiredString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new BundleValidationException($"Docker inspection property '{propertyName}' is missing.");
        }

        return value.GetString()!;
    }

    private static long RequiredPositiveInt64(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result) ||
            result <= 0)
        {
            throw new BundleValidationException(
                $"Docker inspection property '{propertyName}' must be a positive 64-bit integer.");
        }

        return result;
    }

    private sealed record ProcessResult(string StandardOutput, string StandardError);
}

#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

const long MaximumArchiveBytes = 1L * 1024 * 1024 * 1024;
const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
const int MaximumEntries = 50_000;

try
{
    var options = Options.Parse(args);
    if (options.Help)
    {
        Console.WriteLine(
            "Usage: dotnet run eng/materialize-runtime-matrix.cs -- " +
            "--target ID --artifact linux-runtime|windows-runtime|reference-package " +
            "--destination PATH [--matrix PATH] [--archive PATH]");
        return 0;
    }

    var matrixPath = Path.GetFullPath(options.MatrixPath ??
        Path.Combine(Directory.GetCurrentDirectory(), "profiles", "runtime-matrix.json"));
    using var matrix = JsonDocument.Parse(
        await File.ReadAllBytesAsync(matrixPath),
        new JsonDocumentOptions { MaxDepth = 32 });
    var source = ResolveSource(matrix.RootElement, options.TargetId!, options.Artifact!);
    ValidateSource(source);

    var destination = Path.GetFullPath(options.Destination!);
    RequireSafeDestination(destination);
    var destinationParent = Directory.GetParent(destination)?.FullName
        ?? throw new InvalidOperationException("The destination must have a parent directory.");
    Directory.CreateDirectory(destinationParent);

    var staging = Path.Combine(destinationParent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
    Directory.CreateDirectory(staging);
    try
    {
        var archivePath = Path.Combine(staging, ArchiveName(source.Url));
        if (options.ArchivePath is { } suppliedArchive)
        {
            await CopyAndVerifyAsync(Path.GetFullPath(suppliedArchive), archivePath, source.Sha512);
        }
        else
        {
            await DownloadAndVerifyAsync(source.Url, archivePath, source.Sha512);
        }

        var content = Path.Combine(staging, "content");
        Directory.CreateDirectory(content);
        if (source.Url.AbsolutePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            ExtractTarGzip(archivePath, content);
        else if (source.Url.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                 source.Url.AbsolutePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            ExtractZip(archivePath, content);
        else
            throw new InvalidDataException("The locked artifact has an unsupported archive format.");

        await WriteMaterializationManifestAsync(
            Path.Combine(content, ".sharplabnext-materialization.json"),
            options.TargetId!,
            options.Artifact!,
            source);

        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"Destination '{destination}' already exists; refusing a non-atomic merge.");
        Directory.Move(content, destination);
        Console.WriteLine(destination);
    }
    finally
    {
        TryDelete(staging);
    }
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"Runtime matrix materialization failed: {exception.Message}");
    return 1;
}

static LockedSource ResolveSource(JsonElement root, string targetId, string artifact)
{
    if (artifact is "linux-runtime" or "windows-runtime")
    {
        var target = RequiredArray(root, "coreClr")
            .EnumerateArray()
            .SingleOrDefault(item => string.Equals(RequiredString(item, "id"), targetId, StringComparison.Ordinal));
        if (target.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"CoreCLR target '{targetId}' is not present in the runtime matrix.");
        var property = artifact == "linux-runtime" ? "linux" : "windows";
        return ReadSource(RequiredProperty(target, property));
    }

    if (artifact != "reference-package")
        throw new ArgumentException($"Unsupported artifact kind '{artifact}'.");

    foreach (var target in RequiredArray(root, "coreClr").EnumerateArray())
    {
        if (string.Equals(RequiredString(target, "id"), targetId, StringComparison.Ordinal))
            return ReadSource(RequiredProperty(target, "referencePackage"));
    }
    foreach (var target in RequiredArray(RequiredProperty(root, "framework"), "targets").EnumerateArray())
    {
        if (!string.Equals(RequiredString(target, "id"), targetId, StringComparison.Ordinal))
            continue;
        if (!target.TryGetProperty("referencePackage", out var package))
            throw new InvalidDataException($"Target '{targetId}' has no locked reference package.");
        return ReadSource(package);
    }
    throw new InvalidDataException($"Reference target '{targetId}' is not present in the runtime matrix.");
}

static LockedSource ReadSource(JsonElement value) => new(
    new Uri(RequiredString(value, "url"), UriKind.Absolute),
    RequiredString(value, "sha512"));

static void ValidateSource(LockedSource source)
{
    ValidateDownloadUri(source.Url, allowCdn: false);
    if (source.Sha512.Length != 128 || source.Sha512.Any(static character =>
            character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        throw new InvalidDataException("Locked SHA-512 must be 128 lowercase hexadecimal characters.");
}

static async Task DownloadAndVerifyAsync(Uri source, string destination, string expectedHash)
{
    using var handler = new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
    };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SharpLabNext-Runtime-Materializer/1.0");
    using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    ValidateDownloadUri(
        response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("The artifact response has no final URI."),
        allowCdn: true);
    if (response.Content.Headers.ContentLength is > MaximumArchiveBytes)
        throw new InvalidDataException("Locked artifact exceeds the archive-size limit.");
    await using var input = await response.Content.ReadAsStreamAsync();
    await WriteAndVerifyAsync(input, destination, expectedHash);
}

static void ValidateDownloadUri(Uri value, bool allowCdn)
{
    if (value.Scheme != Uri.UriSchemeHttps || value.UserInfo.Length != 0 || !value.IsDefaultPort)
        throw new InvalidDataException("Locked artifacts must use ordinary HTTPS URLs without credentials.");
    var approved = value.Host is "builds.dotnet.microsoft.com" or "api.nuget.org" ||
        allowCdn && value.Host is "globalcdn.nuget.org" or "nuget.azure.cn";
    if (!approved)
        throw new InvalidDataException($"Artifact download host '{value.Host}' is not approved.");
}

static async Task CopyAndVerifyAsync(string source, string destination, string expectedHash)
{
    var info = new FileInfo(source);
    if (!info.Exists)
        throw new FileNotFoundException("The supplied archive does not exist.", source);
    if (info.Length > MaximumArchiveBytes)
        throw new InvalidDataException("Supplied archive exceeds the archive-size limit.");
    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
    await WriteAndVerifyAsync(input, destination, expectedHash);
}

static async Task WriteAndVerifyAsync(Stream input, string destination, string expectedHash)
{
    await using var output = new FileStream(
        destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
    var buffer = new byte[128 * 1024];
    long total = 0;
    while (true)
    {
        var read = await input.ReadAsync(buffer);
        if (read == 0)
            break;
        total = checked(total + read);
        if (total > MaximumArchiveBytes)
            throw new InvalidDataException("Locked artifact exceeds the archive-size limit.");
        hash.AppendData(buffer, 0, read);
        await output.WriteAsync(buffer.AsMemory(0, read));
    }
    await output.FlushAsync();
    var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual), Convert.FromHexString(expectedHash)))
        throw new InvalidDataException($"SHA-512 mismatch: expected {expectedHash}, observed {actual}.");
}

static void ExtractZip(string archivePath, string destination)
{
    using var archive = ZipFile.OpenRead(archivePath);
    if (archive.Entries.Count > MaximumEntries)
        throw new InvalidDataException("ZIP archive contains too many entries.");
    long expanded = 0;
    foreach (var entry in archive.Entries)
    {
        expanded = checked(expanded + entry.Length);
        if (expanded > MaximumExpandedBytes)
            throw new InvalidDataException("ZIP archive exceeds the expanded-size limit.");
        var output = SafeOutputPath(destination, entry.FullName);
        if (string.IsNullOrEmpty(entry.Name))
        {
            Directory.CreateDirectory(output);
            continue;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        entry.ExtractToFile(output, overwrite: false);
    }
}

static void ExtractTarGzip(string archivePath, string destination)
{
    using var file = File.OpenRead(archivePath);
    using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var archive = new TarReader(gzip);
    long expanded = 0;
    var entries = 0;
    while (archive.GetNextEntry(copyData: false) is { } entry)
    {
        if (++entries > MaximumEntries)
            throw new InvalidDataException("TAR archive contains too many entries.");
        var output = SafeOutputPath(destination, entry.Name);
        if (entry.EntryType is TarEntryType.Directory)
        {
            Directory.CreateDirectory(output);
            continue;
        }
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            throw new InvalidDataException($"TAR entry '{entry.Name}' has unsupported type '{entry.EntryType}'.");
        expanded = checked(expanded + entry.Length);
        if (expanded > MaximumExpandedBytes)
            throw new InvalidDataException("TAR archive exceeds the expanded-size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            entry.DataStream?.CopyTo(target);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(output, entry.Mode & (UnixFileMode)Convert.ToInt32("777", 8));
    }
}

static async Task WriteMaterializationManifestAsync(
    string path,
    string targetId,
    string artifact,
    LockedSource source)
{
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("targetId", targetId);
        writer.WriteString("artifact", artifact);
        writer.WriteString("sourceUri", source.Url.AbsoluteUri);
        writer.WriteString("sha512", source.Sha512);
        writer.WriteEndObject();
        await writer.FlushAsync();
    }
    await stream.WriteAsync("\n"u8.ToArray());
}

static string SafeOutputPath(string root, string relativePath)
{
    if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        throw new InvalidDataException("Archive contains an invalid path.");
    var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
    var output = Path.GetFullPath(Path.Combine(root, normalized));
    var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
    if (!output.StartsWith(prefix, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal))
        throw new InvalidDataException("Archive contains a path outside the destination.");
    return output;
}

static void RequireSafeDestination(string destination)
{
    var root = Path.GetPathRoot(destination);
    if (string.IsNullOrEmpty(root) ||
        string.Equals(Path.TrimEndingDirectorySeparator(destination), Path.TrimEndingDirectorySeparator(root),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        throw new ArgumentException("The destination cannot be a filesystem root.");
}

static string ArchiveName(Uri source)
{
    var name = Path.GetFileName(source.AbsolutePath);
    return !string.IsNullOrWhiteSpace(name) && name.All(static character =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
        ? name
        : throw new InvalidDataException("Locked artifact URL has an unsafe filename.");
}

static JsonElement RequiredProperty(JsonElement value, string name) =>
    value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
        ? property
        : throw new InvalidDataException($"Required matrix property '{name}' is missing.");

static JsonElement RequiredArray(JsonElement value, string name)
{
    var result = RequiredProperty(value, name);
    return result.ValueKind == JsonValueKind.Array
        ? result
        : throw new InvalidDataException($"Matrix property '{name}' must be an array.");
}

static string RequiredString(JsonElement value, string name)
{
    var result = RequiredProperty(value, name);
    return result.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(result.GetString())
        ? result.GetString()!
        : throw new InvalidDataException($"Matrix property '{name}' must be a non-empty string.");
}

static void TryDelete(string path)
{
    try
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

sealed record LockedSource(Uri Url, string Sha512);

sealed record Options(
    string? MatrixPath,
    string? TargetId,
    string? Artifact,
    string? Destination,
    string? ArchivePath,
    bool Help)
{
    public static Options Parse(string[] values)
    {
        string? matrix = null;
        string? target = null;
        string? artifact = null;
        string? destination = null;
        string? archive = null;
        var help = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Length; index++)
        {
            var option = values[index];
            if (!seen.Add(option))
                throw new ArgumentException($"Duplicate option '{option}'.");
            switch (option)
            {
                case "-h" or "--help": help = true; break;
                case "--matrix": matrix = Value(values, ref index, option); break;
                case "--target": target = Value(values, ref index, option); break;
                case "--artifact": artifact = Value(values, ref index, option); break;
                case "--destination": destination = Value(values, ref index, option); break;
                case "--archive": archive = Value(values, ref index, option); break;
                default: throw new ArgumentException($"Unknown option '{option}'.");
            }
        }
        if (!help && new[] { target, artifact, destination }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("--target, --artifact, and --destination are required.");
        return new Options(matrix, target, artifact, destination, archive, help);
    }

    private static string Value(string[] values, ref int index, string option) =>
        ++index < values.Length && !string.IsNullOrWhiteSpace(values[index])
            ? values[index]
            : throw new ArgumentException($"Option '{option}' requires a value.");
}

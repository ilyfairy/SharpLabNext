using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SharpLabNext.ProfileUpdater;

public sealed record DotNetChannelResolution(string Channel, string RuntimeVersion, string RuntimeCommit, string JitCommit, Uri RuntimeUri, string RuntimeSha512, string SdkVersion, Uri SdkUri, string SdkSha512, DateOnly ReleaseDate);

public sealed record NuGetPackageResolution(string PackageId, string Version, Uri PackageUri, string PackageContentHash, string PackageSha512);

public sealed record GitCommitResolution(string Commit, Uri RepositoryUri, Uri ArchiveUri, string ArchiveSha256, string ProductVersion);

public interface IProfileSourceClient
{
    Task<DotNetChannelResolution> ResolveDotNetChannelAsync(string channel, CancellationToken cancellationToken = default);

    Task<NuGetPackageResolution> ResolveLatestStablePackageAsync(string packageId, CancellationToken cancellationToken = default);

    Task<NuGetPackageResolution> ResolveExactPackageAsync(string packageId, string version, CancellationToken cancellationToken = default);

    Task<GitCommitResolution> ResolveGitCommitAsync(string owner, string repository, string branch, CancellationToken cancellationToken = default);
}

public sealed class OfficialProfileSourceClient(HttpClient httpClient) : IProfileSourceClient
{
    private const long MaximumRuntimeArchiveBytes = 512L * 1024 * 1024;
    private const long MaximumVersionFileBytes = 4096;

    public async Task<DotNetChannelResolution> ResolveDotNetChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        ValidateToken(channel, nameof(channel));
        var uri = new Uri(
            $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{Uri.EscapeDataString(channel)}/releases.json");
        using var document = await GetJsonAsync(uri, cancellationToken);
        var root = document.RootElement;
        var latestRelease = RequiredString(root, "latest-release");
        var releases = RequiredArray(root, "releases");
        var release = releases.EnumerateArray().SingleOrDefault(candidate => string.Equals(RequiredString(candidate, "release-version"), latestRelease, StringComparison.Ordinal));
        if (release.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($".NET channel '{channel}' does not contain latest release '{latestRelease}'.");
        }

        var runtime = RequiredObject(release, "runtime");
        var sdk = RequiredObject(release, "sdk");
        var runtimeFile = FindLinuxX64Archive(runtime, "dotnet-runtime-");
        var sdkFile = FindLinuxX64Archive(sdk, "dotnet-sdk-");
        var runtimeUri = RequiredUri(runtimeFile, "url");
        var runtimeSha512 = RequiredSha512(runtimeFile, "hash");
        var runtimeIdentity = await ResolveRuntimeIdentityAsync(runtimeUri, runtimeSha512, cancellationToken);
        var releaseDate = DateOnly.Parse(RequiredString(release, "release-date"), System.Globalization.CultureInfo.InvariantCulture);
        return new DotNetChannelResolution(channel, RequiredString(runtime, "version"), runtimeIdentity.RuntimeCommit, runtimeIdentity.JitCommit, runtimeUri, runtimeSha512, RequiredString(sdk, "version"), RequiredUri(sdkFile, "url"), RequiredSha512(sdkFile, "hash"), releaseDate);
    }

    private async Task<RuntimeArchiveIdentity> ResolveRuntimeIdentityAsync(Uri runtimeUri, string expectedSha512, CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"sharplabnext-runtime-{Guid.NewGuid():N}.tar.gz");
        try
        {
            using var response = await httpClient.GetAsync(runtimeUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
            {
                var buffer = new byte[1024 * 128];
                long totalBytes = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    totalBytes += read;
                    if (totalBytes > MaximumRuntimeArchiveBytes)
                        throw new InvalidDataException(".NET runtime archive exceeds the 512 MiB verification limit.");
                    hash.AppendData(buffer.AsSpan(0, read));
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                var actualSha512 = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(expectedSha512, actualSha512, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($".NET runtime archive SHA-512 mismatch: expected '{expectedSha512}', actual '{actualSha512}'.");
                }
            }

            await using var archive = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: false);
            using var reader = new TarReader(gzip, leaveOpen: false);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (!string.Equals(Path.GetFileName(entry.Name), ".version", StringComparison.Ordinal) || entry.DataStream is null)
                {
                    continue;
                }
                if (entry.Length is < 1 or > MaximumVersionFileBytes)
                    throw new InvalidDataException(".NET runtime archive .version has an invalid size.");

                using var textReader = new StreamReader(entry.DataStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
                var contents = await textReader.ReadToEndAsync(cancellationToken);
                var commit = contents.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(static value => value.ToLowerInvariant()).FirstOrDefault(IsCommit);
                if (commit is null)
                    throw new InvalidDataException(".NET runtime archive .version does not contain a commit SHA.");
                return new RuntimeArchiveIdentity(commit, commit);
            }

            throw new InvalidDataException(".NET runtime archive does not contain .version provenance.");
        }
        finally
        {
            if (File.Exists(archivePath))
                File.Delete(archivePath);
        }
    }

    public async Task<NuGetPackageResolution> ResolveLatestStablePackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        ValidatePackageId(packageId);
        var id = packageId.ToLowerInvariant();
        var indexUri = new Uri($"https://api.nuget.org/v3-flatcontainer/{id}/index.json");
        using var document = await GetJsonAsync(indexUri, cancellationToken);
        var versions = RequiredArray(document.RootElement, "versions").EnumerateArray().Select(static item => item.GetString()).Where(static version => !string.IsNullOrWhiteSpace(version) && !version.Contains('-')).Select(static version => version!).OrderBy(static version => ParseVersion(version)).ThenBy(static version => version, StringComparer.Ordinal).ToArray();
        var latest = versions.LastOrDefault() ?? throw new InvalidDataException($"NuGet package '{packageId}' has no stable versions.");
        return await ResolveExactPackageAsync(packageId, latest, cancellationToken);
    }

    public async Task<NuGetPackageResolution> ResolveExactPackageAsync(string packageId, string version, CancellationToken cancellationToken = default)
    {
        ValidatePackageId(packageId);
        ValidateToken(version, nameof(version));
        var id = packageId.ToLowerInvariant();
        var normalizedVersion = version.ToLowerInvariant();
        var packageUri = new Uri(
            $"https://api.nuget.org/v3-flatcontainer/{id}/{normalizedVersion}/{id}.{normalizedVersion}.nupkg");
        using var response = await httpClient.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var packageStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var hash = await SHA512.HashDataAsync(packageStream, cancellationToken);
        var packageContentHash = $"sha512-{Convert.ToBase64String(hash)}";

        return new NuGetPackageResolution(packageId, version, packageUri, packageContentHash, Convert.ToHexStringLower(hash));
    }

    public async Task<GitCommitResolution> ResolveGitCommitAsync(string owner, string repository, string branch, CancellationToken cancellationToken = default)
    {
        ValidateToken(owner, nameof(owner));
        ValidateToken(repository, nameof(repository));
        ValidateToken(branch, nameof(branch));
        var uri = new Uri(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/commits/{Uri.EscapeDataString(branch)}");
        using var document = await GetJsonAsync(uri, cancellationToken);
        var sha = RequiredString(document.RootElement, "sha").ToLowerInvariant();
        if (sha.Length != 40 || sha.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException("GitHub returned an invalid commit SHA.");
        }

        var repositoryUri = new Uri($"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}");
        var archiveUri = new Uri($"{repositoryUri}/archive/{sha}.tar.gz");
        using var archiveResponse = await httpClient.GetAsync(archiveUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        archiveResponse.EnsureSuccessStatusCode();
        await using var archiveStream = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken);
        var archiveHash = await SHA256.HashDataAsync(archiveStream, cancellationToken);
        var productVersion = await ResolveGitProductVersionAsync(owner, repository, branch, sha, cancellationToken);
        return new GitCommitResolution(sha, repositoryUri, archiveUri, Convert.ToHexStringLower(archiveHash), productVersion);
    }

    private async Task<string> ResolveGitProductVersionAsync(string owner, string repository, string reference, string commit, CancellationToken cancellationToken)
    {
        if (!string.Equals(owner, "dotnet", StringComparison.Ordinal) || !string.Equals(repository, "roslyn", StringComparison.Ordinal))
        {
            var tagVersion = reference.StartsWith('v') ? reference[1..] : reference;
            if (tagVersion.Length > 0 && tagVersion.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+') && char.IsAsciiDigit(tagVersion[0]))
            {
                return tagVersion;
            }
            throw new InvalidDataException($"GitHub channel '{owner}/{repository}@{reference}' must use a version tag or register a product-version resolver.");
        }

        var uri = new Uri(
            $"https://raw.githubusercontent.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/{commit}/eng/Versions.props");
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var major = RequiredVersionPart(document, "MajorVersion");
        var minor = RequiredVersionPart(document, "MinorVersion");
        var patch = RequiredVersionPart(document, "PatchVersion");
        return $"{major}.{minor}.{patch}";
    }

    private static string RequiredVersionPart(XDocument document, string name)
    {
        var value = document.Descendants(name).Select(static element => element.Value.Trim()).FirstOrDefault();
        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? value! : throw new InvalidDataException($"Roslyn eng/Versions.props has no numeric {name}.");
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static JsonElement FindLinuxX64Archive(JsonElement component, string fileNamePrefix)
    {
        var file = RequiredArray(component, "files").EnumerateArray().FirstOrDefault(candidate => string.Equals(RequiredString(candidate, "rid"), "linux-x64", StringComparison.Ordinal) && RequiredString(candidate, "name").StartsWith(fileNamePrefix, StringComparison.Ordinal) && RequiredString(candidate, "name").EndsWith(".tar.gz", StringComparison.Ordinal));
        return file.ValueKind == JsonValueKind.Undefined
            ? throw new InvalidDataException(".NET release metadata has no linux-x64 tar.gz asset.") : file;
    }

    private static JsonElement RequiredArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Official metadata property '{propertyName}' is missing or is not an array.");
        }

        return value;
    }

    private static JsonElement RequiredObject(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Official metadata property '{propertyName}' is missing or is not an object.");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Official metadata property '{propertyName}' is missing or is not a string.");
        }

        return value.GetString() ?? throw new InvalidDataException($"Official metadata property '{propertyName}' is null.");
    }

    private static Uri RequiredUri(JsonElement parent, string propertyName)
    {
        var value = RequiredString(parent, propertyName);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri : throw new InvalidDataException($"Official metadata property '{propertyName}' is not an HTTPS URI.");
    }

    private static string RequiredSha512(JsonElement parent, string propertyName)
    {
        var value = RequiredString(parent, propertyName).ToLowerInvariant();
        if (value.Length != 128 || value.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException($"Official metadata property '{propertyName}' is not a SHA-512 digest.");
        }

        return value;
    }

    private static Version ParseVersion(string value)
    {
        var core = value.Split('-', 2)[0];
        return Version.TryParse(core, out var version) ? version : new Version(0, 0);
    }

    private static bool IsCommit(string value) =>
        value.Length is 40 or 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidatePackageId(string packageId)
    {
        ValidateToken(packageId, nameof(packageId));
        if (packageId.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
        {
            throw new ArgumentException("The NuGet package ID is malformed.", nameof(packageId));
        }
    }

    private static void ValidateToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 160 || value.Contains('/') || value.Contains('\\') || value.Contains('\0'))
        {
            throw new ArgumentException("The source identifier is malformed.", parameterName);
        }
    }

    private sealed record RuntimeArchiveIdentity(string RuntimeCommit, string JitCommit);
}

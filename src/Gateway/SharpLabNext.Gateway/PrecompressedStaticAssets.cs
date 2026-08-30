using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace SharpLabNext.Gateway;

internal sealed class PrecompressedStaticAssetServer
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly Representation[] Representations =
    [
        new("br", ".br"),
        new("zstd", ".zst"),
        new("gzip", ".gz"),
        new("identity", "")
    ];

    private readonly string[] _roots;
    private readonly FileExtensionContentTypeProvider _contentTypes = new();

    public PrecompressedStaticAssetServer(IEnumerable<string> roots)
    {
        _roots = roots.Where(static root => !string.IsNullOrWhiteSpace(root)).Select(static root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).Distinct(PathComparer()).ToArray();
    }

    public async Task<bool> TryServeRequestAsync(HttpContext context)
    {
        if (!CanServe(context.Request) || IsApplicationRequest(context.Request.Path))
            return false;

        var requestedPath = context.Request.Path.Value;
        var relativePath = string.IsNullOrEmpty(requestedPath) || requestedPath == "/"
            ? "index.html" : requestedPath.TrimStart('/');
        if (relativePath.EndsWith('/'))
            relativePath += "index.html";

        return await TryServeFileAsync(context, relativePath);
    }

    public Task<bool> TryServeIndexAsync(HttpContext context) =>
        CanServe(context.Request) && !IsApplicationRequest(context.Request.Path)
            ? TryServeFileAsync(context, "index.html") : Task.FromResult(false);

    private async Task<bool> TryServeFileAsync(HttpContext context, string relativePath)
    {
        var sourcePath = ResolveFile(relativePath);
        if (sourcePath is null)
            return false;

        var selection = SelectRepresentation(context.Request, sourcePath);
        context.Response.Headers.Vary = "Accept-Encoding";
        if (selection is null)
        {
            context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
            context.Response.ContentLength = 0;
            return true;
        }

        var contentType = _contentTypes.TryGetContentType(sourcePath, out var resolvedContentType)
            ? resolvedContentType : "application/octet-stream";
        context.Response.Headers.CacheControl = CacheControlFor(relativePath);
        if (selection.Encoding != "identity")
            context.Response.Headers.ContentEncoding = selection.Encoding;

        var selectedFile = new FileInfo(selection.Path);
        var lastModified = new DateTimeOffset(selectedFile.LastWriteTimeUtc);
        var entityTag = new EntityTagHeaderValue($"\"{lastModified.UtcTicks:x}-{selectedFile.Length:x}\"");
        await TypedResults.PhysicalFile(selection.Path, contentType, lastModified: lastModified, entityTag: entityTag, enableRangeProcessing: true).ExecuteAsync(context);
        return true;
    }

    private string? ResolveFile(string relativePath)
    {
        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(relativePath);
        }
        catch (UriFormatException)
        {
            return null;
        }

        try
        {
            decodedPath = decodedPath.Replace('/', Path.DirectorySeparatorChar);
            foreach (var root in _roots)
            {
                var candidate = Path.GetFullPath(Path.Combine(root, decodedPath));
                var rootPrefix = root + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(rootPrefix, PathComparison) || !File.Exists(candidate))
                    continue;
                return candidate;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
        return null;
    }

    private static SelectedRepresentation? SelectRepresentation(HttpRequest request, string sourcePath)
    {
        var accepted = AcceptedEncodings.Parse(request);
        SelectedRepresentation? best = null;
        var bestQuality = 0d;
        foreach (var representation in Representations)
        {
            var quality = accepted.QualityFor(representation.Encoding);
            if (quality <= 0 || quality < bestQuality)
                continue;

            var path = sourcePath + representation.Suffix;
            if (!File.Exists(path))
                continue;

            if (best is null || quality > bestQuality)
            {
                best = new SelectedRepresentation(representation.Encoding, path);
                bestQuality = quality;
            }
        }
        return best;
    }

    private static bool CanServe(HttpRequest request) =>
        !request.HttpContext.WebSockets.IsWebSocketRequest &&
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method));

    private static bool IsApplicationRequest(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase);

    private static string CacheControlFor(string relativePath)
    {
        if (string.Equals(relativePath, "index.html", StringComparison.OrdinalIgnoreCase))
            return "no-cache";
        return IsHashedAsset(relativePath)
            ? "public,max-age=31536000,immutable" : "public,max-age=3600";
    }

    private static bool IsHashedAsset(string relativePath)
    {
        if (!relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        var separator = stem.LastIndexOf('-');
        if (separator < 0 || stem.Length - separator - 1 < 8)
            return false;
        foreach (var character in stem.AsSpan(separator + 1))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-'))
                return false;
        }
        return true;
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record Representation(string Encoding, string Suffix);
    private sealed record SelectedRepresentation(string Encoding, string Path);

    private sealed class AcceptedEncodings
    {
        private readonly IReadOnlyDictionary<string, double> _explicitQualities;
        private readonly double? _wildcardQuality;
        private readonly bool _headerPresent;

        private AcceptedEncodings(IReadOnlyDictionary<string, double> explicitQualities, double? wildcardQuality, bool headerPresent)
        {
            _explicitQualities = explicitQualities;
            _wildcardQuality = wildcardQuality;
            _headerPresent = headerPresent;
        }

        public static AcceptedEncodings Parse(HttpRequest request)
        {
            if (!request.Headers.ContainsKey("Accept-Encoding"))
                return new AcceptedEncodings(new Dictionary<string, double>(), null, false);

            var qualities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double? wildcard = null;
            try
            {
                var values = request.GetTypedHeaders().AcceptEncoding;
                if (values is null)
                    return new AcceptedEncodings(qualities, null, true);
                foreach (var value in values)
                {
                    var encoding = value.Value.ToString();
                    var quality = value.Quality ?? 1;
                    if (encoding == "*")
                    {
                        wildcard = wildcard is null ? quality : Math.Max(wildcard.Value, quality);
                        continue;
                    }
                    qualities[encoding] = qualities.TryGetValue(encoding, out var existing)
                        ? Math.Max(existing, quality) : quality;
                }
            }
            catch (FormatException)
            {
                return new AcceptedEncodings(new Dictionary<string, double>(), null, false);
            }
            return new AcceptedEncodings(qualities, wildcard, true);
        }

        public double QualityFor(string encoding)
        {
            if (!_headerPresent)
                return encoding == "identity" ? 1 : 0;
            if (_explicitQualities.TryGetValue(encoding, out var quality))
                return quality;
            if (encoding == "identity")
                return _wildcardQuality == 0 ? 0 : 1;
            return _wildcardQuality ?? 0;
        }
    }
}

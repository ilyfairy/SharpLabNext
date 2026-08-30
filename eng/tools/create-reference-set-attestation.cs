#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property RestorePackagesWithLockFile=false

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var values = Parse(args);
if (values.ContainsKey("help"))
{
    Console.WriteLine("Usage: dotnet run eng/tools/create-reference-set-attestation.cs -- " + "--root PATH --id ID --target-framework TFM --digest DIGEST " + "--provenance-kind KIND --resolved-version VERSION " + "[--package ID] [--source-uri URI] [--commit COMMIT] " + "[--source-archive-digest DIGEST]");
    return;
}

var root = Path.GetFullPath(Required(values, "root"));
var id = Required(values, "id");
var targetFramework = Required(values, "target-framework");
var digest = Required(values, "digest");
var provenanceKind = Required(values, "provenance-kind");
var resolvedVersion = Required(values, "resolved-version");
if (!Directory.Exists(root))
    throw new InvalidOperationException($"Reference root '{root}' does not exist.");

var files = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal).Select(path =>
    {
        using var stream = File.OpenRead(path);
        return new AttestedFile(Path.GetFileName(path), stream.Length, $"sha256:{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}");
    }).ToArray();
if (files.Length == 0)
    throw new InvalidOperationException("The reference root contains no DLL files.");

var canonical = new StringBuilder();
foreach (var file in files)
{
    canonical.Append(file.Digest).Append("  ").Append(file.Size).Append("  ").Append(file.Path).Append('\n');
}
var contentDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant()}";
var document = new AttestationDocument(1, new ReferenceSet(id, targetFramework, digest, contentDigest, new Provenance(provenanceKind, resolvedVersion, Optional(values, "package"), Optional(values, "source-uri"), Optional(values, "commit"), Optional(values, "source-archive-digest"))), files);
var output = Path.Combine(root, "reference-set.attestation.json");
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(document, AttestationJsonContext.Default.AttestationDocument) + "\n");
Console.WriteLine(output);

static Dictionary<string, string?> Parse(string[] arguments)
{
    var values = new Dictionary<string, string?>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (argument is "-h" or "--help")
        {
            values["help"] = null;
            continue;
        }
        if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
            throw new ArgumentException($"Invalid argument '{argument}'.");
        var key = argument[2..];
        if (!values.TryAdd(key, arguments[++index]))
            throw new ArgumentException($"Duplicate argument '--{key}'.");
    }
    return values;
}

static string Required(IReadOnlyDictionary<string, string?> values, string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"--{key} is required.");

static string? Optional(IReadOnlyDictionary<string, string?> values, string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

sealed record AttestationDocument(int SchemaVersion, ReferenceSet ReferenceSet, IReadOnlyList<AttestedFile> Files);

sealed record ReferenceSet(string Id, string TargetFramework, string Digest, string ContentDigest, Provenance Provenance);

sealed record Provenance(string Kind, string ResolvedVersion, string? Package, string? SourceUri, string? Commit, string? SourceArchiveDigest);

sealed record AttestedFile(string Path, long Size, string Digest);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AttestationDocument))]
sealed partial class AttestationJsonContext : JsonSerializerContext;

#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

var options = PackOptions.Parse(args);
var root = FindRepositoryRoot();
var manifestPath = Path.Combine(root, "eng", "sdk-packages.json");
var manifest = JsonSerializer.Deserialize(
    File.ReadAllText(manifestPath),
    PackJsonContext.Default.SdkPackageManifest)
    ?? throw new InvalidOperationException("eng/sdk-packages.json is empty.");
if (manifest.SchemaVersion != 1 || manifest.Packages.Count == 0)
    throw new InvalidOperationException("Unsupported or empty SDK package manifest.");

var version = options.Version ?? ReadDefaultVersion(Path.Combine(root, "Directory.Build.props"));
var output = ResolveRepositoryPath(root, options.Output ?? Path.Combine("artifacts", "packages"));
var work = ResolveRepositoryPath(root, Path.Combine("artifacts", ".sdk-pack-work"));
var firstPass = Path.Combine(work, "first");
var secondPass = Path.Combine(work, "second");

Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");
Environment.SetEnvironmentVariable("NUGET_XMLDOC_MODE", "skip");

if (!options.SkipRestore)
{
    Run(root, "dotnet", [
        "restore",
        "SharpLabNext.slnx",
        "--locked-mode",
        $"--property:SharpLabNextPackageVersion={version}"
    ]);
}

PrepareDirectory(work, root);
PackPass(root, manifest, version, firstPass);
PackPass(root, manifest, version, secondPass);
VerifyReproducible(manifest, version, firstPass, secondPass);

PrepareDirectory(output, root);
foreach (var package in manifest.Packages)
{
    var fileName = PackageFileName(package.Id, version);
    File.Copy(Path.Combine(firstPass, fileName), Path.Combine(output, fileName));
}
WriteChecksums(output);

Run(root, "dotnet", [
    "run",
    "eng/verify-packages.cs",
    "--",
    "--packages", output,
    "--version", version
]);

Directory.Delete(work, recursive: true);
Console.WriteLine($"Packed {manifest.Packages.Count} reproducible SDK packages at {output}.");

static void PackPass(
    string root,
    SdkPackageManifest manifest,
    string version,
    string output)
{
    PrepareDirectory(output, root);
    foreach (var package in manifest.Packages)
    {
        Run(root, "dotnet", [
            "pack",
            package.Project,
            "--configuration", "Release",
            "--no-restore",
            "--output", output,
            $"--property:SharpLabNextPackageVersion={version}",
            "--property:ContinuousIntegrationBuild=true",
            $"--property:PathMap={root}=/_/"
        ]);
    }

    var actualPackages = Directory.GetFiles(output, "*.nupkg", SearchOption.TopDirectoryOnly);
    if (actualPackages.Length != manifest.Packages.Count)
    {
        throw new InvalidOperationException(
            $"Expected {manifest.Packages.Count} packages, but pack produced {actualPackages.Length}.");
    }

    foreach (var package in manifest.Packages)
    {
        var path = Path.Combine(output, PackageFileName(package.Id, version));
        if (!File.Exists(path))
            throw new FileNotFoundException($"Pack did not produce {Path.GetFileName(path)}.", path);
        NormalizePackage(path, version);
    }
}

static void NormalizePackage(string packagePath, string version)
{
    const string relationshipsPath = "_rels/.rels";
    const string corePropertiesDirectory = "package/services/metadata/core-properties/";
    const string canonicalCorePropertiesPath = corePropertiesDirectory + "core-properties.psmdcp";
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    using (var input = ZipFile.OpenRead(packagePath))
    {
        foreach (var entry in input.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var path = entry.FullName.StartsWith(corePropertiesDirectory, StringComparison.Ordinal)
                && entry.FullName.EndsWith(".psmdcp", StringComparison.Ordinal)
                    ? canonicalCorePropertiesPath
                    : entry.FullName;
            if (!files.TryAdd(path, buffer.ToArray()))
                throw new InvalidDataException($"Package contains duplicate normalized entry '{path}'.");
        }
    }

    if (!files.TryGetValue(relationshipsPath, out var relationships))
        throw new InvalidDataException("Package does not contain _rels/.rels.");
    files[relationshipsPath] = NormalizeRelationships(relationships, canonicalCorePropertiesPath);

    var nuspec = files.Keys.SingleOrDefault(static path =>
        !path.Contains('/') && path.EndsWith(".nuspec", StringComparison.Ordinal));
    if (nuspec is null)
        throw new InvalidDataException("Package must contain exactly one root nuspec.");
    files[nuspec] = NormalizeNuspec(files[nuspec], version);

    var temporaryPath = packagePath + ".normalized";
    File.Delete(temporaryPath);
    using (var output = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
    {
        foreach (var file in files.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var entry = output.CreateEntry(file.Key, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = 0;
            using var stream = entry.Open();
            stream.Write(file.Value);
        }
    }
    File.Move(temporaryPath, packagePath, overwrite: true);
}

static byte[] NormalizeRelationships(byte[] content, string corePropertiesPath)
{
    var document = ParseXml(content);
    XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
    foreach (var relationship in document.Root?.Elements(ns + "Relationship")
        ?? throw new InvalidDataException("Invalid package relationships document."))
    {
        var type = (string?)relationship.Attribute("Type") ?? string.Empty;
        if (type.EndsWith("/manifest", StringComparison.Ordinal))
            relationship.SetAttributeValue("Id", "RManifest");
        else if (type.EndsWith("/metadata/core-properties", StringComparison.Ordinal))
        {
            relationship.SetAttributeValue("Id", "RCoreProperties");
            relationship.SetAttributeValue("Target", "/" + corePropertiesPath);
        }
    }
    return SerializeXml(document);
}

static byte[] NormalizeNuspec(byte[] content, string version)
{
    var document = ParseXml(content);
    var root = document.Root ?? throw new InvalidDataException("Invalid nuspec document.");
    var ns = root.Name.Namespace;
    var metadata = root.Element(ns + "metadata")
        ?? throw new InvalidDataException("Nuspec does not contain metadata.");
    metadata.Element(ns + "version")!.Value = version;
    foreach (var dependency in metadata.Descendants(ns + "dependency"))
    {
        var id = (string?)dependency.Attribute("id");
        if (id?.StartsWith("SharpLabNext.", StringComparison.OrdinalIgnoreCase) == true)
            dependency.SetAttributeValue("version", $"[{version}]");
    }
    return SerializeXml(document);
}

static byte[] SerializeXml(XDocument document)
{
    using var buffer = new MemoryStream();
    using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        NewLineHandling = NewLineHandling.None,
        OmitXmlDeclaration = false
    }))
    {
        document.Save(writer);
    }
    return buffer.ToArray();
}

static XDocument ParseXml(byte[] content)
{
    using var stream = new MemoryStream(content, writable: false);
    return XDocument.Load(stream);
}

static void VerifyReproducible(
    SdkPackageManifest manifest,
    string version,
    string firstPass,
    string secondPass)
{
    foreach (var package in manifest.Packages)
    {
        var fileName = PackageFileName(package.Id, version);
        var firstHash = HashFile(Path.Combine(firstPass, fileName));
        var secondHash = HashFile(Path.Combine(secondPass, fileName));
        if (!StringComparer.Ordinal.Equals(firstHash, secondHash))
        {
            throw new InvalidOperationException(
                $"Package {fileName} is not reproducible: {firstHash} != {secondHash}.");
        }
    }
}

static void WriteChecksums(string output)
{
    var lines = Directory.GetFiles(output, "*.nupkg", SearchOption.TopDirectoryOnly)
        .OrderBy(Path.GetFileName, StringComparer.Ordinal)
        .Select(path => $"{HashFile(path).ToLowerInvariant()}  {Path.GetFileName(path)}")
        .ToArray();
    File.WriteAllLines(Path.Combine(output, "packages.sha256"), lines, new UTF8Encoding(false));
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static string PackageFileName(string id, string version) => $"{id}.{version}.nupkg";

static string ReadDefaultVersion(string propsPath)
{
    var document = XDocument.Load(propsPath);
    var value = document.Descendants("SharpLabNextPackageVersion")
        .Select(static element => element.Value.Trim())
        .FirstOrDefault(static value => value.Length > 0);
    return value ?? throw new InvalidOperationException(
        "Directory.Build.props does not define SharpLabNextPackageVersion.");
}

static void Run(string workingDirectory, string fileName, IReadOnlyList<string> arguments)
{
    Console.WriteLine($"> {fileName} {string.Join(' ', arguments.Select(Quote))}");
    var startInfo = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Could not start {fileName}.");
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}.");
}

static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
            return directory.FullName;
    }
    throw new DirectoryNotFoundException("Could not locate the SharpLabNext repository root.");
}

static string ResolveRepositoryPath(string root, string path)
{
    var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    var relative = Path.GetRelativePath(root, fullPath);
    if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidOperationException("Pack output must be a directory inside the repository.");
    return fullPath;
}

static void PrepareDirectory(string path, string root)
{
    _ = ResolveRepositoryPath(root, path);
    if (Directory.Exists(path))
        Directory.Delete(path, recursive: true);
    Directory.CreateDirectory(path);
}

internal sealed record PackOptions(string? Version, string? Output, bool SkipRestore)
{
    public static PackOptions Parse(string[] arguments)
    {
        string? version = null;
        string? output = null;
        var skipRestore = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--version":
                    version = ReadValue(arguments, ref index, "--version");
                    break;
                case "--output":
                    output = ReadValue(arguments, ref index, "--output");
                    break;
                case "--skip-restore":
                    skipRestore = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arguments[index]}'.");
            }
        }
        return new PackOptions(version, output, skipRestore);
    }

    private static string ReadValue(string[] arguments, ref int index, string option)
    {
        if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
            throw new ArgumentException($"{option} requires a value.");
        return arguments[index];
    }
}

internal sealed record SdkPackageManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("packages")] IReadOnlyList<SdkPackageDefinition> Packages);

internal sealed record SdkPackageDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("targetFrameworks")] IReadOnlyList<string> TargetFrameworks,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string> Dependencies,
    [property: JsonPropertyName("requiredFiles")] IReadOnlyList<string> RequiredFiles);

[JsonSerializable(typeof(SdkPackageManifest))]
internal sealed partial class PackJsonContext : JsonSerializerContext;

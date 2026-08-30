#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property RestorePackagesWithLockFile=false
#:property LangVersion=14.0
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

var options = VerifyOptions.Parse(args);
var root = FindRepositoryRoot();
var packageDirectory = Path.GetFullPath(options.Packages);
var manifest = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "eng", "sdk-packages.json")), VerifyJsonContext.Default.SdkPackageManifest) ?? throw new InvalidOperationException("eng/sdk-packages.json is empty.");
if (manifest.SchemaVersion != 1)
    throw new InvalidOperationException("Unsupported SDK package manifest schema.");

var expectedNames = manifest.Packages.Select(package => PackageFileName(package.Id, options.Version)).Order(StringComparer.Ordinal).ToArray();
var actualNames = Directory.GetFiles(packageDirectory, "*.nupkg", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
Require(expectedNames.SequenceEqual(actualNames, StringComparer.Ordinal), $"Package set differs. Expected [{string.Join(", ", expectedNames)}], actual [{string.Join(", ", actualNames)}].");

foreach (var package in manifest.Packages)
    VerifyPackage(Path.Combine(packageDirectory, PackageFileName(package.Id, options.Version)), package, options.Version);
VerifyChecksums(packageDirectory, expectedNames);
if (!options.SkipConsumer)
    VerifyConsumer(root, packageDirectory, options.Version);

Console.WriteLine($"Verified {manifest.Packages.Count} SDK packages at {packageDirectory}.");

static void VerifyPackage(string path, SdkPackageDefinition package, string version)
{
    using var archive = ZipFile.OpenRead(path);
    var entries = archive.Entries.Where(static entry => !entry.FullName.EndsWith('/')).ToArray();
    var names = entries.Select(static entry => entry.FullName).ToArray();
    Require(names.Distinct(StringComparer.Ordinal).Count() == names.Length, $"{package.Id} contains duplicate ZIP entries.");
    Require(names.SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal), $"{package.Id} entries are not in canonical order.");
    foreach (var entry in entries)
    {
        Require(!entry.FullName.StartsWith('/') && !entry.FullName.Contains('\\') && !entry.FullName.Split('/').Any(static segment => segment is "." or ".."), $"{package.Id} contains unsafe entry '{entry.FullName}'.");
        Require(entry.LastWriteTime.DateTime == new DateTime(2000, 1, 1), $"{package.Id} entry '{entry.FullName}' does not use the reproducible timestamp.");
    }

    var nuspecEntry = entries.SingleOrDefault(static entry => !entry.FullName.Contains('/') && entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
    Require(nuspecEntry is not null, $"{package.Id} must contain one root nuspec.");
    var nuspec = ReadXml(nuspecEntry!);
    var nuspecRoot = nuspec.Root ?? throw new InvalidDataException("Invalid nuspec XML.");
    var ns = nuspecRoot.Name.Namespace;
    var metadata = nuspecRoot.Element(ns + "metadata") ?? throw new InvalidDataException($"{package.Id} nuspec has no metadata.");
    Require(Value(metadata, ns + "id") == package.Id, $"{package.Id} nuspec ID differs.");
    Require(Value(metadata, ns + "version") == version, $"{package.Id} version differs.");
    Require(Value(metadata, ns + "license") == "BSD-2-Clause", $"{package.Id} license differs.");
    Require(Value(metadata, ns + "readme") == "README.md", $"{package.Id} readme metadata differs.");
    var repository = metadata.Element(ns + "repository");
    Require((string?)repository?.Attribute("url") == "https://github.com/sharplabnext/SharpLabNext",
        $"{package.Id} repository URL differs.");

    var dependencies = metadata.Descendants(ns + "dependency").Select(dependency => new { Id = (string?)dependency.Attribute("id") ?? string.Empty, Version = (string?)dependency.Attribute("version") ?? string.Empty }).ToArray();
    Require(dependencies.Select(static dependency => dependency.Id).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(package.Dependencies.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase), $"{package.Id} dependency set differs from eng/sdk-packages.json.");
    foreach (var dependency in dependencies)
        Require(dependency.Version == $"[{version}]", $"{package.Id} dependency {dependency.Id} must use exact version [{version}], not {dependency.Version}.");

    foreach (var requiredFile in package.RequiredFiles)
        Require(names.Contains(requiredFile, StringComparer.Ordinal), $"{package.Id} is missing {requiredFile}.");
    Require(names.Contains("package/services/metadata/core-properties/core-properties.psmdcp", StringComparer.Ordinal), $"{package.Id} does not use the canonical core-properties entry.");

    var expectedAssemblyVersion = ParseAssemblyVersion(version);
    foreach (var framework in package.TargetFrameworks)
    {
        var assemblyPath = $"lib/{framework}/{package.Id}.dll";
        var assemblyEntry = entries.SingleOrDefault(entry => entry.FullName == assemblyPath);
        Require(assemblyEntry is not null, $"{package.Id} is missing {assemblyPath}.");
        using var assemblyStream = assemblyEntry!.Open();
        using var assemblyBytes = new MemoryStream();
        assemblyStream.CopyTo(assemblyBytes);
        assemblyBytes.Position = 0;
        using var peReader = new PEReader(assemblyBytes);
        Require(peReader.HasMetadata, $"{assemblyPath} is not a managed assembly.");
        var metadataReader = peReader.GetMetadataReader();
        var definition = metadataReader.GetAssemblyDefinition();
        Require(metadataReader.GetString(definition.Name) == package.Id, $"{assemblyPath} assembly name differs.");
        Require(definition.Version == expectedAssemblyVersion, $"{assemblyPath} assembly version {definition.Version} differs from {expectedAssemblyVersion}.");
    }
}

static void VerifyChecksums(string packageDirectory, IReadOnlyList<string> expectedNames)
{
    var checksumPath = Path.Combine(packageDirectory, "packages.sha256");
    Require(File.Exists(checksumPath), "packages.sha256 is missing.");
    var expected = expectedNames.Select(name =>
    {
        using var stream = File.OpenRead(Path.Combine(packageDirectory, name));
        return $"{Convert.ToHexStringLower(SHA256.HashData(stream))}  {name}";
    }).ToArray();
    var actual = File.ReadAllLines(checksumPath);
    Require(expected.SequenceEqual(actual, StringComparer.Ordinal), "packages.sha256 content differs.");
}

static void VerifyConsumer(string root, string packageDirectory, string version)
{
    var directory = Path.Combine(root, "artifacts", ".sdk-package-consumer");
    if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    Directory.CreateDirectory(directory);
    var escapedSource = SecurityElement.Escape(packageDirectory) ?? throw new InvalidOperationException("Could not escape the package source path.");
    File.WriteAllText(Path.Combine(directory, "NuGet.config"), $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="sharplabnext-sdk" value="{{escapedSource}}" />
          </packageSources>
        </configuration>
        """, new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(directory, "Consumer.csproj"), $$"""
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <LangVersion>14.0</LangVersion>
            <Nullable>enable</Nullable>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <NuGetAudit>false</NuGetAudit>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
            <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="SharpLabNext.LanguageWorker.Sdk" Version="[{{version}}]" />
            <PackageReference Include="SharpLabNext.LanguageWorker.Conformance" Version="[{{version}}]" />
            <PackageReference Include="SharpLabNext.ArtifactWorker.Sdk" Version="[{{version}}]" />
            <PackageReference Include="SharpLabNext.RuntimeProfile.Sdk" Version="[{{version}}]" />
            <PackageReference Include="SharpLab.Runtime" Version="[{{version}}]" />
          </ItemGroup>
        </Project>
        """, new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(directory, "Program.cs"), """
        using SharpLabNext.ArtifactStore.Client;
        using SharpLabNext.Artifacts.Contracts;
        using SharpLabNext.ArtifactWorker.Sdk;
        using SharpLabNext.Contracts;
        using SharpLabNext.Contracts.Grpc;
        using SharpLabNext.LanguageWorker.Conformance;
        using SharpLabNext.LanguageWorker.Sdk;
        using SharpLabNext.RuntimeProfile.Sdk;
        using SharpLabNext.WorkerHost;
        using SharpLab.Runtime;

        Type[] publicDependencyClosure =
        [
            typeof(ServiceIdentity),
            typeof(ArtifactManifest),
            typeof(IWorkerControlService),
            typeof(IArtifactStoreClient),
            typeof(WorkerHostExtensions),
            typeof(LanguageWorkerCapabilityManifest),
            typeof(LanguageWorkerConformanceRunner),
            typeof(ArtifactWorkerCapabilityManifest),
            typeof(RuntimeProfileDefinition),
            typeof(RuntimeServices)
        ];
        Console.WriteLine(publicDependencyClosure.Length);
        """, new UTF8Encoding(false));

    var packages = Path.Combine(directory, "packages");
    Run(directory, "dotnet", [
        "restore", "Consumer.csproj",
        "--configfile", "NuGet.config",
        "--packages", packages,
        "--use-lock-file"
    ]);
    Run(directory, "dotnet", [
        "restore", "Consumer.csproj",
        "--configfile", "NuGet.config",
        "--packages", packages,
        "--locked-mode"
    ]);
    Run(directory, "dotnet", ["build", "Consumer.csproj", "--configuration", "Release", "--no-restore"]);
    Directory.Delete(directory, recursive: true);
}

static XDocument ReadXml(ZipArchiveEntry entry)
{
    using var stream = entry.Open();
    return XDocument.Load(stream);
}

static string Value(XElement parent, XName name) => parent.Element(name)?.Value ?? throw new InvalidDataException($"Missing nuspec element {name.LocalName}.");

static Version ParseAssemblyVersion(string packageVersion)
{
    var stable = packageVersion.Split(['-', '+'], 2)[0];
    var parts = stable.Split('.').Select(int.Parse).ToArray();
    return parts.Length switch
    {
        1 => new Version(parts[0], 0, 0, 0),
        2 => new Version(parts[0], parts[1], 0, 0),
        3 => new Version(parts[0], parts[1], parts[2], 0),
        4 => new Version(parts[0], parts[1], parts[2], parts[3]),
        _ => throw new InvalidOperationException($"Unsupported package version '{packageVersion}'.")
    };
}

static void Run(string workingDirectory, string fileName, IReadOnlyList<string> arguments)
{
    Console.WriteLine($"> {fileName} {string.Join(' ', arguments)}");
    var startInfo = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}.");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidDataException(message);
}

static string PackageFileName(string id, string version) => $"{id}.{version}.nupkg";

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
            return directory.FullName;
    }
    throw new DirectoryNotFoundException("Could not locate the SharpLabNext repository root.");
}

internal sealed record VerifyOptions(string Packages, string Version, bool SkipConsumer)
{
    public static VerifyOptions Parse(string[] arguments)
    {
        string? packages = null;
        string? version = null;
        var skipConsumer = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--packages":
                    packages = ReadValue(arguments, ref index, "--packages");
                    break;
                case "--version":
                    version = ReadValue(arguments, ref index, "--version");
                    break;
                case "--skip-consumer":
                    skipConsumer = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arguments[index]}'.");
            }
        }
        if (packages is null || version is null)
            throw new ArgumentException("Usage: verify-packages.cs --packages PATH --version VERSION [--skip-consumer]");
        return new VerifyOptions(packages, version, skipConsumer);
    }

    private static string ReadValue(string[] arguments, ref int index, string option)
    {
        if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
            throw new ArgumentException($"{option} requires a value.");
        return arguments[index];
    }
}

internal sealed record SdkPackageManifest([property: JsonPropertyName("schemaVersion")] int SchemaVersion, [property: JsonPropertyName("packages")] IReadOnlyList<SdkPackageDefinition> Packages);

internal sealed record SdkPackageDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("targetFrameworks")] IReadOnlyList<string> TargetFrameworks,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string> Dependencies,
    [property: JsonPropertyName("requiredFiles")] IReadOnlyList<string> RequiredFiles);

[JsonSerializable(typeof(SdkPackageManifest))]
internal sealed partial class VerifyJsonContext : JsonSerializerContext;

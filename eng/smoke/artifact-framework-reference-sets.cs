#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length != 3)
    throw new ArgumentException("Usage: artifact-framework-reference-sets.cs <runtime-matrix.json> <reference-root> <processor.dll>");

var matrixPath = Path.GetFullPath(args[0]);
var referenceRoot = Path.GetFullPath(args[1]);
var processorPath = Path.GetFullPath(args[2]);
if (!File.Exists(matrixPath) || !Directory.Exists(referenceRoot) || !File.Exists(processorPath))
    throw new FileNotFoundException("The runtime matrix, reference root, or artifact processor is unavailable.");

using var matrix = JsonDocument.Parse(await File.ReadAllBytesAsync(matrixPath));
var targets = matrix.RootElement.GetProperty("framework").GetProperty("targets").EnumerateArray().Select(static target => new FrameworkTarget(target.GetProperty("id").GetString()!, target.GetProperty("referenceSetId").GetString()!, target.GetProperty("targetFramework").GetString()!, target.GetProperty("version").GetString()!, target.TryGetProperty("referencePackage", out var package) ? package.GetProperty("packageContentHash").GetString()! : target.GetProperty("referenceComposition").GetProperty("sourceIdentityDigest").GetString()!)).ToArray();
if (targets.Length != 14)
    throw new InvalidDataException($"Expected 14 Framework targets, observed {targets.Length}.");

var dotnetHost = ResolveDotNetHost();
var sdkVersion = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(matrixPath)!, "..", "global.json"))).RootElement.GetProperty("sdk").GetProperty("version").GetString() ?? throw new InvalidDataException("global.json does not declare an SDK version.");
var dotnetRoot = Path.GetDirectoryName(dotnetHost)!;
var compilerPath = Path.Combine(dotnetRoot, "sdk", sdkVersion, "Roslyn", "bincore", "csc.dll");
if (!File.Exists(compilerPath))
    throw new FileNotFoundException("The locked SDK C# compiler is unavailable.", compilerPath);

var workRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-framework-artifact-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(workRoot);
try
{
    foreach (var target in targets)
    {
        var targetRoot = Path.Combine(referenceRoot, target.ReferenceSetId);
        ValidateReferenceRoot(target, targetRoot);
        var targetWork = Path.Combine(workRoot, target.Id);
        Directory.CreateDirectory(targetWork);
        var sourcePath = Path.Combine(targetWork, "FrameworkArtifactSmoke.cs");
        var assemblyPath = Path.Combine(targetWork, "FrameworkArtifactSmoke.dll");
        await File.WriteAllTextAsync(sourcePath, "public sealed class FrameworkArtifactSmoke { " + "public static System.Net.WebClient CreateClient() { return new System.Net.WebClient(); } }\n", new UTF8Encoding(false));
        await CompileAsync(dotnetHost, compilerPath, targetRoot, sourcePath, assemblyPath);
        ValidateCompiledReferences(target, targetRoot, assemblyPath);

        await RunProcessorAsync(dotnetHost, processorPath, target, targetRoot, assemblyPath, targetWork, "il");
        await RunProcessorAsync(dotnetHost, processorPath, target, targetRoot, assemblyPath, targetWork, "decompiled-csharp");
        if (target.Id is "netfx20" or "netfx30" or "netfx40" or "netfx48")
        {
            await RunProcessorAsync(dotnetHost, processorPath, target, targetRoot, assemblyPath, targetWork, "verify");
        }

        Console.WriteLine($"{target.ReferenceSetId}: compile, IL, C#, and reference resolution passed.");
    }
}
finally
{
    Directory.Delete(workRoot, recursive: true);
}

static void ValidateReferenceRoot(FrameworkTarget target, string root)
{
    if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "mscorlib.dll")) || !File.Exists(Path.Combine(root, "System.dll")) || !File.Exists(Path.Combine(root, "reference-set.attestation.json")))
    {
        throw new InvalidDataException($"Reference set '{target.ReferenceSetId}' is incomplete.");
    }

    using var attestation = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "reference-set.attestation.json")));
    var identity = attestation.RootElement.GetProperty("referenceSet");
    if (identity.GetProperty("id").GetString() != target.ReferenceSetId || identity.GetProperty("targetFramework").GetString() != target.TargetFramework || identity.GetProperty("digest").GetString() != target.IdentityDigest)
    {
        throw new InvalidDataException($"Reference set '{target.ReferenceSetId}' attestation identity is inconsistent.");
    }

    var files = attestation.RootElement.GetProperty("files").EnumerateArray().Select(static file => new AttestedFile(file.GetProperty("path").GetString()!, file.GetProperty("size").GetInt64(), file.GetProperty("digest").GetString()!)).OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray();
    var actualPaths = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
    if (!actualPaths.SequenceEqual(files.Select(static file => file.Path), StringComparer.Ordinal))
        throw new InvalidDataException($"Reference set '{target.ReferenceSetId}' file closure is inconsistent.");
    var canonical = new StringBuilder();
    foreach (var file in files)
    {
        var path = Path.Combine(root, file.Path);
        using var stream = File.OpenRead(path);
        var digest = $"sha256:{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}";
        if (stream.Length != file.Size || digest != file.Digest)
        {
            throw new InvalidDataException($"Reference set '{target.ReferenceSetId}' file '{file.Path}' failed attestation.");
        }
        canonical.Append(file.Digest).Append("  ").Append(file.Size).Append("  ").Append(file.Path).Append('\n');
    }
    var contentDigest =
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant()}";
    if (identity.GetProperty("contentDigest").GetString() != contentDigest)
    {
        throw new InvalidDataException($"Reference set '{target.ReferenceSetId}' content digest is inconsistent.");
    }
}

static async Task CompileAsync(string dotnetHost, string compilerPath, string referenceRoot, string sourcePath, string assemblyPath)
{
    var responsePath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "compiler.rsp");
    var arguments = new List<string> { "/nologo", "/noconfig", "/nostdlib+", "/deterministic+", "/target:library", $"/out:\"{assemblyPath}\"" };
    arguments.AddRange(Directory.EnumerateFiles(referenceRoot, "*.dll", SearchOption.TopDirectoryOnly).Where(IsManagedAssembly).Order(StringComparer.Ordinal).Select(static path => $"/reference:\"{path}\""));
    arguments.Add($"\"{sourcePath}\"");
    await File.WriteAllLinesAsync(responsePath, arguments, new UTF8Encoding(false));
    await RunAsync(dotnetHost, ["exec", compilerPath, $"@{responsePath}"], Path.GetDirectoryName(assemblyPath)!);
}

static bool IsManagedAssembly(string path)
{
    try
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        return pe.HasMetadata && pe.GetMetadataReader().IsAssembly;
    }
    catch (BadImageFormatException)
    {
        return false;
    }
}

static void ValidateCompiledReferences(FrameworkTarget target, string referenceRoot, string assemblyPath)
{
    var expected = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase)
    {
        ["mscorlib"] = ReadAssemblyVersion(Path.Combine(referenceRoot, "mscorlib.dll")),
        ["System"] = ReadAssemblyVersion(Path.Combine(referenceRoot, "System.dll"))
    };
    using var stream = File.OpenRead(assemblyPath);
    using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
    var metadata = pe.GetMetadataReader();
    var observed = metadata.AssemblyReferences.Select(handle => metadata.GetAssemblyReference(handle)).ToDictionary(reference => metadata.GetString(reference.Name), static reference => reference.Version, StringComparer.OrdinalIgnoreCase);
    foreach (var (name, expectedVersion) in expected)
    {
        if (!observed.TryGetValue(name, out var actualVersion) || actualVersion != expectedVersion)
        {
            throw new InvalidDataException($"Compiled '{target.ReferenceSetId}' artifact resolved {name} {actualVersion}, " + $"but its own reference root requires {expectedVersion}.");
        }
    }
}

static Version ReadAssemblyVersion(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
    return pe.GetMetadataReader().GetAssemblyDefinition().Version;
}

static async Task RunProcessorAsync(string dotnetHost, string processorPath, FrameworkTarget target, string referenceRoot, string assemblyPath, string workRoot, string operation)
{
    var requestPath = Path.Combine(workRoot, $"{operation}.request.json");
    var responsePath = Path.Combine(workRoot, $"{operation}.response.json");
    var outputPath = Path.Combine(workRoot, $"{operation}.txt");
    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
    {
        ProtocolVersion = 1,
        Operation = operation,
        AssemblyPath = assemblyPath,
        PortablePdbPath = (string?)null,
        OutputPath = outputPath,
        ReferenceRoots = new[] { referenceRoot },
        SystemModuleName = "mscorlib",
        IncludeSequencePoints = false,
        IncludeCompilerGeneratedMembers = true,
        IncludeMetadataTokens = true,
        MaxCharacters = 1_000_000,
        MaxFindings = 1_000,
        RewriterProfileId = (string?)null,
        PortablePdbOutputPath = (string?)null,
        ArtifactFormat = "dotnet-framework-managed-pe-v1"
    }));
    await RunAsync(dotnetHost, [processorPath, "--request", requestPath, "--response", responsePath], workRoot);
    using var response = JsonDocument.Parse(await File.ReadAllBytesAsync(responsePath));
    var outcome = response.RootElement.GetProperty("Outcome").GetString();
    if (outcome != "succeeded")
    {
        throw new InvalidDataException($"Processor operation '{operation}' failed for '{target.ReferenceSetId}' with '{outcome}': " + response.RootElement.GetRawText());
    }
    if (operation == "il" && !File.ReadAllText(outputPath).Contains("FrameworkArtifactSmoke", StringComparison.Ordinal) || operation == "decompiled-csharp" && !File.ReadAllText(outputPath).Contains("class FrameworkArtifactSmoke", StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Processor operation '{operation}' returned incomplete output for '{target.ReferenceSetId}'.");
    }
}

static async Task RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
{
    var startInfo = new ProcessStartInfo { FileName = fileName, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"'{fileName}' exited with {process.ExitCode}.\n{await stdout}\n{await stderr}");
    }
}

static string ResolveDotNetHost()
{
    var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        return Path.GetFullPath(configured);
    var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        var candidate = Path.Combine(directory, executableName);
        if (File.Exists(candidate))
            return Path.GetFullPath(candidate);
    }
    throw new InvalidOperationException("The dotnet host path is unavailable.");
}

sealed record FrameworkTarget(string Id, string ReferenceSetId, string TargetFramework, string FrameworkVersion, string IdentityDigest);

sealed record AttestedFile(string Path, long Size, string Digest);

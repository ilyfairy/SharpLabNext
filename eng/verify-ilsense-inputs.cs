#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property NuGetLockFilePath=obj/verify-ilsense-inputs.packages.lock.json

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

const string SubmodulePath = "third_party/ILSense";
const string SubmoduleUrl = "https://github.com/OrgEleCho/ILSense.git";
const string CoreProjectPath = "third_party/ILSense/src/EleCho.ILSense/EleCho.ILSense.csproj";
const string NuGetLockPath = "src/Workers/IL/SharpLabNext.Worker.IL/packages.EleCho.ILSense.lock.json";
string[] DirectPackages =
[
    "Microsoft.SourceLink.GitHub",
    "PolySharp",
    "System.Collections.Immutable",
    "System.Reflection.Metadata",
    "System.Text.Json"
];
string[] TargetFrameworks = [".NETStandard,Version=v2.0", "net10.0"];

try
{
    var options = Options.Parse(args);
    var repositoryRoot = Path.GetFullPath(options.RepositoryRoot);
    var releaseLockPath = Path.GetFullPath(
        options.ReleaseLockPath ?? Path.Combine(repositoryRoot, "profiles", "lock.json"));
    var submoduleRoot = Path.Combine(repositoryRoot, SubmodulePath.Replace('/', Path.DirectorySeparatorChar));
    var projectPath = Path.Combine(repositoryRoot, CoreProjectPath.Replace('/', Path.DirectorySeparatorChar));
    var nugetLockPath = Path.Combine(repositoryRoot, NuGetLockPath.Replace('/', Path.DirectorySeparatorChar));

    Require(File.Exists(Path.Combine(repositoryRoot, ".gitmodules")), ".gitmodules is missing.");
    Require(File.Exists(projectPath), "The ILSense Core project is missing; initialize the submodule recursively.");
    Require(File.Exists(nugetLockPath), $"The committed ILSense NuGet lock is missing: {NuGetLockPath}");

    var configuredPaths = await RunGitAsync(
        repositoryRoot,
        ["config", "--file", Path.Combine(repositoryRoot, ".gitmodules"), "--get-regexp", "^submodule\\..*\\.path$"]);
    var configuredPathLines = Lines(configuredPaths);
    Require(configuredPathLines.Length == 1 && configuredPathLines[0].EndsWith($" {SubmodulePath}", StringComparison.Ordinal),
        ".gitmodules must declare exactly the approved ILSense submodule path.");
    var configuredUrl = (await RunGitAsync(
        repositoryRoot,
        ["config", "--file", Path.Combine(repositoryRoot, ".gitmodules"), "--get", "submodule.third_party/ILSense.url"])).Trim();
    Require(string.Equals(configuredUrl, SubmoduleUrl, StringComparison.Ordinal),
        $"The ILSense submodule URL must be '{SubmoduleUrl}'.");

    var indexEntry = (await RunGitAsync(
        repositoryRoot,
        ["ls-files", "--stage", "--", SubmodulePath])).Trim();
    var indexParts = indexEntry.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
    Require(indexParts is ["160000", _, "0", SubmodulePath] && IsCommit(indexParts[1]),
        "The ILSense path must be a stage-0 Git gitlink.");
    var gitlinkCommit = indexParts[1];
    var checkoutCommit = (await RunGitAsync(submoduleRoot, ["rev-parse", "HEAD"])).Trim();
    Require(string.Equals(checkoutCommit, gitlinkCommit, StringComparison.Ordinal),
        $"The ILSense checkout '{checkoutCommit}' does not match gitlink '{gitlinkCommit}'.");
    var submoduleStatus = await RunGitAsync(
        submoduleRoot,
        ["status", "--porcelain=v1", "--untracked-files=all"]);
    Require(string.IsNullOrWhiteSpace(submoduleStatus), "The ILSense submodule checkout is dirty.");
    var commitTimestamp = (await RunGitAsync(submoduleRoot, ["show", "-s", "--format=%ct", "HEAD"])).Trim();

    using var releaseLock = JsonDocument.Parse(await File.ReadAllBytesAsync(releaseLockPath));
    var components = RequiredProperty(releaseLock.RootElement, "components");
    var runtime = RequiredProperty(components, "ilsense");
    var source = RequiredProperty(components, "ilsense-source");
    var version = RequiredString(runtime, "resolvedVersion");
    Require(RequiredString(runtime, "kind") == "runtime-dependency", "ilsense.kind must be runtime-dependency.");
    Require(RequiredString(source, "kind") == "source", "ilsense-source.kind must be source.");
    Require(version == RequiredString(source, "resolvedVersion"), "ILSense runtime and source versions differ.");
    Require(gitlinkCommit == RequiredString(runtime, "commit") && gitlinkCommit == RequiredString(source, "commit"),
        "ILSense lock commits must match the actual gitlink and checkout.");
    var digest = RequiredString(runtime, "digest");
    Require(IsSha256(digest) && digest == RequiredString(source, "digest"),
        "ILSense runtime and source archive digests must be the same SHA-256 identity.");
    Require(RequiredString(runtime, "sourceUri") == $"https://github.com/OrgEleCho/ILSense/tree/{gitlinkCommit}",
        "ilsense.sourceUri must identify the gitlink commit.");
    Require(RequiredString(source, "sourceUri") == $"https://codeload.github.com/OrgEleCho/ILSense/tar.gz/{gitlinkCommit}",
        "ilsense-source.sourceUri must identify the gitlink archive.");

    var projectVersion = XDocument.Load(projectPath)
        .Descendants("Version")
        .Select(static element => element.Value.Trim())
        .SingleOrDefault();
    Require(version == projectVersion, $"ILSense project version '{projectVersion}' does not match lock version '{version}'.");

    using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(
        Path.Combine(repositoryRoot, "profiles", "provenance", "ilsense.json")));
    var provenanceRoot = provenance.RootElement;
    Require(RequiredString(provenanceRoot, "componentId") == "ilsense" &&
            RequiredString(provenanceRoot, "sourceComponentId") == "ilsense-source",
        "ILSense provenance must resolve identity through the two locked components.");
    Require(RequiredString(provenanceRoot, "compatibilityGroup") == $"ilsense-v{version}",
        "ILSense provenance compatibilityGroup does not match the locked version.");
    Require(RequiredString(provenanceRoot, "license") == "MIT", "ILSense provenance must declare MIT.");
    var provenanceBuilder = RequiredProperty(provenanceRoot, "builder");
    Require(long.TryParse(commitTimestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var commitEpoch) &&
            RequiredProperty(provenanceBuilder, "sourceDateEpoch").GetInt64() == commitEpoch,
        "ILSense provenance sourceDateEpoch does not match the gitlink commit timestamp.");
    var provenanceBuild = RequiredProperty(provenanceRoot, "build");
    Require(RequiredString(provenanceBuild, "languageServerProject") == CoreProjectPath,
        "ILSense provenance must identify the gitlink Core project.");
    Require(RequiredString(provenanceBuild, "targetFramework") == "net10.0",
        "ILSense provenance target framework must match the worker build.");

    using var packageLock = JsonDocument.Parse(await File.ReadAllBytesAsync(nugetLockPath));
    Require(RequiredProperty(packageLock.RootElement, "version").GetInt32() == 2,
        "The ILSense NuGet lock must use lock format version 2.");
    var dependencyGraphs = RequiredProperty(packageLock.RootElement, "dependencies");
    var lockedFrameworks = dependencyGraphs.EnumerateObject().Select(static property => property.Name).ToArray();
    Require(lockedFrameworks.Length == TargetFrameworks.Length &&
            TargetFrameworks.All(framework => lockedFrameworks.Contains(framework, StringComparer.Ordinal)),
        "The ILSense NuGet lock must contain exactly the netstandard2.0 and net10.0 graphs.");
    foreach (var framework in TargetFrameworks)
    {
        var graph = RequiredProperty(dependencyGraphs, framework);
        foreach (var package in DirectPackages)
        {
            var dependency = RequiredProperty(graph, package);
            Require(RequiredString(dependency, "type") == "Direct", $"{framework}/{package} must be directly locked.");
            _ = RequiredString(dependency, "resolved");
            _ = RequiredString(dependency, "contentHash");
        }
    }

    var evaluated = await RunProcessAsync(
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        [
            "msbuild", projectPath,
            "-getProperty:SharpLabNextIsILSenseProject",
            "-getProperty:RestorePackagesWithLockFile",
            "-getProperty:NuGetLockFilePath"
        ],
        repositoryRoot);
    using var evaluatedProperties = JsonDocument.Parse(evaluated.Output);
    var properties = RequiredProperty(evaluatedProperties.RootElement, "Properties");
    Require(RequiredString(properties, "SharpLabNextIsILSenseProject") == "true",
        "The superproject ILSense MSBuild identity hook is not active.");
    Require(RequiredString(properties, "RestorePackagesWithLockFile") == "true",
        "The evaluated ILSense project does not require a NuGet lock.");
    var evaluatedLockPath = Path.GetFullPath(RequiredString(properties, "NuGetLockFilePath"));
    Require(PathsEqual(evaluatedLockPath, nugetLockPath),
        $"The evaluated ILSense NuGet lock path is '{evaluatedLockPath}', expected '{nugetLockPath}'.");

    if (options.VerifyRestore)
    {
        await RunProcessAsync(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [
                "restore", projectPath,
                "--force-evaluate",
                "--use-lock-file",
                "--lock-file-path", nugetLockPath,
                "--locked-mode",
                "/p:RestorePackagesWithLockFile=true"
            ],
            repositoryRoot);
    }

    Console.WriteLine(
        $"ILSense inputs valid: {version} {gitlinkCommit}, {TargetFrameworks.Length} locked dependency graphs, digest {digest}.");
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"ILSense input validation failed: {exception.Message}");
    return 1;
}

static JsonElement RequiredProperty(JsonElement value, string name)
{
    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
        throw new InvalidDataException($"Required JSON property '{name}' is missing.");
    return property;
}

static string RequiredString(JsonElement value, string name)
{
    var property = RequiredProperty(value, name);
    return property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
        ? property.GetString()!
        : throw new InvalidDataException($"Required JSON property '{name}' must be a non-empty string.");
}

static async Task<string> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments)
{
    var result = await RunProcessAsync("git", ["-C", workingDirectory, .. arguments], workingDirectory);
    return result.Output;
}

static async Task<ProcessResult> RunProcessAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    startInfo.Environment["DOTNET_NOLOGO"] = "1";
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await outputTask;
    var error = await errorTask;
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"'{fileName} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}: {error.Trim()}");
    }
    return new ProcessResult(output, error);
}

static string[] Lines(string value) =>
    value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static bool IsCommit(string value) =>
    value.Length == 40 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

static bool IsSha256(string value)
{
    if (!value.StartsWith("sha256:", StringComparison.Ordinal) || value.Length != 71)
        return false;
    for (var index = 7; index < value.Length; index++)
    {
        if (value[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;
    }
    return true;
}

static bool PathsEqual(string left, string right) =>
    string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidDataException(message);
}

sealed record ProcessResult(string Output, string Error);

sealed record Options(string RepositoryRoot, string? ReleaseLockPath, bool VerifyRestore)
{
    public static Options Parse(string[] arguments)
    {
        string repositoryRoot = Directory.GetCurrentDirectory();
        string? releaseLockPath = null;
        var verifyRestore = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--repository-root":
                    repositoryRoot = RequiredValue(arguments, ref index);
                    break;
                case "--lock":
                    releaseLockPath = RequiredValue(arguments, ref index);
                    break;
                case "--verify-restore":
                    verifyRestore = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arguments[index]}'.");
            }
        }
        return new Options(repositoryRoot, releaseLockPath, verifyRestore);
    }

    private static string RequiredValue(string[] values, ref int index)
    {
        index++;
        return index < values.Length && !string.IsNullOrWhiteSpace(values[index])
            ? values[index]
            : throw new ArgumentException("An option value is missing.");
    }
}

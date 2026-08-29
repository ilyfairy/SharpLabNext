#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property NuGetLockFilePath=obj/run-with-bake-environment.packages.lock.json
#:project ../src/Tools/SharpLabNext.ProfileUpdater/SharpLabNext.ProfileUpdater.csproj

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLabNext.Catalog;
using SharpLabNext.ProfileUpdater;

string? lockPath = null;
string? baseImageManifestPath = null;
string? sourceRevision = null;
string? repositoryRoot = null;
string? runtimeMatrixPath = null;
var allowUncommittedSourceForDevelopment = false;
var allowDevelopmentImageInputs = false;
var emitEnvironmentJson = false;
const string developmentGrantEnvironmentVariable =
    "SHARPLABNEXT_BAKE_ALLOW_UNCOMMITTED_SOURCE_FOR_DEVELOPMENT";
const string developmentImageInputsGrantEnvironmentVariable =
    "SHARPLABNEXT_BAKE_ALLOW_DEVELOPMENT_IMAGE_INPUTS";
const string environmentJsonPrefix = "SHARPLABNEXT_BAKE_ENVIRONMENT_JSON=";
var imagePrefix = "sharplabnext";
var developmentImageInputs = new Dictionary<string, string>(StringComparer.Ordinal);
var command = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--lock":
            lockPath = RequiredValue(args, ref index);
            break;
        case "--base-images":
            baseImageManifestPath = RequiredValue(args, ref index);
            break;
        case "--source-revision":
            sourceRevision = RequiredValue(args, ref index);
            break;
        case "--repository-root":
            repositoryRoot = RequiredValue(args, ref index);
            break;
        case "--runtime-matrix":
            runtimeMatrixPath = RequiredValue(args, ref index);
            break;
        case "--allow-uncommitted-source-for-development":
            allowUncommittedSourceForDevelopment = true;
            break;
        case "--allow-development-image-inputs":
            allowDevelopmentImageInputs = true;
            break;
        case "--emit-environment-json":
            emitEnvironmentJson = true;
            break;
        case "--development-image-input":
            AddDevelopmentImageInput(
                developmentImageInputs,
                RequiredValue(args, ref index));
            break;
        case "--image-prefix":
            imagePrefix = RequiredValue(args, ref index);
            break;
        case "--":
            command.AddRange(args[(index + 1)..]);
            index = args.Length;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[index]}'.");
            return 64;
    }
}

if (string.IsNullOrWhiteSpace(lockPath) ||
    string.IsNullOrWhiteSpace(baseImageManifestPath) ||
    string.IsNullOrWhiteSpace(sourceRevision) ||
    string.IsNullOrWhiteSpace(repositoryRoot))
{
    Console.Error.WriteLine(
        "Usage: dotnet run eng/run-with-bake-environment.cs -- " +
        "--lock PATH --base-images PATH --source-revision REVISION --repository-root PATH " +
        "[--runtime-matrix PATH] " +
        "[--allow-uncommitted-source-for-development] " +
        "[--allow-development-image-inputs] " +
        "[--development-image-input NAME=REFERENCE] " +
        "[--emit-environment-json] " +
        "[--image-prefix PREFIX] [-- COMMAND [ARG...]]");
    return 64;
}

if (developmentImageInputs.Count > 0 && !allowDevelopmentImageInputs)
{
    Console.Error.WriteLine(
        "--development-image-input requires --allow-development-image-inputs.");
    return 64;
}

if (emitEnvironmentJson && command.Count > 0)
{
    Console.Error.WriteLine(
        "--emit-environment-json cannot be combined with a child command.");
    return 64;
}

try
{
    await VerifyILSenseInputsAsync(repositoryRoot, lockPath);
    var sourceDateEpoch = await SourceDateEpochResolver.ResolveAsync(
        repositoryRoot,
        sourceRevision,
        allowUncommittedSourceForDevelopment);
    var controlRuntimeTargetFramework = await ReadControlRuntimeTargetFrameworkAsync(
        runtimeMatrixPath ?? Path.Combine(repositoryRoot, "profiles", "runtime-matrix.json"));
    var environment = await BakeEnvironmentResolver.CreateAsync(
        lockPath,
        baseImageManifestPath,
        sourceRevision,
        sourceDateEpoch,
        imagePrefix,
        controlRuntimeTargetFramework: controlRuntimeTargetFramework);
    environment["DEVELOPMENT_IMAGE_INPUTS"] =
        allowDevelopmentImageInputs ? "true" : "false";
    foreach (var pair in developmentImageInputs)
        environment[pair.Key] = pair.Value;
    if (emitEnvironmentJson)
    {
        Console.WriteLine(
            $"{environmentJsonPrefix}{JsonSerializer.Serialize(
                environment,
                BakeEnvironmentJsonSerializerContext.Default.DictionaryStringString)}");
        return 0;
    }
    if (command.Count == 0)
    {
        foreach (var pair in environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"{pair.Key}={pair.Value}");
        return 0;
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = command[0],
        UseShellExecute = false
    };
    foreach (var argument in command.Skip(1))
        startInfo.ArgumentList.Add(argument);
    foreach (var pair in environment)
        startInfo.Environment[pair.Key] = pair.Value;
    startInfo.Environment.Remove(developmentGrantEnvironmentVariable);
    startInfo.Environment.Remove(developmentImageInputsGrantEnvironmentVariable);
    if (allowUncommittedSourceForDevelopment)
        startInfo.Environment[developmentGrantEnvironmentVariable] = "true";
    if (allowDevelopmentImageInputs)
        startInfo.Environment[developmentImageInputsGrantEnvironmentVariable] = "true";

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        Console.Error.WriteLine($"Could not start '{command[0]}'.");
        return 1;
    }
    await process.WaitForExitAsync();
    return process.ExitCode;
}
catch (Exception exception) when (
    exception is BakeEnvironmentValidationException or CatalogValidationException or IOException or JsonException or InvalidDataException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<string> ReadControlRuntimeTargetFrameworkAsync(string path)
{
    await using var stream = File.OpenRead(Path.GetFullPath(path));
    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: CancellationToken.None);
    if (!document.RootElement.TryGetProperty("controlRuntime", out var controlRuntime) ||
        controlRuntime.ValueKind != JsonValueKind.Object ||
        !controlRuntime.TryGetProperty("targetFramework", out var targetFramework) ||
        targetFramework.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(targetFramework.GetString()))
    {
        throw new InvalidDataException(
            $"Runtime matrix '{path}' must declare controlRuntime.targetFramework.");
    }
    return targetFramework.GetString()!;
}

static async Task VerifyILSenseInputsAsync(string repositoryRoot, string releaseLockPath)
{
    var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    var verifier = Path.Combine(repositoryRoot, "eng", "verify-ilsense-inputs.cs");
    var startInfo = new ProcessStartInfo
    {
        FileName = dotnet,
        WorkingDirectory = repositoryRoot,
        UseShellExecute = false
    };
    foreach (var argument in new[]
    {
        "run", verifier, "--",
        "--repository-root", repositoryRoot,
        "--lock", releaseLockPath
    })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo);
    if (process is null)
        throw new BakeEnvironmentValidationException("Could not start the ILSense input verifier.");
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
        throw new BakeEnvironmentValidationException("ILSense source, lock, and gitlink validation failed.");
}

static string RequiredValue(string[] values, ref int index)
{
    index++;
    if (index >= values.Length || string.IsNullOrWhiteSpace(values[index]))
        throw new BakeEnvironmentValidationException("An option value is missing.");
    return values[index];
}

static void AddDevelopmentImageInput(
    IDictionary<string, string> inputs,
    string configured)
{
    var separator = configured.IndexOf('=');
    if (separator <= 0 || separator == configured.Length - 1)
        throw new BakeEnvironmentValidationException(
            "--development-image-input must use NAME=REFERENCE.");

    var name = configured[..separator];
    var reference = configured[(separator + 1)..];
    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "CPPCLI_PREPARED_BASE_IMAGE",
        "JSHARP_TOOLCHAIN_IMAGE"
    };
    if (!allowed.Contains(name))
        throw new BakeEnvironmentValidationException(
            $"Development image input '{name}' is not supported.");
    if (reference.Length > 512 ||
        reference.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
        !reference.Contains(':', StringComparison.Ordinal))
    {
        throw new BakeEnvironmentValidationException(
            $"Development image input '{name}' has an invalid Docker reference.");
    }
    if (!inputs.TryAdd(name, reference))
        throw new BakeEnvironmentValidationException(
            $"Development image input '{name}' was supplied more than once.");
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class BakeEnvironmentJsonSerializerContext : JsonSerializerContext
{
}

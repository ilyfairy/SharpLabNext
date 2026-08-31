#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property NuGetLockFilePath=obj/run-with-bake-environment.packages.lock.json
#:project ../../src/Tools/SharpLabNext.ProfileUpdater/SharpLabNext.ProfileUpdater.csproj

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
var emitEnvironmentJson = false;
const string sourceIdentityModeEnvironmentVariable = "SHARPLABNEXT_SOURCE_IDENTITY_MODE";
const string contentSourceIdentityMode = "content";
const string environmentJsonPrefix = "SHARPLABNEXT_BAKE_ENVIRONMENT_JSON=";
var imagePrefix = "sharplabnext";
var imageInputs = new Dictionary<string, string>(StringComparer.Ordinal);
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
        case "--emit-environment-json":
            emitEnvironmentJson = true;
            break;
        case "--image-input":
            AddImageInput(imageInputs, RequiredValue(args, ref index));
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

var sourceIdentityMode = string.Equals(Environment.GetEnvironmentVariable(sourceIdentityModeEnvironmentVariable), contentSourceIdentityMode, StringComparison.OrdinalIgnoreCase) ? SourceIdentityMode.Content : SourceIdentityMode.VerifiedRevision;

if (string.IsNullOrWhiteSpace(lockPath) || string.IsNullOrWhiteSpace(baseImageManifestPath) || string.IsNullOrWhiteSpace(sourceRevision) || string.IsNullOrWhiteSpace(repositoryRoot))
{
    Console.Error.WriteLine("Usage: dotnet run eng/tools/run-with-bake-environment.cs -- " + "--lock PATH --base-images PATH --source-revision REVISION --repository-root PATH " + "[--runtime-matrix PATH] [--image-input NAME=REFERENCE] " + "[--emit-environment-json] [--image-prefix PREFIX] [-- COMMAND [ARG...]]");
    return 64;
}

if (emitEnvironmentJson && command.Count > 0)
{
    Console.Error.WriteLine("--emit-environment-json cannot be combined with a child command.");
    return 64;
}

try
{
    await VerifyILSenseInputsAsync(repositoryRoot, lockPath, sourceIdentityMode == SourceIdentityMode.Content);
    var sourceDateEpoch = await SourceDateEpochResolver.ResolveAsync(repositoryRoot, sourceRevision, sourceIdentityMode);
    var controlRuntimeTargetFramework = await ReadControlRuntimeTargetFrameworkAsync(runtimeMatrixPath ?? Path.Combine(repositoryRoot, "profiles", "runtime-matrix.json"));
    var environment = await BakeEnvironmentResolver.CreateAsync(lockPath, baseImageManifestPath, sourceRevision, sourceDateEpoch, imagePrefix, controlRuntimeTargetFramework: controlRuntimeTargetFramework);
    foreach (var pair in imageInputs)
        environment[pair.Key] = pair.Value;
    if (emitEnvironmentJson)
    {
        Console.WriteLine($"{environmentJsonPrefix}{JsonSerializer.Serialize(environment, BakeEnvironmentJsonSerializerContext.Default.DictionaryStringString)}");
        return 0;
    }
    if (command.Count == 0)
    {
        foreach (var pair in environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine($"{pair.Key}={pair.Value}");
        return 0;
    }

    var startInfo = new ProcessStartInfo { FileName = command[0], UseShellExecute = false };
    foreach (var argument in command.Skip(1))
        startInfo.ArgumentList.Add(argument);
    foreach (var pair in environment)
        startInfo.Environment[pair.Key] = pair.Value;
    if (sourceIdentityMode == SourceIdentityMode.Content)
    {
        startInfo.Environment[sourceIdentityModeEnvironmentVariable] = contentSourceIdentityMode;
    }

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        Console.Error.WriteLine($"Could not start '{command[0]}'.");
        return 1;
    }
    await process.WaitForExitAsync();
    return process.ExitCode;
}
catch (Exception exception) when (exception is BakeEnvironmentValidationException or CatalogValidationException or IOException or JsonException or InvalidDataException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<string> ReadControlRuntimeTargetFrameworkAsync(string path)
{
    await using var stream = File.OpenRead(Path.GetFullPath(path));
    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: CancellationToken.None);
    if (!document.RootElement.TryGetProperty("controlRuntime", out var controlRuntime) || controlRuntime.ValueKind != JsonValueKind.Object || !controlRuntime.TryGetProperty("targetFramework", out var targetFramework) || targetFramework.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(targetFramework.GetString()))
    {
        throw new InvalidDataException($"Runtime matrix '{path}' must declare controlRuntime.targetFramework.");
    }
    return targetFramework.GetString()!;
}

static async Task VerifyILSenseInputsAsync(string repositoryRoot, string releaseLockPath, bool allowMissingGit)
{
    var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    var verifier = Path.Combine(repositoryRoot, "eng", "tools", "verify-ilsense-inputs.cs");
    var startInfo = new ProcessStartInfo { FileName = dotnet, WorkingDirectory = repositoryRoot, UseShellExecute = false };
    var verifierArguments = new List<string> { "run", verifier, "--", "--repository-root", repositoryRoot, "--lock", releaseLockPath };
    if (allowMissingGit) verifierArguments.Add("--allow-missing-git");
    foreach (var argument in verifierArguments)
        startInfo.ArgumentList.Add(argument);

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

static void AddImageInput(IDictionary<string, string> inputs, string configured)
{
    var separator = configured.IndexOf('=');
    if (separator <= 0 || separator == configured.Length - 1)
        throw new BakeEnvironmentValidationException("--image-input must use NAME=REFERENCE.");

    var name = configured[..separator];
    var reference = configured[(separator + 1)..];
    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "CPPCLI_PREPARED_BASE_IMAGE",
        "JSHARP_TOOLCHAIN_IMAGE"
    };
    if (!allowed.Contains(name))
        throw new BakeEnvironmentValidationException($"Image input '{name}' is not supported.");
    if (reference.Length > 512 || reference.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)) || !reference.Contains(':', StringComparison.Ordinal))
    {
        throw new BakeEnvironmentValidationException($"Image input '{name}' has an invalid Docker reference.");
    }
    if (!inputs.TryAdd(name, reference))
        throw new BakeEnvironmentValidationException($"Image input '{name}' was supplied more than once.");
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class BakeEnvironmentJsonSerializerContext : JsonSerializerContext;

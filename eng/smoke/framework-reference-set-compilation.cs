#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:project ../../src/Workers/Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable.csproj

using System.Text.Json;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Roslyn;

if (args.Length != 2)
{
    throw new ArgumentException(
        "Usage: framework-reference-set-compilation.cs <runtime-matrix.json> <materialized-reference-set-root>");
}

var matrixPath = Path.GetFullPath(args[0]);
var referenceRoot = Path.GetFullPath(args[1]);
var targets = LoadTargets(matrixPath, referenceRoot);
if (targets.Count != 14)
    throw new InvalidDataException($"Expected 14 Framework reference sets, observed {targets.Count}.");

using var references = new ReferenceSetProvider(targets.Select(static target => target.Definition).ToArray());
var identity = new RoslynWorkerIdentity(
    "framework-reference-smoke",
    "roslyn-stable-netfx48",
    CSharpBuildService.GetLoadedCompilerVersion(),
    null,
    "framework-reference-smoke-image");
var csharp = new CSharpBuildService(references, identity, CompilationLimits.Default, AstLimits.Default);
var visualBasic = new VisualBasicBuildService(references, identity, CompilationLimits.Default, AstLimits.Default);

foreach (var target in targets)
{
    await VerifyCSharpAsync(csharp, target, BuildOutputKind.Library, BuildConfiguration.Release);
    await VerifyCSharpAsync(csharp, target, BuildOutputKind.Console, BuildConfiguration.Release);
    await VerifyVisualBasicAsync(visualBasic, target, BuildOutputKind.Library, BuildConfiguration.Release);
    await VerifyVisualBasicAsync(visualBasic, target, BuildOutputKind.Console, BuildConfiguration.Release);

    if (target.Definition.TargetFramework is "net20" or "net30" or "net35")
    {
        var csharpDebug = await VerifyCSharpAsync(
            csharp,
            target,
            BuildOutputKind.Console,
            BuildConfiguration.Debug);
        var visualBasicDebug = await VerifyVisualBasicAsync(
            visualBasic,
            target,
            BuildOutputKind.Console,
            BuildConfiguration.Debug);
        RequirePortablePdb(csharpDebug, target.Definition.Id, "C#");
        RequirePortablePdb(visualBasicDebug, target.Definition.Id, "Visual Basic");
    }

    Console.WriteLine(
        $"{target.Definition.Id}: C#/VB library+console passed" +
        (target.Definition.TargetFramework is "net20" or "net30" or "net35"
            ? "; legacy Debug portable PDB passed"
            : string.Empty));
}

Console.WriteLine("Verified all 14 materialized .NET Framework reference sets with real Roslyn C# and VB builds.");

static IReadOnlyList<FrameworkTarget> LoadTargets(string matrixPath, string referenceRoot)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(matrixPath));
    var result = new List<FrameworkTarget>();
    foreach (var target in document.RootElement.GetProperty("framework").GetProperty("targets").EnumerateArray())
    {
        var referenceSetId = target.GetProperty("referenceSetId").GetString()
            ?? throw new InvalidDataException("Framework target has no referenceSetId.");
        var targetFramework = target.GetProperty("targetFramework").GetString()
            ?? throw new InvalidDataException($"Framework target '{referenceSetId}' has no targetFramework.");
        var resolvedVersion = target.TryGetProperty("referencePackage", out var package)
            ? package.GetProperty("version").GetString()
            : target.GetProperty("referenceComposition").GetProperty("resolvedVersion").GetString();
        if (string.IsNullOrWhiteSpace(resolvedVersion))
            throw new InvalidDataException($"Framework target '{referenceSetId}' has no reference identity version.");

        var path = Path.Combine(referenceRoot, referenceSetId);
        var attestationPath = Path.Combine(path, "reference-set.attestation.json");
        if (!Directory.Exists(path) || !File.Exists(attestationPath))
            throw new InvalidDataException($"Materialized reference set '{referenceSetId}' is missing at '{path}'.");
        using var attestation = JsonDocument.Parse(File.ReadAllBytes(attestationPath));
        var digest = attestation.RootElement
            .GetProperty("referenceSet")
            .GetProperty("digest")
            .GetString()
            ?? throw new InvalidDataException($"Reference set '{referenceSetId}' attestation has no digest.");

        var definition = new ReferenceSetDefinition(
            referenceSetId,
            path,
            targetFramework,
            resolvedVersion,
            digest,
            attestationPath,
            IncludeSharpLabRuntime: false);
        result.Add(new FrameworkTarget(definition, definition.GetRuntimeFrameworkVersion()));
    }
    return result;
}

static async Task<CompiledArtifact> VerifyCSharpAsync(
    CSharpBuildService service,
    FrameworkTarget target,
    BuildOutputKind outputKind,
    BuildConfiguration configuration)
{
    var source = outputKind == BuildOutputKind.Library
        ? "public sealed class Calculator { public int Add(int left, int right) { return left + right; } }"
        : "public static class Program { public static void Main() { System.Console.WriteLine(42); } }";
    var request = CreateRequest(
        "csharp",
        target.Definition.Id,
        "Program.cs",
        source,
        outputKind,
        configuration,
        languageVersion: "14.0");
    var response = await service.ExecuteAsync(request, CancellationToken.None);
    return VerifyArtifact(response, target, outputKind, "C#");
}

static async Task<CompiledArtifact> VerifyVisualBasicAsync(
    VisualBasicBuildService service,
    FrameworkTarget target,
    BuildOutputKind outputKind,
    BuildConfiguration configuration)
{
    var source = outputKind == BuildOutputKind.Library
        ? "Public NotInheritable Class Calculator\n    Public Function Add(left As Integer, right As Integer) As Integer\n        Return left + right\n    End Function\nEnd Class"
        : "Imports System\nPublic Module Program\n    Public Sub Main()\n        Console.WriteLine(42)\n    End Sub\nEnd Module";
    var request = CreateRequest(
        "visual-basic",
        target.Definition.Id,
        "Program.vb",
        source,
        outputKind,
        configuration,
        languageVersion: "latest");
    var response = await service.ExecuteAsync(request, CancellationToken.None);
    return VerifyArtifact(response, target, outputKind, "Visual Basic");
}

static BuildRequest CreateRequest(
    string languageId,
    string referenceSetId,
    string fileName,
    string source,
    BuildOutputKind outputKind,
    BuildConfiguration configuration,
    string languageVersion)
{
    var options = new BuildOptions(
        configuration,
        Optimize: configuration == BuildConfiguration.Release,
        outputKind,
        AllowUnsafe: false,
        EmitPortablePdb: true,
        NullableContextMode.Disable,
        languageVersion,
        PreprocessorSymbols: [],
        CheckOverflow: true);
    var workspace = new WorkspaceSnapshot(
        ContractSchemaVersions.WorkspaceSnapshot,
        1,
        1,
        languageId,
        [new WorkspaceFile(fileName, 1, source)],
        fileName,
        [fileName],
        referenceSetId,
        options);
    var requestId = $"framework-smoke-{Guid.NewGuid():N}";
    return new BuildRequest(
        requestId,
        $"idempotency-{requestId}",
        "framework-reference-compilation-smoke",
        "roslyn-stable-netfx48",
        referenceSetId,
        workspace,
        DateTimeOffset.UtcNow.AddMinutes(2),
        options,
        BuildTarget.Artifact);
}

static CompiledArtifact VerifyArtifact(
    WorkerBuildExecution response,
    FrameworkTarget target,
    BuildOutputKind outputKind,
    string language)
{
    var result = response.Result as BuildResult
        ?? throw new InvalidDataException($"{language} {target.Definition.Id} returned no BuildResult.");
    if (result.Outcome != BuildOutcome.Succeeded)
    {
        throw new InvalidDataException(
            $"{language} {target.Definition.Id} {outputKind} failed: " +
            string.Join(" | ", result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    var artifact = response.Artifact
        ?? throw new InvalidDataException($"{language} {target.Definition.Id} {outputKind} returned no artifact.");
    RequireEqual(target.Definition.Id, artifact.ReferenceSetId, "artifact referenceSetId");
    RequireEqual(target.Definition.Id, artifact.Manifest.ReferenceSetId, "manifest referenceSetId");
    RequireEqual(target.Definition.TargetFramework, artifact.TargetFramework, "artifact targetFramework");
    RequireEqual(target.Definition.TargetFramework, artifact.Manifest.TargetFramework, "manifest targetFramework");
    RequireEqual("dotnet-framework-managed-pe-v1", artifact.ArtifactFormat, "artifact format");
    RequireEqual("netfx-clr-wine", artifact.Manifest.RuntimeRequirement.Family, "runtime family");
    if (artifact.Manifest.RuntimeRequirement.RequiredRuntimeFeatureTags.Count != 0)
        throw new InvalidDataException($"{target.Definition.Id} managed artifact unexpectedly requires a Wine feature tag.");
    var framework = artifact.Manifest.RuntimeRequirement.Frameworks.Single();
    RequireEqual(".NETFramework", framework.Name, "framework requirement name");
    RequireEqual(target.RuntimeFrameworkVersion, framework.MinimumVersion, "framework requirement version");
    if (artifact.Manifest.OutputKind != outputKind)
        throw new InvalidDataException($"{target.Definition.Id} output kind does not match {outputKind}.");
    var expectedEntryAssembly = outputKind == BuildOutputKind.Library
        ? "SharpLabNext.User.dll"
        : "SharpLabNext.User.exe";
    RequireEqual(expectedEntryAssembly, artifact.Manifest.EntryAssembly, "entry assembly");
    if ((outputKind == BuildOutputKind.Library) != (artifact.Manifest.EntryPoint is null))
        throw new InvalidDataException($"{target.Definition.Id} entry-point contract does not match {outputKind}.");
    return artifact;
}

static void RequirePortablePdb(CompiledArtifact artifact, string referenceSetId, string language)
{
    if (artifact.PortablePdb.Length == 0 ||
        !artifact.Manifest.Files.Any(static file => file.Role == "portable-pdb"))
    {
        throw new InvalidDataException($"{language} {referenceSetId} Debug build did not produce a portable PDB.");
    }
}

static void RequireEqual(string expected, string? actual, string description)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        throw new InvalidDataException($"{description} is '{actual}', expected '{expected}'.");
}

sealed record FrameworkTarget(ReferenceSetDefinition Definition, string RuntimeFrameworkVersion);

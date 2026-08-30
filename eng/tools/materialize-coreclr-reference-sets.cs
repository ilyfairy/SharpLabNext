#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property RestorePackagesWithLockFile=false

using System.IO.Compression;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args is ["--self-test"])
{
    await RunSelfTestAsync();
    return;
}

var options = Options.Parse(args);
var matrix = ReadJsonObject(options.MatrixPath, "Runtime matrix");
var releaseLock = ReadJsonObject(options.LockPath, "Release lock");
var referenceSets = ReadReferenceSets(matrix, releaseLock, options.Overrides);

Directory.CreateDirectory(options.OutputDirectory);
Directory.CreateDirectory(options.ArchiveDirectory);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

foreach (var referenceSet in referenceSets)
{
    var archiveName = $"{referenceSet.Package.ToLowerInvariant()}.{referenceSet.ResolvedVersion}.nupkg";
    var archivePath = SafeChild(options.ArchiveDirectory, archiveName);
    await EnsureArchiveAsync(http, referenceSet.SourceUri, archivePath, referenceSet.Sha512);

    var destination = SafeChild(options.OutputDirectory, referenceSet.Id);
    RecreateDirectory(destination);
    ExtractReferenceAssemblies(archivePath, destination, referenceSet.TargetFramework);
    if (referenceSet.IncludeSharpLabRuntime)
    {
        if (options.RuntimeAssemblyPath is null)
            throw new InvalidDataException($"Reference set '{referenceSet.Id}' requires SharpLab.Runtime.");
        File.Copy(options.RuntimeAssemblyPath, Path.Combine(destination, "SharpLab.Runtime.dll"), overwrite: false);
    }

    ValidateMaterializedSet(destination, referenceSet);
    await WriteAttestationAsync(destination, referenceSet);
}

WriteAppsettings(options, referenceSets);
Console.WriteLine($"Materialized {referenceSets.Count} locked CoreCLR reference sets.");

static JsonObject ReadJsonObject(string path, string description) => JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException($"{description} is not a JSON object.");

static IReadOnlyList<LockedReferenceSet> ReadReferenceSets(JsonObject matrix, JsonObject releaseLock, CandidateOverrides overrides)
{
    var coreClr = matrix["coreClr"]?.AsArray() ?? throw new InvalidDataException("Runtime matrix does not contain a coreClr array.");
    var targets = coreClr.Select(static item => item?.AsObject() ?? throw new InvalidDataException("Runtime matrix coreClr item is not an object.")).ToDictionary(static target => Required(target, "id"), StringComparer.Ordinal);
    var components = releaseLock["components"]?.AsObject() ?? throw new InvalidDataException("Release lock does not contain components.");

    if (targets.Count != Contract.ExpectedTargets.Count || !targets.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Contract.ExpectedTargets.Select(static target => target.RuntimeId)))
    {
        throw new InvalidDataException("Runtime matrix must contain exactly the 12 approved CoreCLR reference-set rows.");
    }

    var result = new List<LockedReferenceSet>(Contract.ExpectedTargets.Count);
    foreach (var expected in Contract.ExpectedTargets)
    {
        var matrixTarget = targets[expected.RuntimeId];
        RequireEqual(expected.ReferenceSetId, Required(matrixTarget, "referenceSetId"), $"Runtime matrix {expected.RuntimeId} referenceSetId");
        var matrixPackage = matrixTarget["referencePackage"]?.AsObject() ?? throw new InvalidDataException($"Runtime matrix {expected.RuntimeId} has no referencePackage.");
        RequireEqual(expected.Package, Required(matrixPackage, "id"), $"Runtime matrix {expected.RuntimeId} reference package");

        var lockComponent = components[expected.ReferenceSetId]?.AsObject() ?? throw new InvalidDataException($"Release lock has no '{expected.ReferenceSetId}' reference-set component.");
        var referenceSet = ReadLockComponent(expected, lockComponent);
        RequireEqual(referenceSet.ResolvedVersion, RequiredNuGetVersion(matrixPackage, "version"), $"Runtime matrix {expected.RuntimeId} reference package version");
        RequireEqual(referenceSet.SourceUri, RequiredHttpsUri(matrixPackage, "url"), $"Runtime matrix {expected.RuntimeId} reference package URL");
        RequireEqual(referenceSet.Sha512, RequiredLowerHex(matrixPackage, "sha512", 128), $"Runtime matrix {expected.RuntimeId} reference package SHA-512");
        RequireEqual(referenceSet.PackageContentHash, RequiredNuGetContentHash(matrixPackage, "packageContentHash"), $"Runtime matrix {expected.RuntimeId} reference package content hash");
        overrides.Verify(referenceSet);
        result.Add(referenceSet);
    }

    return result;
}

static LockedReferenceSet ReadLockComponent(ExpectedTarget expected, JsonObject component)
{
    RequireEqual("reference-set", Required(component, "kind"), $"{expected.ReferenceSetId}.kind");
    var version = RequiredNuGetVersion(component, "resolvedVersion");
    var package = Required(component, "package");
    RequireEqual(expected.Package, package, $"{expected.ReferenceSetId}.package");
    var sourceUri = RequiredHttpsUri(component, "sourceUri");
    var expectedUri =
        $"https://api.nuget.org/v3-flatcontainer/{package.ToLowerInvariant()}/{version}/" +
        $"{package.ToLowerInvariant()}.{version}.nupkg";
    RequireEqual(expectedUri, sourceUri, $"{expected.ReferenceSetId}.sourceUri");
    var sha512 = RequiredLowerHex(component, "sha512", 128);
    var contentHash = RequiredNuGetContentHash(component, "packageContentHash");
    RequireEqual("sha512-" + Convert.ToBase64String(Convert.FromHexString(sha512)), contentHash, $"{expected.ReferenceSetId}.packageContentHash");
    return new LockedReferenceSet(expected.ReferenceSetId, expected.TargetFramework, expected.Package, version, sourceUri, sha512, contentHash, expected.IncludeSharpLabRuntime);
}

static async Task EnsureArchiveAsync(HttpClient http, string sourceUri, string archivePath, string expectedSha512)
{
    if (File.Exists(archivePath))
    {
        if ((File.GetAttributes(archivePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Cached archive '{archivePath}' must be a regular file.");
        var cachedLength = new FileInfo(archivePath).Length;
        if (cachedLength <= Contract.MaximumArchiveBytes && await MatchesSha512Async(archivePath, expectedSha512))
        {
            return;
        }
        File.Delete(archivePath);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
    var temporaryPath = $"{archivePath}.tmp.{Guid.NewGuid():N}";
    try
    {
        using var response = await http.GetAsync(sourceUri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > Contract.MaximumArchiveBytes)
            throw new InvalidDataException($"Downloaded archive '{sourceUri}' exceeds the {Contract.MaximumArchiveBytes}-byte limit.");
        await using (var input = await response.Content.ReadAsStreamAsync())
        await using (var output = File.Create(temporaryPath))
            await CopyWithLimitAsync(input, output, Contract.MaximumArchiveBytes);
        if (!await MatchesSha512Async(temporaryPath, expectedSha512))
            throw new InvalidDataException($"Downloaded archive '{sourceUri}' does not match its locked SHA-512.");
        File.Move(temporaryPath, archivePath, overwrite: true);
    }
    finally
    {
        File.Delete(temporaryPath);
    }
}

static async Task CopyWithLimitAsync(Stream input, Stream output, long maximumBytes)
{
    var buffer = new byte[64 * 1024];
    long total = 0;
    while (true)
    {
        var read = await input.ReadAsync(buffer);
        if (read == 0)
            return;
        total = checked(total + read);
        if (total > maximumBytes)
            throw new InvalidDataException($"Downloaded archive exceeds the {maximumBytes}-byte limit.");
        await output.WriteAsync(buffer.AsMemory(0, read));
    }
}

static async Task<bool> MatchesSha512Async(string path, string expected)
{
    await using var stream = File.OpenRead(path);
    var actual = Convert.ToHexString(await SHA512.HashDataAsync(stream)).ToLowerInvariant();
    return string.Equals(actual, expected, StringComparison.Ordinal);
}

static void ExtractReferenceAssemblies(string archivePath, string destination, string targetFramework)
{
    const int maximumEntries = 20_000;
    const long maximumExpandedBytes = 512L * 1024 * 1024;
    var prefix = $"ref/{targetFramework}/";
    using var archive = ZipFile.OpenRead(archivePath);
    if (archive.Entries.Count > maximumEntries)
        throw new InvalidDataException($"Reference package '{archivePath}' contains too many entries.");

    long declaredExpandedBytes = 0;
    long actualExpandedBytes = 0;
    var extracted = 0;
    var archiveFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in archive.Entries)
    {
        declaredExpandedBytes = checked(declaredExpandedBytes + entry.Length);
        if (declaredExpandedBytes > maximumExpandedBytes)
            throw new InvalidDataException($"Reference package '{archivePath}' exceeds the expanded-size limit.");
        ValidateArchiveEntry(entry, archiveFiles, archivePath);
        if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName[prefix.Length..].Contains('/') || !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var destinationPath = SafeChild(destination, entry.Name);
        using var input = entry.Open();
        using var output = File.Create(destinationPath);
        var copied = CopyWithLimit(input, output, maximumExpandedBytes - actualExpandedBytes, $"Reference package '{archivePath}' exceeds the expanded-size limit.");
        if (copied != entry.Length)
            throw new InvalidDataException($"Reference package '{archivePath}' has inconsistent ZIP entry lengths.");
        actualExpandedBytes = checked(actualExpandedBytes + copied);
        extracted++;
    }

    if (extracted == 0)
        throw new InvalidDataException($"Reference package '{archivePath}' has no {prefix} assemblies.");
}

static long CopyWithLimit(Stream input, Stream output, long maximumBytes, string failureMessage)
{
    var buffer = new byte[64 * 1024];
    long total = 0;
    while (true)
    {
        var read = input.Read(buffer);
        if (read == 0)
            return total;
        total = checked(total + read);
        if (total > maximumBytes)
            throw new InvalidDataException(failureMessage);
        output.Write(buffer, 0, read);
    }
}

static void ValidateArchiveEntry(ZipArchiveEntry entry, ISet<string> archiveFiles, string archivePath)
{
    var name = entry.FullName;
    if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.StartsWith('\\') || name.Contains('\\') || name.Contains(':'))
        throw new InvalidDataException($"Reference package '{archivePath}' contains an unsafe ZIP path.");
    var isDirectory = name.EndsWith('/');
    var components = name.Split('/', StringSplitOptions.None);
    if (components.Any(static component => component is "." or ".." or ""))
    {
        if (!isDirectory || components[..^1].Any(static component => component is "." or ".." or ""))
            throw new InvalidDataException($"Reference package '{archivePath}' contains an unsafe ZIP path.");
    }
    if (isDirectory)
        return;
    if (!archiveFiles.Add(name))
        throw new InvalidDataException($"Reference package '{archivePath}' contains duplicate or case-colliding ZIP files.");
}

static void ValidateMaterializedSet(string path, LockedReferenceSet referenceSet)
{
    ValidateRequiredReferenceAssemblyFiles(path, referenceSet.Id);
    var systemRuntime = Path.Combine(path, "System.Runtime.dll");
    ValidateReferenceAssembly(systemRuntime, referenceSet.Id);
    if (referenceSet.IncludeSharpLabRuntime)
        ValidateSharpLabRuntimeAssembly(Path.Combine(path, "SharpLab.Runtime.dll"), referenceSet.Id);
}

static void ValidateRequiredReferenceAssemblyFiles(string path, string referenceSetId)
{
    foreach (var name in Contract.RequiredReferenceAssemblyNames)
    {
        if (!File.Exists(Path.Combine(path, name)))
            throw new InvalidDataException($"Reference set '{referenceSetId}' is missing required CoreCLR reference assembly '{name}'.");
    }
}

static void ValidateReferenceAssembly(string path, string referenceSetId)
{
    try
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            throw new BadImageFormatException("Assembly has no metadata.");
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        if (!assembly.GetCustomAttributes().Any(handle => string.Equals(GetAttributeTypeName(metadata, metadata.GetCustomAttribute(handle).Constructor), "System.Runtime.CompilerServices.ReferenceAssemblyAttribute", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Reference set '{referenceSetId}' contains an implementation System.Runtime assembly.");
        }
    }
    catch (InvalidDataException)
    {
        throw;
    }
    catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
    {
        throw new InvalidDataException($"Reference set '{referenceSetId}' has an unreadable System.Runtime assembly.", exception);
    }
}

static void ValidateSharpLabRuntimeAssembly(string path, string referenceSetId)
{
    const string requiredTargetFramework = ".NETStandard,Version=v2.1";
    try
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            throw new BadImageFormatException("Assembly has no metadata.");
        var metadata = pe.GetMetadataReader();
        var targetFrameworkAttributes = metadata.GetAssemblyDefinition().GetCustomAttributes().Where(handle => string.Equals(GetAttributeTypeName(metadata, metadata.GetCustomAttribute(handle).Constructor), "System.Runtime.Versioning.TargetFrameworkAttribute", StringComparison.Ordinal)).Select(handle => ReadTargetFrameworkAttribute(metadata, metadata.GetCustomAttribute(handle))).ToArray();
        ValidateSharpLabRuntimeTargetFramework(targetFrameworkAttributes, referenceSetId, requiredTargetFramework);
    }
    catch (InvalidDataException)
    {
        throw;
    }
    catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
    {
        throw new InvalidDataException($"Reference set '{referenceSetId}' has an unreadable SharpLab.Runtime assembly.", exception);
    }
}

static void ValidateSharpLabRuntimeTargetFramework(IReadOnlyList<string> targetFrameworkAttributes, string referenceSetId, string requiredTargetFramework = ".NETStandard,Version=v2.1")
{
    if (targetFrameworkAttributes.Count != 1 || !string.Equals(targetFrameworkAttributes[0], requiredTargetFramework, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Reference set '{referenceSetId}' must contain SharpLab.Runtime built for {requiredTargetFramework}.");
    }
}

static string ReadTargetFrameworkAttribute(MetadataReader metadata, CustomAttribute attribute)
{
    var value = metadata.GetBlobReader(attribute.Value);
    if (value.ReadUInt16() != 1)
        throw new InvalidDataException("SharpLab.Runtime has an invalid TargetFrameworkAttribute payload.");
    return value.ReadSerializedString() ?? throw new InvalidDataException("SharpLab.Runtime has a null TargetFrameworkAttribute value.");
}

static string? GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
{
    EntityHandle type = constructor.Kind switch
    {
        HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
        HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
        _ => default
    };
    return type.Kind switch
    {
        HandleKind.TypeReference => FormatReference(reader, reader.GetTypeReference((TypeReferenceHandle)type)),
        HandleKind.TypeDefinition => FormatDefinition(reader, reader.GetTypeDefinition((TypeDefinitionHandle)type)),
        _ => null
    };
}

static string FormatReference(MetadataReader reader, TypeReference type) => $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

static string FormatDefinition(MetadataReader reader, TypeDefinition type) => $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

static async Task WriteAttestationAsync(string path, LockedReferenceSet referenceSet)
{
    var files = Directory.EnumerateFiles(path, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.Ordinal).Select(file =>
        {
            using var stream = File.OpenRead(file);
            return new AttestedFile(Path.GetFileName(file), stream.Length, $"sha256:{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}");
        }).ToArray();
    var canonical = string.Concat(files.Select(file => $"{file.Digest}  {file.Size}  {file.Path}\n"));
    var contentDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}";
    var document = new AttestationDocument(1, new AttestedReferenceSet(referenceSet.Id, referenceSet.TargetFramework, referenceSet.PackageContentHash, contentDigest, new Provenance("nuget-package", referenceSet.ResolvedVersion, referenceSet.Package, referenceSet.SourceUri, null, $"sha512:{referenceSet.Sha512}")), files);
    await WriteUtf8LfAsync(Path.Combine(path, "reference-set.attestation.json"), JsonSerializer.Serialize(document, AttestationJsonContext.Default.AttestationDocument).ReplaceLineEndings("\n") + "\n");
}

static void WriteAppsettings(Options options, IReadOnlyList<LockedReferenceSet> referenceSets)
{
    var appsettings = ReadJsonObject(options.AppsettingsTemplatePath, "Roslyn appsettings template");
    var configured = new JsonObject();
    foreach (var referenceSet in referenceSets)
        configured[referenceSet.Id] = new JsonObject { ["Path"] = $"/reference-sets/{referenceSet.Id}", ["TargetFramework"] = referenceSet.TargetFramework, ["FrameworkVersion"] = referenceSet.ResolvedVersion, ["Digest"] = referenceSet.PackageContentHash, ["IncludeSharpLabRuntime"] = referenceSet.IncludeSharpLabRuntime };
    appsettings["ReferenceSets"] = configured;
    Directory.CreateDirectory(Path.GetDirectoryName(options.AppsettingsOutputPath)!);
    WriteUtf8Lf(options.AppsettingsOutputPath, appsettings.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true }).ReplaceLineEndings("\n") + "\n");
}

static void WriteUtf8Lf(string path, string content)
{
    if (content.Contains('\r'))
        throw new InvalidDataException("Generated JSON must use LF line endings.");
    File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    VerifyUtf8Lf(path);
}

static async Task WriteUtf8LfAsync(string path, string content)
{
    if (content.Contains('\r'))
        throw new InvalidDataException("Generated JSON must use LF line endings.");
    await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    VerifyUtf8Lf(path);
}

static void VerifyUtf8Lf(string path)
{
    var bytes = File.ReadAllBytes(path);
    if (bytes.Length == 0 || bytes is [0xef, 0xbb, 0xbf, ..] || bytes.Contains((byte)'\r') || bytes[^1] != (byte)'\n')
        throw new InvalidDataException($"Generated JSON '{path}' must be non-empty UTF-8 without BOM and LF-terminated.");
    _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
}

static async Task RunSelfTestAsync()
{
    const string version = "1.2.3";
    var sha512 = new string('a', 128);
    var packageContentHash = "sha512-" + Convert.ToBase64String(Convert.FromHexString(sha512));
    var matrix = new JsonObject { ["coreClr"] = new JsonArray(Contract.ExpectedTargets.Select(target => (JsonNode)new JsonObject { ["id"] = target.RuntimeId, ["referenceSetId"] = target.ReferenceSetId, ["referencePackage"] = new JsonObject { ["id"] = target.Package, ["version"] = version, ["url"] = $"https://api.nuget.org/v3-flatcontainer/{target.Package.ToLowerInvariant()}/{version}/{target.Package.ToLowerInvariant()}.{version}.nupkg", ["sha512"] = sha512, ["packageContentHash"] = packageContentHash } }).ToArray()) };
    var components = new JsonObject();
    foreach (var target in Contract.ExpectedTargets)
        components[target.ReferenceSetId] = new JsonObject { ["kind"] = "reference-set", ["resolvedVersion"] = version, ["package"] = target.Package, ["sourceUri"] = $"https://api.nuget.org/v3-flatcontainer/{target.Package.ToLowerInvariant()}/{version}/{target.Package.ToLowerInvariant()}.{version}.nupkg", ["sha512"] = sha512, ["packageContentHash"] = packageContentHash };
    var resolved = ReadReferenceSets(matrix, new JsonObject { ["components"] = components.DeepClone() }, CandidateOverrides.Empty);
    if (resolved.Count != Contract.ExpectedTargets.Count || resolved.Count(static set => set.IncludeSharpLabRuntime) != 9)
        throw new InvalidOperationException("CoreCLR materializer self-test did not resolve the approved reference closure.");
    var candidateValues = Contract.ExpectedTargets.ToDictionary(
        static target => target.BuildArgumentPrefix,
        target => new CandidateIdentity(version,
            $"https://api.nuget.org/v3-flatcontainer/{target.Package.ToLowerInvariant()}/{version}/{target.Package.ToLowerInvariant()}.{version}.nupkg",
            sha512,
            packageContentHash),
        StringComparer.Ordinal);
    var candidateArguments = candidateValues.SelectMany(pair => new[]
    {
        new KeyValuePair<string, string>($"{pair.Key}-version", pair.Value.Version),
        new KeyValuePair<string, string>($"{pair.Key}-url", pair.Value.Url),
        new KeyValuePair<string, string>($"{pair.Key}-sha512", pair.Value.Sha512),
        new KeyValuePair<string, string>($"{pair.Key}-content-hash", pair.Value.ContentHash)
    }).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    _ = ReadReferenceSets(matrix, new JsonObject { ["components"] = components.DeepClone() }, CandidateOverrides.Parse(candidateArguments));
    candidateArguments["netcoreapp20-content-hash"] = "sha512-" + Convert.ToBase64String(new byte[64]);
    try
    {
        _ = ReadReferenceSets(matrix, new JsonObject { ["components"] = components.DeepClone() }, CandidateOverrides.Parse(candidateArguments));
        throw new InvalidOperationException("CoreCLR materializer self-test accepted legacy CoreCLR build-argument drift.");
    }
    catch (InvalidDataException) { }
    candidateArguments.Remove("net9-url");
    try
    {
        _ = CandidateOverrides.Parse(candidateArguments);
        throw new InvalidOperationException("CoreCLR materializer self-test accepted incomplete CoreCLR build arguments.");
    }
    catch (ArgumentException) { }
    var invalidComponents = components.DeepClone().AsObject();
    invalidComponents["net10-ref"]!.AsObject()["sourceUri"] = "https://example.invalid/reference.nupkg";
    try
    {
        _ = ReadReferenceSets(matrix, new JsonObject { ["components"] = invalidComponents }, CandidateOverrides.Empty);
        throw new InvalidOperationException("CoreCLR materializer self-test accepted a non-canonical package URI.");
    }
    catch (InvalidDataException) { }
    var driftedMatrix = matrix.DeepClone().AsObject();
    driftedMatrix["coreClr"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "dotnet-10")!["referencePackage"]!["sha512"] =
        new string('b', 128);
    AssertIdentityRejected(
        driftedMatrix,
        new JsonObject { ["components"] = components.DeepClone() },
        "CoreCLR materializer self-test accepted matrix/lock identity drift.");
    var invalidContentHashLock = components.DeepClone().AsObject();
    invalidContentHashLock["net10-ref"]!.AsObject()["packageContentHash"] =
        "sha512-" + Convert.ToBase64String(new byte[64]);
    AssertIdentityRejected(
        matrix,
        new JsonObject { ["components"] = invalidContentHashLock },
        "CoreCLR materializer self-test accepted an unbound package content hash.");
    try
    {
        _ = RequiredNuGetVersion(new JsonObject { ["version"] = "../../escape" }, "version");
        throw new InvalidOperationException("CoreCLR materializer self-test accepted an unsafe package version.");
    }
    catch (InvalidDataException) { }
    try
    {
        _ = CopyWithLimit(new MemoryStream(new byte[4]), Stream.Null, 3, "bounded copy rejected");
        throw new InvalidOperationException("CoreCLR materializer self-test accepted an oversized extracted stream.");
    }
    catch (InvalidDataException exception) when (exception.Message == "bounded copy rejected") { }
    try
    {
        ValidateSharpLabRuntimeTargetFramework([".NETCoreApp,Version=v10.0"], "self-test-ref");
        throw new InvalidOperationException("CoreCLR materializer self-test accepted SharpLab.Runtime outside netstandard2.1.");
    }
    catch (InvalidDataException) { }
    var temporary = Path.Combine(Path.GetTempPath(), $"SharpLabNext.CoreReferenceMaterializer.{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(temporary);
        var archive = Path.Combine(temporary, "reference.nupkg");
        CreateZip(archive,
        [
            ("ref/net10.0/System.Runtime.dll", new byte[] { 1 }),
            ("ref/net10.0/System.Console.dll", new byte[] { 2 }),
            ("ref/net10.0/System.Collections.dll", new byte[] { 3 }),
            ("ref/net10.0/netstandard.dll", new byte[] { 4 }),
            ("lib/ignored.dll", new byte[] { 5 })
        ]);
        var extracted = Path.Combine(temporary, "extracted");
        Directory.CreateDirectory(extracted);
        ExtractReferenceAssemblies(archive, extracted, "net10.0");
        if (Directory.GetFiles(extracted, "*.dll").Length != 4 || File.Exists(Path.Combine(extracted, "ignored.dll")))
            throw new InvalidOperationException("CoreCLR materializer self-test extracted an incorrect reference closure.");
        ValidateRequiredReferenceAssemblyFiles(extracted, "self-test-ref");
        var collectionsPath = Path.Combine(extracted, "System.Collections.dll");
        File.Delete(collectionsPath);
        try
        {
            ValidateRequiredReferenceAssemblyFiles(extracted, "self-test-ref");
            throw new InvalidOperationException("CoreCLR materializer self-test accepted a missing required assembly.");
        }
        catch (InvalidDataException) { }
        File.WriteAllBytes(collectionsPath, [3]);

        var duplicate = Path.Combine(temporary, "duplicate.nupkg");
        CreateZip(duplicate,
        [
            ("ref/net10.0/A.dll", new byte[] { 1 }),
            ("ref/net10.0/a.dll", new byte[] { 2 })
        ]);
        AssertZipRejected(duplicate);
        var traversal = Path.Combine(temporary, "traversal.nupkg");
        CreateZip(traversal,
        [
            ("../escape.dll", new byte[] { 1 }),
            ("ref/net10.0/System.Runtime.dll", new byte[] { 2 })
        ]);
        AssertZipRejected(traversal);
        var drivePath = Path.Combine(temporary, "drive-path.nupkg");
        CreateZip(drivePath,
        [
            ("C:/escape.dll", new byte[] { 1 }),
            ("ref/net10.0/System.Runtime.dll", new byte[] { 2 })
        ]);
        AssertZipRejected(drivePath);

        var appsettingsTemplate = Path.Combine(temporary, "appsettings.template.json");
        WriteUtf8Lf(appsettingsTemplate, "{\n  \"ReferenceSets\": {}\n}\n");
        var attestationRoot = Path.Combine(temporary, "attestation");
        Directory.CreateDirectory(attestationRoot);
        File.Copy(Path.Combine(extracted, "System.Runtime.dll"), Path.Combine(attestationRoot, "System.Runtime.dll"));
        await WriteAttestationAsync(attestationRoot, resolved.Single(static set => set.Id == "net10-ref"));
        VerifyUtf8Lf(Path.Combine(attestationRoot, "reference-set.attestation.json"));
        var appsettingsOutput = Path.Combine(temporary, "generated", "appsettings.json");
        WriteAppsettings(new Options("matrix", "lock", "output", "archive", appsettingsTemplate, appsettingsOutput, null, CandidateOverrides.Empty), resolved);
        VerifyUtf8Lf(appsettingsOutput);
        var generated = ReadJsonObject(appsettingsOutput, "Generated appsettings");
        if (generated["ReferenceSets"]?.AsObject().Count != Contract.ExpectedTargets.Count)
            throw new InvalidOperationException("CoreCLR materializer self-test did not generate all ReferenceSets settings.");
    }
    finally
    {
        if (Directory.Exists(temporary))
            Directory.Delete(temporary, recursive: true);
    }
    Console.WriteLine("CoreCLR reference-set materializer self-test passed.");
}

static void AssertIdentityRejected(JsonObject matrix, JsonObject releaseLock, string failureMessage)
{
    try
    {
        _ = ReadReferenceSets(matrix, releaseLock, CandidateOverrides.Empty);
        throw new InvalidOperationException(failureMessage);
    }
    catch (InvalidDataException) { }
}

static void CreateZip(string path, IReadOnlyList<(string Path, byte[] Contents)> files)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var file in files)
    {
        var entry = archive.CreateEntry(file.Path);
        using var stream = entry.Open();
        stream.Write(file.Contents);
    }
}

static void AssertZipRejected(string archive)
{
    var destination = Path.Combine(Path.GetDirectoryName(archive)!, $"extract-{Guid.NewGuid():N}");
    Directory.CreateDirectory(destination);
    try
    {
        try
        {
            ExtractReferenceAssemblies(archive, destination, "net10.0");
            throw new InvalidOperationException("CoreCLR materializer self-test accepted an unsafe ZIP archive.");
        }
        catch (InvalidDataException) { }
    }
    finally
    {
        Directory.Delete(destination, recursive: true);
    }
}

static void RecreateDirectory(string path)
{
    if (Directory.Exists(path))
        Directory.Delete(path, recursive: true);
    Directory.CreateDirectory(path);
}

static string SafeChild(string parent, string name)
{
    if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        throw new InvalidDataException($"Unsafe child name '{name}'.");
    var root = Path.GetFullPath(parent);
    var candidate = Path.GetFullPath(Path.Combine(root, name));
    if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidDataException($"Unsafe child path '{name}'.");
    return candidate;
}

static string Required(JsonObject value, string property) => value[property]?.GetValue<string>() is { Length: > 0 } text ? text : throw new InvalidDataException($"Required property '{property}' is missing.");

static string RequiredNuGetVersion(JsonObject value, string property)
{
    var text = Required(value, property);
    if (text.Length > 128 || !Regex.IsMatch(text, "^[0-9](?:[0-9A-Za-z.-]{0,126}[0-9A-Za-z])?$", RegexOptions.CultureInvariant))
    {
        throw new InvalidDataException($"Property '{property}' must be a bounded normalized NuGet version safe for use in a cache filename.");
    }
    return text;
}

static string RequiredHttpsUri(JsonObject value, string property)
{
    var text = Required(value, property);
    if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
    {
        throw new InvalidDataException($"Property '{property}' must be an absolute HTTPS URL without credentials, query, or fragment.");
    }
    return text;
}

static string RequiredLowerHex(JsonObject value, string property, int length)
{
    var text = Required(value, property);
    if (text.Length != length || !Regex.IsMatch(text, "^[0-9a-f]+$", RegexOptions.CultureInvariant))
        throw new InvalidDataException($"Property '{property}' must be a {length}-character lower-case hexadecimal digest.");
    return text;
}

static string RequiredNuGetContentHash(JsonObject value, string property)
{
    var text = Required(value, property);
    if (!text.StartsWith("sha512-", StringComparison.Ordinal) || !TryDecodeSha512(text[7..]))
        throw new InvalidDataException($"Property '{property}' must be a NuGet SHA-512 package content hash.");
    return text;
}

static bool TryDecodeSha512(string value)
{
    try { return Convert.FromBase64String(value).Length == 64; }
    catch (FormatException) { return false; }
}

static void RequireEqual(string expected, string actual, string name)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        throw new InvalidDataException($"{name} must equal '{expected}'.");
}

sealed record ExpectedTarget(string RuntimeId, string ReferenceSetId, string TargetFramework, string Package, bool IncludeSharpLabRuntime, string BuildArgumentPrefix, string VersionBuildArgument);
sealed record LockedReferenceSet(string Id, string TargetFramework, string Package, string ResolvedVersion, string SourceUri, string Sha512, string PackageContentHash, bool IncludeSharpLabRuntime);
sealed record AttestationDocument(int SchemaVersion, AttestedReferenceSet ReferenceSet, IReadOnlyList<AttestedFile> Files);
sealed record AttestedReferenceSet(string Id, string TargetFramework, string Digest, string ContentDigest, Provenance Provenance);
sealed record Provenance(string Kind, string ResolvedVersion, string? Package, string? SourceUri, string? Commit, string? SourceArchiveDigest);
sealed record AttestedFile(string Path, long Size, string Digest);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AttestationDocument))]
sealed partial class AttestationJsonContext : JsonSerializerContext;

static class Contract
{
    public const long MaximumArchiveBytes = 256L * 1024 * 1024;

    public static IReadOnlyList<string> RequiredReferenceAssemblyNames { get; } =
    [
        "System.Runtime.dll",
        "System.Console.dll",
        "System.Collections.dll",
        "netstandard.dll"
    ];

    public static IReadOnlyList<ExpectedTarget> ExpectedTargets { get; } =
    [
        new("dotnet-core-2.0", "netcoreapp2.0-ref", "netcoreapp2.0", "Microsoft.NETCore.App", false, "netcoreapp20", "NETCOREAPP20_REFERENCE_VERSION"),
        new("dotnet-core-2.1", "netcoreapp2.1-ref", "netcoreapp2.1", "Microsoft.NETCore.App", false, "netcoreapp21", "NETCOREAPP21_REFERENCE_VERSION"),
        new("dotnet-core-2.2", "netcoreapp2.2-ref", "netcoreapp2.2", "Microsoft.NETCore.App", false, "netcoreapp22", "NETCOREAPP22_REFERENCE_VERSION"),
        new("dotnet-core-3.0", "netcoreapp3.0-ref", "netcoreapp3.0", "Microsoft.NETCore.App.Ref", true, "netcoreapp30", "NETCOREAPP30_REFERENCE_VERSION"),
        new("dotnet-core-3.1", "netcoreapp3.1-ref", "netcoreapp3.1", "Microsoft.NETCore.App.Ref", true, "netcoreapp31", "NETCOREAPP31_REFERENCE_VERSION"),
        new("dotnet-5", "net5-ref", "net5.0", "Microsoft.NETCore.App.Ref", true, "net5", "NET5_REFERENCE_VERSION"),
        new("dotnet-6", "net6-ref", "net6.0", "Microsoft.NETCore.App.Ref", true, "net6", "NET6_REFERENCE_VERSION"),
        new("dotnet-7", "net7-ref", "net7.0", "Microsoft.NETCore.App.Ref", true, "net7", "NET7_REFERENCE_VERSION"),
        new("dotnet-8", "net8-ref", "net8.0", "Microsoft.NETCore.App.Ref", true, "net8", "NET8_REFERENCE_VERSION"),
        new("dotnet-9", "net9-ref", "net9.0", "Microsoft.NETCore.App.Ref", true, "net9", "NET9_REFERENCE_VERSION"),
        new("dotnet-10", "net10-ref", "net10.0", "Microsoft.NETCore.App.Ref", true, "net10", "NET10_REFERENCE_PACK_VERSION"),
        new("dotnet-11-preview", "net11-preview-ref", "net11.0", "Microsoft.NETCore.App.Ref", true, "net11", "NET11_REFERENCE_VERSION")
    ];
}

sealed record CandidateIdentity(string Version, string Url, string Sha512, string ContentHash);

sealed record CandidateOverrides(IReadOnlyDictionary<string, CandidateIdentity> Identities)
{
    public static CandidateOverrides Empty { get; } = new(new Dictionary<string, CandidateIdentity>(StringComparer.Ordinal));

    public static CandidateOverrides Parse(IReadOnlyDictionary<string, string> values)
    {
        var overrideKeys = Contract.ExpectedTargets.SelectMany(static target => new[]
            {
                $"{target.BuildArgumentPrefix}-version",
                $"{target.BuildArgumentPrefix}-url",
                $"{target.BuildArgumentPrefix}-sha512",
                $"{target.BuildArgumentPrefix}-content-hash"
            }).ToArray();
        if (!overrideKeys.Any(values.ContainsKey))
            return Empty;

        var identities = new Dictionary<string, CandidateIdentity>(StringComparer.Ordinal);
        foreach (var target in Contract.ExpectedTargets)
        {
            var prefix = target.BuildArgumentPrefix;
            identities.Add(target.ReferenceSetId, new CandidateIdentity(RequiredValue(values, $"{prefix}-version"), RequiredValue(values, $"{prefix}-url"), RequiredValue(values, $"{prefix}-sha512"), RequiredValue(values, $"{prefix}-content-hash")));
        }
        return new CandidateOverrides(identities);
    }

    public void Verify(LockedReferenceSet set)
    {
        if (!Identities.TryGetValue(set.Id, out var identity))
            return;
        if (!string.Equals(set.ResolvedVersion, identity.Version, StringComparison.Ordinal) || !string.Equals(set.SourceUri, identity.Url, StringComparison.Ordinal) || !string.Equals(set.Sha512, identity.Sha512, StringComparison.Ordinal) || !string.Equals(set.PackageContentHash, identity.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Candidate override for '{set.Id}' does not match the release lock.");
        }
    }

    private static string RequiredValue(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"--{key} is required.");
}

sealed record Options(string MatrixPath, string LockPath, string OutputDirectory, string ArchiveDirectory, string AppsettingsTemplatePath, string AppsettingsOutputPath, string? RuntimeAssemblyPath, CandidateOverrides Overrides)
{
    public static Options Parse(string[] arguments)
    {
        var allowedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "matrix", "lock", "output", "archive-cache", "appsettings-template", "appsettings-output", "runtime-assembly"
        };
        foreach (var target in Contract.ExpectedTargets)
        {
            var prefix = target.BuildArgumentPrefix;
            allowedKeys.Add($"{prefix}-version");
            allowedKeys.Add($"{prefix}-url");
            allowedKeys.Add($"{prefix}-sha512");
            allowedKeys.Add($"{prefix}-content-hash");
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var key = arguments[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length || !allowedKeys.Contains(key[2..]) || !values.TryAdd(key[2..], arguments[++index]))
                throw new ArgumentException($"Invalid argument '{key}'.");
        }
        string RequiredValue(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"--{key} is required.");
        string? OptionalValue(string key) => values.GetValueOrDefault(key);
        var runtimeAssembly = OptionalValue("runtime-assembly");
        if (!string.IsNullOrWhiteSpace(runtimeAssembly) && !File.Exists(runtimeAssembly))
            throw new ArgumentException($"Runtime assembly '{runtimeAssembly}' does not exist.");
        return new Options(Path.GetFullPath(RequiredValue("matrix")), Path.GetFullPath(RequiredValue("lock")), Path.GetFullPath(RequiredValue("output")), Path.GetFullPath(RequiredValue("archive-cache")), Path.GetFullPath(RequiredValue("appsettings-template")), Path.GetFullPath(RequiredValue("appsettings-output")), string.IsNullOrWhiteSpace(runtimeAssembly) ? null : Path.GetFullPath(runtimeAssembly), CandidateOverrides.Parse(values));
    }
}

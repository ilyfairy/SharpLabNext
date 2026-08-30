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
using System.Text.RegularExpressions;

if (args is ["--self-test"])
{
    await RunSelfTestAsync();
    return;
}

var options = Options.Parse(args);
var matrix = JsonNode.Parse(await File.ReadAllTextAsync(options.MatrixPath))?.AsObject() ?? throw new InvalidDataException("Runtime matrix is not a JSON object.");
var targets = matrix["framework"]?["targets"]?.AsArray().Select(static value => value?.AsObject() ?? throw new InvalidDataException("Framework target is not a JSON object.")).ToArray() ?? throw new InvalidDataException("Runtime matrix does not contain framework targets.");

ValidateTargets(targets);
Directory.CreateDirectory(options.OutputDirectory);
Directory.CreateDirectory(options.ArchiveDirectory);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var configuredReferenceSets = new JsonObject();
var packages = targets.Where(static target => target["referencePackage"] is JsonObject).ToDictionary(static target => Required(target, "id"), ReadPackage, StringComparer.Ordinal);

foreach (var target in targets.Where(static target => target["referencePackage"] is JsonObject))
{
    var id = Required(target, "referenceSetId");
    var targetFramework = Required(target, "targetFramework");
    var package = packages[Required(target, "id")];

    var archivePath = Path.Combine(options.ArchiveDirectory, $"{package.Id.ToLowerInvariant()}.{package.Version}.nupkg");
    await EnsureArchiveAsync(http, package.Uri, archivePath, package.Sha512);

    var referenceSetPath = SafeChild(options.OutputDirectory, id);
    RecreateDirectory(referenceSetPath);
    ExtractReferenceAssemblies(archivePath, referenceSetPath, package.FrameworkVersion);
    ValidateMaterializedSet(referenceSetPath, id, targetFramework);
    await WriteAttestationAsync(referenceSetPath, id, targetFramework, package.PackageContentHash, PackageProvenance(package));

    AddConfiguration(configuredReferenceSets, id, targetFramework, package.Version, package.PackageContentHash);
}

foreach (var target in targets.Where(static target => target["referenceComposition"] is JsonObject))
{
    var id = Required(target, "referenceSetId");
    var targetFramework = Required(target, "targetFramework");
    var composition = target["referenceComposition"]!.AsObject();
    var sources = RequiredArray(composition, "sources").Select(value => value?.AsObject() ?? throw new InvalidDataException($"Reference composition '{id}' source is not an object.")).Select(source => new CompositionSource(Required(source, "role"), Required(source, "selection"), packages[Required(source, "targetId")])).ToArray();
    var resolvedVersion = Required(composition, "resolvedVersion");
    var sourceIdentityDigest = RequiredLowerSha256(composition, "sourceIdentityDigest");
    var actualSourceIdentityDigest = ComputeCompositionSourceIdentity(id, targetFramework, Required(composition, "kind"), resolvedVersion, sources);
    if (!string.Equals(sourceIdentityDigest, actualSourceIdentityDigest, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Reference composition '{id}' source identity does not match its locked digest.");
    }

    var archivePaths = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var source in sources)
    {
        var package = source.Package;
        var archivePath = Path.Combine(options.ArchiveDirectory, $"{package.Id.ToLowerInvariant()}.{package.Version}.nupkg");
        await EnsureArchiveAsync(http, package.Uri, archivePath, package.Sha512);
        archivePaths.Add(source.Role, archivePath);
    }

    var referenceSetPath = SafeChild(options.OutputDirectory, id);
    RecreateDirectory(referenceSetPath);
    ExtractReferenceAssemblies(archivePaths["base"], referenceSetPath, sources.Single(static source => source.Role == "base").Package.FrameworkVersion);
    ExtractNetFx30Extensions(archivePaths["extension"], referenceSetPath, sources.Single(static source => source.Role == "extension").Package.FrameworkVersion);
    ValidateNetFx30Composition(referenceSetPath);
    ValidateMaterializedSet(referenceSetPath, id, targetFramework);
    await WriteAttestationAsync(referenceSetPath, id, targetFramework, sourceIdentityDigest, CompositionProvenance(composition, sources));
    AddConfiguration(configuredReferenceSets, id, targetFramework, resolvedVersion, sourceIdentityDigest);
}

var appsettings = JsonNode.Parse(await File.ReadAllTextAsync(options.AppsettingsTemplatePath))?.AsObject() ?? throw new InvalidDataException("Roslyn Framework appsettings template is not a JSON object.");
appsettings["ReferenceSets"] = configuredReferenceSets;
Directory.CreateDirectory(Path.GetDirectoryName(options.AppsettingsOutputPath)!);
await File.WriteAllTextAsync(options.AppsettingsOutputPath, appsettings.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true }) + "\n");

Console.WriteLine($"Materialized {configuredReferenceSets.Count} locked Framework reference sets, including the net30 composition.");

static void ValidateTargets(IReadOnlyList<JsonObject> targets)
{
    if (targets.Count != 14)
        throw new InvalidDataException($"Expected 14 Framework targets, observed {targets.Count}.");

    var ids = new HashSet<string>(StringComparer.Ordinal);
    var referenceSetIds = new HashSet<string>(StringComparer.Ordinal);
    foreach (var target in targets)
    {
        var id = Required(target, "id");
        var referenceSetId = Required(target, "referenceSetId");
        if (!Regex.IsMatch(id, "^netfx[0-9]{2,3}$", RegexOptions.CultureInvariant) || !ids.Add(id))
            throw new InvalidDataException($"Framework target id '{id}' is invalid or duplicated.");
        if (!Regex.IsMatch(referenceSetId, "^netfx[0-9]{2,3}-managed-ref$", RegexOptions.CultureInvariant) || !referenceSetIds.Add(referenceSetId))
        {
            throw new InvalidDataException($"Framework reference set id '{referenceSetId}' is invalid or duplicated.");
        }
        if ((target["referencePackage"] is JsonObject) == (target["referenceComposition"] is JsonObject))
        {
            throw new InvalidDataException($"Framework target '{id}' must define exactly one reference package or composition.");
        }
    }

    var compositionTargets = targets.Where(static target => target["referenceComposition"] is JsonObject).ToArray();
    if (compositionTargets.Length != 1 || Required(compositionTargets[0], "id") != "netfx30")
    {
        throw new InvalidDataException("Exactly netfx30 must use the locked reference composition; every other Framework target requires one package.");
    }

    var netFx30 = compositionTargets[0];
    if (Required(netFx30, "version") != "3.0" || Required(netFx30, "targetFramework") != "net30" || Required(netFx30, "referenceSetId") != "netfx30-managed-ref")
    {
        throw new InvalidDataException("The netfx30 composition target identity is inconsistent.");
    }
    var composition = netFx30["referenceComposition"]!.AsObject();
    if (Required(composition, "kind") != "nuget-package-composition" || Required(composition, "resolvedVersion") != "net30-union-v1")
    {
        throw new InvalidDataException("The netfx30 reference composition recipe is unsupported.");
    }
    _ = RequiredLowerSha256(composition, "sourceIdentityDigest");
    var sources = RequiredArray(composition, "sources").Select(value => value?.AsObject() ?? throw new InvalidDataException("The netfx30 reference composition contains an invalid source.")).ToArray();
    if (sources.Length != 2 || !MatchesSource(sources[0], "base", "netfx20", "all") || !MatchesSource(sources[1], "extension", "netfx35", "assembly-version:3.0.0.0"))
    {
        throw new InvalidDataException("The netfx30 reference composition must be the ordered netfx20 base plus netfx35 AssemblyVersion 3.0.0.0 extension.");
    }
    if (!sources.All(source => ids.Contains(Required(source, "targetId"))))
        throw new InvalidDataException("The netfx30 reference composition refers to an unknown Framework target.");
}

static bool MatchesSource(JsonObject source, string role, string targetId, string selection) => string.Equals(Required(source, "role"), role, StringComparison.Ordinal) && string.Equals(Required(source, "targetId"), targetId, StringComparison.Ordinal) && string.Equals(Required(source, "selection"), selection, StringComparison.Ordinal);

static LockedReferencePackage ReadPackage(JsonObject target)
{
    var targetId = Required(target, "id");
    var referenceSetId = Required(target, "referenceSetId");
    var targetFramework = Required(target, "targetFramework");
    var frameworkVersion = Required(target, "version");
    var package = target["referencePackage"]?.AsObject() ?? throw new InvalidDataException($"Framework target '{targetId}' has no reference package.");
    var packageId = Required(package, "id");
    var packageVersion = Required(package, "version");
    var packageUrl = Required(package, "url");
    var packageSha512 = RequiredLowerHex(package, "sha512", 128);
    var packageContentHash = Required(package, "packageContentHash");
    ValidatePackageIdentity(referenceSetId, targetFramework, frameworkVersion, packageId, packageVersion, packageUrl, packageContentHash);
    return new LockedReferencePackage(targetId, frameworkVersion, packageId, packageVersion, new Uri(packageUrl, UriKind.Absolute), packageSha512, packageContentHash);
}

static void ValidatePackageIdentity(string referenceSetId, string targetFramework, string frameworkVersion, string packageId, string packageVersion, string packageUrl, string packageContentHash)
{
    var expectedReferenceSetId = $"netfx{frameworkVersion.Replace(".", string.Empty, StringComparison.Ordinal)}-managed-ref";
    var expectedTargetFramework = $"net{frameworkVersion.Replace(".", string.Empty, StringComparison.Ordinal)}";
    var expectedPackageId = $"Microsoft.NETFramework.ReferenceAssemblies.{expectedTargetFramework}";
    var expectedUrl =
        $"https://api.nuget.org/v3-flatcontainer/{expectedPackageId.ToLowerInvariant()}/{packageVersion}/" +
        $"{expectedPackageId.ToLowerInvariant()}.{packageVersion}.nupkg";
    if (!string.Equals(referenceSetId, expectedReferenceSetId, StringComparison.Ordinal) || !string.Equals(targetFramework, expectedTargetFramework, StringComparison.Ordinal) || !string.Equals(packageId, expectedPackageId, StringComparison.Ordinal) || !string.Equals(packageUrl, expectedUrl, StringComparison.Ordinal) || !packageContentHash.StartsWith("sha512-", StringComparison.Ordinal) || packageContentHash.Length <= "sha512-".Length)
    {
        throw new InvalidDataException($"Framework package identity for '{referenceSetId}' is inconsistent.");
    }
}

static async Task EnsureArchiveAsync(HttpClient http, Uri uri, string archivePath, string expectedSha512, Func<int, CancellationToken, Task>? retryDelay = null, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || !string.Equals(uri.Host, "api.nuget.org", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
    {
        throw new InvalidDataException($"Reference package URI '{uri}' is not an approved NuGet flat-container URI.");
    }

    if (File.Exists(archivePath) && await HasSha512Async(archivePath, expectedSha512, cancellationToken))
        return;

    File.Delete(archivePath);
    const int maximumAttempts = 3;
    for (var attempt = 1; attempt <= maximumAttempts; attempt++)
    {
        var temporaryPath = archivePath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            if (!await HasSha512Async(temporaryPath, expectedSha512, cancellationToken))
                throw new InvalidDataException($"Reference package '{uri}' failed SHA-512 verification.");
            File.Move(temporaryPath, archivePath);
            return;
        }
        catch (Exception exception) when (attempt < maximumAttempts && !cancellationToken.IsCancellationRequested && IsTransientDownloadFailure(exception))
        {
            Console.Error.WriteLine($"Reference package download attempt {attempt}/{maximumAttempts} failed transiently for '{uri}': " + exception.Message);
            File.Delete(temporaryPath);
            if (retryDelay is null)
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            else
                await retryDelay(attempt, cancellationToken);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

static bool IsTransientDownloadFailure(Exception exception) => exception switch
{
    HttpRequestException { StatusCode: null } => true,
    HttpRequestException { StatusCode: { } statusCode } =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500,
    IOException => true,
    TimeoutException => true,
    TaskCanceledException => true,
    _ => false
};

static async Task RunSelfTestAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.FrameworkReferences.{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        RequireSelfTest(RuntimeFrameworkVersion("net20") == "2.0", "net20 runtime version derivation failed.");
        RequireSelfTest(RuntimeFrameworkVersion("net451") == "4.5.1", "net451 runtime version derivation failed.");

        var payload = Encoding.UTF8.GetBytes("locked reference package");
        var expectedSha512 = Convert.ToHexString(SHA512.HashData(payload)).ToLowerInvariant();
        var uri = new Uri("https://api.nuget.org/v3-flatcontainer/example/1.0.0/example.1.0.0.nupkg", UriKind.Absolute);
        var archivePath = Path.Combine(root, "example.1.0.0.nupkg");
        var transientHandler = new SequenceHttpMessageHandler(attempt => attempt switch
        {
            1 => throw new HttpRequestException("simulated TLS EOF"),
            2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
        });
        using (var http = new HttpClient(transientHandler))
            await EnsureArchiveAsync(http, uri, archivePath, expectedSha512, static (_, _) => Task.CompletedTask);
        RequireSelfTest(transientHandler.Attempts == 3, "Transient failures did not use exactly three attempts.");
        RequireSelfTest(File.ReadAllBytes(archivePath).SequenceEqual(payload), "The successful retry did not publish the verified archive.");
        RequireSelfTest(!Directory.EnumerateFiles(root, "*.tmp-*", SearchOption.TopDirectoryOnly).Any(), "A retry left a temporary archive behind.");

        var notFoundHandler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using (var http = new HttpClient(notFoundHandler))
        {
            var missingPath = Path.Combine(root, "missing.nupkg");
            try
            {
                await EnsureArchiveAsync(http, uri, missingPath, expectedSha512, static (_, _) => Task.CompletedTask);
                throw new InvalidOperationException("A non-transient HTTP status unexpectedly succeeded.");
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { }
        }
        RequireSelfTest(notFoundHandler.Attempts == 1, "A non-transient HTTP status was retried.");

        var corruptHandler = new SequenceHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x01]) });
        using (var http = new HttpClient(corruptHandler))
        {
            var corruptPath = Path.Combine(root, "corrupt.nupkg");
            try
            {
                await EnsureArchiveAsync(http, uri, corruptPath, expectedSha512, static (_, _) => Task.CompletedTask);
                throw new InvalidOperationException("A digest mismatch unexpectedly succeeded.");
            }
            catch (InvalidDataException) { }
        }
        RequireSelfTest(corruptHandler.Attempts == 1, "A digest mismatch was retried.");

        var cancellationHandler = new SequenceHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        using (var http = new HttpClient(cancellationHandler))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                await EnsureArchiveAsync(http, uri, Path.Combine(root, "cancelled.nupkg"), expectedSha512, static (_, _) => Task.CompletedTask, cancellation.Token);
                throw new InvalidOperationException("A cancelled download unexpectedly succeeded.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        RequireSelfTest(cancellationHandler.Attempts == 0, "A cancelled download reached the HTTP handler.");
        Console.WriteLine("Framework reference package retry self-test passed.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RequireSelfTest(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task<bool> HasSha512Async(string path, string expected, CancellationToken cancellationToken = default)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    var digest = Convert.ToHexString(await SHA512.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    return string.Equals(digest, expected, StringComparison.Ordinal);
}

static void ExtractReferenceAssemblies(string archivePath, string outputPath, string frameworkVersion)
{
    using var archive = ZipFile.OpenRead(archivePath);
    var root = $"build/.NETFramework/v{frameworkVersion}/";
    var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in archive.Entries)
    {
        if (!entry.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.Length <= 0)
            continue;
        var relative = entry.FullName[root.Length..];
        if (relative.StartsWith("Facades/", StringComparison.OrdinalIgnoreCase))
            relative = relative["Facades/".Length..];
        if (relative.Contains('/', StringComparison.Ordinal) || !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        if (!files.TryAdd(relative, entry))
            throw new InvalidDataException($"Reference package contains duplicate flattened assembly '{relative}'.");
    }

    foreach (var (name, entry) in files.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
    {
        using var source = entry.Open();
        using var destination = new FileStream(Path.Combine(outputPath, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
    }
}

static void ExtractNetFx30Extensions(string archivePath, string outputPath, string frameworkVersion)
{
    using var archive = ZipFile.OpenRead(archivePath);
    var root = $"build/.NETFramework/v{frameworkVersion}/";
    var selected = new Dictionary<string, (ZipArchiveEntry Entry, Version AssemblyVersion)>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in archive.Entries)
    {
        if (!entry.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.Length <= 0)
            continue;
        var relative = entry.FullName[root.Length..];
        if (relative.StartsWith("Facades/", StringComparison.OrdinalIgnoreCase))
            relative = relative["Facades/".Length..];
        if (relative.Contains('/', StringComparison.Ordinal) || !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var assemblyVersion = TryReadAssemblyVersion(entry);
        if (assemblyVersion is null || assemblyVersion != new Version(3, 0, 0, 0))
            continue;
        if (!selected.TryAdd(relative, (entry, assemblyVersion)))
            throw new InvalidDataException($"Reference package contains duplicate flattened assembly '{relative}'.");
    }

    var expected = NetFx30ExtensionAssemblies().Order(StringComparer.Ordinal).ToArray();
    var observed = selected.Keys.Order(StringComparer.Ordinal).ToArray();
    if (!observed.SequenceEqual(expected, StringComparer.Ordinal))
    {
        var missing = expected.Except(observed, StringComparer.Ordinal);
        var unexpected = observed.Except(expected, StringComparer.Ordinal);
        throw new InvalidDataException("The netfx35 package's AssemblyVersion 3.0.0.0 extension set does not match the locked net30 recipe. " + $"Missing: [{string.Join(", ", missing)}]; unexpected: [{string.Join(", ", unexpected)}].");
    }

    var existing = Directory.EnumerateFiles(outputPath, "*.dll", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var name in expected)
    {
        if (!existing.Add(name))
            throw new InvalidDataException($"The net30 composition contains a colliding assembly '{name}'.");
        var entry = selected[name].Entry;
        using var source = entry.Open();
        using var destination = new FileStream(Path.Combine(outputPath, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
    }
}

static Version? TryReadAssemblyVersion(ZipArchiveEntry entry)
{
    using var bytes = new MemoryStream(checked((int)entry.Length));
    using (var source = entry.Open())
        source.CopyTo(bytes);
    bytes.Position = 0;
    using var pe = new PEReader(bytes, PEStreamOptions.LeaveOpen);
    if (!pe.HasMetadata)
        return null;
    var metadata = pe.GetMetadataReader();
    if (!metadata.IsAssembly)
        return null;
    return metadata.GetAssemblyDefinition().Version;
}

static void ValidateNetFx30Composition(string path)
{
    var files = Directory.EnumerateFiles(path, "*.dll", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
    if (files.Length != 75)
        throw new InvalidDataException($"The net30 composition must contain 75 assemblies, observed {files.Length}.");
    var extensions = NetFx30ExtensionAssemblies();
    if (!extensions.All(file => files.Contains(file, StringComparer.Ordinal)))
        throw new InvalidDataException("The net30 composition is missing a locked 3.0 extension assembly.");
}

static string[] NetFx30ExtensionAssemblies() =>
[
    "PresentationBuildTasks.dll",
    "PresentationCore.dll",
    "PresentationFramework.Aero.dll",
    "PresentationFramework.Classic.dll",
    "PresentationFramework.dll",
    "PresentationFramework.Luna.dll",
    "PresentationFramework.Royale.dll",
    "ReachFramework.dll",
    "System.IdentityModel.dll",
    "System.IdentityModel.Selectors.dll",
    "System.IO.Log.dll",
    "System.Printing.dll",
    "System.Runtime.Serialization.dll",
    "System.ServiceModel.dll",
    "System.Speech.dll",
    "System.Workflow.Activities.dll",
    "System.Workflow.ComponentModel.dll",
    "System.Workflow.Runtime.dll",
    "UIAutomationClient.dll",
    "UIAutomationClientsideProviders.dll",
    "UIAutomationProvider.dll",
    "UIAutomationTypes.dll",
    "WindowsBase.dll",
    "WindowsFormsIntegration.dll"
];

static void ValidateMaterializedSet(string path, string id, string targetFramework)
{
    var files = Directory.EnumerateFiles(path, "*.dll", SearchOption.TopDirectoryOnly).ToArray();
    if (files.Length == 0 || !File.Exists(Path.Combine(path, "mscorlib.dll")) || !File.Exists(Path.Combine(path, "System.dll")) || File.Exists(Path.Combine(path, "SharpLab.Runtime.dll")))
    {
        throw new InvalidDataException($"Reference set '{id}' is incomplete or contains a forbidden runtime helper.");
    }
    if (string.Equals(targetFramework, "net48", StringComparison.Ordinal) && (!File.Exists(Path.Combine(path, "System.Runtime.dll")) || !File.Exists(Path.Combine(path, "netstandard.dll"))))
    {
        throw new InvalidDataException("The net48 reference set is missing required facade assemblies.");
    }
}

static async Task WriteAttestationAsync(string root, string id, string targetFramework, string digest, JsonObject provenance)
{
    var files = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal).Select(path =>
        {
            using var stream = File.OpenRead(path);
            return new AttestedFile(Path.GetFileName(path), stream.Length, $"sha256:{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}");
        }).ToArray();
    var canonical = new StringBuilder();
    foreach (var file in files)
        canonical.Append(file.Digest).Append("  ").Append(file.Size).Append("  ").Append(file.Path).Append('\n');
    var contentDigest =
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant()}";
    var document = new JsonObject { ["schemaVersion"] = 1, ["referenceSet"] = new JsonObject { ["id"] = id, ["targetFramework"] = targetFramework, ["digest"] = digest, ["contentDigest"] = contentDigest, ["provenance"] = provenance }, ["files"] = new JsonArray(files.Select(static file => (JsonNode)new JsonObject { ["path"] = file.Path, ["size"] = file.Size, ["digest"] = file.Digest }).ToArray()) };
    await File.WriteAllTextAsync(Path.Combine(root, "reference-set.attestation.json"), document.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true }) + "\n");
}

static JsonObject PackageProvenance(LockedReferencePackage package) => new()
{
    ["kind"] = "nuget-package",
    ["resolvedVersion"] = package.Version,
    ["package"] = package.Id,
    ["sourceUri"] = package.Uri.AbsoluteUri,
    ["sourceArchiveDigest"] = $"sha512:{package.Sha512}"
};

static JsonObject CompositionProvenance(JsonObject composition, IReadOnlyList<CompositionSource> sources) => new()
{
    ["kind"] = Required(composition, "kind"),
    ["resolvedVersion"] = Required(composition, "resolvedVersion"),
    ["sources"] = new JsonArray(sources.Select(source => (JsonNode)new JsonObject { ["role"] = source.Role, ["selection"] = source.Selection, ["package"] = source.Package.Id, ["resolvedVersion"] = source.Package.Version, ["sourceUri"] = source.Package.Uri.AbsoluteUri, ["sourceArchiveDigest"] = $"sha512:{source.Package.Sha512}", ["packageContentHash"] = source.Package.PackageContentHash }).ToArray())
};

static string ComputeCompositionSourceIdentity(string referenceSetId, string targetFramework, string kind, string resolvedVersion, IReadOnlyList<CompositionSource> sources)
{
    var canonical = new StringBuilder().Append("referenceSet=").Append(referenceSetId).Append('\n').Append("targetFramework=").Append(targetFramework).Append('\n').Append("kind=").Append(kind).Append('\n').Append("resolvedVersion=").Append(resolvedVersion).Append('\n');
    foreach (var source in sources)
    {
        canonical.Append("source=").Append(source.Role).Append('\t').Append(source.Selection).Append('\t').Append(source.Package.Id).Append('\t').Append(source.Package.Version).Append('\t').Append(source.Package.Uri.AbsoluteUri).Append('\t').Append("sha512:").Append(source.Package.Sha512).Append('\t').Append(source.Package.PackageContentHash).Append('\n');
    }
    return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant()}";
}

static void AddConfiguration(JsonObject configuredReferenceSets, string id, string targetFramework, string resolvedVersion, string digest)
{
    var configuration = new JsonObject { ["Path"] = $"/reference-sets/{id}", ["TargetFramework"] = targetFramework, ["FrameworkVersion"] = resolvedVersion, ["RuntimeFrameworkVersion"] = RuntimeFrameworkVersion(targetFramework), ["Digest"] = digest, ["IncludeSharpLabRuntime"] = false };
    configuredReferenceSets.Add(id, configuration);
}

static string RuntimeFrameworkVersion(string targetFramework)
{
    if (!targetFramework.StartsWith("net", StringComparison.Ordinal))
        throw new InvalidDataException($"Target framework '{targetFramework}' is not a recognized .NET Framework TFM.");

    var digits = targetFramework.AsSpan(3);
    if (digits.Length is not (2 or 3) || digits.IndexOfAnyExceptInRange('0', '9') >= 0)
    {
        throw new InvalidDataException($"Target framework '{targetFramework}' is not a recognized .NET Framework TFM.");
    }

    return digits.Length == 2
        ? $"{digits[0]}.{digits[1]}" : $"{digits[0]}.{digits[1]}.{digits[2]}";
}

static string SafeChild(string root, string name)
{
    var fullRoot = Path.GetFullPath(root);
    var child = Path.GetFullPath(Path.Combine(fullRoot, name));
    if (!child.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidDataException($"Reference set path '{name}' escapes the output directory.");
    return child;
}

static void RecreateDirectory(string path)
{
    if (Directory.Exists(path))
        Directory.Delete(path, recursive: true);
    Directory.CreateDirectory(path);
}

static string Required(JsonObject value, string name) => value[name]?.GetValue<string>() is { Length: > 0 } result ? result : throw new InvalidDataException($"Required property '{name}' is missing.");

static string RequiredLowerHex(JsonObject value, string name, int length)
{
    var result = Required(value, name);
    if (result.Length != length || result.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
    {
        throw new InvalidDataException($"Property '{name}' must contain {length} lowercase hexadecimal characters.");
    }
    return result;
}

static string RequiredLowerSha256(JsonObject value, string name)
{
    var result = Required(value, name);
    if (!result.StartsWith("sha256:", StringComparison.Ordinal) || result.Length != "sha256:".Length + 64)
    {
        throw new InvalidDataException($"Property '{name}' must contain a lowercase SHA-256 digest.");
    }
    foreach (var character in result.AsSpan("sha256:".Length))
    {
        if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException($"Property '{name}' must contain a lowercase SHA-256 digest.");
    }
    return result;
}

static JsonArray RequiredArray(JsonObject value, string name) => value[name]?.AsArray() ?? throw new InvalidDataException($"Required array property '{name}' is missing.");

sealed record AttestedFile(string Path, long Size, string Digest);

sealed record LockedReferencePackage(string TargetId, string FrameworkVersion, string Id, string Version, Uri Uri, string Sha512, string PackageContentHash);

sealed record CompositionSource(string Role, string Selection, LockedReferencePackage Package);

sealed class SequenceHttpMessageHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    public int Attempts { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Attempts++;
        return Task.FromResult(responseFactory(Attempts));
    }
}

sealed record Options(string MatrixPath, string OutputDirectory, string ArchiveDirectory, string AppsettingsTemplatePath, string AppsettingsOutputPath)
{
    public static Options Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
                throw new ArgumentException($"Invalid argument '{arguments[index]}'.");
            if (!values.TryAdd(arguments[index][2..], arguments[++index]))
                throw new ArgumentException($"Duplicate argument '{arguments[index - 1]}'.");
        }

        return new(FullPath(values, "matrix"), FullPath(values, "output"), FullPath(values, "archive-cache"), FullPath(values, "appsettings-template"), FullPath(values, "appsettings-output"));
    }

    private static string FullPath(Dictionary<string, string> values, string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? Path.GetFullPath(value) : throw new ArgumentException($"--{name} is required.");
}

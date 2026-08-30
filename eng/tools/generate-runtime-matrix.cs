#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property NuGetLockFilePath=obj/generate-runtime-matrix.packages.lock.json

#pragma warning disable IL2026, IL3050, CA1861

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.Encodings.Web;
using System.Diagnostics;
using System.Security.Cryptography;

try
{
    var options = Options.Parse(args);
    var root = Path.GetFullPath(options.RepositoryRoot);
    var matrixPath = Path.GetFullPath(options.MatrixPath ?? Path.Combine(root, "profiles", "runtime-matrix.json"));
    var catalogPath = Path.GetFullPath(options.CatalogPath ?? Path.Combine(root, "profiles", "catalog", "catalog.json"));
    // Matrix output is a candidate until an immutable image has passed the
    // promotion gates. Keep it out of the top-level directory consumed by
    // release/deployment tooling, where active profiles live.
    var profileDirectory = Path.GetFullPath(options.ProfileDirectory ?? Path.Combine(root, "profiles", "runtimes", "candidates"));
    var matrix = JsonNode.Parse(await File.ReadAllTextAsync(matrixPath))?.AsObject() ?? throw new InvalidDataException("Runtime matrix is not a JSON object.");
    await ValidatePromotionReceiptsAsync(root, matrixPath);
    var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))?.AsObject() ?? throw new InvalidDataException("Catalog is not a JSON object.");

    var references = RequiredArray(catalog, "referenceSets");
    var toolchains = RequiredArray(catalog, "toolchains");
    var runtimes = RequiredArray(catalog, "runtimes");
    // Snapshot the identities that were already active before adding any
    // matrix candidates. These IDs must not be replaced in-place by a refresh.
    var activeProfileDirectory = Path.Combine(root, "profiles", "runtimes");
    var activeProfileIds = ReadTopLevelProfileIds(activeProfileDirectory);
    foreach (var id in ReadSelectableCatalogRuntimeIds(runtimes))
        activeProfileIds.Add(id);
    var compatibility = RequiredArray(catalog, "compatibility");
    // Catalog revisions may contain an older generated ID for a relation that
    // is now emitted under a more descriptive name.  Compatibility identity
    // is the semantic tuple, not the display ID, so normalize that tuple
    // before adding matrix candidates.  A healthy rule always wins over a
    // rejected duplicate while the first rule's stable ID is retained.
    NormalizeCompatibilityRules(compatibility);
    var presets = RequiredArray(catalog, "presets");
    var generatedProfiles = new List<string>();

    var coreClrTargets = RequiredArray(matrix, "coreClr").Select(static item => item!.AsObject()).ToArray();
    foreach (var target in coreClrTargets)
    {
        var targetId = RequiredString(target, "id");
        var referenceSetId = RequiredString(target, "referenceSetId");
        AddReferenceSet(references, target, targetId, referenceSetId, "coreclr");

        var linuxId = $"{targetId}-linux-x64";
        var linuxCapability = RequiredObject(target, "linuxCapability");
        var linuxPromotion = LoadPromotionBinding(root, linuxCapability, linuxId);
        var linuxRunImplementation = ResolveRunImplementation(target, linuxCapability, "linux", linuxPromotion);
        AddRuntime(runtimes, target, linuxId, targetId, "linux", linuxCapability, "coreclr", linuxRunImplementation, linuxPromotion);
        WriteProfile(profileDirectory, CreateCoreClrProfile(target, linuxId, "linux", linuxCapability, linuxPromotion), options, generatedProfiles, activeProfileIds);
        AddToolchainEdges(compatibility, referenceSetId, targetId, targetId.StartsWith("dotnet-core-", StringComparison.Ordinal) ? "legacy CoreCLR reference package is not installed in a worker image" : "runtime matrix candidate has not passed worker image preflight", IsVerified(linuxCapability));
        AddRuntimeEdge(compatibility, "dotnet-managed-pe-v1", linuxId, IsVerified(linuxCapability), "Linux CoreCLR candidate has not passed product-image preflight");
        AddPresetSet(presets, target, referenceSetId, linuxId, IsVerified(linuxCapability), targetId);

        var wineId = $"wine-{targetId}-linux-x64";
        var wineCapability = RequiredObject(target, "wineCapability");
        var winePromotion = LoadPromotionBinding(root, wineCapability, wineId);
        AddRuntime(runtimes, target, wineId, targetId, "wine", wineCapability, "coreclr-wine", RuntimeOperationImplementations.LegacyJitInspector, winePromotion);
        WriteProfile(profileDirectory, CreateCoreClrProfile(target, wineId, "wine", wineCapability, winePromotion), options, generatedProfiles, activeProfileIds);
        AddRuntimeEdge(compatibility, "dotnet-managed-pe-v1", wineId, IsVerified(wineCapability), "Wine CoreCLR candidate has not passed product-image preflight");
    }
    SetCoreClrReferenceSetAllowLists(toolchains, coreClrTargets);

    var mono = RequiredObject(matrix, "mono");
    var monoCapability = RequiredObject(mono, "capability");
    var monoId = RequiredString(mono, "id");
    var monoReferenceSetId = RequiredString(mono, "referenceSetId");
    RemoveStaleMonoCandidates(runtimes, compatibility, presets, profileDirectory, monoId, options, activeProfileIds);
    var monoPromotion = LoadPromotionBinding(root, monoCapability, monoId);
    AddRuntime(runtimes, mono, monoId, monoId, "mono", monoCapability, "mono", RuntimeOperationImplementations.TargetRuntimeRunner, monoPromotion);
    WriteProfile(profileDirectory, CreateMonoProfile(mono, monoId, monoCapability, monoPromotion), options, generatedProfiles, activeProfileIds);
    // Mono consumes the existing .NET Framework 4.8 reference set.  It is
    // intentionally not duplicated in the matrix entry, so fail closed if a
    // catalog refresh removes that shared reference set.
    RequireExistingReferenceSet(references, monoReferenceSetId, monoId);
    AddToolchainEdges(compatibility, monoReferenceSetId, monoId, "Mono candidate has not passed product preflight", IsVerified(monoCapability), ["roslyn-stable-netfx48"]);
    // Older generator revisions modeled Mono as a CoreCLR-format runtime.
    // Remove that stale candidate edge so a refresh cannot leave an invalid
    // format/runtime pair alongside the corrected Framework edge.
    RemoveRule(compatibility, $"dotnet-managed-pe-v1-{monoId}");
    AddRuntimeEdge(compatibility, "dotnet-framework-managed-pe-v1", monoId, IsVerified(monoCapability), "Mono candidate has not passed product-image preflight");
    AddPresetSet(presets, mono, monoReferenceSetId, monoId, IsVerified(monoCapability), monoId, framework: true);

    var frameworkTargets = RequiredArray(RequiredObject(matrix, "framework"), "targets").Select(static item => item!.AsObject()).ToArray();
    var frameworkTargetsById = frameworkTargets.ToDictionary(static target => RequiredString(target, "id"), StringComparer.Ordinal);
    foreach (var target in frameworkTargets)
    {
        var targetId = RequiredString(target, "id");
        var referenceSetId = RequiredString(target, "referenceSetId");
        var capability = RequiredObject(target, "capability");
        var runtimeId = $"wine-{targetId}-linux-x64";
        var promotion = LoadPromotionBinding(root, capability, runtimeId);
        AddRuntime(runtimes, target, runtimeId, targetId, "framework", capability, "netfx-clr-wine", RuntimeOperationImplementations.TargetRuntimeRunner, promotion);
        WriteProfile(profileDirectory, CreateFrameworkProfile(target, runtimeId, capability, promotion), options, generatedProfiles, activeProfileIds);
        AddRuntimeEdge(compatibility, "dotnet-framework-managed-pe-v1", runtimeId, IsVerified(capability), "Framework Wine prefix is operator-supplied and has not passed product-image preflight");
        // Mixed-mode PE requires the separately audited CLR4/C++CLI 4.8
        // contract. Older framework prefixes are generated as managed-only
        // candidates until an operator supplies evidence for mixed loading.
        if (string.Equals(targetId, "netfx48", StringComparison.Ordinal))
        {
            AddRuntimeEdge(compatibility, "dotnet-framework-mixed-pe-v1", runtimeId, IsVerified(capability), "Framework Wine prefix is operator-supplied and has not passed product-image preflight");
        }
        else
        {
            RemoveSemanticRule(compatibility, "artifact-runtime", "dotnet-framework-mixed-pe-v1", runtimeId);
        }
        AddReferenceSet(references, target, targetId, referenceSetId, "netfx-clr-wine", frameworkTargetsById);
        AddToolchainEdges(compatibility, referenceSetId, targetId, "Framework reference package or Wine prefix is not installed", IsVerified(capability));
        AddPresetSet(presets, target, referenceSetId, runtimeId, IsVerified(capability), targetId, framework: true);
    }
    SetFrameworkReferenceSetAllowList(toolchains, frameworkTargets);

    // A candidate refresh can replace an existing runtime with an explicitly
    // unavailable identity.  Revoke any old allowed edge for that known
    // runtime as well; otherwise the resolver could still plan through a
    // stale edge even though the runtime itself is no longer selectable.
    RevokeUnavailableRuntimeEdges(runtimes, compatibility);

    if (options.Check)
    {
        EnsureUniqueCompatibilityRules(compatibility);
        Console.WriteLine($"Runtime matrix generation check passed ({generatedProfiles.Count} profile inputs).");
        return 0;
    }

    EnsureUniqueCompatibilityRules(compatibility);
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n" };
    var catalogBytes = SerializeJsonWithLf(catalog, jsonOptions);
    await File.WriteAllBytesAsync(catalogPath, catalogBytes);
    Console.WriteLine($"Updated {catalogPath}; generated/verified {generatedProfiles.Count} runtime profiles.");
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"Runtime matrix generation failed: {exception.Message}");
    return 1;
}

static void AddReferenceSet(JsonArray references, JsonObject target, string targetId, string referenceSetId, string family, IReadOnlyDictionary<string, JsonObject>? frameworkTargets = null)
{
    var identity = ResolveReferenceIdentity(target, targetId, referenceSetId, frameworkTargets);
    var status = RequiredString(target, "supportStatus");
    var capability = target.ContainsKey("linuxCapability")
        ? RequiredObject(target, "linuxCapability")
        : RequiredObject(target, "capability");
    var reference = new JsonObject
    {
        ["id"] = referenceSetId,
        ["displayName"] = RequiredString(target, "version"),
        ["targetFramework"] = TargetFramework(target, targetId),
        ["digest"] = identity.Digest,
        ["runtimeFamily"] = family,
        // Managed Framework references describe the target contract itself.
        // Requiring a Wine implementation tag here would incorrectly prevent
        // the same managed PE from running on Mono. Mixed C++/CLI uses its
        // separate netfx48-ref contract and retains the Wine requirement.
        ["requiredRuntimeFeatureTags"] = new JsonArray(),
        ["metadataFeatureTags"] = new JsonArray(),
        ["availability"] = Availability(IsVerified(capability), "Reference package is locked but not installed in a worker image.")
    };
    ApplyLifecycle(reference, target);
    UpsertCandidate(references, referenceSetId, reference);
}

static ReferenceIdentity ResolveReferenceIdentity(JsonObject target, string targetId, string referenceSetId, IReadOnlyDictionary<string, JsonObject>? frameworkTargets)
{
    if (target["referencePackage"] is JsonObject package)
    {
        if (target["referenceComposition"] is not null)
            throw new InvalidDataException($"Target '{targetId}' defines both a reference package and composition.");
        return new(RequiredString(package, "version"), RequiredString(package, "packageContentHash"));
    }

    var composition = target["referenceComposition"]?.AsObject()
        ?? throw new InvalidDataException($"Target '{targetId}' has no reference package or composition.");
    if (frameworkTargets is null)
        throw new InvalidDataException($"Target '{targetId}' reference composition cannot be resolved outside the Framework matrix.");
    var kind = RequiredString(composition, "kind");
    var resolvedVersion = RequiredString(composition, "resolvedVersion");
    var targetFramework = TargetFramework(target, targetId);
    var sources = RequiredArray(composition, "sources")
        .Select(value => value?.AsObject() ?? throw new InvalidDataException($"Target '{targetId}' reference composition source is invalid."))
        .Select(source =>
        {
            var sourceTargetId = RequiredString(source, "targetId");
            if (!frameworkTargets.TryGetValue(sourceTargetId, out var sourceTarget) || sourceTarget["referencePackage"] is not JsonObject sourcePackage)
            {
                throw new InvalidDataException($"Target '{targetId}' reference composition source '{sourceTargetId}' has no locked package.");
            }
            return new ResolvedCompositionSource(RequiredString(source, "role"), RequiredString(source, "selection"), sourcePackage);
        }).ToArray();
    if (!string.Equals(targetId, "netfx30", StringComparison.Ordinal) ||
        !string.Equals(referenceSetId, "netfx30-managed-ref", StringComparison.Ordinal) ||
        !string.Equals(targetFramework, "net30", StringComparison.Ordinal) ||
        !string.Equals(kind, "nuget-package-composition", StringComparison.Ordinal) ||
        !string.Equals(resolvedVersion, "net30-union-v1", StringComparison.Ordinal) ||
        sources.Length != 2 ||
        sources[0].Role != "base" || sources[0].Selection != "all" ||
        sources[1].Role != "extension" || sources[1].Selection != "assembly-version:3.0.0.0")
    {
        throw new InvalidDataException($"Target '{targetId}' uses an unsupported reference composition recipe.");
    }

    var canonical = new StringBuilder().Append("referenceSet=").Append(referenceSetId).Append('\n').Append("targetFramework=").Append(targetFramework).Append('\n').Append("kind=").Append(kind).Append('\n').Append("resolvedVersion=").Append(resolvedVersion).Append('\n');
    foreach (var source in sources)
    {
        canonical.Append("source=").Append(source.Role).Append('\t').Append(source.Selection).Append('\t').Append(RequiredString(source.Package, "id")).Append('\t').Append(RequiredString(source.Package, "version")).Append('\t').Append(RequiredString(source.Package, "url")).Append('\t').Append("sha512:").Append(RequiredString(source.Package, "sha512")).Append('\t').Append(RequiredString(source.Package, "packageContentHash")).Append('\n');
    }
    var actualDigest =
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant()}";
    var lockedDigest = RequiredString(composition, "sourceIdentityDigest");
    if (!string.Equals(lockedDigest, actualDigest, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Target '{targetId}' reference composition source identity does not match its locked digest.");
    }
    return new(resolvedVersion, lockedDigest);
}

static void SetCoreClrReferenceSetAllowLists(JsonArray toolchains, IReadOnlyList<JsonObject> coreClrTargets)
{
    var allowedIds = coreClrTargets.Select(target => RequiredString(target, "referenceSetId")).Distinct(StringComparer.Ordinal).ToArray();
    if (allowedIds.Length != coreClrTargets.Count)
        throw new InvalidDataException("The CoreCLR reference-set allow-list is duplicated.");

    foreach (var toolchainId in new[] { "roslyn-stable", "roslyn-main" })
    {
        var toolchain = toolchains.Select(static value => value?.AsObject() ?? throw new InvalidDataException("Catalog toolchain entry is not an object.")).SingleOrDefault(candidate => string.Equals(candidate["id"]?.GetValue<string>(), toolchainId, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Catalog does not contain toolchain '{toolchainId}'.");
        if (!allowedIds.Contains(toolchain["defaultReferenceSetId"]?.GetValue<string>(), StringComparer.Ordinal))
        {
            throw new InvalidDataException($"The CoreCLR reference-set allow-list does not contain the default for '{toolchainId}'.");
        }
        toolchain["allowedReferenceSetIds"] = new JsonArray(allowedIds.Select(static id => JsonValue.Create(id)).ToArray());
    }
}

static void SetFrameworkReferenceSetAllowList(JsonArray toolchains, IReadOnlyList<JsonObject> frameworkTargets)
{
    var toolchain = toolchains.Select(static value => value?.AsObject() ?? throw new InvalidDataException("Catalog toolchain entry is not an object.")).SingleOrDefault(candidate => string.Equals(candidate["id"]?.GetValue<string>(), "roslyn-stable-netfx48", StringComparison.Ordinal))
        ?? throw new InvalidDataException("Catalog does not contain the Roslyn .NET Framework toolchain.");
    var allowedIds = frameworkTargets.Select(target => RequiredString(target, "referenceSetId")).Distinct(StringComparer.Ordinal).ToArray();
    if (allowedIds.Length != frameworkTargets.Count || !allowedIds.Contains(toolchain["defaultReferenceSetId"]?.GetValue<string>(), StringComparer.Ordinal))
    {
        throw new InvalidDataException("The Framework reference-set allow-list is incomplete or duplicated.");
    }
    toolchain["allowedReferenceSetIds"] = new JsonArray(allowedIds.Select(static id => JsonValue.Create(id)).ToArray());
}

static void RequireExistingReferenceSet(JsonArray references, string referenceSetId, string targetId)
{
    if (FindId(references, referenceSetId) < 0)
    {
        throw new InvalidDataException($"Runtime matrix target '{targetId}' references shared set '{referenceSetId}', but the Catalog does not contain it.");
    }
}

static void AddRuntime(JsonArray runtimes, JsonObject target, string runtimeId, string targetId, string platform, JsonObject capability, string family, string runImplementationId, PromotionBinding? promotion)
{
    var status = RequiredString(target, "supportStatus");
    var version = target.ContainsKey("version") ? RequiredString(target, "version") : RequiredString(target, "resolvedVersion");
    var effective = EffectiveCapabilities(capability, targetId, platform, runImplementationId);
    var verified = IsVerified(capability);
    var payload = PlatformPayload(target, platform);
    var hash = payload?["sha512"]?.GetValue<string>() ??
        target["capability"]?["promotionState"]?.GetValue<string>() ?? "unverified";
    var runtimeImageId = promotion?.ImageId ?? RuntimeImageReference(runtimeId);
    var accepted = platform switch
    {
        "framework" when string.Equals(targetId, "netfx48", StringComparison.Ordinal) =>
            new[] { "dotnet-framework-managed-pe-v1", "dotnet-framework-mixed-pe-v1" },
        "framework" => new[] { "dotnet-framework-managed-pe-v1" },
        "mono" => new[] { "dotnet-framework-managed-pe-v1" },
        _ => new[] { "dotnet-managed-pe-v1" }
    };
    var acceptedFrameworks = AcceptedFrameworks(target, platform, version);
    var acceptedRuntimeFamilies = AcceptedRuntimeFamilies(family);
    var environment = platform == "mono" ? "mono" : platform == "linux" ? "coreclr" : "wine";
    var isolation = platform is "linux" or "mono" ? "standard" : "wine";
    var runtime = new JsonObject
    {
        ["id"] = runtimeId,
        ["displayName"] = platform == "linux" ? $".NET {version} / Linux x64" :
            platform == "wine" ? $".NET {version} / Wine x64" :
            platform == "mono" ? $"Mono {version} / Linux x64" : $".NET Framework {version} / Wine x64",
        ["family"] = family,
        ["resolvedVersion"] = version,
        ["rid"] = "linux-x64",
        ["architecture"] = "x64",
        ["acceptedArtifactFormats"] = new JsonArray(accepted.Select(static value => JsonValue.Create(value)).ToArray()),
        // A blocked matrix row is deliberately not selectable and therefore
        // exposes no Catalog capabilities.  Once verified, only capabilities
        // backed by the profile operation or an explicit instrumentation
        // evidence declaration are copied into the Catalog.
        ["capabilities"] = verified ? new JsonArray(effective.Select(static value => JsonValue.Create(value)).ToArray()) : new JsonArray(),
        ["runtimeImageId"] = runtimeImageId,
        ["acceptedRuntimeFamilies"] = new JsonArray(acceptedRuntimeFamilies.Select(static value => JsonValue.Create(value)).ToArray()),
        ["acceptedFrameworks"] = platform == "mono"
            ? new JsonArray(new JsonObject { ["name"] = ".NETFramework", ["exactVersion"] = "4.8" })
            : acceptedFrameworks,
        ["containerIsolationKind"] = isolation,
        ["containerEnvironmentKind"] = environment,
        ["providedRuntimeFeatureTags"] = RuntimeFeatureTags(targetId, family),
        ["providedMetadataFeatureTags"] = new JsonArray(),
        ["availability"] = Availability(verified, BlockReason(capability, targetId))
    };
    // CoreCLR payloads have an upstream source/JIT commit that can be closed
    // against the release lock.  Mono and Desktop CLR under Wine are
    // operator-supplied runtime images instead; their identity is the
    // immutable image digest and they must never expose a payload hash as a
    // CoreCLR commit.
    if (promotion is not null)
    {
        runtime["runtimeCommit"] = promotion.RuntimeCommit;
        runtime["jitVersion"] = promotion.JitVersion;
        runtime["jitCommit"] = promotion.JitCommit;
    }
    else if (!IsOperatorRuntimeFamily(family))
    {
        runtime["runtimeCommit"] = target["runtimeCommit"]?.GetValue<string>() ?? $"payload-sha512:{hash}";
        runtime["jitVersion"] = version;
        runtime["jitCommit"] = target["jitCommit"]?.GetValue<string>() ?? $"payload-sha512:{hash}";
    }
    if (effective.Contains("jit-asm", StringComparer.Ordinal))
    {
        var sourceMappingKind = promotion?.JitSourceMappingKind;
        if (sourceMappingKind is null && platform == "linux")
        {
            sourceMappingKind = target["checkedJit"]?["sourceMappingKind"]?.GetValue<string>() ??
                target["profilerProvider"]?["sourceMappingKind"]?.GetValue<string>();
        }
        runtime["jitSourceMappingKind"] = sourceMappingKind ?? "none";
    }
    ApplyLifecycle(runtime, target);
    if (platform == "wine" && effective.Count == 0)
        runtime["visibility"] = "hidden";
    UpsertCandidate(runtimes, runtimeId, runtime);
}

static void AddToolchainEdges(JsonArray rules, string referenceSetId, string targetId, string reason, bool allowed, IReadOnlyList<string>? toolchainOverride = null)
{
    var toolchains = toolchainOverride ?? (targetId.StartsWith("netfx", StringComparison.Ordinal)
        ? new[] { "roslyn-stable-netfx48" }
        : new[] { "roslyn-stable", "roslyn-main" });
    foreach (var toolchain in toolchains)
    {
        var id = $"{toolchain}-{referenceSetId}";
        var rule = new JsonObject { ["id"] = id, ["kind"] = "toolchain-reference-set", ["fromId"] = toolchain, ["toId"] = referenceSetId, ["allowed"] = allowed };
        if (!allowed)
            rule["reason"] = reason;
        UpsertCandidateRule(rules, id, rule, allowed);
    }
}

static void AddRuntimeEdge(JsonArray rules, string format, string runtimeId, bool allowed, string reason)
{
    var id = $"{format}-{runtimeId}";
    var rule = new JsonObject { ["id"] = id, ["kind"] = "artifact-runtime", ["fromId"] = format, ["toId"] = runtimeId, ["allowed"] = allowed };
    if (!allowed)
        rule["reason"] = reason;
    UpsertCandidateRule(rules, id, rule, allowed);
}

static void RevokeUnavailableRuntimeEdges(JsonArray runtimes, JsonArray rules)
{
    var availabilityById = runtimes.Select(static value => value?.AsObject() ?? throw new InvalidDataException("Runtime entry is not a JSON object.")).ToDictionary(static runtime => RequiredString(runtime, "id"), static runtime => IsSelectable(runtime), StringComparer.Ordinal);

    foreach (var rule in rules.OfType<JsonObject>())
    {
        if (!string.Equals(rule["kind"]?.GetValue<string>(), "artifact-runtime", StringComparison.Ordinal) || rule["allowed"]?.GetValue<bool>() != true || !availabilityById.TryGetValue(rule["toId"]?.GetValue<string>() ?? string.Empty, out var selectable) || selectable)
        {
            continue;
        }

        rule["allowed"] = false;
        rule["reason"] = "The target runtime is not selectable until its candidate image passes promotion preflight.";
    }
}

static void AddPresetSet(JsonArray presets, JsonObject target, string referenceSetId, string runtimeId, bool allowed, string suffix, bool framework = false)
{
    var toolchain = framework ? "roslyn-stable-netfx48" : "roslyn-stable";
    foreach (var language in new[] { (Id: "csharp", Name: "C#"), (Id: "visual-basic", Name: "Visual Basic") })
    {
        var id = $"{language.Id}-{toolchain}-{suffix}";
        var preset = new JsonObject { ["id"] = id, ["displayName"] = $"{language.Name} / {RequiredString(target, "version")}", ["languageId"] = language.Id, ["toolchainId"] = toolchain, ["referenceSetId"] = referenceSetId, ["defaultOutputId"] = "decompiled-csharp", ["defaultRuntimeId"] = runtimeId, ["availability"] = Availability(allowed, "The runtime matrix candidate has not passed product preflight.") };
        ApplyLifecycle(preset, target);
        UpsertCandidate(presets, id, preset);
    }
}

static void UpsertCandidate(JsonArray values, string id, JsonObject candidate)
{
    var index = FindId(values, id);
    if (index < 0)
    {
        values.Add(candidate);
        return;
    }

    // A healthy installed entry is the last-known-good release identity. It
    // remains authoritative while a newer matrix candidate is staged.
    if (values[index]?.AsObject() is { } existing && IsSelectable(existing))
    {
        CopyLifecycle(candidate, existing);
        return;
    }
    values[index] = candidate;
}

static void CopyLifecycle(JsonObject source, JsonObject target)
{
    foreach (var key in new[] { "supportStatus", "supportEndDate", "visibility" })
    {
        if (source[key] is { } value)
            target[key] = value.DeepClone();
        else
            target.Remove(key);
    }
}

static void UpsertCandidateRule(JsonArray values, string id, JsonObject candidate, bool allowed)
{
    var semanticIndex = FindSemanticRule(values, candidate);
    if (semanticIndex >= 0)
    {
        var existing = values[semanticIndex]?.AsObject()
            ?? throw new InvalidDataException("Compatibility rule is not a JSON object.");
        // A healthy release route remains authoritative while a candidate is
        // being generated.  Otherwise refresh the existing semantic edge in
        // place, retaining its stable ID even if the generator's convention
        // changed between revisions.
        if (IsRuleAllowed(existing))
            return;

        var stableId = RequiredString(existing, "id");
        ReplaceRule(existing, candidate, stableId);
        return;
    }

    var index = FindId(values, id);
    if (index < 0)
    {
        values.Add(candidate);
        return;
    }

    var existingById = values[index]?.AsObject();
    // Do not take a healthy release route away during candidate generation;
    // update rejected/candidate routes so a later verified matrix state can be
    // promoted without deleting and recreating the Catalog.
    if (existingById?["allowed"]?.GetValue<bool>() == true)
        return;
    values[index] = candidate;
}

static int FindSemanticRule(JsonArray values, JsonObject candidate)
{
    var key = CompatibilityKey(candidate);
    for (var index = 0; index < values.Count; index++)
    {
        if (values[index]?.AsObject() is { } rule &&
            string.Equals(CompatibilityKey(rule), key, StringComparison.Ordinal))
        {
            return index;
        }
    }
    return -1;
}

static void NormalizeCompatibilityRules(JsonArray rules)
{
    var firstBySemanticKey = new Dictionary<string, int>(StringComparer.Ordinal);
    var remove = new HashSet<int>();
    for (var index = 0; index < rules.Count; index++)
    {
        var rule = rules[index]?.AsObject() ?? throw new InvalidDataException("Compatibility rule is not a JSON object.");
        var key = CompatibilityKey(rule);
        if (!firstBySemanticKey.TryGetValue(key, out var firstIndex))
        {
            firstBySemanticKey.Add(key, index);
            continue;
        }

        var first = rules[firstIndex]?.AsObject() ?? throw new InvalidDataException("Compatibility rule is not a JSON object.");
        // Keep the first ID as the stable identity, but allow a later healthy
        // candidate to replace a stale rejected rule's payload.
        if (!IsRuleAllowed(first) && IsRuleAllowed(rule))
            ReplaceRule(first, rule, RequiredString(first, "id"));
        remove.Add(index);
    }

    foreach (var index in remove.OrderByDescending(static value => value))
        rules.RemoveAt(index);
}

static void EnsureUniqueCompatibilityRules(JsonArray rules)
{
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (var index = 0; index < rules.Count; index++)
    {
        var rule = rules[index]?.AsObject()
            ?? throw new InvalidDataException("Compatibility rule is not a JSON object.");
        var key = CompatibilityKey(rule);
        if (!seen.Add(key))
        {
            throw new InvalidDataException($"Compatibility catalog contains duplicate semantic edge '{key}' at index {index}.");
        }
    }
}

static string CompatibilityKey(JsonObject rule) => string.Join("\u001f", RequiredString(rule, "kind"), RequiredString(rule, "fromId"), RequiredString(rule, "toId"));

static bool IsRuleAllowed(JsonObject rule) => rule["allowed"]?.GetValue<bool>() == true;

static void ReplaceRule(JsonObject target, JsonObject source, string stableId)
{
    var properties = source.Select(static property => (property.Key, Value: property.Value?.DeepClone())).ToArray();
    foreach (var key in target.Select(static property => property.Key).ToArray())
        target.Remove(key);
    foreach (var (key, value) in properties)
        target[key] = value;
    target["id"] = stableId;
}

static void RemoveRule(JsonArray values, string id)
{
    for (var index = values.Count - 1; index >= 0; index--)
    {
        if (string.Equals(values[index]?.AsObject()["id"]?.GetValue<string>(), id, StringComparison.Ordinal))
            values.RemoveAt(index);
    }
}

static void RemoveSemanticRule(JsonArray values, string kind, string fromId, string toId)
{
    for (var index = values.Count - 1; index >= 0; index--)
    {
        if (values[index]?.AsObject() is { } rule &&
            string.Equals(rule["kind"]?.GetValue<string>(), kind, StringComparison.Ordinal) &&
            string.Equals(rule["fromId"]?.GetValue<string>(), fromId, StringComparison.Ordinal) &&
            string.Equals(rule["toId"]?.GetValue<string>(), toId, StringComparison.Ordinal))
        {
            values.RemoveAt(index);
        }
    }
}

static void RemoveStaleMonoCandidates(JsonArray runtimes, JsonArray compatibility, JsonArray presets, string profileDirectory, string currentId, Options options, IReadOnlySet<string> activeProfileIds)
{
    var staleIds = runtimes
        .Select(static value => value?.AsObject() ?? throw new InvalidDataException("Runtime entry is not a JSON object."))
        .Where(runtime => string.Equals(runtime["family"]?.GetValue<string>(), "mono", StringComparison.Ordinal) && !string.Equals(RequiredString(runtime, "id"), currentId, StringComparison.Ordinal) && !IsSelectable(runtime) && !activeProfileIds.Contains(RequiredString(runtime, "id")))
        .Select(runtime => RequiredString(runtime, "id"))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    foreach (var staleId in staleIds)
    {
        for (var index = runtimes.Count - 1; index >= 0; index--)
        {
            if (string.Equals(runtimes[index]?.AsObject()["id"]?.GetValue<string>(), staleId, StringComparison.Ordinal))
            {
                runtimes.RemoveAt(index);
            }
        }

        for (var index = compatibility.Count - 1; index >= 0; index--)
        {
            if (compatibility[index]?.AsObject() is { } rule &&
                (string.Equals(rule["fromId"]?.GetValue<string>(), staleId, StringComparison.Ordinal) || string.Equals(rule["toId"]?.GetValue<string>(), staleId, StringComparison.Ordinal)))
            {
                compatibility.RemoveAt(index);
            }
        }

        for (var index = presets.Count - 1; index >= 0; index--)
        {
            if (presets[index]?.AsObject() is { } preset &&
                string.Equals(preset["defaultRuntimeId"]?.GetValue<string>(), staleId, StringComparison.Ordinal))
            {
                presets.RemoveAt(index);
            }
        }

        if (!options.Check)
        {
            var profilePath = Path.Combine(profileDirectory, $"{staleId}.json");
            if (File.Exists(profilePath))
                File.Delete(profilePath);
        }

        Console.WriteLine($"Removed stale unpromoted Mono candidate '{staleId}'.");
    }
}

static int FindId(JsonArray values, string id)
{
    for (var index = 0; index < values.Count; index++)
    {
        if (string.Equals(values[index]?.AsObject()["id"]?.GetValue<string>(), id, StringComparison.Ordinal))
            return index;
    }
    return -1;
}

static bool IsSelectable(JsonObject value) =>
    value["availability"]?.AsObject()["installed"]?.GetValue<bool>() == true &&
    string.Equals(value["availability"]?.AsObject()["health"]?.GetValue<string>(), "healthy", StringComparison.Ordinal);

static JsonObject CreateCoreClrProfile(JsonObject target, string runtimeId, string platform, JsonObject capability, PromotionBinding? promotion)
{
    var version = RequiredString(target, "version");
    var referenceVersion = target["referencePackage"]?.AsObject()?["version"]?.GetValue<string>() ?? version;
    var runImplementation = ResolveRunImplementation(target, capability, platform, promotion);
    var effectiveCapabilities = EffectiveCapabilities(capability, runtimeId, platform, runImplementation);
    var usesModernRunner = string.Equals(runImplementation, RuntimeOperationImplementations.Runner, StringComparison.Ordinal);
    var helper = promotion?.RunAssemblyPath ?? (usesModernRunner ? RuntimeHelperPaths.Runner : RuntimeHelperPaths.LegacyJitInspector);
    // These arguments are consumed by the Windows dotnet host under Wine.  Keep
    // the host-side layout paths in Linux form, but pass mounted host files to
    // Wine using its explicit Z: mapping so resolution is independent of the
    // current working directory and Wine prefix drive aliases.
    var wineHelper = WineZPath(helper);
    var wineDotnet = WineZPath(TargetWineDotnet());
    // Pin the framework explicitly on Linux too. The helper targets
    // netcoreapp2.0 so one binary can run across the matrix, but selecting the
    // target runtime must not depend on the host's version-specific default
    // roll-forward policy.
    var runArgs = usesModernRunner
        ? new JsonArray(helper, "{entryAssembly}", "--", "{arguments}") : new JsonArray("exec", "--fx-version", version, helper, "--runtime-version", version, "run", "{entryAssembly}", "--", "{arguments}");
    var operations = new JsonObject { ["run"] = Operation(runImplementation, platform == "wine" ? "wine-z" : "unix", platform == "wine" ? WineX64Host() : TargetDotnet(platform), platform == "wine" ? new JsonArray(wineDotnet, "exec", "--fx-version", version, wineHelper, "--runtime-version", version, "run", "{entryAssembly}", "--", "{arguments}") : runArgs) };
    if (Strings(capability, "capabilities").Contains("jit-asm", StringComparer.Ordinal))
    {
        var checkedJit = platform == "linux" ? target["checkedJit"]?.AsObject() : null;
        var profilerProvider = platform == "linux" ? target["profilerProvider"]?.AsObject() : null;
        if (checkedJit is not null && profilerProvider is not null)
            throw new InvalidDataException($"Runtime '{runtimeId}' cannot select Checked JIT and the Linux profiler provider together.");
        var jitImplementation = promotion?.JitImplementation ??
            (checkedJit is not null ? RuntimeOperationImplementations.CheckedJitBridge : profilerProvider is not null ? RuntimeOperationImplementations.JitInspector : RuntimeOperationImplementations.LegacyJitInspector);
        if (string.Equals(jitImplementation, RuntimeOperationImplementations.JitInspector, StringComparison.Ordinal))
        {
            var jitAssemblyPath = promotion?.JitAssemblyPath ?? RuntimeHelperPaths.JitInspector;
            var sourceMappingKind = promotion?.JitSourceMappingKind ??
                RequiredString(profilerProvider ?? throw new InvalidDataException($"Runtime '{runtimeId}' selects the modern JIT inspector without a profilerProvider lock."), "sourceMappingKind");
            operations["jit"] = Operation(jitImplementation, "unix", TargetDotnet(platform), new JsonArray(jitAssemblyPath, "{entryAssembly}", "{methodFilter}"), jit: true, sourceMappingKind: sourceMappingKind);
            operations["jit"]!["profilerPath"] = promotion?.JitProfilerPath ?? RuntimeHelperPaths.JitProfiler;
        }
        else if (string.Equals(jitImplementation, RuntimeOperationImplementations.CheckedJitBridge, StringComparison.Ordinal))
        {
            if (platform != "linux")
                throw new InvalidDataException("The Checked-JIT bridge supports only Linux CoreCLR candidates.");
            var jitAssemblyPath = promotion?.JitAssemblyPath ?? RuntimeHelperPaths.CheckedJitBridge;
            var sourceMappingKind = promotion?.JitSourceMappingKind ??
                RequiredString(checkedJit ?? throw new InvalidDataException($"Runtime '{runtimeId}' selects the Checked-JIT bridge without a checkedJit source lock."), "sourceMappingKind");
            operations["jit"] = Operation(RuntimeOperationImplementations.CheckedJitBridge, "unix", TargetDotnet(platform), new JsonArray(jitAssemblyPath, "jit", "{entryAssembly}", "{methodFilter}"), jit: true, sourceMappingKind: sourceMappingKind);
        }
        else
        {
            var jitAssemblyPath = promotion?.JitAssemblyPath ?? helper;
            var wineJitAssemblyPath = WineZPath(jitAssemblyPath);
            operations["jit"] = Operation(
                RuntimeOperationImplementations.LegacyJitInspector,
                platform == "wine" ? "wine-z" : "unix",
                platform == "wine" ? WineX64Host() : TargetDotnet(platform),
                platform == "wine"
                    ? new JsonArray(wineDotnet, "exec", "--fx-version", version, wineJitAssemblyPath, "--runtime-version", version, "jit", "{entryAssembly}", "{methodFilter}") : new JsonArray("exec", "--fx-version", version, jitAssemblyPath, "--runtime-version", version, "jit", "{entryAssembly}", "{methodFilter}"),
                jit: true,
                sourceMappingKind: "none");
        }
    }
    var isWine = platform == "wine";
    var jitHelper = operations.ContainsKey("jit")
        ? operations["jit"]?["implementationId"]?.GetValue<string>() switch
        {
            RuntimeOperationImplementations.JitInspector => RuntimeHelperPaths.JitInspector,
            RuntimeOperationImplementations.CheckedJitBridge => RuntimeHelperPaths.CheckedJitBridge,
            _ => RuntimeHelperPaths.LegacyJitInspector
        }
        : null;
    return ProfileBase(runtimeId, target, platform == "wine" ? "coreclr-wine" : "coreclr",
        version, new[] { "dotnet-managed-pe-v1" }, operations,
        effectiveCapabilities,
        isWine
            ? WineContainer("/opt/wine-dotnet", RequiredString(capability, "executionUser")) : StandardContainer("coreclr"),
        isWine ? WineLayout(TargetWineDotnet(), helper, "/opt/wine-dotnet", "wine-coreclr") : DotnetLayout(TargetDotnet(platform), helper, jitHelper),
        acceptedFrameworkName: "Microsoft.NETCore.App",
        acceptedFrameworkVersion: version,
        acceptedFrameworkMinimumVersion: referenceVersion,
        acceptedRuntimeFamilies: AcceptedRuntimeFamilies(platform == "wine" ? "coreclr-wine" : "coreclr"),
        securityPolicyId: "runtime-job-default",
        promotion: promotion);
}

static JsonObject CreateFrameworkProfile(JsonObject target, string runtimeId, JsonObject capability, PromotionBinding? promotion)
{
    var version = RequiredString(target, "version");
    var effectiveCapabilities = EffectiveCapabilities(capability, runtimeId, "framework", RuntimeOperationImplementations.TargetRuntimeRunner);
    var prefix = string.Equals(RequiredString(target, "clrGeneration"), "clr2", StringComparison.Ordinal)
        ? "/opt/wine-netfx-clr2" : "/opt/wine-netfx-clr4";
    var implementation = promotion?.RunImplementation ?? RuntimeOperationImplementations.TargetRuntimeRunner;
    var runner = promotion?.RunAssemblyPath ?? RuntimeHelperPaths.TargetRuntimeRunner;
    var command = new JsonArray(WineZPath(runner), "run", "{entryAssembly}", "--", "{arguments}");
    var operations = new JsonObject { ["run"] = Operation(implementation, "wine-z", WineX64Host(), command) };
    string? jitInspector = null;
    if (effectiveCapabilities.Contains("jit-asm", StringComparer.Ordinal))
    {
        var jitImplementation = promotion?.JitImplementation ??
            RuntimeOperationImplementations.DesktopClrJitInspector;
        jitInspector = promotion?.JitAssemblyPath ?? RuntimeHelperPaths.WineRunner;
        var mappingKind = promotion?.JitSourceMappingKind ?? "none";
        if (!string.Equals(jitImplementation, RuntimeOperationImplementations.DesktopClrJitInspector, StringComparison.Ordinal) || !string.Equals(jitInspector, RuntimeHelperPaths.WineRunner, StringComparison.Ordinal) || !string.Equals(mappingKind, "none", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Framework runtime '{runtimeId}' requires the bounded Desktop CLR JIT provider with sourceMappingKind=none.");
        }
        operations["jit"] = Operation(jitImplementation, "unix", "/usr/share/dotnet/dotnet", new JsonArray(RuntimeHelperPaths.WineRunner, "desktop-jit", "{entryAssembly}", "{methodFilter}"), jit: true, sourceMappingKind: mappingKind);
    }
    var formats = string.Equals(RequiredString(target, "id"), "netfx48", StringComparison.Ordinal)
        ? new[] { "dotnet-framework-managed-pe-v1", "dotnet-framework-mixed-pe-v1" }
        : new[] { "dotnet-framework-managed-pe-v1" };
    return ProfileBase(runtimeId, target, "netfx-clr-wine", version, formats, operations, effectiveCapabilities, WineContainer(prefix, "0:0"), WineLayout(WineX64Host(), runner, prefix, "wine-netfx", jitInspector), acceptedFrameworkName: ".NETFramework", acceptedFrameworkVersion: version, acceptedRuntimeFamilies: AcceptedRuntimeFamilies("netfx-clr-wine"), providedRuntimeFeatureTags: RuntimeFeatureTags(RequiredString(target, "id"), "netfx-clr-wine"), securityPolicyId: "runtime-job-wine-netfx", promotion: promotion);
}

static JsonObject CreateMonoProfile(JsonObject target, string runtimeId, JsonObject capability, PromotionBinding? promotion)
{
    var effectiveCapabilities = EffectiveCapabilities(capability, runtimeId, "mono", RuntimeOperationImplementations.TargetRuntimeRunner);
    var implementation = promotion?.RunImplementation ?? RuntimeOperationImplementations.TargetRuntimeRunner;
    var runner = promotion?.RunAssemblyPath ?? RuntimeHelperPaths.TargetRuntimeRunner;
    var operations = new JsonObject { ["run"] = Operation(implementation, "unix", "/usr/bin/mono", new JsonArray(runner, "run", "{entryAssembly}", "--", "{arguments}")) };
    if (effectiveCapabilities.Contains("jit-asm", StringComparer.Ordinal))
    {
        var jitImplementation = promotion?.JitImplementation ??
            RuntimeOperationImplementations.MonoJitInspector;
        var jitAssemblyPath = promotion?.JitAssemblyPath ?? RuntimeHelperPaths.MonoJitInspector;
        var mappingKind = promotion?.JitSourceMappingKind ?? "none";
        if (!string.Equals(jitImplementation, RuntimeOperationImplementations.MonoJitInspector, StringComparison.Ordinal) || !string.Equals(mappingKind, "none", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Mono runtime '{runtimeId}' requires the bounded Mono JIT inspector with sourceMappingKind=none.");
        }
        operations["jit"] = Operation(jitImplementation, "unix", "/usr/share/dotnet/dotnet", new JsonArray(jitAssemblyPath, "{entryAssembly}", "{methodFilter}"), jit: true, sourceMappingKind: mappingKind);
    }
    return ProfileBase(runtimeId, target, "mono", RequiredString(target, "version"), new[] { "dotnet-framework-managed-pe-v1" }, operations,
        effectiveCapabilities,
        StandardContainer("mono"),
        DotnetLayout("/usr/bin/mono", runner, RuntimeHelperPaths.MonoJitInspector),
        acceptedFrameworkName: ".NETFramework",
        acceptedFrameworkVersion: "4.8",
        acceptedRuntimeFamilies: AcceptedRuntimeFamilies("mono"),
        securityPolicyId: "runtime-job-default",
        promotion: promotion);
}

static JsonObject ProfileBase(string id, JsonObject target, string family, string version, IEnumerable<string> formats, JsonObject operations, IReadOnlyList<string> declaredCapabilities, JsonObject container, JsonObject layout, string? acceptedFrameworkName = null, string? acceptedFrameworkVersion = null, string? acceptedFrameworkMinimumVersion = null, IReadOnlyList<string>? acceptedRuntimeFamilies = null, JsonArray? providedRuntimeFeatureTags = null, string securityPolicyId = "runtime-job-default", PromotionBinding? promotion = null)
{
    var payload = PlatformPayload(target, string.Equals(family, "coreclr-wine", StringComparison.Ordinal) ? "wine" : "linux");
    var hash = payload?["sha512"]?.GetValue<string>() ?? target["capability"]?["promotionState"]?.GetValue<string>() ?? "unverified";
    var acceptedFrameworks = acceptedFrameworkName is null
        ? new JsonArray() : new JsonArray(AcceptedFramework(acceptedFrameworkName, acceptedFrameworkMinimumVersion, acceptedFrameworkVersion));
    var profile = new JsonObject
    {
        ["schemaVersion"] = 1,
        ["id"] = id,
        ["image"] = promotion?.ImageReference ?? RuntimeImageReference(id),
        ["family"] = family,
        ["acceptedRuntimeFamilies"] = new JsonArray((acceptedRuntimeFamilies ?? AcceptedRuntimeFamilies(family)).Select(static value => JsonValue.Create(value)).ToArray()),
        ["acceptedFrameworks"] = acceptedFrameworks,
        ["runtimeVersion"] = version,
        ["runtimeCommit"] = promotion?.RuntimeCommit ?? (IsOperatorRuntimeFamily(family) ? "not-applicable" : target["runtimeCommit"]?.GetValue<string>() ?? $"payload-sha512:{hash}"),
        ["jitVersion"] = promotion?.JitVersion ?? (IsOperatorRuntimeFamily(family) ? "not-applicable" : version),
        ["jitCommit"] = promotion?.JitCommit ?? (IsOperatorRuntimeFamily(family) ? "not-applicable" : target["jitCommit"]?.GetValue<string>() ?? $"payload-sha512:{hash}"),
        ["runtimeImageId"] = promotion?.ImageId ?? RuntimeImageReference(id),
        ["rid"] = "linux-x64",
        ["architecture"] = "x64",
        ["cpuFeatureProfile"] = "x64-v2",
        ["acceptedArtifactFormats"] = new JsonArray(formats.Select(static value => JsonValue.Create(value)).ToArray()),
        // Operations are always present in the candidate profile, even while
        // the matrix row is blocked. Instrumentation capabilities are added
        // only by EffectiveCapabilities after their explicit evidence gate.
        ["capabilities"] = new JsonArray(declaredCapabilities.Concat(operationNames(operations)).Distinct(StringComparer.Ordinal).Select(static value => JsonValue.Create(value)).ToArray()),
        ["providedRuntimeFeatureTags"] = providedRuntimeFeatureTags ?? RuntimeFeatureTags(id, family),
        ["providedMetadataFeatureTags"] = new JsonArray(),
        ["allowedSecurityPolicyIds"] = new JsonArray(securityPolicyId),
        ["container"] = container,
        ["operations"] = operations,
        ["layout"] = layout,
        ["securityPolicies"] = new JsonArray(SecurityPolicy(securityPolicyId))
    };

    if (promotion is not null)
    {
        profile["promotionReceipt"] = new JsonObject { ["path"] = promotion.ReceiptPath, ["sha256"] = promotion.ReceiptSha256 };
    }

    return profile;

    static IEnumerable<string> operationNames(JsonObject value)
    {
        if (value.ContainsKey("run")) yield return "run";
        if (value.ContainsKey("jit")) yield return "jit-asm";
    }
}

static JsonObject? PlatformPayload(JsonObject target, string platform)
{
    return platform switch
    {
        "wine" => target["windows"]?.AsObject(),
        "linux" => target["linux"]?.AsObject(),
        _ => target["linux"]?.AsObject() ?? target["windows"]?.AsObject()
    };
}

static JsonObject Operation(string implementationId, string pathStyle, string executable, JsonArray argv, bool jit = false, string sourceMappingKind = "none")
{
    var operation = new JsonObject { ["implementationId"] = implementationId, ["pathStyle"] = pathStyle, ["command"] = new JsonObject { ["executable"] = executable, ["argv"] = argv } };
    if (jit)
        operation["sourceMappingKind"] = sourceMappingKind;
    return operation;
}

static JsonObject StandardContainer(string environment) => new()
{
    ["isolationKind"] = "standard",
    ["environmentKind"] = environment,
    ["executionUser"] = "1654:1654"
};

static JsonObject WineContainer(string prefix, string executionUser)
{
    if (executionUser is not ("0:0" or "1654:1654"))
    {
        throw new InvalidDataException($"Wine execution user '{executionUser}' is not one of the closed runtime identities.");
    }
    return new JsonObject { ["isolationKind"] = "wine", ["environmentKind"] = "wine", ["executionUser"] = executionUser, ["winePrefixPath"] = prefix };
}

static JsonObject DotnetLayout(string host, string helper, string? jitInspector = null)
{
    var layout = new JsonObject { ["runnerKind"] = "dotnet", ["dotNetHostPath"] = host, ["runnerAssemblyPath"] = helper };
    if (jitInspector is not null)
        layout["jitInspectorAssemblyPath"] = jitInspector;
    return layout;
}

static JsonObject WineLayout(string host, string runner, string prefix, string runnerKind, string? jitInspector = null)
{
    var layout = new JsonObject { ["runnerKind"] = runnerKind, ["dotNetHostPath"] = host, ["wineHostPath"] = WineX64Host(), ["winePrefixPath"] = prefix, ["runnerAssemblyPath"] = runner };
    if (jitInspector is not null)
        layout["jitInspectorAssemblyPath"] = jitInspector;
    return layout;
}

static JsonObject SecurityPolicy(string id) => id switch
{
    // Keep this definition byte-for-byte aligned with the supervisor's
    // default policy. Matrix candidates must not silently widen the budget.
    "runtime-job-default" => new JsonObject { ["id"] = id, ["memoryBytes"] = 268435456, ["nanoCpus"] = 1000000000, ["pidsLimit"] = 64, ["maximumDurationSeconds"] = 10, ["maximumArtifactBytes"] = 67108864, ["maximumOutputBytes"] = 1048576, ["tmpfsBytes"] = 33554432 },
    // Wine + the outer .NET control process + Desktop CLR JIT exceed 64 Linux
    // tasks because the pids cgroup also counts managed threads.
    "runtime-job-wine-netfx" => new JsonObject { ["id"] = id, ["memoryBytes"] = 1073741824, ["nanoCpus"] = 1000000000, ["pidsLimit"] = 128, ["maximumDurationSeconds"] = 30, ["maximumArtifactBytes"] = 67108864, ["maximumOutputBytes"] = 1048576, ["tmpfsBytes"] = 33554432 },
    "runtime-job-wine-jsharp20" => new JsonObject { ["id"] = id, ["memoryBytes"] = 1073741824, ["nanoCpus"] = 1000000000, ["pidsLimit"] = 64, ["maximumDurationSeconds"] = 30, ["maximumArtifactBytes"] = 67108864, ["maximumOutputBytes"] = 1048576, ["tmpfsBytes"] = 33554432 },
    _ => throw new InvalidDataException($"Unknown runtime security policy '{id}'.")
};

static IReadOnlyList<string> AcceptedRuntimeFamilies(string family) => family switch
{
    // Managed artifacts produced by the normal Roslyn/IL pipeline declare
    // 'coreclr'; a Wine host is an execution-compatible implementation of
    // that family, but must still retain its own family for validation.
    "coreclr-wine" => ["coreclr-wine", "coreclr"],
    // Mono consumes the existing .NET Framework managed-PE contract. Keep
    // 'mono' explicit while accepting that producer family as an alias.
    "mono" => ["mono", "netfx-clr-wine"],
    _ => [family]
};

static JsonArray RuntimeFeatureTags(string targetId, string family) =>
    family == "netfx-clr-wine" &&
    targetId.Contains("netfx48", StringComparison.OrdinalIgnoreCase)
        ? new JsonArray("runtime.netfx48-wine")
        : new JsonArray();

static JsonArray AcceptedFrameworks(JsonObject target, string platform, string version)
{
    if (platform == "mono")
    {
        return new JsonArray(new JsonObject { ["name"] = ".NETFramework", ["exactVersion"] = "4.8" });
    }

    if (platform == "framework")
    {
        return new JsonArray(new JsonObject { ["name"] = ".NETFramework", ["exactVersion"] = version });
    }

    var referenceVersion = target["referencePackage"]?.AsObject()?["version"]?.GetValue<string>() ?? version;
    return new JsonArray(AcceptedFramework("Microsoft.NETCore.App", referenceVersion, version));
}

static JsonObject AcceptedFramework(string name, string? minimumVersion, string? maximumVersion)
{
    if (string.IsNullOrWhiteSpace(minimumVersion) || string.Equals(minimumVersion, maximumVersion, StringComparison.Ordinal))
    {
        return new JsonObject { ["name"] = name, ["exactVersion"] = maximumVersion ?? minimumVersion };
    }

    return new JsonObject { ["name"] = name, ["minimumVersion"] = minimumVersion, ["maximumVersion"] = maximumVersion };
}

static JsonObject Availability(bool installed, string reason)
{
    var availability = new JsonObject { ["installed"] = installed, ["health"] = installed ? "healthy" : "not-installed" };
    if (!installed)
        availability["reason"] = reason;
    return availability;
}

static void ApplyLifecycle(JsonObject output, JsonObject target)
{
    output["supportStatus"] = RequiredString(target, "supportStatus");
    if (target["supportEndDate"] is JsonValue supportEndDate)
        output["supportEndDate"] = supportEndDate.DeepClone();
    output["visibility"] = RequiredString(target, "visibility");
}

static bool IsVerified(JsonObject capability) => string.Equals(RequiredString(capability, "promotionState"), "verified", StringComparison.Ordinal);

static PromotionBinding? LoadPromotionBinding(string repositoryRoot, JsonObject capability, string profileId)
{
    if (!IsVerified(capability))
        return null;

    var reference = RequiredObject(capability, "promotionReceipt");
    var relativePath = RequiredString(reference, "path");
    var expectedPath = $"profiles/runtime-promotion-receipts/{profileId}.json";
    if (!string.Equals(relativePath, expectedPath, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Runtime promotion receipt for '{profileId}' must use canonical path '{expectedPath}'.");
    }

    var absolutePath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    var allowedDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, "profiles", "runtime-promotion-receipts"));
    if (!absolutePath.StartsWith(allowedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"Runtime promotion receipt for '{profileId}' escapes its evidence directory.");

    var bytes = File.ReadAllBytes(absolutePath);
    var actualDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    var expectedDigest = RequiredString(reference, "sha256");
    if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Runtime promotion receipt for '{profileId}' changed after validation; " + $"expected {expectedDigest}, observed {actualDigest}.");
    }

    var receipt = JsonNode.Parse(bytes)?.AsObject()
        ?? throw new InvalidDataException($"Runtime promotion receipt for '{profileId}' is not a JSON object.");
    if (!string.Equals(RequiredString(receipt, "profileId"), profileId, StringComparison.Ordinal))
        throw new InvalidDataException($"Runtime promotion receipt profile identity does not match '{profileId}'.");

    var image = RequiredObject(receipt, "image");
    var runtimeIdentity = RequiredObject(receipt, "runtimeIdentity");
    var operations = RequiredObject(receipt, "operations");
    var run = RequiredObject(operations, "run");
    var jit = operations["jit"] as JsonObject;
    var jitCheck = RequiredArray(receipt, "checks").OfType<JsonObject>().SingleOrDefault(check => string.Equals(check["capability"]?.GetValue<string>(), "jit-asm", StringComparison.Ordinal));
    return new PromotionBinding(
        relativePath,
        expectedDigest,
        RequiredString(image, "reference"),
        RequiredString(image, "imageId"),
        RequiredString(runtimeIdentity, "runtimeCommit"),
        RequiredString(runtimeIdentity, "jitVersion"),
        RequiredString(runtimeIdentity, "jitCommit"),
        RequiredString(run, "implementation"),
        RequiredString(run, "assemblyPath"),
        RequiredSha256(run, "assemblySha256"),
        jit is null ? null : RequiredString(jit, "implementation"),
        jit is null ? null : RequiredString(jit, "assemblyPath"),
        jit is null ? null : RequiredSha256(jit, "assemblySha256"),
        jit?["profilerPath"]?.GetValue<string>(),
        jit?["profilerSha256"] is null ? null : RequiredSha256(jit, "profilerSha256"),
        jitCheck is null ? null : RequiredString(jitCheck, "sourceMappingKind"),
        jitCheck is null ? null : RequiredString(jitCheck, "mappingSource"));
}

static string ResolveRunImplementation(JsonObject target, JsonObject capability, string platform, PromotionBinding? promotion)
{
    if (platform is "mono" or "framework")
        return RuntimeOperationImplementations.TargetRuntimeRunner;
    if (platform == "wine")
        return RuntimeOperationImplementations.LegacyJitInspector;

    var hasInstrumentation = capability["instrumentationCapabilities"]?.AsArray().Count > 0;
    var hasProfilerProvider = platform == "linux" && target["profilerProvider"] is JsonObject;
    return hasInstrumentation ||
        hasProfilerProvider ||
        string.Equals(promotion?.JitSourceMappingKind, "linux-profiler", StringComparison.Ordinal) ||
        string.Equals(promotion?.RunImplementation, RuntimeOperationImplementations.Runner, StringComparison.Ordinal)
        ? RuntimeOperationImplementations.Runner
        : RuntimeOperationImplementations.LegacyJitInspector;
}

static IReadOnlyList<string> EffectiveCapabilities(JsonObject capability, string targetId, string platform, string runImplementationId)
{
    var declared = Strings(capability, "capabilities");
    var verified = IsVerified(capability);
    var instrumentation = capability["instrumentationCapabilities"]?.AsArray().Select(static item => item?.GetValue<string>() ?? throw new InvalidDataException("Instrumentation capability entries cannot be null.")).ToArray() ?? [];

    var supportedInstrumentation = new HashSet<string>(instrumentation, StringComparer.Ordinal);
    if (supportedInstrumentation.Count != instrumentation.Length)
    {
        throw new InvalidDataException($"Runtime matrix target '{targetId}' declares duplicate instrumentation capabilities.");
    }

    var known = new HashSet<string>(["run", "jit-asm", "inspection", "execution-flow"], StringComparer.Ordinal);
    var unknown = declared.Where(capabilityId => !known.Contains(capabilityId)).ToArray();
    if (unknown.Length > 0)
    {
        throw new InvalidDataException($"Runtime matrix target '{targetId}' declares unsupported capability(s): {string.Join(", ", unknown)}.");
    }

    var undeclaredEvidence = supportedInstrumentation.Where(capabilityId => !declared.Contains(capabilityId, StringComparer.Ordinal)).ToArray();
    if (undeclaredEvidence.Length > 0)
    {
        throw new InvalidDataException($"Runtime matrix target '{targetId}' provides instrumentation evidence for undeclared capability(s): {string.Join(", ", undeclaredEvidence)}.");
    }

    if (supportedInstrumentation.Count > 0 && platform != "linux")
    {
        throw new InvalidDataException($"Runtime matrix target '{targetId}' declares instrumentation evidence on unsupported platform '{platform}'.");
    }

    var declaredInstrumentation = declared.Where(IsInstrumentationCapability).ToArray();
    var missingEvidence = declaredInstrumentation.Where(capabilityId => !supportedInstrumentation.Contains(capabilityId)).ToArray();
    if (missingEvidence.Length > 0 && verified)
    {
        throw new InvalidDataException($"Runtime matrix target '{targetId}' is verified but instrumentation capability(s) lack explicit retained-image evidence: {string.Join(", ", missingEvidence)}.");
    }

    // A blocked row can retain aspirational capabilities in the source matrix,
    // but they must not leak into a generated selectable Catalog entry. Once a
    // row is verified, the evidence list is the allowlist for instrumentation.
    var effective = declared.Where(capabilityId => !IsInstrumentationCapability(capabilityId) || (verified && supportedInstrumentation.Contains(capabilityId))).ToArray();
    if (effective.Any(IsInstrumentationCapability) && !StringComparer.Ordinal.Equals(runImplementationId, RuntimeOperationImplementations.Runner))
    {
        throw new InvalidDataException($"Runtime matrix target '{targetId}' cannot expose instrumentation through Run implementation '{runImplementationId}'; '{RuntimeOperationImplementations.Runner}' is required.");
    }
    return effective;
}

static bool IsInstrumentationCapability(string capability) => capability is "inspection" or "execution-flow";

static string BlockReason(JsonObject capability, string targetId) => capability["blockedReason"]?.GetValue<string>() ?? $"Runtime matrix target '{targetId}' has not passed product preflight.";

static string TargetFramework(JsonObject target, string targetId) => target["targetFramework"]?.GetValue<string>() ?? TargetFrameworkFromChannel(target, targetId);

static string TargetFrameworkFromChannel(JsonObject target, string targetId)
{
    var channel = RequiredString(target, "channel");
    if (targetId.StartsWith("dotnet-core-", StringComparison.Ordinal))
        return $"netcoreapp{channel}";
    return channel.EndsWith(".0", StringComparison.Ordinal) ? $"net{channel}" : $"net{channel}.0";
}

static string TargetDotnet(string platform) => platform == "linux" ? "/opt/sharplabnext/target-dotnet/dotnet" : throw new InvalidOperationException("Unknown CoreCLR platform.");

static string TargetWineDotnet() => "/opt/wine-dotnet/drive_c/dotnet/dotnet.exe";

// The distro wine-stable dispatcher prefers /usr/lib/wine/wine whenever that
// loader exists. In an intentionally x64-only image the corresponding i386
// payload is absent, so that path fails before it can load kernel32.dll. Use
// Wine's explicit x64 loader for every generated runtime operation.
static string WineX64Host() => "/usr/lib/wine/wine64";

// Candidate image names are derived from the profile ID in exactly the same
// way as the generic Bake targets. Published runtime-dotnet10/runtime-dotnet11
// images remain separate active profiles and must not leak into matrix output.
static string RuntimeImageReference(string id) => $"sharplabnext/runtime-{id}:candidate";

static bool IsOperatorRuntimeFamily(string family) => family is "mono" or "netfx-clr-wine";

static string WineZPath(string unixPath)
{
    if (string.IsNullOrWhiteSpace(unixPath) || !unixPath.StartsWith('/'))
        throw new InvalidDataException($"Wine Z: path must be an absolute Unix path: '{unixPath}'.");
    return $"Z:{unixPath.Replace('/', '\\')}";
}

static IReadOnlyList<string> Strings(JsonObject value, string name) => value[name]?.AsArray().Select(static item => item!.GetValue<string>()).ToArray() ?? throw new InvalidDataException($"Matrix property '{name}' is missing.");

static JsonArray RequiredArray(JsonObject value, string name) => value[name]?.AsArray() ?? throw new InvalidDataException($"Required array '{name}' is missing.");

static JsonObject RequiredObject(JsonObject value, string name) => value[name]?.AsObject() ?? throw new InvalidDataException($"Required object '{name}' is missing.");

static string RequiredString(JsonObject value, string name) => value[name]?.GetValue<string>() is { Length: > 0 } result
    ? result : throw new InvalidDataException($"Required string '{name}' is missing.");

static string RequiredSha256(JsonObject value, string name)
{
    var result = RequiredString(value, name);
    if (result.Length != 71 || !result.StartsWith("sha256:", StringComparison.Ordinal) || result.AsSpan(7).ToArray().Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
    {
        throw new InvalidDataException($"Required digest '{name}' must be sha256:<64 lowercase hex>.");
    }
    return result;
}

static async Task ValidatePromotionReceiptsAsync(string repositoryRoot, string matrixPath)
{
    var validatorPath = Path.Combine(repositoryRoot, "eng", "release", "runtime-promotion-receipt-validation.mjs");
    if (!File.Exists(validatorPath))
        throw new FileNotFoundException("Runtime promotion receipt validator is missing.", validatorPath);

    var startInfo = new ProcessStartInfo("node")
    {
        WorkingDirectory = repositoryRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add(validatorPath);
    startInfo.ArgumentList.Add("--repository-root");
    startInfo.ArgumentList.Add(repositoryRoot);
    startInfo.ArgumentList.Add("--matrix");
    startInfo.ArgumentList.Add(matrixPath);

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the runtime promotion receipt validator.");
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = (await standardOutput).Trim();
    var error = (await standardError).Trim();
    if (process.ExitCode != 0)
    {
        throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? $"Runtime promotion receipt validation failed with exit code {process.ExitCode}." : error);
    }
    if (!string.IsNullOrWhiteSpace(output))
        Console.WriteLine(output);
}

static void WriteProfile(string directory, JsonObject profile, Options options, List<string> generated, IReadOnlySet<string> activeProfileIds)
{
    Directory.CreateDirectory(directory);
    var id = profile["id"]!.GetValue<string>();
    var path = Path.Combine(directory, id + ".json");
    generated.Add(path);
    if (options.Check)
        return;
    // Candidate output is intentionally separate from the active profile
    // directory.  An active Catalog ID must still receive a candidate file so
    // it can be reviewed/promoted atomically; only an explicit write directly
    // to profiles/runtimes is protected by the active-ID guard.
    var activeDirectory = Path.GetFullPath(Path.Combine(options.RepositoryRoot, "profiles", "runtimes"));
    var writingActiveDirectory = string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), Path.TrimEndingDirectorySeparator(activeDirectory), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    if (writingActiveDirectory && activeProfileIds.Contains(id) && !options.AllowActiveProfileOverwrite)
    {
        // A refresh can discover a newer candidate for the same logical ID,
        // but must not replace an active top-level profile in-place. The
        // candidate directory is the normal path; this guard also protects
        // callers that explicitly pass profiles/runtimes as the destination.
        Console.WriteLine($"Preserved active runtime profile '{id}'; candidate was not written to the active directory.");
        return;
    }
    if (File.Exists(path) && !options.OverwriteProfiles)
        return;
    var profileJsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n" };
    File.WriteAllBytes(path, SerializeJsonWithLf(profile, profileJsonOptions));
}

static byte[] SerializeJsonWithLf(JsonNode node, JsonSerializerOptions options)
{
    var json = node.ToJsonString(options).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    if (!json.EndsWith('\n'))
        json += "\n";
    return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(json);
}

static HashSet<string> ReadTopLevelProfileIds(string directory)
{
    if (!Directory.Exists(directory))
        return new HashSet<string>(StringComparer.Ordinal);

    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
    {
        try
        {
            var document = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            var id = document?["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }
        catch (JsonException)
        {
            // The schema validator reports malformed active profiles. Do not
            // let a malformed file disable candidate generation here.
        }
    }
    return ids;
}

static HashSet<string> ReadSelectableCatalogRuntimeIds(JsonArray runtimes)
{
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var item in runtimes)
    {
        var runtime = item?.AsObject();
        var availability = runtime?["availability"]?.AsObject();
        if (availability?["installed"]?.GetValue<bool>() == true &&
            string.Equals(availability["health"]?.GetValue<string>(), "healthy", StringComparison.Ordinal) &&
            runtime?["id"]?.GetValue<string>() is { Length: > 0 } id)
        {
            ids.Add(id);
        }
    }
    return ids;
}

sealed record ReferenceIdentity(string ResolvedVersion, string Digest);

sealed record ResolvedCompositionSource(string Role, string Selection, JsonObject Package);

sealed record Options(string RepositoryRoot, string? MatrixPath, string? CatalogPath, string? ProfileDirectory, bool OverwriteProfiles, bool AllowActiveProfileOverwrite, bool Check)
{
    public static Options Parse(string[] args)
    {
        var root = Directory.GetCurrentDirectory();
        string? matrix = null, catalog = null, profiles = null;
        var overwrite = false;
        var allowActiveOverwrite = false;
        var check = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--repository-root": root = Next(args, ref index); break;
                case "--matrix": matrix = Next(args, ref index); break;
                case "--catalog": catalog = Next(args, ref index); break;
                case "--profiles": profiles = Next(args, ref index); break;
                case "--overwrite-profiles": overwrite = true; break;
                case "--allow-active-profile-overwrite": allowActiveOverwrite = true; break;
                case "--check": check = true; break;
                case "-h" or "--help":
                    Console.WriteLine("Usage: dotnet run eng/tools/generate-runtime-matrix.cs -- [--repository-root PATH] [--check] [--overwrite-profiles] [--allow-active-profile-overwrite]");
                    Environment.Exit(0);
                    break;
                default: throw new ArgumentException($"Unknown option '{args[index]}'.");
            }
        }
        if (allowActiveOverwrite && !overwrite)
            throw new ArgumentException("--allow-active-profile-overwrite requires --overwrite-profiles.");
        return new Options(root, matrix, catalog, profiles, overwrite, allowActiveOverwrite, check);
    }

    private static string Next(string[] args, ref int index) => ++index < args.Length && !string.IsNullOrWhiteSpace(args[index])
            ? args[index] : throw new ArgumentException("An option value is missing.");
}

static class RuntimeOperationImplementations
{
    public const string Runner = "sharplabnext-runner-v1";
    public const string JitInspector = "sharplabnext-jit-inspector-v1";
    public const string LegacyJitInspector = "sharplabnext-legacy-jit-inspector-v1";
    public const string CheckedJitBridge = "sharplabnext-checked-jit-bridge-v1";
    public const string MonoJitInspector = "sharplabnext-mono-jit-inspector-v1";
    public const string DesktopClrJitInspector = "sharplabnext-desktop-clr-jit-inspector-v1";
    public const string WineRunner = "sharplabnext-wine-runner-v1";
    public const string TargetRuntimeRunner = "sharplabnext-target-runtime-runner-v1";
}

static class RuntimeHelperPaths
{
    public const string Runner = "/opt/sharplabnext/SharpLabNext.Runner.dll";
    public const string JitInspector = "/opt/sharplabnext/SharpLabNext.JitInspector.dll";
    public const string LegacyJitInspector = "/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll";
    public const string CheckedJitBridge = "/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll";
    public const string MonoJitInspector = "/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll";
    public const string WineRunner = "/opt/sharplabnext/SharpLabNext.WineRunner.dll";
    public const string TargetRuntimeRunner = "/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe";
    public const string JitProfiler = "/opt/sharplabnext/SharpLabNext.JitProfiler.so";
}

sealed record PromotionBinding(
    string ReceiptPath,
    string ReceiptSha256,
    string ImageReference,
    string ImageId,
    string RuntimeCommit,
    string JitVersion,
    string JitCommit,
    string RunImplementation,
    string RunAssemblyPath,
    string RunAssemblySha256,
    string? JitImplementation,
    string? JitAssemblyPath,
    string? JitAssemblySha256,
    string? JitProfilerPath,
    string? JitProfilerSha256,
    string? JitSourceMappingKind,
    string? MappingSource);

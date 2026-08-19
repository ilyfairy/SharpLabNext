using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;

namespace SharpLabNext.Gateway;

public enum GatewayDependencyKind
{
    ArtifactStore,
    RuntimeSupervisor,
    LanguageWorker,
    ArtifactWorker
}

public sealed record GatewayDependencyTarget(
    string Id,
    GatewayDependencyKind Kind,
    Uri BaseAddress,
    string? ServiceToken);

public sealed record GatewayDependencyProbeResult(
    string Id,
    GatewayDependencyKind Kind,
    bool Ready,
    string? Reason,
    IReadOnlyDictionary<string, RuntimeProfileProbeIdentity> RuntimeProfiles)
{
    public IReadOnlySet<string> RuntimeProfileIds => RuntimeProfiles.Keys.ToHashSet(StringComparer.Ordinal);

    public static GatewayDependencyProbeResult Unavailable(
        GatewayDependencyTarget target,
        string reason) =>
        new(
            target.Id,
            target.Kind,
            false,
            reason,
            new Dictionary<string, RuntimeProfileProbeIdentity>(StringComparer.Ordinal));
}

public sealed record RuntimeProfileProbeIdentity(
    string Id,
    string Family,
    string RuntimeVersion,
    string Rid,
    string Architecture,
    IReadOnlySet<string> AcceptedArtifactFormats,
    IReadOnlySet<string> Capabilities,
    IReadOnlySet<string> ProvidedRuntimeFeatureTags,
    IReadOnlySet<string> ProvidedMetadataFeatureTags,
    string? RuntimeCommit = null,
    string? JitVersion = null,
    string? JitCommit = null,
    string? RuntimeImageId = null,
    IReadOnlySet<string>? AcceptedRuntimeFamilies = null,
    IReadOnlySet<string>? AcceptedFrameworks = null,
    string? ContainerIsolationKind = null,
    string? ContainerEnvironmentKind = null,
    string? JitSourceMappingKind = null);

public sealed record GatewayDependencySnapshot(
    CatalogDocument Catalog,
    IReadOnlyDictionary<string, GatewayDependencyProbeResult> Dependencies,
    DateTimeOffset ObservedAtUtc,
    bool Ready)
{
    public bool ArtifactStoreReady =>
        Dependencies.TryGetValue(GatewayDependencyHealthService.ArtifactStoreDependencyId, out var dependency) &&
        dependency.Ready;

    public bool RuntimeSupervisorReady =>
        Dependencies.TryGetValue(GatewayDependencyHealthService.RuntimeSupervisorDependencyId, out var dependency) &&
        dependency.Ready;
}

public sealed record GatewayDependencyHealthOptions(
    bool Enabled,
    Uri ArtifactStoreBaseAddress,
    Uri RuntimeSupervisorBaseAddress,
    TimeSpan CacheDuration,
    TimeSpan ProbeTimeout,
    string? ServiceToken = null)
{
    public void Validate()
    {
        if (CacheDuration <= TimeSpan.Zero || CacheDuration > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("DependencyHealth:CacheDuration must be between zero and one minute.");
        if (ProbeTimeout < TimeSpan.FromMilliseconds(100) || ProbeTimeout > TimeSpan.FromSeconds(30))
            throw new InvalidOperationException("DependencyHealth:ProbeTimeout must be between 100 ms and 30 seconds.");
    }
}

public interface IGatewayDependencyProbe
{
    Task<GatewayDependencyProbeResult> ProbeAsync(
        GatewayDependencyTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class HttpGatewayDependencyProbe(IHttpClientFactory httpClientFactory)
    : IGatewayDependencyProbe
{
    public async Task<GatewayDependencyProbeResult> ProbeAsync(
        GatewayDependencyTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var health = await SendAsync(target, "/health/ready", deadline.Token).ConfigureAwait(false);
            if (!health.IsSuccessStatusCode)
            {
                return GatewayDependencyProbeResult.Unavailable(
                    target,
                    $"Readiness probe returned HTTP {(int)health.StatusCode}.");
            }

            if (target.Kind != GatewayDependencyKind.RuntimeSupervisor)
            {
                return new GatewayDependencyProbeResult(
                    target.Id,
                    target.Kind,
                    true,
                    null,
                    new Dictionary<string, RuntimeProfileProbeIdentity>(StringComparer.Ordinal));
            }

            using var status = await SendAsync(target, "/api/v1/runtime/status", deadline.Token)
                .ConfigureAwait(false);
            if (!status.IsSuccessStatusCode)
            {
                return GatewayDependencyProbeResult.Unavailable(
                    target,
                    $"Runtime profile probe returned HTTP {(int)status.StatusCode}.");
            }

            await using var content = await status.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: deadline.Token)
                .ConfigureAwait(false);
            if (!TryReadRuntimeProfiles(document.RootElement, out var profiles))
            {
                return GatewayDependencyProbeResult.Unavailable(
                    target,
                    "Runtime profile probe returned malformed identity data.");
            }

            return new GatewayDependencyProbeResult(
                target.Id,
                target.Kind,
                true,
                null,
                profiles);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GatewayDependencyProbeResult.Unavailable(target, "Readiness probe timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return GatewayDependencyProbeResult.Unavailable(target, "Readiness probe failed.");
        }
    }

    private static bool TryReadRuntimeProfiles(
        JsonElement root,
        out IReadOnlyDictionary<string, RuntimeProfileProbeIdentity> profiles)
    {
        var parsed = new Dictionary<string, RuntimeProfileProbeIdentity>(StringComparer.Ordinal);
        profiles = parsed;
        if (!ContractJson.TryGetProperty(root, "Profiles", out var profileArray) ||
            profileArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var profile in profileArray.EnumerateArray())
        {
            var id = ContractJson.GetString(profile, "Id");
            var family = ContractJson.GetString(profile, "Family");
            var runtimeVersion = ContractJson.GetString(profile, "RuntimeVersion");
            var rid = ContractJson.GetString(profile, "Rid");
            var architecture = ContractJson.GetString(profile, "Architecture");
            var runtimeCommit = ContractJson.GetString(profile, "RuntimeCommit");
            var jitVersion = ContractJson.GetString(profile, "JitVersion");
            var jitCommit = ContractJson.GetString(profile, "JitCommit");
            var runtimeImageId = ContractJson.GetString(profile, "RuntimeImageId");
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(family) ||
                string.IsNullOrWhiteSpace(runtimeVersion) ||
                string.IsNullOrWhiteSpace(rid) ||
                string.IsNullOrWhiteSpace(architecture) ||
                !TryReadStringSet(profile, "AcceptedArtifactFormats", out var acceptedArtifactFormats) ||
                !TryReadStringSet(profile, "Capabilities", out var capabilities) ||
                !TryReadStringSet(profile, "ProvidedRuntimeFeatureTags", out var runtimeTags) ||
                !TryReadStringSet(profile, "ProvidedMetadataFeatureTags", out var metadataTags) ||
                !parsed.TryAdd(
                    id,
                    new RuntimeProfileProbeIdentity(
                        id,
                        family,
                        runtimeVersion,
                        rid,
                        architecture,
                        acceptedArtifactFormats,
                        capabilities,
                        runtimeTags,
                        metadataTags,
                        runtimeCommit,
                        jitVersion,
                        jitCommit,
                        runtimeImageId,
                        TryReadOptionalStringSet(profile, "AcceptedRuntimeFamilies"),
                        TryReadOptionalFrameworkSet(profile, "AcceptedFrameworks"),
                        TryReadContainerIdentity(profile),
                        TryReadContainerEnvironment(profile),
                        TryReadJitSourceMappingKind(profile))))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlySet<string>? TryReadOptionalStringSet(
        JsonElement parent,
        string propertyName)
    {
        if (!ContractJson.TryGetProperty(parent, propertyName, out _))
            return null;
        return TryReadStringSet(parent, propertyName, out var values) ? values : null;
    }

    private static HashSet<string>? TryReadOptionalFrameworkSet(
        JsonElement parent,
        string propertyName)
    {
        if (!ContractJson.TryGetProperty(parent, propertyName, out var array))
            return null;
        if (array.ValueKind != JsonValueKind.Array)
            return null;
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var framework in array.EnumerateArray())
        {
            if (framework.ValueKind != JsonValueKind.Object)
                return null;
            var name = ContractJson.GetString(framework, "Name");
            var minimum = ContractJson.GetString(framework, "MinimumVersion") ?? string.Empty;
            var maximum = ContractJson.GetString(framework, "MaximumVersion") ?? string.Empty;
            var exact = ContractJson.GetString(framework, "ExactVersion") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) ||
                !values.Add(string.Join('|', name, minimum, maximum, exact)))
            {
                return null;
            }
        }
        return values;
    }

    private static string? TryReadContainerIdentity(JsonElement profile)
    {
        if (!ContractJson.TryGetProperty(profile, "Container", out var container) ||
            container.ValueKind != JsonValueKind.Object)
            return null;
        return ContractJson.GetString(container, "IsolationKind");
    }

    private static string? TryReadContainerEnvironment(JsonElement profile)
    {
        if (!ContractJson.TryGetProperty(profile, "Container", out var container) ||
            container.ValueKind != JsonValueKind.Object)
            return null;
        return ContractJson.GetString(container, "EnvironmentKind");
    }

    private static string? TryReadJitSourceMappingKind(JsonElement profile)
    {
        if (!ContractJson.TryGetProperty(profile, "Operations", out var operations) ||
            operations.ValueKind != JsonValueKind.Object ||
            !ContractJson.TryGetProperty(operations, "Jit", out var jit) ||
            jit.ValueKind != JsonValueKind.Object)
            return null;
        return ContractJson.GetString(jit, "SourceMappingKind");
    }

    private static bool TryReadStringSet(
        JsonElement parent,
        string propertyName,
        out IReadOnlySet<string> values)
    {
        var parsed = new HashSet<string>(StringComparer.Ordinal);
        values = parsed;
        if (!ContractJson.TryGetProperty(parent, propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(element.GetString()) ||
                !parsed.Add(element.GetString()!))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(
        GatewayDependencyTarget target,
        string path,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(nameof(HttpGatewayDependencyProbe));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(target.BaseAddress, path));
        if (target.ServiceToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.ServiceToken);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class GatewayDependencyHealthService(
    CatalogDocument baselineCatalog,
    LanguageWorkerEndpointRegistry languageWorkers,
    ArtifactWorkerEndpointRegistry artifactWorkers,
    GatewayDependencyHealthOptions options,
    IGatewayDependencyProbe probe) : IDisposable
{
    public const string ArtifactStoreDependencyId = "artifact-store";
    public const string RuntimeSupervisorDependencyId = "runtime-supervisor";

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly GatewayDependencyFailureHysteresis _failureHysteresis = new();
    private GatewayDependencySnapshot? _cached;
    private DateTimeOffset _cacheExpiresAtUtc;

    public async Task<GatewayDependencySnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            return StaticSnapshot();
        var now = DateTimeOffset.UtcNow;
        if (_cached is { } cached && now < _cacheExpiresAtUtc)
            return cached;

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cached is { } refreshed && now < _cacheExpiresAtUtc)
                return refreshed;
            var snapshot = await RefreshAsync(now, cancellationToken).ConfigureAwait(false);
            _cached = snapshot;
            _cacheExpiresAtUtc = now.Add(options.CacheDuration);
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private GatewayDependencySnapshot StaticSnapshot()
    {
        var dependencies = new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
        {
            [ArtifactStoreDependencyId] = ReadyStatic(ArtifactStoreDependencyId, GatewayDependencyKind.ArtifactStore),
            [RuntimeSupervisorDependencyId] = new GatewayDependencyProbeResult(
                RuntimeSupervisorDependencyId,
                GatewayDependencyKind.RuntimeSupervisor,
                true,
                null,
                baselineCatalog.Runtimes.Where(static runtime => runtime.Availability.IsSelectable)
                    .ToDictionary(
                        static runtime => runtime.Id,
                        RuntimeProfileIdentityFromCatalog,
                        StringComparer.Ordinal))
        };
        foreach (var endpoint in languageWorkers.Endpoints)
            dependencies[LanguageDependencyId(endpoint.WorkerId)] = ReadyStatic(
                LanguageDependencyId(endpoint.WorkerId),
                GatewayDependencyKind.LanguageWorker);
        foreach (var endpoint in artifactWorkers.Endpoints)
            dependencies[ArtifactDependencyId(endpoint.WorkerId)] = ReadyStatic(
                ArtifactDependencyId(endpoint.WorkerId),
                GatewayDependencyKind.ArtifactWorker);
        return new GatewayDependencySnapshot(baselineCatalog, dependencies, DateTimeOffset.UtcNow, true);
    }

    private async Task<GatewayDependencySnapshot> RefreshAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var targets = new List<GatewayDependencyTarget>
        {
            new(
                ArtifactStoreDependencyId,
                GatewayDependencyKind.ArtifactStore,
                options.ArtifactStoreBaseAddress,
                options.ServiceToken),
            new(
                RuntimeSupervisorDependencyId,
                GatewayDependencyKind.RuntimeSupervisor,
                options.RuntimeSupervisorBaseAddress,
                options.ServiceToken)
        };
        targets.AddRange(languageWorkers.Endpoints.Select(static endpoint => new GatewayDependencyTarget(
            LanguageDependencyId(endpoint.WorkerId),
            GatewayDependencyKind.LanguageWorker,
            endpoint.BaseAddress,
            endpoint.ServiceToken)));
        targets.AddRange(artifactWorkers.Endpoints.Select(static endpoint => new GatewayDependencyTarget(
            ArtifactDependencyId(endpoint.WorkerId),
            GatewayDependencyKind.ArtifactWorker,
            endpoint.BaseAddress,
            endpoint.ServiceToken)));

        var results = await Task.WhenAll(targets.Select(target =>
            probe.ProbeAsync(target, options.ProbeTimeout, cancellationToken))).ConfigureAwait(false);
        var observedDependencies = results.ToDictionary(static result => result.Id, StringComparer.Ordinal);
        var dependencies = _failureHysteresis.Apply(observedDependencies, _cached?.Dependencies);
        var catalog = OverlayCatalog(dependencies);
        var ready = RequiredDependenciesAreReady(dependencies);
        return new GatewayDependencySnapshot(catalog, dependencies, observedAtUtc, ready);
    }

    private CatalogDocument OverlayCatalog(
        IReadOnlyDictionary<string, GatewayDependencyProbeResult> dependencies)
    {
        var toolchains = baselineCatalog.Toolchains.Select(toolchain => toolchain with
        {
            Availability = OverlayAvailability(
                toolchain.Availability,
                Find(dependencies, LanguageDependencyId(toolchain.WorkerId)),
                $"Language worker '{toolchain.WorkerId}' is not configured.")
        }).ToArray();
        var processors = baselineCatalog.ArtifactProcessors.Select(processor => processor with
        {
            Availability = OverlayAvailability(
                processor.Availability,
                Find(dependencies, ArtifactDependencyId(processor.WorkerId)),
                $"Artifact worker '{processor.WorkerId}' is not configured.")
        }).ToArray();
        var supervisor = Find(dependencies, RuntimeSupervisorDependencyId);
        var runtimes = baselineCatalog.Runtimes.Select(runtime => runtime with
        {
            Availability = OverlayRuntimeAvailability(runtime, supervisor)
        }).ToArray();
        var toolchainById = toolchains.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var runtimeById = runtimes.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var referenceSets = baselineCatalog.ReferenceSets.Select(referenceSet => referenceSet with
        {
            Availability = OverlayReferenceSetAvailability(referenceSet, toolchains)
        }).ToArray();
        var referenceSetById = referenceSets.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var presets = baselineCatalog.Presets.Select(preset => preset with
        {
            Availability = OverlayPresetAvailability(
                preset,
                toolchainById,
                referenceSetById,
                runtimeById)
        }).ToArray();
        var revision = DynamicRevision(dependencies);
        return baselineCatalog with
        {
            Revision = revision,
            Toolchains = toolchains,
            ReferenceSets = referenceSets,
            Runtimes = runtimes,
            ArtifactProcessors = processors,
            Presets = presets
        };
    }

    private bool RequiredDependenciesAreReady(
        IReadOnlyDictionary<string, GatewayDependencyProbeResult> dependencies)
    {
        if (Find(dependencies, ArtifactStoreDependencyId)?.Ready != true ||
            Find(dependencies, RuntimeSupervisorDependencyId)?.Ready != true)
        {
            return false;
        }

        if (baselineCatalog.Toolchains.Where(static item => item.Availability.IsSelectable).Any(toolchain =>
                Find(dependencies, LanguageDependencyId(toolchain.WorkerId))?.Ready != true))
        {
            return false;
        }

        if (baselineCatalog.ArtifactProcessors.Where(static item => item.Availability.IsSelectable).Any(processor =>
                Find(dependencies, ArtifactDependencyId(processor.WorkerId))?.Ready != true))
        {
            return false;
        }

        var supervisor = Find(dependencies, RuntimeSupervisorDependencyId);
        return baselineCatalog.Runtimes
            .Where(static runtime => runtime.Availability.IsSelectable)
            .All(runtime => RuntimeProfileMismatch(runtime, supervisor!) is null);
    }

    private static ComponentAvailability OverlayAvailability(
        ComponentAvailability baseline,
        GatewayDependencyProbeResult? dependency,
        string missingReason)
    {
        if (!baseline.IsSelectable)
            return baseline;
        return dependency?.Ready == true
            ? baseline
            : Unavailable(dependency?.Reason ?? missingReason);
    }

    private static ComponentAvailability OverlayRuntimeAvailability(
        RuntimeManifest runtime,
        GatewayDependencyProbeResult? supervisor)
    {
        if (!runtime.Availability.IsSelectable)
            return runtime.Availability;
        if (supervisor?.Ready != true)
            return Unavailable(supervisor?.Reason ?? "Runtime Supervisor is not configured.");
        if (!supervisor.RuntimeProfiles.ContainsKey(runtime.Id))
            return Unavailable($"Runtime profile '{runtime.Id}' is not loaded by Runtime Supervisor.");
        var mismatch = RuntimeProfileMismatch(runtime, supervisor);
        return mismatch is null
            ? runtime.Availability
            : Unavailable(mismatch);
    }

    private static ComponentAvailability OverlayReferenceSetAvailability(
        ReferenceSetManifest referenceSet,
        IReadOnlyList<ToolchainManifest> toolchains)
    {
        if (!referenceSet.Availability.IsSelectable)
            return referenceSet.Availability;
        return toolchains.Any(toolchain =>
                toolchain.Availability.IsSelectable &&
                toolchain.AllowedReferenceSetIds.Contains(referenceSet.Id, StringComparer.Ordinal))
            ? referenceSet.Availability
            : Unavailable($"No healthy toolchain provides reference set '{referenceSet.Id}'.");
    }

    private static ComponentAvailability OverlayPresetAvailability(
        ProfilePreset preset,
        Dictionary<string, ToolchainManifest> toolchains,
        Dictionary<string, ReferenceSetManifest> referenceSets,
        Dictionary<string, RuntimeManifest> runtimes)
    {
        if (!preset.Availability.IsSelectable)
            return preset.Availability;
        if (!toolchains.TryGetValue(preset.ToolchainId, out var toolchain) || !toolchain.Availability.IsSelectable)
            return Unavailable($"Preset toolchain '{preset.ToolchainId}' is unavailable.");
        if (!referenceSets.TryGetValue(preset.ReferenceSetId, out var referenceSet) || !referenceSet.Availability.IsSelectable)
            return Unavailable($"Preset reference set '{preset.ReferenceSetId}' is unavailable.");
        if (preset.DefaultRuntimeId is { } runtimeId &&
            (!runtimes.TryGetValue(runtimeId, out var runtime) || !runtime.Availability.IsSelectable))
        {
            return Unavailable($"Preset runtime '{runtimeId}' is unavailable.");
        }
        return preset.Availability;
    }

    private string DynamicRevision(
        IReadOnlyDictionary<string, GatewayDependencyProbeResult> dependencies)
    {
        var identity = string.Join('\n', dependencies.Values
            .OrderBy(static dependency => dependency.Id, StringComparer.Ordinal)
            .Select(dependency => string.Join('|',
                dependency.Id,
                dependency.Ready ? "ready" : "unavailable",
                string.Join(',', dependency.RuntimeProfiles.Values
                    .OrderBy(static profile => profile.Id, StringComparer.Ordinal)
                    .Select(static profile => profile.StableIdentity())))));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return $"{baselineCatalog.Revision}-h{digest[..12]}";
    }

    private static GatewayDependencyProbeResult? Find(
        IReadOnlyDictionary<string, GatewayDependencyProbeResult> dependencies,
        string id) =>
        dependencies.TryGetValue(id, out var result) ? result : null;

    private static ComponentAvailability Unavailable(string reason) => new()
    {
        Installed = true,
        Health = "unavailable",
        Reason = reason
    };

    private static GatewayDependencyProbeResult ReadyStatic(string id, GatewayDependencyKind kind) =>
        new(
            id,
            kind,
            true,
            null,
            new Dictionary<string, RuntimeProfileProbeIdentity>(StringComparer.Ordinal));

    private static RuntimeProfileProbeIdentity RuntimeProfileIdentityFromCatalog(RuntimeManifest runtime) => new(
        runtime.Id,
        runtime.Family,
        runtime.ResolvedVersion,
        runtime.Rid,
        runtime.Architecture,
        runtime.AcceptedArtifactFormats.ToHashSet(StringComparer.Ordinal),
        runtime.Capabilities.ToHashSet(StringComparer.Ordinal),
        runtime.ProvidedRuntimeFeatureTags.ToHashSet(StringComparer.Ordinal),
        runtime.ProvidedMetadataFeatureTags.ToHashSet(StringComparer.Ordinal),
        runtime.RuntimeCommit,
        runtime.JitVersion,
        runtime.JitCommit,
        runtime.RuntimeImageId,
        runtime.AcceptedRuntimeFamilies.ToHashSet(StringComparer.Ordinal),
        runtime.AcceptedFrameworks
            .Select(static framework => string.Join(
                '|',
                framework.Name,
                framework.MinimumVersion ?? string.Empty,
                framework.MaximumVersion ?? string.Empty,
                framework.ExactVersion ?? string.Empty))
            .ToHashSet(StringComparer.Ordinal),
        runtime.ContainerIsolationKind,
        runtime.ContainerEnvironmentKind,
        runtime.JitSourceMappingKind);

    private static string? RuntimeProfileMismatch(
        RuntimeManifest runtime,
        GatewayDependencyProbeResult supervisor)
    {
        if (!supervisor.RuntimeProfiles.TryGetValue(runtime.Id, out var loaded))
            return $"Runtime profile '{runtime.Id}' is not loaded by Runtime Supervisor.";
        if (!StringComparer.Ordinal.Equals(runtime.Family, loaded.Family))
            return RuntimeIdentityMismatch(runtime.Id, "family", runtime.Family, loaded.Family);
        if (!StringComparer.Ordinal.Equals(runtime.ResolvedVersion, loaded.RuntimeVersion))
            return RuntimeIdentityMismatch(
                runtime.Id,
                "runtime version",
                runtime.ResolvedVersion,
                loaded.RuntimeVersion);
        if (!StringComparer.Ordinal.Equals(runtime.Rid, loaded.Rid))
            return RuntimeIdentityMismatch(runtime.Id, "RID", runtime.Rid, loaded.Rid);
        if (!StringComparer.Ordinal.Equals(runtime.Architecture, loaded.Architecture))
            return RuntimeIdentityMismatch(
                runtime.Id,
                "architecture",
                runtime.Architecture,
                loaded.Architecture);
        if (runtime.RuntimeCommit is { } runtimeCommit &&
            !StringComparer.Ordinal.Equals(runtimeCommit, loaded.RuntimeCommit))
        {
            return RuntimeIdentityMismatch(
                runtime.Id,
                "runtime commit",
                runtimeCommit,
                loaded.RuntimeCommit ?? "<missing>");
        }
        if (runtime.JitVersion is { } jitVersion &&
            !StringComparer.Ordinal.Equals(jitVersion, loaded.JitVersion))
        {
            return RuntimeIdentityMismatch(
                runtime.Id,
                "JIT version",
                jitVersion,
                loaded.JitVersion ?? "<missing>");
        }
        if (runtime.JitCommit is { } jitCommit &&
            !StringComparer.Ordinal.Equals(jitCommit, loaded.JitCommit))
        {
            return RuntimeIdentityMismatch(
                runtime.Id,
                "JIT commit",
                jitCommit,
                loaded.JitCommit ?? "<missing>");
        }
        if (runtime.RuntimeImageId is { } imageId &&
            !StringComparer.Ordinal.Equals(imageId, loaded.RuntimeImageId))
        {
            return RuntimeIdentityMismatch(
                runtime.Id,
                "runtime image ID",
                imageId,
                loaded.RuntimeImageId ?? "<missing>");
        }
        if (!loaded.AcceptedArtifactFormats.SetEquals(runtime.AcceptedArtifactFormats))
            return RuntimeContractMismatch(runtime.Id, "accepted artifact formats");
        if (!loaded.Capabilities.SetEquals(runtime.Capabilities))
            return RuntimeContractMismatch(runtime.Id, "capabilities");
        if (!loaded.ProvidedRuntimeFeatureTags.SetEquals(runtime.ProvidedRuntimeFeatureTags))
            return RuntimeContractMismatch(runtime.Id, "runtime feature tags");
        if (!loaded.ProvidedMetadataFeatureTags.SetEquals(runtime.ProvidedMetadataFeatureTags))
            return RuntimeContractMismatch(runtime.Id, "metadata feature tags");
        if (runtime.AcceptedRuntimeFamilies.Count > 0 &&
            (loaded.AcceptedRuntimeFamilies is null ||
             !loaded.AcceptedRuntimeFamilies.SetEquals(runtime.AcceptedRuntimeFamilies)))
            return RuntimeContractMismatch(runtime.Id, "accepted runtime families");
        if (runtime.AcceptedFrameworks.Count > 0 &&
            (loaded.AcceptedFrameworks is null ||
             !loaded.AcceptedFrameworks.SetEquals(runtime.AcceptedFrameworks.Select(static framework => string.Join(
                 '|',
                 framework.Name,
                 framework.MinimumVersion ?? string.Empty,
                 framework.MaximumVersion ?? string.Empty,
                 framework.ExactVersion ?? string.Empty)))))
            return RuntimeContractMismatch(runtime.Id, "accepted frameworks");
        if (runtime.ContainerIsolationKind is { } isolation &&
            !StringComparer.Ordinal.Equals(isolation, loaded.ContainerIsolationKind))
            return RuntimeIdentityMismatch(
                runtime.Id,
                "container isolation",
                isolation,
                loaded.ContainerIsolationKind ?? "<missing>");
        if (runtime.ContainerEnvironmentKind is { } environment &&
            !StringComparer.Ordinal.Equals(environment, loaded.ContainerEnvironmentKind))
            return RuntimeIdentityMismatch(
                runtime.Id,
                "container environment",
                environment,
                loaded.ContainerEnvironmentKind ?? "<missing>");
        if (runtime.JitSourceMappingKind is { } mapping &&
            !StringComparer.Ordinal.Equals(mapping, loaded.JitSourceMappingKind))
            return RuntimeIdentityMismatch(
                runtime.Id,
                "JIT source mapping",
                mapping,
                loaded.JitSourceMappingKind ?? "<missing>");
        return null;
    }

    private static string RuntimeIdentityMismatch(
        string id,
        string field,
        string expected,
        string actual) =>
        $"Runtime profile '{id}' {field} mismatch: catalog expects '{expected}', Supervisor loaded '{actual}'.";

    private static string RuntimeContractMismatch(string id, string field) =>
        $"Runtime profile '{id}' {field} do not match the catalog contract.";

    public static string LanguageDependencyId(string workerId) => $"language-worker:{workerId}";

    public static string ArtifactDependencyId(string workerId) => $"artifact-worker:{workerId}";

    public void Dispose() => _refreshLock.Dispose();
}

internal static class RuntimeProfileProbeIdentityExtensions
{
    public static string StableIdentity(this RuntimeProfileProbeIdentity profile) => string.Join(
        ':',
        [
            profile.Id,
            profile.Family,
            profile.RuntimeVersion,
            profile.Rid,
            profile.Architecture,
            StableSet(profile.AcceptedArtifactFormats),
            StableSet(profile.Capabilities),
            StableSet(profile.ProvidedRuntimeFeatureTags),
            StableSet(profile.ProvidedMetadataFeatureTags),
            profile.RuntimeCommit ?? string.Empty,
            profile.JitVersion ?? string.Empty,
            profile.JitCommit ?? string.Empty,
            profile.RuntimeImageId ?? string.Empty,
            StableSet(profile.AcceptedRuntimeFamilies ?? Enumerable.Empty<string>()),
            StableSet(profile.AcceptedFrameworks ?? Enumerable.Empty<string>()),
            profile.ContainerIsolationKind ?? string.Empty,
            profile.ContainerEnvironmentKind ?? string.Empty,
            profile.JitSourceMappingKind ?? string.Empty
        ]);

    private static string StableSet(IEnumerable<string> values) =>
        string.Join(',', values.Order(StringComparer.Ordinal));
}

internal sealed class GatewayDependencyFailureHysteresis
{
    private readonly int _requiredConsecutiveFailures;
    private readonly Dictionary<string, int> _failureCounts = new(StringComparer.Ordinal);

    public GatewayDependencyFailureHysteresis(int requiredConsecutiveFailures = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredConsecutiveFailures, 1);
        _requiredConsecutiveFailures = requiredConsecutiveFailures;
    }

    public IReadOnlyDictionary<string, GatewayDependencyProbeResult> Apply(
        IReadOnlyDictionary<string, GatewayDependencyProbeResult> observed,
        IReadOnlyDictionary<string, GatewayDependencyProbeResult>? previous)
    {
        var stabilized = new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal);
        foreach (var (id, current) in observed)
        {
            if (current.Ready)
            {
                _failureCounts[id] = 0;
                stabilized[id] = current;
                continue;
            }

            var failureCount = _failureCounts.GetValueOrDefault(id) + 1;
            _failureCounts[id] = failureCount;
            if (failureCount < _requiredConsecutiveFailures &&
                previous?.TryGetValue(id, out var last) == true &&
                last.Ready)
            {
                stabilized[id] = last;
                continue;
            }

            stabilized[id] = current;
        }

        foreach (var removed in _failureCounts.Keys.Except(observed.Keys, StringComparer.Ordinal).ToArray())
            _failureCounts.Remove(removed);
        return stabilized;
    }
}

public static class GatewayPipelineAvailability
{
    public static string? GetUnavailableReason(
        GatewayDependencySnapshot snapshot,
        ResolveSelectionResponse resolution,
        bool requireArtifactStore)
    {
        var recorded = resolution.SelectionChanges.FirstOrDefault(static change =>
            change.Reason == SelectionChangeReason.ProfileUnavailable);
        if (recorded is not null)
            return recorded.Message;
        if (requireArtifactStore && !snapshot.ArtifactStoreReady)
            return "Artifact Store is unavailable.";

        var selection = resolution.EffectiveSelection;
        var toolchain = snapshot.Catalog.Toolchains.FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.Id, selection.ToolchainId));
        if (toolchain is null || !toolchain.Availability.IsSelectable)
            return toolchain?.Availability.Reason ?? "The selected toolchain is unavailable.";
        var referenceSet = snapshot.Catalog.ReferenceSets.FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.Id, selection.ReferenceSetId));
        if (referenceSet is null || !referenceSet.Availability.IsSelectable)
            return referenceSet?.Availability.Reason ?? "The selected reference set is unavailable.";
        if (selection.RuntimeId is { } runtimeId)
        {
            var runtime = snapshot.Catalog.Runtimes.FirstOrDefault(item =>
                StringComparer.Ordinal.Equals(item.Id, runtimeId));
            if (runtime is null || !runtime.Availability.IsSelectable)
                return runtime?.Availability.Reason ?? "The selected runtime is unavailable.";
        }
        foreach (var providerId in resolution.PipelinePlan.Stages
                     .Where(static stage => stage.Kind is PipelineStageKind.Transform or PipelineStageKind.Render or PipelineStageKind.Verify)
                     .Select(static stage => stage.ProviderId)
                     .Distinct(StringComparer.Ordinal))
        {
            var processor = snapshot.Catalog.ArtifactProcessors.FirstOrDefault(item =>
                StringComparer.Ordinal.Equals(item.WorkerId, providerId) ||
                StringComparer.Ordinal.Equals(item.Id, providerId));
            if (processor is null || !processor.Availability.IsSelectable)
                return processor?.Availability.Reason ?? $"Artifact provider '{providerId}' is unavailable.";
        }
        return null;
    }
}

using SharpLabNext.Catalog;

namespace SharpLabNext.ProfileUpdater;

public sealed record ReleaseLockChange(string ComponentId, string? PreviousVersion, string? NewVersion);

public sealed record ReleaseLockUpdateResult(ReleaseLockDocument Candidate, IReadOnlyList<ReleaseLockChange> Changes);

public sealed class ReleaseLockUpdater(IProfileSourceClient sourceClient, string channelRoot)
{
    private readonly string channelRoot = Path.GetFullPath(channelRoot);

    public async Task<ReleaseLockUpdateResult> ResolveAsync(ReleaseLockDocument current, string? releaseId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        var manifests = await ProfileChannelManifestLoader.LoadAsync(channelRoot, cancellationToken);

        var runtimeTasks = manifests.Runtimes.ToDictionary(static intent => intent.Id, intent => sourceClient.ResolveDotNetChannelAsync(intent.Channel, cancellationToken), StringComparer.Ordinal);
        var nugetTasks = manifests.Components.Where(static intent => string.Equals(intent.SourceType, "nuget", StringComparison.Ordinal)).ToDictionary(static intent => intent.Id, intent => ResolveNuGetAsync(intent, cancellationToken), StringComparer.Ordinal);
        var gitTasks = manifests.Components.Where(static intent => string.Equals(intent.SourceType, "github-commit", StringComparison.Ordinal)).ToDictionary(static intent => intent.Id, intent => sourceClient.ResolveGitCommitAsync(intent.GitOwner!, intent.GitRepository!, intent.GitReference!, cancellationToken), StringComparer.Ordinal);

        await Task.WhenAll(runtimeTasks.Values.Concat<Task>(nugetTasks.Values).Concat(gitTasks.Values));

        var referenceTasks = manifests.Runtimes.ToDictionary(static intent => intent.ReferenceSetId, intent => sourceClient.ResolveExactPackageAsync(intent.ReferencePackage, runtimeTasks[intent.Id].Result.RuntimeVersion, cancellationToken), StringComparer.Ordinal);
        await Task.WhenAll(referenceTasks.Values);

        var components = current.Components.Where(static pair => !IsPackageManagerOwnedComponent(pair.Key, pair.Value)).ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value with { PatchDigest = null },
                StringComparer.Ordinal);

        foreach (var intent in manifests.Runtimes)
        {
            var resolution = await runtimeTasks[intent.Id];
            Set(components, intent.Id, DotNetRuntimeComponent(resolution));
            Set(components, intent.ReferenceSetId, NuGetComponent("reference-set", await referenceTasks[intent.ReferenceSetId]));
            if (intent.SdkComponentId is not null)
                Set(components, intent.SdkComponentId, DotNetSdkComponent(resolution));
        }

        foreach (var intent in manifests.Components)
        {
            if (string.Equals(intent.SourceType, "nuget", StringComparison.Ordinal))
            {
                var component = NuGetComponent(intent.Kind, await nugetTasks[intent.Id]) with { Commit = intent.ProvenanceCommit };
                Set(components, intent.Id, component);
                continue;
            }

            var source = await gitTasks[intent.Id];
            var resolvedVersion = intent.Version ?? source.ProductVersion;
            if (intent.Version is not null && !string.Equals(intent.Version, source.ProductVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Profile channel '{intent.Id}' expected Git product version '{intent.Version}', " + $"but source '{intent.GitOwner}/{intent.GitRepository}@{intent.GitReference}' resolved '{source.ProductVersion}'.");
            }

            var digest = $"sha256:{source.ArchiveSha256}";
            Set(components, intent.Id, new LockedComponent { Kind = intent.Kind, ResolvedVersion = resolvedVersion, Commit = source.Commit, SourceUri = intent.SourceComponentId is null ? source.ArchiveUri.AbsoluteUri : $"{source.RepositoryUri.AbsoluteUri.TrimEnd('/')}/tree/{source.Commit}", Digest = digest });
            if (intent.SourceComponentId is not null)
            {
                Set(components, intent.SourceComponentId, new LockedComponent { Kind = "source", ResolvedVersion = resolvedVersion, Commit = source.Commit, SourceUri = source.ArchiveUri.AbsoluteUri, Digest = digest });
            }
        }

        foreach (var derived in manifests.DerivedComponents)
        {
            var resolvedVersion = ProfileChannelManifestLoader.VersionPlaceholderRegex().Replace(derived.VersionTemplate, match => RequiredResolvedVersion(components, match.Groups["id"].Value));
            var component = derived.IdentitySourceComponentId is null
                ? new LockedComponent { Kind = derived.Kind, ResolvedVersion = resolvedVersion }
                : components[derived.IdentitySourceComponentId] with { Kind = derived.Kind, ResolvedVersion = resolvedVersion, PatchDigest = null, ImageId = null };
            Set(components, derived.Id, component);
        }

        var candidate = new ReleaseLockDocument { SchemaVersion = current.SchemaVersion, ReleaseId = string.IsNullOrWhiteSpace(releaseId) ? current.ReleaseId : releaseId, ResolvedAt = DateTimeOffset.UtcNow, Components = components };
        var changes = current.Components.Keys.Union(components.Keys, StringComparer.Ordinal).Where(componentId =>
            {
                var hadPrevious = current.Components.TryGetValue(componentId, out var previous);
                var hasCurrent = components.TryGetValue(componentId, out var next);
                return !hadPrevious || !hasCurrent || !ComponentEquals(previous!, next!);
            })
            .Select(componentId => new ReleaseLockChange(componentId, current.Components.TryGetValue(componentId, out var previous) ? previous.ResolvedVersion : null, components.TryGetValue(componentId, out var next) ? next.ResolvedVersion : null)).OrderBy(static change => change.ComponentId, StringComparer.Ordinal).ToArray();
        return new ReleaseLockUpdateResult(candidate, changes);
    }

    private Task<NuGetPackageResolution> ResolveNuGetAsync(ComponentChannelIntent intent, CancellationToken cancellationToken) =>
        string.Equals(intent.Policy, "latest-stable", StringComparison.Ordinal)
            ? sourceClient.ResolveLatestStablePackageAsync(intent.Package!, cancellationToken) : sourceClient.ResolveExactPackageAsync(intent.Package!, intent.Version!, cancellationToken);

    private static string RequiredResolvedVersion(Dictionary<string, LockedComponent> components, string componentId) =>
        components.TryGetValue(componentId, out var component) && !string.IsNullOrWhiteSpace(component.ResolvedVersion)
            ? component.ResolvedVersion : throw new InvalidDataException($"Derived profile component references '{componentId}', which has no resolved version.");

    private static LockedComponent DotNetRuntimeComponent(DotNetChannelResolution release) => new()
    {
        Kind = "runtime",
        ResolvedVersion = release.RuntimeVersion,
        Commit = release.RuntimeCommit,
        JitCommit = release.JitCommit,
        SourceUri = release.RuntimeUri.AbsoluteUri,
        Sha512 = release.RuntimeSha512,
        ReleaseDate = release.ReleaseDate
    };

    private static LockedComponent DotNetSdkComponent(DotNetChannelResolution release) => new()
    {
        Kind = "sdk",
        ResolvedVersion = release.SdkVersion,
        SourceUri = release.SdkUri.AbsoluteUri,
        Sha512 = release.SdkSha512,
        ReleaseDate = release.ReleaseDate
    };

    private static LockedComponent NuGetComponent(string kind, NuGetPackageResolution package) => new()
    {
        Kind = kind,
        ResolvedVersion = package.Version,
        Package = package.PackageId,
        SourceUri = package.PackageUri.AbsoluteUri,
        PackageContentHash = package.PackageContentHash,
        Sha512 = package.PackageSha512
    };

    private static bool ComponentEquals(LockedComponent left, LockedComponent right) =>
        string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
        string.Equals(left.ResolvedVersion, right.ResolvedVersion, StringComparison.Ordinal) &&
        string.Equals(left.Commit, right.Commit, StringComparison.Ordinal) &&
        string.Equals(left.JitCommit, right.JitCommit, StringComparison.Ordinal) &&
        string.Equals(left.Digest, right.Digest, StringComparison.Ordinal) &&
        string.Equals(left.PatchDigest, right.PatchDigest, StringComparison.Ordinal) &&
        string.Equals(left.SourceUri, right.SourceUri, StringComparison.Ordinal) &&
        string.Equals(left.Sha512, right.Sha512, StringComparison.Ordinal) &&
        string.Equals(left.Package, right.Package, StringComparison.Ordinal) &&
        string.Equals(left.PackageContentHash, right.PackageContentHash, StringComparison.Ordinal) &&
        left.ReleaseDate == right.ReleaseDate;

    private static bool IsPackageManagerOwnedComponent(string id, LockedComponent component) =>
        string.Equals(component.Kind, "frontend", StringComparison.Ordinal) ||
        id.StartsWith("frontend-", StringComparison.Ordinal);

    private static void Set(Dictionary<string, LockedComponent> components, string id, LockedComponent value) => components[id] = value;
}

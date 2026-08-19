using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SharpLabNext.ProfileUpdater;

internal sealed record ProfileChannelManifestSet(
    IReadOnlyList<RuntimeChannelIntent> Runtimes,
    IReadOnlyList<ComponentChannelIntent> Components,
    IReadOnlyList<DerivedComponentIntent> DerivedComponents);

internal sealed record RuntimeChannelIntent(
    string Id,
    string Channel,
    string Policy,
    string? SdkComponentId,
    string ReferenceSetId,
    string ReferencePackage);

internal sealed record ComponentChannelIntent(
    string Id,
    string Kind,
    string SourceType,
    string? Package,
    string Policy,
    string? Version,
    string? GitOwner,
    string? GitRepository,
    string? GitReference,
    string? SourceComponentId,
    string? ProvenanceCommit);

internal sealed record DerivedComponentIntent(
    string Id,
    string Kind,
    string VersionTemplate,
    string? IdentitySourceComponentId);

internal static partial class ProfileChannelManifestLoader
{
    private const int MaximumManifestBytes = 64 * 1024;
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .WithMaximumRecursion(32)
        .Build();

    public static async Task<ProfileChannelManifestSet> LoadAsync(
        string channelRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelRoot);
        var root = Path.GetFullPath(channelRoot);
        if (!Directory.Exists(root))
            throw new InvalidDataException($"Profile channel directory '{root}' does not exist.");

        var toolchainPath = Path.Combine(root, "toolchains.yaml");
        var toolchains = Deserialize<ToolchainManifestDocument>(
            await ReadManifestAsync(toolchainPath, cancellationToken),
            toolchainPath);
        ValidateUpdate(toolchains.Update, "toolchains.yaml", requireRetainLastKnownGood: false);
        var releaseInputItems = (toolchains.ReleaseInputs ?? [])
            .Select((input, index) => ValidateReleaseInput(input, $"toolchains.yaml releaseInputs[{index}]"))
            .ToArray();
        var releaseInputs = new Dictionary<string, ReleaseInputIntent>(StringComparer.Ordinal);
        foreach (var input in releaseInputItems)
        {
            if (!releaseInputs.TryAdd(input.Id, input))
                throw new InvalidDataException($"Profile channel release input ID '{input.Id}' is duplicated.");
        }
        var componentIntents = (toolchains.Channels ?? [])
            .Select((channel, index) => ValidateComponent(
                channel,
                releaseInputs,
                $"toolchains.yaml channels[{index}]"))
            .ToArray();
        var derivedIntents = (toolchains.DerivedComponents ?? [])
            .Select((component, index) => ValidateDerived(
                component,
                componentIntents,
                $"toolchains.yaml derivedComponents[{index}]"))
            .ToArray();

        var runtimeIntents = new List<RuntimeChannelIntent>();
        foreach (var path in Directory.EnumerateFiles(root, "*.yaml", SearchOption.TopDirectoryOnly)
                     .Where(path => !string.Equals(path, toolchainPath, PathComparison))
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var document = Deserialize<RuntimeManifestDocument>(
                await ReadManifestAsync(path, cancellationToken),
                path);
            runtimeIntents.Add(ValidateRuntime(document, Path.GetFileName(path)));
        }

        if (runtimeIntents.Count == 0)
            throw new InvalidDataException("At least one runtime channel manifest is required.");
        if (componentIntents.Length == 0)
            throw new InvalidDataException("At least one toolchain channel manifest is required.");

        ValidateUniqueOutputIds(runtimeIntents, componentIntents, derivedIntents);
        return new ProfileChannelManifestSet(runtimeIntents, componentIntents, derivedIntents);
    }

    private static async Task<string> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"Required profile channel manifest '{path}' does not exist.");
        var info = new FileInfo(path);
        if (info.Length is < 1 or > MaximumManifestBytes)
            throw new InvalidDataException($"Profile channel manifest '{path}' has an invalid size.");
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    private static T Deserialize<T>(string yaml, string path)
    {
        try
        {
            return Deserializer.Deserialize<T>(yaml)
                ?? throw new InvalidDataException($"Profile channel manifest '{path}' is empty.");
        }
        catch (YamlException exception)
        {
            throw new InvalidDataException($"Profile channel manifest '{path}' is invalid: {exception.Message}", exception);
        }
    }

    private static RuntimeChannelIntent ValidateRuntime(RuntimeManifestDocument document, string location)
    {
        var id = RequiredId(document.Id, $"{location}.id");
        if (!string.Equals(document.Kind, "runtime-channel", StringComparison.Ordinal))
            throw Invalid(location, "kind must be 'runtime-channel'");
        var source = document.Source ?? throw Invalid(location, "source is required");
        if (!string.Equals(source.Type, "dotnet-release-metadata", StringComparison.Ordinal))
            throw Invalid(location, "source.type must be 'dotnet-release-metadata'");
        var channel = RequiredVersion(source.Channel, $"{location}.source.channel");
        var policy = RequiredOneOf(
            source.Policy,
            $"{location}.source.policy",
            "latest-release",
            "latest-preview");
        var platform = document.Platform ?? throw Invalid(location, "platform is required");
        if (!string.Equals(platform.Os, "linux", StringComparison.Ordinal) ||
            !string.Equals(platform.Libc, "glibc", StringComparison.Ordinal) ||
            !string.Equals(platform.Architecture, "x64", StringComparison.Ordinal))
        {
            throw Invalid(location, "only linux/glibc/x64 runtime channels are supported");
        }
        ValidateUpdate(document.Update, location, requireRetainLastKnownGood: true);
        var referenceSet = document.ReferenceSet ?? throw Invalid(location, "referenceSet is required");
        return new RuntimeChannelIntent(
            id,
            channel,
            policy,
            OptionalId(document.SdkComponentId, $"{location}.sdkComponentId"),
            RequiredId(referenceSet.Id, $"{location}.referenceSet.id"),
            RequiredPackageId(referenceSet.Package, $"{location}.referenceSet.package"));
    }

    private static ComponentChannelIntent ValidateComponent(
        ComponentManifestDocument document,
        Dictionary<string, ReleaseInputIntent> releaseInputs,
        string location)
    {
        var id = RequiredId(document.Id, $"{location}.id");
        var kind = RequiredOneOf(
            document.Kind,
            $"{location}.kind",
            "toolchain",
            "runtime-dependency",
            "artifact-processor");
        var source = document.Source ?? throw Invalid(location, "source is required");
        ReleaseInputIntent? releaseInput = null;
        if (document.ReleaseInput is not null)
        {
            var releaseInputId = RequiredId(document.ReleaseInput, $"{location}.releaseInput");
            if (!releaseInputs.TryGetValue(releaseInputId, out releaseInput))
                throw Invalid(location, $"releaseInput '{releaseInputId}' is not declared");
        }
        var directCommit = OptionalCommit(document.ProvenanceCommit, $"{location}.provenanceCommit");
        if (directCommit is not null && releaseInput?.ProvenanceCommit is not null)
            throw Invalid(location, "provenanceCommit is duplicated by releaseInput");
        var provenanceCommit = directCommit ?? releaseInput?.ProvenanceCommit;
        var sourceComponentId = OptionalId(document.SourceComponentId, $"{location}.sourceComponentId");

        if (string.Equals(source.Type, "nuget", StringComparison.Ordinal))
        {
            if (sourceComponentId is not null)
                throw Invalid(location, "sourceComponentId is only valid for github-commit sources");
            var policy = RequiredOneOf(source.Policy, $"{location}.source.policy", "latest-stable", "exact");
            if (source.Version is not null && releaseInput?.Version is not null)
                throw Invalid(location, "source.version is duplicated by releaseInput");
            var version = string.Equals(policy, "exact", StringComparison.Ordinal)
                ? RequiredVersion(source.Version ?? releaseInput?.Version, $"{location}.source.version")
                : RequireAbsent(source.Version ?? releaseInput?.Version, $"{location}.source.version");
            return new ComponentChannelIntent(
                id,
                kind,
                "nuget",
                RequiredPackageId(source.Package, $"{location}.source.package"),
                policy,
                version,
                null,
                null,
                null,
                null,
                provenanceCommit);
        }

        if (string.Equals(source.Type, "github-commit", StringComparison.Ordinal))
        {
            if (provenanceCommit is not null)
                throw Invalid(location, "provenanceCommit is not valid for github-commit sources");
            var repository = RequiredRepository(source.Repository, $"{location}.source.repository");
            if (source.Version is not null && releaseInput?.Version is not null)
                throw Invalid(location, "source.version is duplicated by releaseInput");
            var version = OptionalVersion(source.Version ?? releaseInput?.Version, $"{location}.source.version");
            var reference = GitReference(
                source.Branch,
                source.Ref,
                source.TagPrefix,
                version,
                $"{location}.source.branch",
                $"{location}.source.ref",
                $"{location}.source.tagPrefix");
            var parts = repository.Split('/');
            return new ComponentChannelIntent(
                id,
                kind,
                "github-commit",
                null,
                "exact-ref",
                version,
                parts[0],
                parts[1],
                reference,
                sourceComponentId,
                null);
        }

        throw Invalid(location, "source.type must be 'nuget' or 'github-commit'");
    }

    private static ReleaseInputIntent ValidateReleaseInput(ReleaseInputDocument document, string location) => new(
        RequiredId(document.Id, $"{location}.id"),
        RequiredVersion(document.Version, $"{location}.version"),
        OptionalCommit(document.ProvenanceCommit, $"{location}.provenanceCommit"));

    private static DerivedComponentIntent ValidateDerived(
        DerivedManifestDocument document,
        IReadOnlyCollection<ComponentChannelIntent> components,
        string location)
    {
        var id = RequiredId(document.Id, $"{location}.id");
        var kind = RequiredOneOf(document.Kind, $"{location}.kind", "artifact-processor", "toolchain");
        var template = RequiredString(document.VersionTemplate, $"{location}.versionTemplate", 256);
        var componentIds = components.Select(static component => component.Id).ToHashSet(StringComparer.Ordinal);
        var placeholders = VersionPlaceholderRegex().Matches(template)
            .Select(static match => match.Groups["id"].Value)
            .ToArray();
        if (placeholders.Length == 0 || placeholders.Any(id => !componentIds.Contains(id)))
            throw Invalid(location, "versionTemplate must reference declared channel component IDs");
        if (VersionPlaceholderRegex().Replace(template, string.Empty).IndexOfAny(['{', '}']) >= 0)
            throw Invalid(location, "versionTemplate contains an invalid placeholder");
        string? identitySourceComponentId = null;
        if (string.Equals(kind, "toolchain", StringComparison.Ordinal))
        {
            if (placeholders.Length != 1 ||
                !string.Equals(template, $"{{{placeholders[0]}}}", StringComparison.Ordinal))
            {
                throw Invalid(
                    location,
                    "derived toolchain versionTemplate must contain exactly one component placeholder and no other text");
            }
            identitySourceComponentId = placeholders[0];
            var identitySource = components.Single(component =>
                string.Equals(component.Id, identitySourceComponentId, StringComparison.Ordinal));
            if (!string.Equals(identitySource.Kind, "toolchain", StringComparison.Ordinal))
            {
                throw Invalid(
                    location,
                    "derived toolchain versionTemplate must reference a direct toolchain component");
            }
        }
        return new DerivedComponentIntent(id, kind, template, identitySourceComponentId);
    }

    private static void ValidateUpdate(UpdateDocument? update, string location, bool requireRetainLastKnownGood)
    {
        if (update is null)
            throw Invalid(location, "update is required");
        if (!PollIntervalRegex().IsMatch(update.PollInterval ?? string.Empty))
            throw Invalid(location, "update.pollInterval must be an integer followed by 'm' or 'h'");
        if (update.AutoPromoteAfterTests is null)
            throw Invalid(location, "update.autoPromoteAfterTests is required");
        if (requireRetainLastKnownGood && update.RetainLastKnownGood is not true)
            throw Invalid(location, "runtime channels must retain the last-known-good release");
    }

    private static void ValidateUniqueOutputIds(
        IReadOnlyCollection<RuntimeChannelIntent> runtimes,
        IReadOnlyCollection<ComponentChannelIntent> components,
        IReadOnlyCollection<DerivedComponentIntent> derivedComponents)
    {
        var locations = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string id, string location)
        {
            if (!locations.TryAdd(id, location))
                throw new InvalidDataException($"Profile channel output ID '{id}' is duplicated by {locations[id]} and {location}.");
        }

        foreach (var runtime in runtimes)
        {
            Add(runtime.Id, $"runtime '{runtime.Id}'");
            Add(runtime.ReferenceSetId, $"runtime '{runtime.Id}' reference set");
            if (runtime.SdkComponentId is not null)
                Add(runtime.SdkComponentId, $"runtime '{runtime.Id}' SDK");
        }
        foreach (var component in components)
        {
            Add(component.Id, $"component '{component.Id}'");
            if (component.SourceComponentId is not null)
                Add(component.SourceComponentId, $"component '{component.Id}' source");
        }
        foreach (var derived in derivedComponents)
            Add(derived.Id, $"derived component '{derived.Id}'");
    }

    private static string RequiredId(string? value, string location)
    {
        var id = RequiredString(value, location, 80);
        return IdRegex().IsMatch(id) ? id : throw Invalid(location, "is not a valid lowercase component ID");
    }

    private static string? OptionalId(string? value, string location) =>
        value is null ? null : RequiredId(value, location);

    private static string RequiredPackageId(string? value, string location)
    {
        var package = RequiredString(value, location, 160);
        return PackageIdRegex().IsMatch(package) ? package : throw Invalid(location, "is not a valid NuGet package ID");
    }

    private static string RequiredRepository(string? value, string location)
    {
        var repository = RequiredString(value, location, 160);
        return RepositoryRegex().IsMatch(repository) ? repository : throw Invalid(location, "is not a valid GitHub owner/repository");
    }

    private static string RequiredVersion(string? value, string location) =>
        RequiredString(value, location, 160, static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+');

    private static string? OptionalVersion(string? value, string location) =>
        value is null ? null : RequiredVersion(value, location);

    private static string? OptionalCommit(string? value, string location)
    {
        if (value is null)
            return null;
        return CommitRegex().IsMatch(value) ? value : throw Invalid(location, "is not a lowercase 40-character commit SHA");
    }

    private static string RequiredOneOf(string? value, string location, params string[] allowed)
    {
        var result = RequiredString(value, location, 80);
        return allowed.Contains(result, StringComparer.Ordinal)
            ? result
            : throw Invalid(location, $"must be one of: {string.Join(", ", allowed)}");
    }

    private static string GitReference(
        string? branch,
        string? reference,
        string? tagPrefix,
        string? version,
        string branchLocation,
        string referenceLocation,
        string tagPrefixLocation)
    {
        if (new[] { branch, reference, tagPrefix }.Count(static value => value is not null) != 1)
            throw Invalid(branchLocation, "exactly one of source.branch, source.ref, and source.tagPrefix is required");
        if (tagPrefix is not null)
        {
            var prefix = RequiredString(tagPrefix, tagPrefixLocation, 32, static character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
            return version is not null
                ? prefix + version
                : throw Invalid(tagPrefixLocation, "requires source.version or releaseInput");
        }
        return RequiredString(branch ?? reference, branch is null ? referenceLocation : branchLocation, 160, static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '/');
    }

    private static string? RequireAbsent(string? value, string location) =>
        value is null ? null : throw Invalid(location, "must be omitted for latest-stable policy");

    private static string RequiredString(
        string? value,
        string location,
        int maximumLength,
        Func<char, bool>? characterPredicate = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim())
            throw Invalid(location, "is missing or malformed");
        if (characterPredicate is not null && value.Any(character => !characterPredicate(character)))
            throw Invalid(location, "contains unsupported characters");
        return value;
    }

    private static InvalidDataException Invalid(string location, string message) =>
        new($"Profile channel manifest {location}: {message}.");

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,78}[a-z0-9])?$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,158}[A-Za-z0-9])?$")]
    private static partial Regex PackageIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitRegex();

    [GeneratedRegex("^[1-9][0-9]*(?:m|h)$")]
    private static partial Regex PollIntervalRegex();

    [GeneratedRegex("\\{(?<id>[a-z0-9](?:[a-z0-9-]{0,78}[a-z0-9])?)\\}")]
    internal static partial Regex VersionPlaceholderRegex();

    private sealed class RuntimeManifestDocument
    {
        public string? Id { get; init; }
        public string? Kind { get; init; }
        public SourceDocument? Source { get; init; }
        public string? SdkComponentId { get; init; }
        public ReferenceSetDocument? ReferenceSet { get; init; }
        public PlatformDocument? Platform { get; init; }
        public UpdateDocument? Update { get; init; }
    }

    private sealed class ToolchainManifestDocument
    {
        public List<ReleaseInputDocument>? ReleaseInputs { get; init; }
        public List<ComponentManifestDocument>? Channels { get; init; }
        public List<DerivedManifestDocument>? DerivedComponents { get; init; }
        public UpdateDocument? Update { get; init; }
    }

    private sealed class ComponentManifestDocument
    {
        public string? Id { get; init; }
        public string? Kind { get; init; }
        public string? ReleaseInput { get; init; }
        public string? SourceComponentId { get; init; }
        public string? ProvenanceCommit { get; init; }
        public SourceDocument? Source { get; init; }
    }

    private sealed class ReleaseInputDocument
    {
        public string? Id { get; init; }
        public string? Version { get; init; }
        public string? ProvenanceCommit { get; init; }
    }

    private sealed class DerivedManifestDocument
    {
        public string? Id { get; init; }
        public string? Kind { get; init; }
        public string? VersionTemplate { get; init; }
    }

    private sealed class SourceDocument
    {
        public string? Type { get; init; }
        public string? Channel { get; init; }
        public string? Policy { get; init; }
        public string? Package { get; init; }
        public string? Version { get; init; }
        public string? Repository { get; init; }
        public string? Branch { get; init; }
        public string? Ref { get; init; }
        public string? TagPrefix { get; init; }
    }

    private sealed class ReferenceSetDocument
    {
        public string? Id { get; init; }
        public string? Package { get; init; }
    }

    private sealed class PlatformDocument
    {
        public string? Os { get; init; }
        public string? Libc { get; init; }
        public string? Architecture { get; init; }
    }

    private sealed class UpdateDocument
    {
        public string? PollInterval { get; init; }
        public bool? AutoPromoteAfterTests { get; init; }
        public bool? RetainLastKnownGood { get; init; }
    }
}

internal sealed record ReleaseInputIntent(string Id, string Version, string? ProvenanceCommit);

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SharpLabNext.Contracts;

namespace SharpLabNext.LanguageWorker.Sdk;

public static partial class LanguageWorkerCapabilityManifestSerializer
{
    // Capability manifests are packaged configuration documents and retain
    // their canonical lower-camel schema; they are not business HTTP DTOs.
    private static readonly JsonSerializerOptions JsonOptions = new(ContractJson.CreateCanonicalSerializerOptions())
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static LanguageWorkerCapabilityManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static LanguageWorkerCapabilityManifest Load(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var manifest = JsonSerializer.Deserialize<LanguageWorkerCapabilityManifest>(content, JsonOptions) ?? throw new InvalidDataException("The language worker capability manifest is empty.");
        Validate(manifest);
        return manifest;
    }

    public static void Validate(LanguageWorkerCapabilityManifest manifest, ServiceIdentity? serviceIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != 2)
            throw new ArgumentException("The capability manifest schema version must be 2.", nameof(manifest));

        ValidateId(manifest.WorkerId, nameof(manifest.WorkerId));
        ValidateId(manifest.LanguageId, nameof(manifest.LanguageId));
        ValidateIds(manifest.ToolchainIds, nameof(manifest.ToolchainIds));
        if (!ProtocolVersionRegex().IsMatch(manifest.ProtocolVersion))
            throw new ArgumentException("ProtocolVersion must use the '<major>.<minor>' form.", nameof(manifest));

        ValidateIds(manifest.Capabilities, nameof(manifest.Capabilities));
        ValidateIds(manifest.ProducedArtifactFormats, nameof(manifest.ProducedArtifactFormats));
        ValidateIds(manifest.SupportedReferenceSetIds, nameof(manifest.SupportedReferenceSetIds));
        ValidateLimits(manifest.Limits);

        if (serviceIdentity is null)
            return;
        if (serviceIdentity.Kind != ServiceKind.ToolchainWorker)
            throw new ArgumentException("Language workers must use ToolchainWorker service identity.", nameof(serviceIdentity));
        if (!string.Equals(serviceIdentity.Id, manifest.WorkerId, StringComparison.Ordinal))
            throw new ArgumentException("The capability manifest does not match the service identity.", nameof(manifest));
        if (!string.Equals(serviceIdentity.Protocol.ToString(), manifest.ProtocolVersion, StringComparison.Ordinal))
            throw new ArgumentException("The capability manifest protocol does not match the service identity.", nameof(manifest));

        var advertisedCapabilities = new HashSet<string>(serviceIdentity.Capabilities, StringComparer.Ordinal);
        if (manifest.Capabilities.Any(capability => !advertisedCapabilities.Contains(capability)))
            throw new ArgumentException("The service identity must advertise every manifest capability.", nameof(serviceIdentity));
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !IdRegex().IsMatch(value))
            throw new ArgumentException($"{name} is not a valid stable ID.", name);
    }

    private static void ValidateIds(IReadOnlyList<string> values, string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            throw new ArgumentException($"{name} cannot be empty.", name);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ValidateId(value, name);
            if (!distinct.Add(value))
                throw new ArgumentException($"{name} cannot contain duplicate IDs.", name);
        }
    }

    private static void ValidateLimits(LanguageWorkerLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumFiles <= 0 || limits.MaximumSourceUtf8Bytes <= 0 || limits.MaximumArtifactBytes <= 0 || limits.MaximumConcurrentBuilds <= 0 || limits.MaximumBuildMilliseconds <= 0 || limits.MaximumLspMessageBytes <= 0)
        {
            throw new ArgumentException("Every language worker limit must be positive.", nameof(limits));
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolVersionRegex();
}

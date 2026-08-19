using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker.Sdk;

public static partial class ArtifactWorkerCapabilityManifestSerializer
{
    // Capability manifests are packaged configuration documents and retain
    // their canonical lower-camel schema; they are not business HTTP DTOs.
    private static readonly JsonSerializerOptions JsonOptions = new(ContractJson.CreateCanonicalSerializerOptions())
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ArtifactWorkerCapabilityManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static ArtifactWorkerCapabilityManifest Load(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var manifest = JsonSerializer.Deserialize<ArtifactWorkerCapabilityManifest>(content, JsonOptions)
            ?? throw new InvalidDataException("The artifact worker capability manifest is empty.");
        Validate(manifest);
        return manifest;
    }

    public static void Validate(
        ArtifactWorkerCapabilityManifest manifest,
        ServiceIdentity? serviceIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != 1)
            throw new ArgumentException("The capability manifest schema version must be 1.", nameof(manifest));

        ValidateId(manifest.WorkerId, nameof(manifest.WorkerId));
        if (!ProtocolVersionRegex().IsMatch(manifest.ProtocolVersion))
            throw new ArgumentException("ProtocolVersion must use the '<major>.<minor>' form.", nameof(manifest));
        ValidateIds(manifest.Capabilities, nameof(manifest.Capabilities), requireValues: true);
        ValidateIds(manifest.AcceptedArtifactFormats, nameof(manifest.AcceptedArtifactFormats), requireValues: true);
        ValidateIds(manifest.ProducedArtifactFormats, nameof(manifest.ProducedArtifactFormats), requireValues: true);
        ValidateIds(manifest.TransformIds, nameof(manifest.TransformIds), requireValues: false);
        ValidateIds(manifest.RenderOutputIds, nameof(manifest.RenderOutputIds), requireValues: false);
        ValidateIds(manifest.VerificationProfileIds, nameof(manifest.VerificationProfileIds), requireValues: false);
        if (manifest.TransformIds.Count + manifest.RenderOutputIds.Count + manifest.VerificationProfileIds.Count == 0)
            throw new ArgumentException("An artifact worker must declare at least one operation.", nameof(manifest));
        ValidateLimits(manifest.Limits);

        if (serviceIdentity is null)
            return;
        if (serviceIdentity.Kind != ServiceKind.ArtifactWorker)
            throw new ArgumentException("Artifact workers must use ArtifactWorker service identity.", nameof(serviceIdentity));
        if (!string.Equals(serviceIdentity.Id, manifest.WorkerId, StringComparison.Ordinal))
            throw new ArgumentException("The capability manifest does not match the service identity.", nameof(manifest));
        if (!string.Equals(serviceIdentity.Protocol.ToString(), manifest.ProtocolVersion, StringComparison.Ordinal))
            throw new ArgumentException("The capability manifest protocol does not match the service identity.", nameof(manifest));

        var advertised = new HashSet<string>(serviceIdentity.Capabilities, StringComparer.Ordinal);
        if (manifest.Capabilities.Any(capability => !advertised.Contains(capability)))
            throw new ArgumentException("The service identity must advertise every manifest capability.", nameof(serviceIdentity));
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !IdRegex().IsMatch(value))
            throw new ArgumentException($"{name} is not a valid stable ID.", name);
    }

    private static void ValidateIds(IReadOnlyList<string> values, string name, bool requireValues)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (requireValues && values.Count == 0)
            throw new ArgumentException($"{name} cannot be empty.", name);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ValidateId(value, name);
            if (!distinct.Add(value))
                throw new ArgumentException($"{name} cannot contain duplicate IDs.", name);
        }
    }

    private static void ValidateLimits(ArtifactWorkerLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumInputArtifactBytes <= 0 ||
            limits.MaximumOutputArtifactBytes <= 0 ||
            limits.MaximumConcurrentOperations <= 0 ||
            limits.MaximumOperationMilliseconds <= 0 ||
            limits.MaximumRetainedOperations <= 0 ||
            limits.MaximumEventsPerOperation < 8)
        {
            throw new ArgumentException("Artifact worker limits are invalid.", nameof(limits));
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolVersionRegex();
}

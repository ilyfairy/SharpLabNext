using System.Text.Json.Serialization;

namespace SharpLabNext.Contracts;

public sealed record NegotiateRequest(
    string CallerServiceId,
    string ExpectedReleaseId,
    string? ExpectedWorkerImageId,
    IReadOnlyList<ProtocolVersion> SupportedProtocolVersions,
    IReadOnlyList<string> RequiredCapabilities);

public sealed record DescribeRequest(
    string CallerServiceId,
    string? ExpectedReleaseId = null);

public sealed record HealthRequest(
    bool IncludeChecks = true);

public sealed record WorkerDescriptor(
    ServiceIdentity Service,
    string InstanceId,
    WorkerKind WorkerKind,
    string WorkerImageId,
    ProtocolVersion NegotiatedProtocol,
    IReadOnlyList<ProtocolVersion> SupportedProtocolVersions,
    IReadOnlyList<WorkerCapabilityDescriptor> Capabilities,
    IReadOnlyList<string> ProfileIds,
    DateTimeOffset StartedAtUtc,
    IReadOnlyDictionary<string, string>? Identity = null,
    IReadOnlyList<ReferenceSetAttestation>? ReferenceSets = null);

public sealed record ReferenceSetAttestation(
    string Id,
    string TargetFramework,
    string Digest,
    string ContentDigest,
    ReferenceSetProvenance Provenance);

public sealed record ReferenceSetProvenance(
    string Kind,
    string ResolvedVersion,
    string? Package = null,
    string? SourceUri = null,
    string? Commit = null,
    string? SourceArchiveDigest = null,
    IReadOnlyList<ReferenceSetProvenanceSource>? Sources = null);

public sealed record ReferenceSetProvenanceSource(
    string Role,
    string Selection,
    string Package,
    string ResolvedVersion,
    string SourceUri,
    string SourceArchiveDigest,
    string PackageContentHash);

public sealed record WorkerCapabilityDescriptor(
    string Id,
    int ContractVersion,
    bool Available,
    IReadOnlyList<string> ProfileIds,
    string? UnavailableReason = null);

public sealed record HealthResponse(
    HealthStatus Status,
    string ServiceId,
    string InstanceId,
    ProtocolVersion Protocol,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<HealthCheckResult> Checks);

public sealed record HealthCheckResult(
    string Name,
    HealthStatus Status,
    string? PublicMessage,
    TimeSpan? Duration);

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<WorkerKind>))]
public enum WorkerKind
{
    Toolchain,
    ArtifactProcessor,
    RuntimeSupervisor
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<HealthStatus>))]
public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy
}

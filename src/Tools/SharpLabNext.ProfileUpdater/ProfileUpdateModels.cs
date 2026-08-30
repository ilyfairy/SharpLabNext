using System.Text.Json.Serialization;

namespace SharpLabNext.ProfileUpdater;

[JsonConverter(typeof(JsonStringEnumConverter<ProfileUpdateStage>))]
public enum ProfileUpdateStage
{
    Check,
    Resolve,
    Build,
    Test,
    Promote
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileUpdateStageStatus>))]
public enum ProfileUpdateStageStatus
{
    Succeeded,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileUpdateTestScope>))]
public enum ProfileUpdateTestScope
{
    Affected,
    Full
}

public sealed record ProfileUpdateExecutedCommand
{
    public required string FileName { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required int ExitCode { get; init; }
}

public sealed record ProfileUpdateStageReceipt
{
    public required ProfileUpdateStage Stage { get; init; }
    public required ProfileUpdateStageStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public string? Configuration { get; init; }
    public ProfileUpdateTestScope? TestScope { get; init; }
    public IReadOnlyList<ProfileUpdateExecutedCommand> Commands { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record ProfileUpdateReceipt
{
    public required int SchemaVersion { get; init; }
    public required string ReleaseId { get; init; }
    public required string SourceDigest { get; init; }
    public required string CandidateDigest { get; init; }
    public required string CandidatePath { get; init; }
    public required string WorkspacePath { get; init; }
    public string? MaterialDigest { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required IReadOnlyList<ReleaseLockChange> Changes { get; init; }
    public required IReadOnlyList<ProfileUpdateStageReceipt> Stages { get; init; }
}

public sealed record ProfileUpdaterState
{
    public required int SchemaVersion { get; init; }
    public required string ActiveReleaseId { get; init; }
    public required string ActiveLockDigest { get; init; }
    public string? LatestCandidateReleaseId { get; init; }
    public string? LatestCandidateDigest { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? LastKnownGoodReleaseId { get; init; }
    public string? LastKnownGoodDigest { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required ProfileUpdateStageReceipt LastStage { get; init; }
}

public sealed record ProfileUpdateCheckResult(string SourceDigest, string ReleaseId, bool Changed, IReadOnlyList<ReleaseLockChange> Changes, ProfileUpdateStageReceipt Stage);

public sealed record ProfileUpdateCandidateResult(string CandidateDigest, string CandidatePath, ProfileUpdateReceipt Receipt);

public sealed record ProfileUpdateStageResult(string CandidateDigest, string CandidatePath, ProfileUpdateReceipt Receipt, ProfileUpdateStageReceipt Stage);

public sealed class ProfileUpdateCommandFailedException(ProfileUpdateExternalCommand command, int exitCode) : Exception($"External command '{command.FileName}' failed with exit code {exitCode}.")
{
    public ProfileUpdateExternalCommand Command { get; } = command;
    public int ExitCode { get; } = exitCode;
}

public sealed class ProfileUpdateValidationException(string message) : Exception(message);

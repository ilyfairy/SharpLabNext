using System.Text.Json.Serialization;

namespace SharpLabNext.Contracts;

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<ProfileUpdateStatusKind>))]
public enum ProfileUpdateStatusKind
{
    Unknown,
    NotChecked,
    UpToDate,
    UpdateAvailable,
    CandidateInProgress,
    CandidateFailed,
    CandidateApproved
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<ProfileUpdatePublicStage>))]
public enum ProfileUpdatePublicStage
{
    None,
    Check,
    Resolve,
    Build,
    Test,
    Promote
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<ProfileUpdatePublicStageOutcome>))]
public enum ProfileUpdatePublicStageOutcome
{
    NotChecked,
    Succeeded,
    Failed
}

public sealed record ProfileUpdateReleaseStatus
{
    public required string ReleaseId { get; init; }
    public string? LockDigest { get; init; }
}

public sealed record ProfileUpdatePublicError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record ProfileUpdatePublicStageStatus
{
    public required ProfileUpdatePublicStage Stage { get; init; }
    public required ProfileUpdatePublicStageOutcome Outcome { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public ProfileUpdatePublicError? Error { get; init; }
}

public sealed record ProfileUpdateStatusDocument
{
    public required int SchemaVersion { get; init; }
    public required ProfileUpdateStatusKind Status { get; init; }
    public required bool Checked { get; init; }
    public required ProfileUpdateReleaseStatus Active { get; init; }
    public ProfileUpdateReleaseStatus? LastKnownGood { get; init; }
    public ProfileUpdateReleaseStatus? Candidate { get; init; }
    public bool? UpdateAvailable { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required ProfileUpdatePublicStageStatus LastStage { get; init; }
}

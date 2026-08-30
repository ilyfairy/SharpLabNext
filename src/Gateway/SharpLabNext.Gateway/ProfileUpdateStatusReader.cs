using System.Text.Json;
using SharpLabNext.Contracts;

namespace SharpLabNext.Gateway;

internal sealed record ProfileUpdateStatusOptions(string StatusPath)
{
    public const string SectionName = "ProfileUpdates";
}

internal sealed class ProfileUpdateStatusReader(ProfileUpdateStatusOptions options)
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumStatusBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateCanonicalSerializerOptions();

    public async Task<ProfileUpdateStatusDocument> ReadAsync(string activeReleaseId, CancellationToken cancellationToken = default)
    {
        try
        {
            var file = new FileInfo(options.StatusPath);
            if (!file.Exists || file.Length is <= 0 or > MaximumStatusBytes)
                return CreateUnknown(activeReleaseId);

            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<ProfileUpdateStatusDocument>(stream, JsonOptions, cancellationToken);
            return document is not null && IsValid(document, activeReleaseId)
                ? CreatePublicProjection(document) : CreateUnknown(activeReleaseId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CreateUnknown(activeReleaseId);
        }
    }

    internal static ProfileUpdateStatusDocument CreateUnknown(string activeReleaseId) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        Status = ProfileUpdateStatusKind.Unknown,
        Checked = false,
        Active = new ProfileUpdateReleaseStatus { ReleaseId = activeReleaseId },
        UpdateAvailable = null,
        UpdatedAt = DateTimeOffset.UtcNow,
        LastStage = new ProfileUpdatePublicStageStatus { Stage = ProfileUpdatePublicStage.None, Outcome = ProfileUpdatePublicStageOutcome.NotChecked }
    };

    private static bool IsValid(ProfileUpdateStatusDocument document, string activeReleaseId) =>
        document.SchemaVersion == CurrentSchemaVersion &&
        Enum.IsDefined(document.Status) &&
        IsValidRelease(document.Active) &&
        string.Equals(document.Active.ReleaseId, activeReleaseId, StringComparison.Ordinal) &&
        IsValidRelease(document.LastKnownGood) &&
        IsValidRelease(document.Candidate) &&
        Enum.IsDefined(document.LastStage.Stage) &&
        Enum.IsDefined(document.LastStage.Outcome);

    private static bool IsValidRelease(ProfileUpdateReleaseStatus? release) =>
        release is null ||
        (IsSafeId(release.ReleaseId) && IsValidDigest(release.LockDigest));

    private static bool IsSafeId(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsValidDigest(string? value)
    {
        if (value is null)
            return true;
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;
        foreach (var character in value.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static ProfileUpdateStatusDocument CreatePublicProjection(ProfileUpdateStatusDocument source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Status = source.Status,
        Checked = source.Checked,
        Active = CopyRelease(source.Active)!,
        LastKnownGood = CopyRelease(source.LastKnownGood),
        Candidate = CopyRelease(source.Candidate),
        UpdateAvailable = source.UpdateAvailable,
        CheckedAt = source.CheckedAt,
        UpdatedAt = source.UpdatedAt,
        LastStage = new ProfileUpdatePublicStageStatus { Stage = source.LastStage.Stage, Outcome = source.LastStage.Outcome, StartedAt = source.LastStage.StartedAt, CompletedAt = source.LastStage.CompletedAt, Error = source.LastStage.Outcome == ProfileUpdatePublicStageOutcome.Failed ? CreatePublicError(source.LastStage.Error) : null }
    };

    private static ProfileUpdateReleaseStatus? CopyRelease(ProfileUpdateReleaseStatus? source) =>
        source is null
            ? null : new ProfileUpdateReleaseStatus { ReleaseId = source.ReleaseId, LockDigest = source.LockDigest };

    private static ProfileUpdatePublicError CreatePublicError(ProfileUpdatePublicError? source) =>
        source?.Code switch
        {
            "profile-update.check-failed" => PublicError(source.Code, "Profile update check failed; update availability is unknown."),
            "profile-update.resolve-failed" => PublicError(source.Code, "Profile candidate resolution failed; the approved release remains active."),
            "profile-update.build-failed" => PublicError(source.Code, "Profile candidate build failed; the approved release remains active."),
            "profile-update.test-failed" => PublicError(source.Code, "Profile candidate validation failed; the approved release remains active."),
            "profile-update.promote-failed" => PublicError(source.Code, "Profile candidate promotion failed; the previous approved release remains active."),
            "profile-update.failed" => PublicError(source.Code, "Profile update failed; the approved release remains active."),
            _ => PublicError("profile-update.failed", "The profile update check did not complete successfully.")
        };

    private static ProfileUpdatePublicError PublicError(string code, string message) => new()
    {
        Code = code,
        Message = message
    };
}

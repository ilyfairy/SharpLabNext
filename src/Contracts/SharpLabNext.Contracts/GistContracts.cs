namespace SharpLabNext.Contracts;

public sealed record GitHubAuthStatus(bool Available, bool Authenticated, string? Login, string? CsrfToken);

public sealed record GitHubOAuthStartResponse(string AuthorizationUrl);

public sealed record GistSourceFile(string Path, string Text);

public sealed record GistWorkspaceState(
    int SchemaVersion,
    string LanguageId,
    string? ToolchainId,
    string? ReferenceSetId,
    string OutputId,
    string? RuntimeId,
    BuildConfiguration BuildMode,
    string? ReleaseId,
    string ActiveFile,
    IReadOnlyList<string> SourceOrder,
    IReadOnlyList<GistSourceFile> Files,
    string? LegacyBranchId = null);

public sealed record CreateGistRequest(string Description, bool IsPublic, GistWorkspaceState Workspace);

public sealed record UpdateGistRequest(string Description, GistWorkspaceState Workspace);

public sealed record GistDocument(
    string Id,
    string HtmlUrl,
    string? OwnerLogin,
    bool IsPublic,
    bool CanUpdate,
    string Description,
    string SourceFormat,
    GistWorkspaceState Workspace,
    IReadOnlyList<string> Warnings,
    DateTimeOffset? UpdatedAtUtc);

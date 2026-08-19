using System.Text.Json.Serialization;

namespace SharpLabNext.Contracts;

public sealed record OpenLanguageSessionRequest(
    string RequestId,
    string PipelineResolutionId,
    string LanguageId,
    string ToolchainId,
    string ReferenceSetId,
    WorkspaceSnapshot Workspace,
    string LspVersion = ContractSchemaVersions.Lsp);

public sealed record LanguageSession(
    string SessionId,
    string LanguageId,
    string ToolchainId,
    string CompilerBuildIdentity,
    string LspVersion,
    long WorkspaceRevision,
    long SelectionRevision,
    DateTimeOffset ExpiresAtUtc);

public sealed record CloseLanguageSessionRequest(
    string SessionId,
    string? Reason = null);

public sealed record LanguageFrame(
    string SessionId,
    long Sequence,
    LanguageFrameDirection Direction,
    string ContentType,
    byte[] Payload,
    bool EndOfStream = false);

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<LanguageFrameDirection>))]
public enum LanguageFrameDirection
{
    ClientToWorker,
    WorkerToClient
}

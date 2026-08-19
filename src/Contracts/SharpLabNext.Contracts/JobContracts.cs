using System.Text.Json.Serialization;

namespace SharpLabNext.Contracts;

public sealed record BuildRequest(
    string RequestId,
    string IdempotencyKey,
    string PipelineResolutionId,
    string ToolchainId,
    string ReferenceSetId,
    WorkspaceSnapshot Workspace,
    DateTimeOffset DeadlineUtc,
    BuildOptions? Options = null,
    BuildTarget Target = BuildTarget.Artifact)
{
    [JsonIgnore]
    public BuildOptions EffectiveOptions => Options ?? Workspace.BuildOptions;
}

public sealed record TransformArtifactRequest(
    string RequestId,
    string IdempotencyKey,
    string PipelineResolutionId,
    ArtifactRef ArtifactRef,
    string ProcessorId,
    string TransformId,
    TransformArtifactOptions Options,
    DateTimeOffset DeadlineUtc);

public sealed record RenderArtifactRequest(
    string RequestId,
    string IdempotencyKey,
    string PipelineResolutionId,
    ArtifactRef ArtifactRef,
    string ProcessorId,
    string OutputId,
    RenderArtifactOptions Options,
    DateTimeOffset DeadlineUtc);

public sealed record VerifyArtifactRequest(
    string RequestId,
    string IdempotencyKey,
    string PipelineResolutionId,
    ArtifactRef ArtifactRef,
    string ProcessorId,
    VerifyArtifactOptions Options,
    DateTimeOffset DeadlineUtc);

public sealed record RunRequest(
    string RequestId,
    string IdempotencyKey,
    string PipelineResolutionId,
    ArtifactRef ArtifactRef,
    string RuntimeProfileId,
    RunOptions Options,
    DateTimeOffset DeadlineUtc);

public sealed record JitRequest(
    string RequestId,
    string IdempotencyKey,
    string PipelineResolutionId,
    ArtifactRef ArtifactRef,
    string RuntimeProfileId,
    JitOptions Options,
    DateTimeOffset DeadlineUtc);

public sealed record ExplainRequest(
    string RequestId,
    string IdempotencyKey,
    string PipelineResolutionId,
    WorkspaceSnapshot Workspace,
    DateTimeOffset DeadlineUtc);

public sealed record TransformArtifactOptions(
    bool PreservePortablePdb = true,
    bool PreserveSequencePoints = true,
    string? RewriterProfileId = null);

public sealed record RenderArtifactOptions(
    bool IncludeSequencePoints = true,
    bool IncludeCompilerGeneratedMembers = true,
    int MaxCharacters = 1_000_000);

public sealed record VerifyArtifactOptions(
    string VerificationProfileId,
    bool IncludeMetadataTokens = true,
    int MaxFindings = 1_000);

public sealed record RunOptions(
    IReadOnlyList<string> Arguments,
    string? Stdin,
    RunInstrumentation Instrumentation,
    string SecurityPolicyId);

public sealed record JitOptions(
    string? MethodFilter,
    string TieringPolicyId,
    string PgoPolicyId,
    string ProviderId,
    string SecurityPolicyId);

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<BuildTarget>))]
public enum BuildTarget
{
    Artifact,
    CompileCheck,
    Ast,
    GeneratedSource
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<RunInstrumentation>))]
public enum RunInstrumentation
{
    None,
    Inspection,
    ExecutionFlow
}

[JsonConverter(typeof(OperationResultJsonConverter))]
public record OperationResult;

public sealed record BuildResult(
    BuildOutcome Outcome,
    ArtifactRef? ArtifactRef,
    IReadOnlyList<Diagnostic> Diagnostics,
    BuildIdentity Identity,
    long WorkspaceRevision,
    long SelectionRevision) : OperationResult;

public sealed record CompilationCheckResult(
    bool CompilationSucceeded,
    IReadOnlyList<Diagnostic> Diagnostics,
    BuildIdentity Identity,
    long WorkspaceRevision,
    long SelectionRevision) : OperationResult;

public sealed record AstResult(
    AstDocument Document,
    BuildIdentity? Identity = null) : OperationResult;

public sealed record GeneratedSourceResult(
    IReadOnlyList<GeneratedSourceDocument> Documents,
    BuildIdentity Identity,
    long WorkspaceRevision,
    long SelectionRevision) : OperationResult;

public sealed record TransformArtifactResult(
    ArtifactJobOutcome Outcome,
    ArtifactRef? ArtifactRef,
    ArtifactRef SourceArtifactRef,
    string? ArtifactFormat,
    IReadOnlyList<Diagnostic> Diagnostics,
    ArtifactProcessorIdentity? Identity = null) : OperationResult;

public sealed record RenderArtifactResult(
    ArtifactJobOutcome Outcome,
    ContentRef? ContentRef,
    string MediaType,
    IReadOnlyList<LinkedRange> LinkedRanges,
    IReadOnlyList<Diagnostic> Diagnostics,
    ArtifactProcessorIdentity? Identity = null) : OperationResult;

public sealed record VerifyArtifactResult(
    ArtifactVerificationOutcome Outcome,
    IReadOnlyList<VerificationFinding> Findings,
    string VerifierId,
    string VerifierVersion,
    ArtifactProcessorIdentity? Identity = null) : OperationResult;

public sealed record RunResult(
    RunTerminalStatus Status,
    int? ExitCode,
    UserExceptionInfo? Exception,
    TimeSpan Elapsed,
    bool OutputTruncated,
    RuntimeIdentity Identity) : OperationResult;

public sealed record JitResult(
    JitTerminalStatus Status,
    ContentRef? StructuredDocumentRef,
    ContentRef? RawTextRef,
    IReadOnlyList<JitMethodSummary> Methods,
    TimeSpan Elapsed,
    JitIdentity Identity) : OperationResult;

public sealed record ExplainResult(
    ExplanationDocument Document,
    BuildIdentity? Identity = null) : OperationResult;

public sealed record ExplanationDocument(
    string LanguageId,
    string ToolchainId,
    long WorkspaceRevision,
    long SelectionRevision,
    IReadOnlyList<ExplanationFile> Files,
    bool Truncated);

public sealed record ExplanationFile(
    string Path,
    IReadOnlyList<ExplanationNode> Nodes);

public sealed record ExplanationNode(
    string Kind,
    string Title,
    string Description,
    TextRange Range,
    int Depth);

public sealed record BuildIdentity(
    string ReleaseId,
    string LanguageId,
    string ToolchainId,
    string CompilerVersion,
    string? CompilerCommit,
    string ReferenceSetId,
    string WorkerImageId);

public sealed record ArtifactProcessorIdentity(
    string ReleaseId,
    string ProcessorId,
    string ProcessorVersion,
    string WorkerImageId);

public sealed record AstDocument(
    string LanguageId,
    string ToolchainId,
    long WorkspaceRevision,
    AstNode Root,
    bool Truncated);

public sealed record AstNode(
    string Kind,
    TextRange Range,
    TextRange? FullRange,
    IReadOnlyDictionary<string, string?> Properties,
    IReadOnlyList<AstNode> Children);

public sealed record GeneratedSourceDocument(
    string Path,
    ContentRef ContentRef,
    string LanguageId,
    string GeneratorId);

public sealed record LinkedRange(
    string? SourceFilePath,
    TextRange? SourceRange,
    TextRange OutputRange,
    string? Precision = null);

public sealed record VerificationFinding(
    string Code,
    string Message,
    string? TypeName,
    string? MethodName,
    int? MetadataToken,
    string? FilePath,
    TextRange? Range);

public sealed record UserExceptionInfo(
    string TypeName,
    string Message,
    string? StackTrace,
    UserExceptionInfo? InnerException);

public sealed record RuntimeIdentity(
    string RuntimeVersion,
    string RuntimeCommit,
    string RuntimeImageId,
    string Rid,
    string Architecture);

public sealed record JitIdentity(
    string RuntimeVersion,
    string RuntimeCommit,
    string JitVersion,
    string JitCommit,
    string RuntimeImageId,
    string Rid,
    string Architecture,
    string CpuFeatureProfile,
    string TieringPolicy,
    string PgoPolicy,
    string JitProvider,
    string InspectionMethod);

public sealed record JitMethodSummary(
    string MethodId,
    string DisplayName,
    int NativeCodeSize,
    int InstructionCount,
    IReadOnlyList<LinkedRange> LinkedRanges);

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<BuildOutcome>))]
public enum BuildOutcome
{
    Succeeded,
    CompilationFailed,
    EmitFailed
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<ArtifactJobOutcome>))]
public enum ArtifactJobOutcome
{
    Succeeded,
    UnsupportedArtifact,
    InvalidArtifact,
    LimitExceeded
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<ArtifactVerificationOutcome>))]
public enum ArtifactVerificationOutcome
{
    Valid,
    Findings,
    UnsupportedArtifact,
    InvalidArtifact,
    LimitExceeded
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<RunTerminalStatus>))]
public enum RunTerminalStatus
{
    Completed,
    UserException,
    NonZeroExit,
    Timeout,
    OutOfMemory,
    ProcessCrash,
    Cancelled,
    OutputLimitExceeded
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<JitTerminalStatus>))]
public enum JitTerminalStatus
{
    Completed,
    NoMatchingMethods,
    InspectionFailed,
    Timeout,
    OutOfMemory,
    ProcessCrash,
    Cancelled,
    OutputLimitExceeded
}

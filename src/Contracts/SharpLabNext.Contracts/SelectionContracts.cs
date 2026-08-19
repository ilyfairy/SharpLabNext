using System.Text.Json.Serialization;

namespace SharpLabNext.Contracts;

public sealed record ResolveSelectionRequest(
    string LanguageId,
    string? ToolchainId,
    string? ReferenceSetId,
    string OutputId,
    string? RuntimeId,
    BuildConfiguration BuildMode,
    string CatalogRevision,
    long WorkspaceRevision);

public sealed record ResolveSelectionResponse(
    ResolvedSelection EffectiveSelection,
    IReadOnlyList<SelectionChange> SelectionChanges,
    EffectiveCapabilities EffectiveCapabilities,
    string PipelineResolutionId,
    PipelinePlanDescriptor PipelinePlan,
    DateTimeOffset ExpiresAt);

public sealed record ResolvedSelection(
    string LanguageId,
    string ToolchainId,
    string ReferenceSetId,
    string OutputId,
    string? RuntimeId);

public sealed record SelectionChange(
    SelectionField Field,
    string? RequestedValue,
    string? EffectiveValue,
    SelectionChangeReason Reason,
    string Message);

public sealed record EffectiveCapabilities(
    IReadOnlyList<string> LanguageServerCapabilities,
    IReadOnlyList<string> BuildCapabilities,
    IReadOnlyList<string> OutputCapabilities,
    IReadOnlyList<string> RuntimeCapabilities);

public sealed record PipelinePlanDescriptor(
    string ReleaseId,
    string LanguageWorkerId,
    string CompilerWorkerId,
    string ReferenceSetId,
    IReadOnlyList<PipelineStageDescriptor> Stages,
    string? RuntimeId,
    string SecurityPolicyId,
    IReadOnlyList<string> WorkerImageIds);

public sealed record PipelineStageDescriptor(
    string Id,
    PipelineStageKind Kind,
    string ProviderId,
    string? InputArtifactFormat,
    string? OutputArtifactFormat);

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<SelectionField>))]
public enum SelectionField
{
    Language,
    Toolchain,
    ReferenceSet,
    Output,
    Runtime,
    BuildMode
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<SelectionChangeReason>))]
public enum SelectionChangeReason
{
    DefaultApplied,
    LegacyAliasResolved,
    UnsupportedByLanguage,
    IncompatibleReferenceSet,
    IncompatibleArtifact,
    RuntimeNotRequired,
    ProfileUnavailable
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<PipelineStageKind>))]
public enum PipelineStageKind
{
    Build,
    Transform,
    Render,
    Verify,
    Run,
    Jit,
    Explain
}

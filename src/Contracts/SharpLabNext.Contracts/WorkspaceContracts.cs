using System.Text.Json.Serialization;

namespace SharpLabNext.Contracts;

public sealed record WorkspaceSnapshot(
    int SchemaVersion,
    long Revision,
    long SelectionRevision,
    string LanguageId,
    IReadOnlyList<WorkspaceFile> Files,
    string ActiveFile,
    IReadOnlyList<string> SourceOrder,
    string ReferenceSetId,
    BuildOptions BuildOptions);

public sealed record WorkspaceFile(
    string Path,
    long Version,
    string Text);

public sealed record BuildOptions(
    BuildConfiguration Configuration,
    bool Optimize,
    BuildOutputKind OutputKind,
    bool AllowUnsafe,
    bool EmitPortablePdb,
    NullableContextMode NullableContext = NullableContextMode.ProjectDefault,
    string? LanguageVersion = null,
    IReadOnlyList<string>? PreprocessorSymbols = null,
    bool CheckOverflow = false);

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<BuildConfiguration>))]
public enum BuildConfiguration
{
    Debug,
    Release
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<BuildOutputKind>))]
public enum BuildOutputKind
{
    Console,
    Library,
    WindowsApplication,
    Auto
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<NullableContextMode>))]
public enum NullableContextMode
{
    ProjectDefault,
    Disable,
    Enable,
    Warnings,
    Annotations
}

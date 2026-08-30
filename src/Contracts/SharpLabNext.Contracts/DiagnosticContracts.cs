using System.Text.Json.Serialization;

namespace SharpLabNext.Contracts;

public sealed record TextRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter);

public sealed record Diagnostic(
    string Source,
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? FilePath,
    TextRange? Range,
    IReadOnlyList<DiagnosticRelatedInformation> RelatedInformation,
    IReadOnlyList<DiagnosticTag> Tags,
    long WorkspaceRevision,
    long SelectionRevision);

public sealed record DiagnosticRelatedInformation(string Message, string? FilePath, TextRange Range);

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<DiagnosticSeverity>))]
public enum DiagnosticSeverity
{
    Hidden,
    Information,
    Warning,
    Error
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<DiagnosticTag>))]
public enum DiagnosticTag
{
    Unnecessary,
    Deprecated
}

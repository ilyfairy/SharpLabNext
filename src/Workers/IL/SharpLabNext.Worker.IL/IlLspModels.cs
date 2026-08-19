namespace SharpLabNext.Worker.IL;

public sealed record IlLspPosition(int Line, int Character);
public sealed record IlLspRange(IlLspPosition Start, IlLspPosition End);
public sealed record IlLspTextDocumentIdentifier(string Uri);
public sealed record IlLspVersionedTextDocumentIdentifier(string Uri, long Version);
public sealed record IlLspTextDocumentItem(string Uri, string LanguageId, long Version, string Text);
public sealed record IlLspDidOpenParams(IlLspTextDocumentItem TextDocument);
public sealed record IlLspTextChange(IlLspRange? Range, int? RangeLength, string Text);
public sealed record IlLspDidChangeParams(
    IlLspVersionedTextDocumentIdentifier TextDocument,
    IReadOnlyList<IlLspTextChange> ContentChanges);
public sealed record IlLspDidCloseParams(IlLspTextDocumentIdentifier TextDocument);
public sealed record IlLspTextDocumentPositionParams(
    IlLspTextDocumentIdentifier TextDocument,
    IlLspPosition Position);
public sealed record IlLspWorkspaceSymbolParams(string Query);
public sealed record IlLspCompletionParams(
    IlLspTextDocumentIdentifier TextDocument,
    IlLspPosition Position,
    IlLspCompletionContext? Context);
public sealed record IlLspCompletionContext(int TriggerKind, string? TriggerCharacter);
public sealed record IlLspCompletionList(bool IsIncomplete, IReadOnlyList<IlLspCompletionItem> Items);
public sealed record IlLspCompletionItem(
    string Label,
    int Kind,
    string Detail,
    IlLspMarkupContent? Documentation,
    string SortText,
    string FilterText,
    string InsertText,
    int InsertTextFormat,
    IlLspTextEdit TextEdit,
    IReadOnlyList<int> Tags,
    IlLspCompletionItemData Data);
public sealed record IlLspTextEdit(IlLspRange Range, string NewText);
public sealed record IlLspCompletionItemData(
    string Id,
    string Origin,
    string? OpcodeFamily,
    IReadOnlyList<string> CandidateTags,
    IReadOnlyDictionary<string, string> Properties,
    long DocumentVersion,
    long WorkspaceRevision,
    long ReferenceRevision);
public sealed record IlLspMarkupContent(string Kind, string Value);
public sealed record IlLspHover(IlLspMarkupContent Contents, IlLspRange Range);
public sealed record IlLspParameterInformation(string Label, IlLspMarkupContent? Documentation);
public sealed record IlLspSignatureInformation(
    string Label,
    IlLspMarkupContent? Documentation,
    IReadOnlyList<IlLspParameterInformation> Parameters);
public sealed record IlLspSignatureHelp(
    IReadOnlyList<IlLspSignatureInformation> Signatures,
    int ActiveSignature,
    int ActiveParameter);
public sealed record IlLspSemanticTokensParams(IlLspTextDocumentIdentifier TextDocument);
public sealed record IlLspSemanticTokens(string ResultId, IReadOnlyList<int> Data);
public sealed record IlLspDocumentSymbolParams(IlLspTextDocumentIdentifier TextDocument);
public sealed record IlLspDocumentSymbol(
    string Name,
    string? Detail,
    int Kind,
    IlLspRange Range,
    IlLspRange SelectionRange,
    IReadOnlyList<IlLspDocumentSymbol> Children);
public sealed record IlLspCodeActionParams(
    IlLspTextDocumentIdentifier TextDocument,
    IlLspRange Range,
    IlLspCodeActionContext Context);
public sealed record IlLspCodeActionContext(
    IReadOnlyList<IlLspDiagnostic>? Diagnostics,
    IReadOnlyList<string>? Only,
    int? TriggerKind = null);
public sealed record IlLspCodeAction(
    string Title,
    string Kind,
    IReadOnlyList<IlLspDiagnostic>? Diagnostics,
    bool? IsPreferred,
    IlLspWorkspaceEdit Edit,
    IlLspCodeActionData Data);
public sealed record IlLspWorkspaceEdit(
    IReadOnlyDictionary<string, IReadOnlyList<IlLspTextEdit>> Changes);
public sealed record IlLspCodeActionData(
    string Id,
    long DocumentVersion,
    long WorkspaceRevision,
    string? Diagnostic);
public sealed record IlLspWorkspaceSymbol(
    string Name,
    int Kind,
    IlLspLocation Location,
    string? ContainerName,
    IlLspWorkspaceSymbolData Data);
public sealed record IlLspWorkspaceSymbolData(
    string Id,
    string Detail,
    long WorkspaceRevision);
public sealed record IlLspFoldingRangeParams(IlLspTextDocumentIdentifier TextDocument);
public sealed record IlLspFoldingRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter, string Kind);
public sealed record IlLspDiagnostic(
    IlLspRange Range,
    int Severity,
    string Code,
    string Source,
    string Message,
    IReadOnlyList<int> Tags,
    IReadOnlyList<IlLspDiagnosticRelatedInformation> RelatedInformation,
    IlLspDiagnosticData Data);
public sealed record IlLspLocation(string Uri, IlLspRange Range);
public sealed record IlLspDiagnosticRelatedInformation(IlLspLocation Location, string Message);
public sealed record IlLspDiagnosticData(
    long WorkspaceRevision,
    long SelectionRevision,
    long DocumentVersion,
    string DiagnosticKind,
    long ReferenceRevision);
public sealed record IlLspDiagnosticsReport(
    string Uri,
    long Version,
    long WorkspaceRevision,
    long SelectionRevision,
    IReadOnlyList<IlLspDiagnostic> Diagnostics);
public sealed record IlLspDocumentState(
    string Uri,
    string Path,
    long Version,
    long WorkspaceRevision,
    long SelectionRevision);

public sealed class IlLspInvalidParamsException(string message) : IlWorkerException(message);
public sealed class IlLspContentModifiedException(string message) : IlWorkerException(message);
public sealed class IlLspMethodNotFoundException(string message) : IlWorkerException(message);
public sealed class IlLspSessionUnavailableException(string message) : IlWorkerException(message);
public sealed class IlLspLimitExceededException(string message) : IlWorkerException(message);

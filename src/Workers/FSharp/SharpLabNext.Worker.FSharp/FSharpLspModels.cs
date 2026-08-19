namespace SharpLabNext.Worker.FSharp;

public sealed record FSharpLspPosition(int Line, int Character);
public sealed record FSharpLspRange(FSharpLspPosition Start, FSharpLspPosition End);
public sealed record FSharpLspTextDocumentIdentifier(string Uri);
public sealed record FSharpLspVersionedTextDocumentIdentifier(string Uri, long Version);
public sealed record FSharpLspTextDocumentItem(string Uri, string LanguageId, long Version, string Text);
public sealed record FSharpLspDidOpenParams(FSharpLspTextDocumentItem TextDocument);
public sealed record FSharpLspTextChange(FSharpLspRange? Range, int? RangeLength, string Text);
public sealed record FSharpLspDidChangeParams(
    FSharpLspVersionedTextDocumentIdentifier TextDocument,
    IReadOnlyList<FSharpLspTextChange> ContentChanges);
public sealed record FSharpLspDidCloseParams(FSharpLspTextDocumentIdentifier TextDocument);
public sealed record FSharpLspTextDocumentPositionParams(
    FSharpLspTextDocumentIdentifier TextDocument,
    FSharpLspPosition Position);
public sealed record FSharpLspCompletionParams(
    FSharpLspTextDocumentIdentifier TextDocument,
    FSharpLspPosition Position,
    FSharpLspCompletionContext? Context);
public sealed record FSharpLspCompletionContext(int TriggerKind, string? TriggerCharacter);
public sealed record FSharpLspCompletionList(bool IsIncomplete, IReadOnlyList<FSharpLspCompletionItem> Items);
public sealed record FSharpLspCompletionItem(
    string Label,
    int Kind,
    string? Detail,
    FSharpLspMarkupContent? Documentation,
    string SortText,
    string FilterText,
    string InsertText);
public sealed record FSharpLspMarkupContent(string Kind, string Value);
public sealed record FSharpLspHover(FSharpLspMarkupContent Contents, FSharpLspRange Range);
public sealed record FSharpLspSignatureHelpParams(
    FSharpLspTextDocumentIdentifier TextDocument,
    FSharpLspPosition Position,
    FSharpLspSignatureHelpContext? Context);
public sealed record FSharpLspSignatureHelpContext(int TriggerKind, string? TriggerCharacter, bool IsRetrigger);
public sealed record FSharpLspSignatureHelp(
    IReadOnlyList<FSharpLspSignatureInformation> Signatures,
    int ActiveSignature,
    int ActiveParameter);
public sealed record FSharpLspSignatureInformation(
    string Label,
    FSharpLspMarkupContent? Documentation,
    IReadOnlyList<FSharpLspParameterInformation> Parameters,
    int ActiveParameter);
public sealed record FSharpLspParameterInformation(string Label, FSharpLspMarkupContent? Documentation);
public sealed record FSharpLspDocumentSymbolParams(FSharpLspTextDocumentIdentifier TextDocument);
public sealed record FSharpLspDocumentSymbol(
    string Name,
    string? Detail,
    int Kind,
    FSharpLspRange Range,
    FSharpLspRange SelectionRange,
    IReadOnlyList<FSharpLspDocumentSymbol> Children);
public sealed record FSharpLspSemanticTokensParams(FSharpLspTextDocumentIdentifier TextDocument);
public sealed record FSharpLspSemanticTokens(string ResultId, IReadOnlyList<int> Data);
public sealed record FSharpLspCodeActionParams(
    FSharpLspTextDocumentIdentifier TextDocument,
    FSharpLspRange Range,
    FSharpLspCodeActionContext Context);
public sealed record FSharpLspCodeActionContext(
    IReadOnlyList<FSharpLspDiagnostic> Diagnostics,
    IReadOnlyList<string>? Only,
    int? TriggerKind = null);
public sealed record FSharpLspTextEdit(FSharpLspRange Range, string NewText);
public sealed record FSharpLspWorkspaceEdit(
    IReadOnlyDictionary<string, IReadOnlyList<FSharpLspTextEdit>> Changes);
public sealed record FSharpLspCodeAction(
    string Title,
    string Kind,
    IReadOnlyList<FSharpLspDiagnostic>? Diagnostics,
    bool? IsPreferred,
    FSharpLspWorkspaceEdit Edit);
public sealed record FSharpLspDiagnostic(
    FSharpLspRange Range,
    int Severity,
    string Code,
    string Source,
    string Message,
    FSharpLspDiagnosticData Data);
public sealed record FSharpLspDiagnosticData(
    long WorkspaceRevision,
    long SelectionRevision,
    long DocumentVersion);
public sealed record FSharpLspDiagnosticsReport(
    string Uri,
    long Version,
    long WorkspaceRevision,
    long SelectionRevision,
    IReadOnlyList<FSharpLspDiagnostic> Diagnostics);
public sealed record FSharpLspDocumentState(
    string Uri,
    string Path,
    long Version,
    long WorkspaceRevision,
    long SelectionRevision);

public sealed class FSharpLspInvalidParamsException(string message) : FSharpWorkerException(message);
public sealed class FSharpLspContentModifiedException(string message) : FSharpWorkerException(message);
public sealed class FSharpLspSessionUnavailableException(string message) : FSharpWorkerException(message);
public sealed class FSharpLspLimitExceededException(string message) : FSharpWorkerException(message);

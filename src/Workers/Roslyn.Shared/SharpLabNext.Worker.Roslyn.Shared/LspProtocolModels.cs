using System.Text.Json.Serialization;

namespace SharpLabNext.Worker.Roslyn;

public sealed record LspPosition(int Line, int Character);

public sealed record LspRange(LspPosition Start, LspPosition End);

public sealed record LspTextDocumentIdentifier(string Uri);

public sealed record LspVersionedTextDocumentIdentifier(string Uri, long Version);

public sealed record LspTextDocumentItem(
    string Uri,
    string LanguageId,
    long Version,
    string Text);

public sealed record LspDidOpenTextDocumentParams(LspTextDocumentItem TextDocument);

public sealed record LspTextDocumentContentChangeEvent(
    LspRange? Range,
    int? RangeLength,
    string Text);

public sealed record LspDidChangeTextDocumentParams(
    LspVersionedTextDocumentIdentifier TextDocument,
    IReadOnlyList<LspTextDocumentContentChangeEvent> ContentChanges);

public sealed record LspDidCloseTextDocumentParams(LspTextDocumentIdentifier TextDocument);

public sealed record LspTextDocumentPositionParams(
    LspTextDocumentIdentifier TextDocument,
    LspPosition Position);

public sealed record LspCompletionParams(
    LspTextDocumentIdentifier TextDocument,
    LspPosition Position,
    LspCompletionContext? Context);

public sealed record LspCompletionContext(int TriggerKind, string? TriggerCharacter);

public sealed record LspCompletionList(
    bool IsIncomplete,
    IReadOnlyList<LspCompletionItem> Items);

public sealed record LspCompletionItem(
    string Label,
    int? Kind,
    string? Detail,
    LspMarkupContent? Documentation,
    string? SortText,
    string? FilterText,
    string? InsertText,
    int? InsertTextFormat,
    LspTextEdit? TextEdit,
    IReadOnlyList<LspTextEdit>? AdditionalTextEdits,
    LspCompletionItemData Data);

public sealed record LspCompletionItemData(
    string SessionId,
    string CompletionId,
    string DocumentUri,
    long DocumentVersion,
    long WorkspaceRevision,
    long SelectionRevision);

public sealed record LspTextEdit(LspRange Range, string NewText);

public sealed record LspMarkupContent(string Kind, string Value);

public sealed record LspHover(LspMarkupContent Contents, LspRange? Range);

public sealed record LspSignatureHelpParams(
    LspTextDocumentIdentifier TextDocument,
    LspPosition Position,
    LspSignatureHelpContext? Context);

public sealed record LspSignatureHelpContext(
    int TriggerKind,
    string? TriggerCharacter,
    bool IsRetrigger);

public sealed record LspSignatureHelp(
    IReadOnlyList<LspSignatureInformation> Signatures,
    int? ActiveSignature,
    int? ActiveParameter);

public sealed record LspSignatureInformation(
    string Label,
    LspMarkupContent? Documentation,
    IReadOnlyList<LspParameterInformation> Parameters,
    int? ActiveParameter = null);

public sealed record LspParameterInformation(
    string Label,
    LspMarkupContent? Documentation);

public sealed record LspSemanticTokensParams(LspTextDocumentIdentifier TextDocument);

public sealed record LspSemanticTokens(string ResultId, IReadOnlyList<int> Data);

public sealed record LspDocumentSymbolParams(LspTextDocumentIdentifier TextDocument);

public sealed record LspDocumentSymbol(
    string Name,
    string? Detail,
    int Kind,
    LspRange Range,
    LspRange SelectionRange,
    IReadOnlyList<LspDocumentSymbol> Children);

public sealed record LspCodeActionParams(
    LspTextDocumentIdentifier TextDocument,
    LspRange Range,
    LspCodeActionContext Context);

public sealed record LspCodeActionContext(
    IReadOnlyList<LspDiagnostic> Diagnostics,
    IReadOnlyList<string>? Only);

public sealed record LspCodeAction(
    string Title,
    string Kind,
    IReadOnlyList<LspDiagnostic>? Diagnostics,
    bool? IsPreferred,
    LspWorkspaceEdit Edit);

public sealed record LspWorkspaceEdit(
    IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> Changes);

public sealed record LspDiagnosticsReport(
    string Uri,
    long Version,
    long WorkspaceRevision,
    long SelectionRevision,
    IReadOnlyList<LspDiagnostic> Diagnostics);

public sealed record LspDiagnostic(
    LspRange Range,
    int Severity,
    string Code,
    string Source,
    string Message,
    IReadOnlyList<int>? Tags,
    LspDiagnosticData Data);

public sealed record LspDiagnosticData(
    long WorkspaceRevision,
    long SelectionRevision,
    long DocumentVersion);

public sealed record LspDocumentState(
    string Uri,
    string Path,
    long Version,
    long WorkspaceRevision,
    long SelectionRevision);

public sealed class LspInvalidParamsException(string message) : RoslynWorkerException(message);

public sealed class LspContentModifiedException(string message) : RoslynWorkerException(message);

public sealed class LspMethodNotFoundException(string message) : RoslynWorkerException(message);

public sealed class LspSessionUnavailableException(string message) : RoslynWorkerException(message);

public sealed class LspLimitExceededException(string message) : RoslynWorkerException(message);

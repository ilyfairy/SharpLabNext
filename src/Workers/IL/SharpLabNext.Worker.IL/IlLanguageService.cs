using EleCho.ILSense;
using EleCho.ILSense.Contracts;

namespace SharpLabNext.Worker.IL;

public sealed class IlLanguageService
{
    public static IReadOnlyList<string> CompletionTriggerCharacters { get; } =
        [".", "[", "]", ":", "'", "(", ",", "<", "!"];
    public static IReadOnlyList<string> SignatureHelpTriggerCharacters { get; } = ["(", ","];
    public static IReadOnlyList<string> SemanticTokenTypes { get; } =
    [
        "namespace",
        "class",
        "method",
        "field",
        "variable",
        "keyword",
        "number",
        "string",
        "operator",
        "comment",
        "label",
        "macro",
        "invalid",
        "identifier",
        "typeParameter"
    ];
    public static IReadOnlyList<string> SemanticTokenModifiers { get; } =
        ["declaration", "definition", "static", "readonly", "deprecated"];

    public IILLanguageEngine CreateEngine(
        IILMetadataCatalog metadataCatalog,
        IlCompilationLimits compilationLimits,
        IlLspLimits lspLimits) =>
        ILLanguageEngine.Create(new ILLanguageEngineOptions
        {
            MetadataCatalog = metadataCatalog,
            Completion = new CompletionOptions(
                new OpcodeCompletionOptions(IncludeOperandSnippets: false),
                showAdvancedCandidates: true,
                maximumItems: lspLimits.MaxCompletionItems),
            Limits = new ILLimits
            {
                MaxCompletionItems = lspLimits.MaxCompletionItems,
                MaxDiagnostics = lspLimits.MaxDiagnostics,
                MaxTokens = 500_000,
                MaxLineLength = Math.Min(compilationLimits.MaxFileUtf8Bytes, 4 * 1024 * 1024),
                MaxDocumentChars = Math.Min(compilationLimits.MaxFileUtf8Bytes, 32 * 1024 * 1024),
                MaxSemanticTokens = 500_000,
                MaxDocumentSymbols = lspLimits.MaxDocumentSymbols,
                MaxFoldingRanges = lspLimits.MaxDocumentSymbols,
                CompletionTimeout = TimeSpan.FromMilliseconds(500)
            }
        });

    public async Task<IlLspCompletionList> CompleteAsync(
        IILLanguageEngine engine,
        WorkspaceSnapshot snapshot,
        DocumentId document,
        IlLspCompletionParams parameters,
        CancellationToken cancellationToken)
    {
        var source = snapshot.GetRequiredDocument(document).Text;
        ValidatePosition(source, parameters.Position);
        var result = await engine.CompleteAsync(
            new CompletionRequest(
                $"completion_{Guid.NewGuid():N}",
                snapshot,
                document,
                ToSourcePosition(parameters.Position),
                ToCompletionTrigger(parameters.Context)),
            cancellationToken).ConfigureAwait(false);
        return new IlLspCompletionList(
            result.IsIncomplete,
            result.Items.Select(item => MapCompletionItem(item, result)).ToArray());
    }

    public async Task<IlLspDiagnosticsReport> GetDiagnosticsAsync(
        IILLanguageEngine engine,
        WorkspaceSnapshot snapshot,
        DocumentId document,
        string uri,
        long selectionRevision,
        CancellationToken cancellationToken)
    {
        var result = await engine.GetDiagnosticsAsync(snapshot, document, cancellationToken).ConfigureAwait(false);
        return new IlLspDiagnosticsReport(
            uri,
            result.DocumentVersion,
            result.WorkspaceRevision,
            selectionRevision,
            result.Items.Select(item => MapDiagnostic(item, selectionRevision)).ToArray());
    }

    public async Task<IlLspHover?> GetHoverAsync(
        IILLanguageEngine engine,
        WorkspaceSnapshot snapshot,
        DocumentId document,
        IlLspPosition position,
        CancellationToken cancellationToken)
    {
        ValidatePosition(snapshot.GetRequiredDocument(document).Text, position);
        var hover = await engine.GetHoverAsync(
            snapshot,
            document,
            ToSourcePosition(position),
            cancellationToken).ConfigureAwait(false);
        return hover is null
            ? null
            : new IlLspHover(new IlLspMarkupContent("markdown", hover.Markdown), ToLspRange(hover.Range));
    }

    public Task<IlLspLocation?> GetDefinitionAsync(
        IILLanguageEngine engine,
        WorkspaceSnapshot snapshot,
        DocumentId document,
        IlLspPosition position,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePosition(snapshot.GetRequiredDocument(document).Text, position);
        var resolution = engine.ResolveSymbolAt(
            snapshot,
            document,
            ToSourcePosition(position));
        cancellationToken.ThrowIfCancellationRequested();

        // Metadata-only symbols intentionally have no source URI. Standard LSP
        // definition results cannot represent an assembly metadata target, so
        // expose only concrete workspace locations.
        var location = resolution?.Location is { } target
            ? new IlLspLocation(ToDocumentUri(target.Document), ToLspRange(target.Range))
            : null;
        return Task.FromResult<IlLspLocation?>(location);
    }

    public Task<IReadOnlyList<IlLspWorkspaceSymbol>> GetWorkspaceSymbolsAsync(
        IILLanguageEngine engine,
        WorkspaceSnapshot snapshot,
        IlLspWorkspaceSymbolParams parameters,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumResults <= 0)
            throw new IlLspLimitExceededException("The workspace symbol result limit must be positive.");
        var symbols = engine.GetWorkspaceSymbols(snapshot, parameters.Query ?? string.Empty, maximumResults);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IlLspWorkspaceSymbol>>(
            symbols.Items.Select(symbol => MapWorkspaceSymbol(symbol, symbols.WorkspaceRevision)).ToArray());
    }

    public Task<IReadOnlyList<IlLspCodeAction>> GetCodeActionsAsync(
        IILLanguageEngine engine,
        WorkspaceSnapshot snapshot,
        DocumentId document,
        IlLspCodeActionParams parameters,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = snapshot.GetRequiredDocument(document).Text;
        ValidatePosition(source, parameters.Range.Start);
        ValidatePosition(source, parameters.Range.End);
        if (parameters.Range.Start.Line > parameters.Range.End.Line ||
            parameters.Range.Start.Line == parameters.Range.End.Line &&
            parameters.Range.Start.Character > parameters.Range.End.Character)
        {
            throw new IlLspInvalidParamsException("codeAction range end must not precede its start.");
        }
        if (maximumResults <= 0)
            throw new IlLspLimitExceededException("The code action result limit must be positive.");

        var actions = engine.GetCodeActions(
            snapshot,
            document,
            ToSourceSpan(parameters.Range));
        cancellationToken.ThrowIfCancellationRequested();
        var mapped = actions
            .Where(action => AllowsCodeActionKind(parameters.Context.Only, MapCodeActionKind(action.Kind)))
            .Take(maximumResults)
            .Select(action => MapCodeAction(action, parameters.Context))
            .ToArray();
        return Task.FromResult<IReadOnlyList<IlLspCodeAction>>(mapped);
    }

    public async Task<IlLspSignatureHelp> GetSignatureHelpAsync(
        IILLanguageEngine engine,
        WorkspaceSnapshot snapshot,
        DocumentId document,
        IlLspPosition position,
        CancellationToken cancellationToken)
    {
        ValidatePosition(snapshot.GetRequiredDocument(document).Text, position);
        var help = await engine.GetSignatureHelpAsync(
            snapshot,
            document,
            ToSourcePosition(position),
            cancellationToken).ConfigureAwait(false);
        return new IlLspSignatureHelp(
            help.Signatures.Select(static signature => new IlLspSignatureInformation(
                signature.Label,
                signature.Documentation is null
                    ? null
                    : new IlLspMarkupContent("markdown", signature.Documentation),
                signature.Parameters.Select(static parameter => new IlLspParameterInformation(
                    parameter.Label,
                    parameter.Documentation is null
                        ? null
                        : new IlLspMarkupContent("markdown", parameter.Documentation))).ToArray())).ToArray(),
            help.ActiveSignature,
            help.ActiveParameter);
    }

    public async Task<IlLspSemanticTokens> GetSemanticTokensAsync(
        IILLanguageEngine engine,
        WorkspaceSnapshot snapshot,
        DocumentId document,
        CancellationToken cancellationToken)
    {
        var tokens = await engine.GetSemanticTokensAsync(snapshot, document, cancellationToken).ConfigureAwait(false);
        var data = new int[checked(tokens.Count * 5)];
        var dataIndex = 0;
        var previousLine = 0;
        var previousCharacter = 0;
        foreach (var token in tokens
                     .OrderBy(static token => token.Range.Start.Line)
                     .ThenBy(static token => token.Range.Start.Character))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = token.Range.Start.Line;
            var character = token.Range.Start.Character;
            var deltaLine = line - previousLine;
            data[dataIndex++] = deltaLine;
            data[dataIndex++] = deltaLine == 0 ? character - previousCharacter : character;
            data[dataIndex++] = token.Range.End.Character - character;
            data[dataIndex++] = MapSemanticTokenKind(token.Kind);
            data[dataIndex++] = (int)token.Modifiers;
            previousLine = line;
            previousCharacter = character;
        }
        var source = snapshot.GetRequiredDocument(document);
        return new IlLspSemanticTokens(
            $"{source.Version}:{snapshot.Revision}:{engine.Metadata.Generation}",
            data);
    }

    public async Task<IReadOnlyList<IlLspDocumentSymbol>> GetDocumentSymbolsAsync(
        IILLanguageEngine engine,
        WorkspaceSnapshot snapshot,
        DocumentId document,
        CancellationToken cancellationToken)
    {
        var symbols = await engine.GetDocumentSymbolsAsync(snapshot, document, cancellationToken).ConfigureAwait(false);
        return symbols.Select(MapDocumentSymbol).ToArray();
    }

    public async Task<IReadOnlyList<IlLspFoldingRange>> GetFoldingRangesAsync(
        IILLanguageEngine engine,
        WorkspaceSnapshot snapshot,
        DocumentId document,
        CancellationToken cancellationToken)
    {
        var ranges = await engine.GetFoldingRangesAsync(snapshot, document, cancellationToken).ConfigureAwait(false);
        return ranges.Select(static range => new IlLspFoldingRange(
            range.Range.Start.Line,
            range.Range.Start.Character,
            range.Range.End.Line,
            range.Range.End.Character,
            range.Kind switch
            {
                ILFoldingRangeKind.Region => "region",
                ILFoldingRangeKind.Comment => "comment",
                ILFoldingRangeKind.Imports => "imports",
                _ => throw new ArgumentOutOfRangeException(nameof(range), range.Kind, "Unknown IL folding range kind.")
            })).ToArray();
    }

    public static void ValidatePosition(string text, IlLspPosition position)
    {
        if (position.Line < 0 || position.Character < 0)
            throw new IlLspInvalidParamsException("LSP position cannot be negative.");
        var line = 0;
        var lineStart = 0;
        while (line < position.Line)
        {
            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
                throw new IlLspInvalidParamsException("LSP position line is outside the document.");
            lineStart = newline + 1;
            line++;
        }
        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0)
            lineEnd = text.Length;
        if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
            lineEnd--;
        if (position.Character > lineEnd - lineStart)
            throw new IlLspInvalidParamsException("LSP position character is outside the document line.");
    }

    private static IlLspCompletionItem MapCompletionItem(
        ILCompletionCandidate candidate,
        CompletionResult result) =>
        new(
            candidate.Label,
            MapCompletionKind(candidate.Kind),
            candidate.Detail,
            candidate.Documentation is null
                ? null
                : new IlLspMarkupContent("markdown", candidate.Documentation),
            candidate.SortText,
            candidate.FilterText,
            candidate.InsertText,
            candidate.InsertTextFormat == InsertTextFormat.Snippet ? 2 : 1,
            new IlLspTextEdit(
                ToLspRange(candidate.ReplacementRange),
                candidate.SnippetText ?? candidate.InsertText),
            candidate.Tags.Contains(CompletionCandidateTag.Deprecated) ? [1] : [],
            new IlLspCompletionItemData(
                candidate.Id,
                candidate.Origin.ToString(),
                candidate.OpcodeFamilyId,
                candidate.Tags.Select(static tag => tag.ToString()).ToArray(),
                candidate.Data,
                result.DocumentVersion,
                result.WorkspaceRevision,
                result.ReferenceRevision));

    private static IlLspDiagnostic MapDiagnostic(ILDiagnostic diagnostic, long selectionRevision) =>
        new(
            ToLspRange(diagnostic.Range),
            (int)diagnostic.Severity,
            diagnostic.Code.Value,
            diagnostic.Source,
            diagnostic.Message,
            diagnostic.Tags.Select(static tag => (int)tag).ToArray(),
            diagnostic.RelatedInformation.Select(static related => new IlLspDiagnosticRelatedInformation(
                new IlLspLocation(ToDocumentUri(related.Location.Document), ToLspRange(related.Location.Range)),
                related.Message)).ToArray(),
            new IlLspDiagnosticData(
                diagnostic.WorkspaceRevision,
                selectionRevision,
                diagnostic.DocumentVersion,
                diagnostic.Phase.ToString(),
                diagnostic.ReferenceRevision));

    private static IlLspDocumentSymbol MapDocumentSymbol(ILDocumentSymbol symbol) =>
        new(
            symbol.Name,
            symbol.Detail,
            symbol.Kind switch
            {
                ILDocumentSymbolKind.File => 1,
                ILDocumentSymbolKind.Assembly => 2,
                ILDocumentSymbolKind.Namespace => 3,
                ILDocumentSymbolKind.Type => 5,
                ILDocumentSymbolKind.Method => 6,
                ILDocumentSymbolKind.Property => 7,
                ILDocumentSymbolKind.Field => 8,
                ILDocumentSymbolKind.Constructor => 9,
                ILDocumentSymbolKind.Parameter or ILDocumentSymbolKind.Local or ILDocumentSymbolKind.Label => 13,
                ILDocumentSymbolKind.Event => 24,
                _ => throw new ArgumentOutOfRangeException(nameof(symbol), symbol.Kind, "Unknown IL document symbol kind.")
            },
            ToLspRange(symbol.Range),
            ToLspRange(symbol.SelectionRange),
            symbol.Children.Select(MapDocumentSymbol).ToArray());

    private static IlLspWorkspaceSymbol MapWorkspaceSymbol(ILWorkspaceSymbol symbol, long workspaceRevision) =>
        new(
            symbol.Name,
            MapSymbolKind(symbol.Kind),
            new IlLspLocation(ToDocumentUri(symbol.Location.Document), ToLspRange(symbol.Location.Range)),
            symbol.ContainerName,
            new IlLspWorkspaceSymbolData(symbol.Symbol.Value, symbol.Detail, workspaceRevision));

    private static IlLspCodeAction MapCodeAction(
        ILCodeAction action,
        IlLspCodeActionContext context)
    {
        var edits = action.Edits
            .GroupBy(static edit => edit.Document.Value, StringComparer.Ordinal)
            .ToDictionary(
                static group => ToDocumentUri(new DocumentId(group.Key)),
                static group => (IReadOnlyList<IlLspTextEdit>)group
                    .Select(edit => new IlLspTextEdit(ToLspRange(edit.Range), edit.NewText))
                    .ToArray(),
                StringComparer.Ordinal);
        var firstEdit = action.Edits[0];
        var diagnostics = action.Diagnostic is { } diagnostic
            ? (context.Diagnostics ?? [])
                .Where(item => StringComparer.Ordinal.Equals(item.Code, diagnostic.Value))
                .ToArray()
            : null;
        return new IlLspCodeAction(
            action.Title,
            MapCodeActionKind(action.Kind),
            diagnostics is { Length: > 0 } ? diagnostics : null,
            action.IsPreferred,
            new IlLspWorkspaceEdit(edits),
            new IlLspCodeActionData(
                action.Id,
                firstEdit.DocumentVersion,
                firstEdit.WorkspaceRevision,
                action.Diagnostic?.Value));
    }

    private static bool AllowsCodeActionKind(IReadOnlyList<string>? only, string kind) =>
        only is null || only.Count == 0 ||
        only.Any(requested =>
            StringComparer.Ordinal.Equals(requested, kind) ||
            kind.StartsWith(requested + ".", StringComparison.Ordinal));

    private static string MapCodeActionKind(ILCodeActionKind kind) => kind switch
    {
        ILCodeActionKind.QuickFix => "quickfix",
        ILCodeActionKind.RefactorRewrite => "refactor.rewrite",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown IL code action kind.")
    };

    private static int MapSymbolKind(ILSymbolKind kind) => kind switch
    {
        ILSymbolKind.Assembly => 2,
        ILSymbolKind.Type => 5,
        ILSymbolKind.Method => 6,
        ILSymbolKind.Constructor => 9,
        ILSymbolKind.Field => 8,
        ILSymbolKind.Label or ILSymbolKind.Parameter or ILSymbolKind.Local => 13,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown IL symbol kind.")
    };

    private static int MapSemanticTokenKind(ILSemanticTokenKind kind) => kind switch
    {
        ILSemanticTokenKind.Directive or
        ILSemanticTokenKind.Opcode or
        ILSemanticTokenKind.Keyword or
        ILSemanticTokenKind.PrimitiveType => 5,
        ILSemanticTokenKind.Assembly => 11,
        ILSemanticTokenKind.Type => 1,
        ILSemanticTokenKind.Method or ILSemanticTokenKind.Member => 2,
        ILSemanticTokenKind.Field => 3,
        ILSemanticTokenKind.Parameter or ILSemanticTokenKind.Local => 4,
        ILSemanticTokenKind.Number => 6,
        ILSemanticTokenKind.String => 7,
        ILSemanticTokenKind.Operator => 8,
        ILSemanticTokenKind.Comment => 9,
        ILSemanticTokenKind.Label => 10,
        ILSemanticTokenKind.Error => 12,
        ILSemanticTokenKind.Identifier => 13,
        ILSemanticTokenKind.GenericParameter => 14,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown IL semantic token kind.")
    };

    private static int MapCompletionKind(CompletionKind kind) => kind switch
    {
        CompletionKind.Method => 2,
        CompletionKind.Constructor => 4,
        CompletionKind.Field => 5,
        CompletionKind.Parameter or CompletionKind.Local => 6,
        CompletionKind.Type => 7,
        CompletionKind.Interface => 8,
        CompletionKind.Assembly or CompletionKind.Module or CompletionKind.Namespace => 9,
        CompletionKind.Property => 10,
        CompletionKind.Value => 12,
        CompletionKind.Directive or
        CompletionKind.Keyword or
        CompletionKind.Opcode or
        CompletionKind.OpcodeVariant or
        CompletionKind.Primitive => 14,
        CompletionKind.Snippet => 15,
        CompletionKind.Label => 18,
        CompletionKind.Constant => 21,
        CompletionKind.Event => 23,
        CompletionKind.GenericParameter => 25,
        _ => 1
    };

    private static CompletionTrigger ToCompletionTrigger(IlLspCompletionContext? context) =>
        context?.TriggerKind switch
        {
            2 when !string.IsNullOrEmpty(context.TriggerCharacter) =>
                new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, context.TriggerCharacter),
            3 => new CompletionTrigger(CompletionTriggerKind.TriggerForIncompleteCompletions),
            _ => CompletionTrigger.Explicit
        };

    private static SourcePosition ToSourcePosition(IlLspPosition position) =>
        new(position.Line, position.Character);

    private static IlLspRange ToLspRange(SourceSpan range) =>
        new(
            new IlLspPosition(range.Start.Line, range.Start.Character),
            new IlLspPosition(range.End.Line, range.End.Character));

    private static SourceSpan ToSourceSpan(IlLspRange range) =>
        new(ToSourcePosition(range.Start), ToSourcePosition(range.End));

    private static string ToDocumentUri(DocumentId document) =>
        $"sharplabnext:///{string.Join('/', document.Value.Split('/').Select(Uri.EscapeDataString))}";
}

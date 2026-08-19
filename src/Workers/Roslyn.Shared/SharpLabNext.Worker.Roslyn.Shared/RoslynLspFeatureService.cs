using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;

namespace SharpLabNext.Worker.Roslyn;

internal sealed class RoslynLspFeatureService(
    RoslynLanguageSession session,
    LspLimits limits) : IDisposable
{
    private static readonly HashSet<string> ExactSnippetShortcuts = new(StringComparer.OrdinalIgnoreCase)
    {
        "class",
        "ctor",
        "cw",
        "do",
        "else",
        "enum",
        "for",
        "foreach",
        "forr",
        "if",
        "interface",
        "lock",
        "prop",
        "propg",
        "propi",
        "propr",
        "props",
        "sim",
        "struct",
        "svm",
        "unsafe",
        "using",
        "while"
    };

    internal static readonly string[] SemanticTokenTypes =
    [
        "namespace",
        "type",
        "class",
        "enum",
        "interface",
        "struct",
        "typeParameter",
        "parameter",
        "variable",
        "property",
        "enumMember",
        "event",
        "function",
        "method",
        "macro",
        "keyword",
        "modifier",
        "comment",
        "string",
        "number",
        "regexp",
        "operator",
        "delegate",
        "field",
        "label",
        "stringEscapeCharacter"
    ];

    internal static readonly string[] SemanticTokenModifiers =
    [
        "static",
        "deprecated",
        "readonly",
        "abstract",
        "async"
    ];

    private readonly ConcurrentDictionary<string, CachedCompletion> _completionCache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _completionOrder = new();

    public async Task<LspDiagnosticsReport?> GetDiagnosticsAsync(
        string uri,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetDocumentSnapshotAsync(uri, cancellationToken).ConfigureAwait(false);
        if (snapshot.Version != expectedVersion)
            return null;

        var syntaxTree = await snapshot.Document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var compilation = await snapshot.Document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (syntaxTree is null || compilation is null)
            throw new InvalidOperationException("Roslyn did not produce a syntax tree or compilation for the LSP document.");

        var diagnostics = compilation.GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Location.SourceTree == syntaxTree)
            .OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)
            .ThenBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .Take(limits.MaxDiagnostics)
            .Select(diagnostic => ConvertDiagnostic(diagnostic, snapshot))
            .ToArray();
        if (!await session.IsCurrentAsync(
            snapshot.Path,
            snapshot.Version,
            snapshot.WorkspaceRevision,
            cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new LspDiagnosticsReport(
            snapshot.Uri,
            snapshot.Version,
            snapshot.WorkspaceRevision,
            snapshot.SelectionRevision,
            diagnostics);
    }

    public async Task<LspCompletionList> GetCompletionsAsync(
        LspCompletionParams parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await session.GetDocumentSnapshotAsync(parameters.TextDocument.Uri, cancellationToken).ConfigureAwait(false);
        var position = RoslynLanguageSession.ToPosition(snapshot.Text, parameters.Position);
        var completionService = CompletionService.GetService(snapshot.Document)
            ?? throw new LspSessionUnavailableException("Roslyn completion service is unavailable for the language workspace.");
        var trigger = CreateCompletionTrigger(parameters.Context);
        var completionList = await completionService.GetCompletionsAsync(
            snapshot.Document,
            position,
            trigger: trigger,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (completionList is null)
            return new LspCompletionList(false, []);

        var filterStart = position;
        while (filterStart > 0 && SyntaxFacts.IsIdentifierPartCharacter(snapshot.Text[filterStart - 1]))
            filterStart--;
        var filterText = snapshot.Text.ToString(TextSpan.FromBounds(filterStart, position));
        var availableItems = completionList.ItemsList.ToImmutableArray();
        var filteredItems = filterText.Length == 0
            ? availableItems
            : completionService.FilterItems(snapshot.Document, availableItems, filterText);
        var completionCandidates = CompletionCandidates(
            snapshot.Document.Project.Language,
            filterText,
            TextSpan.FromBounds(filterStart, position),
            availableItems,
            filteredItems);
        var items = new List<LspCompletionItem>(Math.Min(completionCandidates.Length, limits.MaxCompletionItems));
        foreach (var candidate in completionCandidates.Take(limits.MaxCompletionItems))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = candidate.Item;
            var eagerEdits = item.Tags.Contains(WellKnownTags.Snippet) ||
                item.IsComplexTextEdit ||
                !string.IsNullOrWhiteSpace(item.InlineDescription)
                ? await GetCompletionEditsAsync(
                    snapshot.Document,
                    snapshot.Text,
                    completionService,
                    item,
                    candidate.ReplacementSpan,
                    cancellationToken).ConfigureAwait(false)
                : null;
            var completionId = Guid.NewGuid().ToString("N");
            var data = new LspCompletionItemData(
                session.SessionId,
                completionId,
                snapshot.Uri,
                snapshot.Version,
                snapshot.WorkspaceRevision,
                snapshot.SelectionRevision);
            AddCompletion(completionId, new CachedCompletion(
                snapshot.Path,
                item,
                data,
                eagerEdits,
                candidate.ReplacementSpan));
            items.Add(new LspCompletionItem(
                candidate.Label,
                CompletionKind(item.Tags),
                candidate.Detail ?? NullIfEmpty(item.InlineDescription),
                null,
                item.SortText,
                candidate.FilterText,
                item.DisplayText,
                eagerEdits?.InsertTextFormat,
                eagerEdits?.TextEdit ?? new LspTextEdit(
                    RoslynLanguageSession.ToRange(
                        snapshot.Text,
                        candidate.ReplacementSpan ?? item.Span),
                    item.DisplayText),
                eagerEdits?.AdditionalTextEdits,
                data));
        }

        return new LspCompletionList(completionCandidates.Length > items.Count, items);
    }

    public async Task<LspCompletionItem> ResolveCompletionAsync(
        LspCompletionItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(item.Data.SessionId, session.SessionId) ||
            !_completionCache.TryGetValue(item.Data.CompletionId, out var cached))
        {
            throw new LspContentModifiedException("The completion item is unknown or has expired.");
        }

        var snapshot = await session.GetDocumentSnapshotAsync(item.Data.DocumentUri, cancellationToken).ConfigureAwait(false);
        if (snapshot.Version != item.Data.DocumentVersion ||
            snapshot.WorkspaceRevision != item.Data.WorkspaceRevision ||
            !StringComparer.Ordinal.Equals(snapshot.Path, cached.Path))
        {
            throw new LspContentModifiedException("The document changed before the completion item was resolved.");
        }

        var completionService = CompletionService.GetService(snapshot.Document)
            ?? throw new LspSessionUnavailableException("Roslyn completion service is unavailable for the language workspace.");
        var description = await completionService
            .GetDescriptionAsync(snapshot.Document, cached.Item, cancellationToken)
            .ConfigureAwait(false);
        var detail = Truncate(
            description is null ? string.Empty : ConcatTaggedText(description.TaggedParts),
            limits.MaxHoverCharacters);
        var edits = cached.EagerEdits ?? await GetCompletionEditsAsync(
            snapshot.Document,
            snapshot.Text,
            completionService,
            cached.Item,
            cached.ReplacementSpan,
            cancellationToken).ConfigureAwait(false);

        return item with
        {
            Detail = detail,
            Documentation = string.IsNullOrWhiteSpace(detail)
                ? null
                : new LspMarkupContent("markdown", $"```{session.MarkdownLanguageId}\n{detail}\n```"),
            InsertTextFormat = edits.InsertTextFormat,
            TextEdit = edits.TextEdit,
            AdditionalTextEdits = edits.AdditionalTextEdits
        };
    }

    private static async Task<ResolvedCompletionEdits> GetCompletionEditsAsync(
        Document document,
        SourceText source,
        CompletionService completionService,
        CompletionItem item,
        TextSpan? replacementSpan,
        CancellationToken cancellationToken)
    {
        var change = await completionService
            .GetChangeAsync(document, item, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var granularChanges = change.TextChanges.IsDefaultOrEmpty
            ? ImmutableArray.Create(change.TextChange)
            : change.TextChanges;
        var constructedFromInlineExpression = await IsInlineSnippetInvocationAsync(
            document,
            source,
            item,
            change.TextChange,
            cancellationToken).ConfigureAwait(false);
        TextChange primaryChange;
        ImmutableArray<TextChange> additionalChanges;
        if (constructedFromInlineExpression)
        {
            primaryChange = change.TextChange;
            additionalChanges = [];
        }
        else
        {
            var primaryIndex = FindPrimaryCompletionChange(granularChanges, item.Span);
            primaryChange = primaryIndex >= 0 ? granularChanges[primaryIndex] : change.TextChange;
            additionalChanges = primaryIndex < 0
                ? []
                : granularChanges.RemoveAt(primaryIndex);
            if (TryCoalescePostfixPrefixInsertion(
                source,
                primaryChange,
                additionalChanges,
                string.Concat(item.DisplayText, " "),
                out var coalescedPrimaryChange,
                out var remainingAdditionalChanges))
            {
                primaryChange = coalescedPrimaryChange;
                additionalChanges = remainingAdditionalChanges;
            }
        }
        int? insertTextFormat = null;
        if (item.Tags.Contains(WellKnownTags.Snippet) &&
            TryGetSnippetCursorOffset(primaryChange, additionalChanges, change.NewPosition, out var cursorOffset))
        {
            if (TryNarrowSnippetPrimaryChange(
                source,
                item.Span,
                primaryChange,
                cursorOffset,
                out var narrowedChange,
                out var narrowedCursorOffset))
            {
                primaryChange = narrowedChange;
                cursorOffset = narrowedCursorOffset;
            }
            var tabStops = await TryGetCSharpSnippetTabStopsAsync(
                document,
                source,
                item,
                primaryChange,
                constructedFromInlineExpression,
                cancellationToken).ConfigureAwait(false);
            primaryChange = new TextChange(
                primaryChange.Span,
                LspSnippetText(
                    primaryChange.NewText ?? string.Empty,
                    cursorOffset,
                    source,
                    primaryChange.Span,
                    tabStops));
            insertTextFormat = 2;
        }
        if (replacementSpan is { } explicitReplacementSpan &&
            explicitReplacementSpan.Contains(primaryChange.Span))
        {
            var preservedPrefix = source.ToString(TextSpan.FromBounds(
                explicitReplacementSpan.Start,
                primaryChange.Span.Start));
            var preservedSuffix = source.ToString(TextSpan.FromBounds(
                primaryChange.Span.End,
                explicitReplacementSpan.End));
            primaryChange = new TextChange(
                explicitReplacementSpan,
                string.Concat(
                    preservedPrefix,
                    primaryChange.NewText ?? string.Empty,
                    preservedSuffix));
        }

        return new ResolvedCompletionEdits(
            new LspTextEdit(
                RoslynLanguageSession.ToRange(source, primaryChange.Span),
                primaryChange.NewText ?? string.Empty),
            insertTextFormat,
            additionalChanges.IsDefaultOrEmpty
                ? null
                : additionalChanges
                    .Select(textChange => new LspTextEdit(
                        RoslynLanguageSession.ToRange(source, textChange.Span),
                        textChange.NewText ?? string.Empty))
                    .ToArray());
    }

    public async Task<LspHover?> GetHoverAsync(
        LspTextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetDocumentSnapshotAsync(parameters.TextDocument.Uri, cancellationToken).ConfigureAwait(false);
        var position = RoslynLanguageSession.ToPosition(snapshot.Text, parameters.Position);
        var quickInfoService = QuickInfoService.GetService(snapshot.Document)
            ?? throw new LspSessionUnavailableException("Roslyn quick info service is unavailable for the language workspace.");
        var quickInfo = await quickInfoService
            .GetQuickInfoAsync(snapshot.Document, position, cancellationToken)
            .ConfigureAwait(false);
        if (quickInfo is null)
            return await CreateSemanticHoverAsync(snapshot, position, cancellationToken).ConfigureAwait(false);

        var sections = quickInfo.Sections
            .Select(section => ConcatTaggedText(section.TaggedParts))
            .Where(static value => !string.IsNullOrWhiteSpace(value));
        var value = Truncate(string.Join(Environment.NewLine, sections), limits.MaxHoverCharacters);
        return new LspHover(
            new LspMarkupContent("markdown", $"```{session.MarkdownLanguageId}\n{value}\n```"),
            RoslynLanguageSession.ToRange(snapshot.Text, quickInfo.Span));
    }

    private async Task<LspHover?> CreateSemanticHoverAsync(
        LspDocumentSnapshot snapshot,
        int position,
        CancellationToken cancellationToken)
    {
        if (snapshot.Text.Length == 0)
            return null;

        var root = await snapshot.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await snapshot.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
            return null;

        var tokenPosition = Math.Clamp(position, 0, snapshot.Text.Length - 1);
        var token = root.FindToken(tokenPosition, findInsideTrivia: true);
        ISymbol? symbol = null;
        foreach (var node in token.Parent?.AncestorsAndSelf() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
            symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (symbol is not null)
                break;
        }

        if (symbol is null)
            return null;

        var format = snapshot.Document.Project.Language == LanguageNames.VisualBasic
            ? SymbolDisplayFormat.VisualBasicErrorMessageFormat
            : SymbolDisplayFormat.CSharpErrorMessageFormat;
        var value = Truncate(symbol.ToDisplayString(format), limits.MaxHoverCharacters);
        return new LspHover(
            new LspMarkupContent("markdown", $"```{session.MarkdownLanguageId}\n{value}\n```"),
            RoslynLanguageSession.ToRange(snapshot.Text, token.Span));
    }

    public async Task<LspSignatureHelp?> GetSignatureHelpAsync(
        LspSignatureHelpParams parameters,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetDocumentSnapshotAsync(parameters.TextDocument.Uri, cancellationToken).ConfigureAwait(false);
        var position = RoslynLanguageSession.ToPosition(snapshot.Text, parameters.Position);
        var root = await snapshot.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await snapshot.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null || snapshot.Text.Length == 0)
            return null;

        if (snapshot.Document.Project.Language == LanguageNames.VisualBasic)
        {
            return VisualBasicLspFeatureAdapter.CreateSignatureHelp(
                root,
                semanticModel,
                snapshot.Text,
                position,
                cancellationToken);
        }

        var tokenPosition = Math.Clamp(position == 0 ? 0 : position - 1, 0, snapshot.Text.Length - 1);
        var token = root.FindToken(tokenPosition, findInsideTrivia: true);
        var argumentList = token.Parent?.AncestorsAndSelf()
            .OfType<ArgumentListSyntax>()
            .FirstOrDefault(list => list.SpanStart <= position && position <= list.Span.End);
        if (argumentList is null)
            return null;

        var methods = GetCandidateMethods(argumentList, semanticModel, cancellationToken);
        if (methods.Length == 0)
            return null;

        var activeParameter = argumentList.Arguments.GetSeparators().Count(separator => separator.SpanStart < position);
        var boundMethod = argumentList.Parent is InvocationExpressionSyntax invocation
            ? semanticModel.GetSymbolInfo(invocation.Expression, cancellationToken).Symbol as IMethodSymbol
            : semanticModel.GetSymbolInfo(argumentList.Parent!, cancellationToken).Symbol as IMethodSymbol;
        var signatures = methods
            .Take(50)
            .Select(method => CreateSignature(method, activeParameter))
            .ToArray();
        var activeSignature = boundMethod is null
            ? 0
            : Array.FindIndex(methods.ToArray(), method => SymbolEqualityComparer.Default.Equals(method, boundMethod));
        if (activeSignature < 0 || activeSignature >= signatures.Length)
            activeSignature = 0;

        return new LspSignatureHelp(signatures, activeSignature, activeParameter);
    }

    public async Task<LspSemanticTokens> GetSemanticTokensAsync(
        LspSemanticTokensParams parameters,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetDocumentSnapshotAsync(parameters.TextDocument.Uri, cancellationToken).ConfigureAwait(false);
        var classified = await Classifier.GetClassifiedSpansAsync(
            snapshot.Document,
            new TextSpan(0, snapshot.Text.Length),
            cancellationToken).ConfigureAwait(false);
        IEnumerable<ClassifiedSpan> effectiveClassifications = classified;
        if (snapshot.Document.Project.Language == LanguageNames.CSharp)
        {
            var root = await snapshot.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is not null)
            {
                effectiveClassifications = SplitCSharpStringEscapes(
                    classified,
                    FindCSharpStringEscapes(root));
            }
        }
        var encoded = EncodeSemanticTokens(
            snapshot.Text,
            effectiveClassifications,
            limits.MaxSemanticTokens,
            cancellationToken);
        return new LspSemanticTokens($"{snapshot.Version}:{snapshot.WorkspaceRevision}", encoded);
    }

    public async Task<IReadOnlyList<LspDocumentSymbol>> GetDocumentSymbolsAsync(
        LspDocumentSymbolParams parameters,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetDocumentSnapshotAsync(parameters.TextDocument.Uri, cancellationToken).ConfigureAwait(false);
        var root = await snapshot.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return [];

        if (snapshot.Document.Project.Language == LanguageNames.VisualBasic)
        {
            return VisualBasicLspFeatureAdapter.CreateDocumentSymbols(
                root,
                snapshot.Text,
                limits.MaxDocumentSymbols,
                cancellationToken);
        }

        var remaining = limits.MaxDocumentSymbols;
        return CreateSymbols(root, snapshot.Text, ref remaining, cancellationToken);
    }

    public async Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(
        LspCodeActionParams parameters,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.GetDocumentSnapshotAsync(parameters.TextDocument.Uri, cancellationToken).ConfigureAwait(false);
        var actions = new List<LspCodeAction>(limits.MaxCodeActions);
        if (snapshot.Document.Project.Language == LanguageNames.CSharp &&
            AllowsKind(parameters.Context.Only, "quickfix"))
            await AddMissingSemicolonActionsAsync(snapshot, parameters, actions, cancellationToken).ConfigureAwait(false);
        if (AllowsKind(parameters.Context.Only, "source.organizeImports"))
        {
            var organized = await Formatter.OrganizeImportsAsync(snapshot.Document, cancellationToken).ConfigureAwait(false);
            await AddWholeDocumentActionAsync(
                snapshot,
                organized,
                "Organize imports",
                "source.organizeImports",
                actions,
                cancellationToken).ConfigureAwait(false);
        }

        if (AllowsKind(parameters.Context.Only, "source.formatDocument"))
        {
            var formatted = await Formatter.FormatAsync(snapshot.Document, cancellationToken: cancellationToken).ConfigureAwait(false);
            await AddWholeDocumentActionAsync(
                snapshot,
                formatted,
                "Format document",
                "source.formatDocument",
                actions,
                cancellationToken).ConfigureAwait(false);
        }

        return actions.Take(limits.MaxCodeActions).ToArray();
    }

    public void ClearCompletionCache()
    {
        _completionCache.Clear();
        while (_completionOrder.TryDequeue(out _))
        {
        }
    }

    public void Dispose() => ClearCompletionCache();

    private static CompletionTrigger CreateCompletionTrigger(LspCompletionContext? context)
    {
        if (context?.TriggerKind == 2 && context.TriggerCharacter is { Length: 1 })
            return CompletionTrigger.CreateInsertionTrigger(context.TriggerCharacter[0]);
        return CompletionTrigger.Invoke;
    }

    private static CompletionCandidate[] CompletionCandidates(
        string language,
        string filterText,
        TextSpan filterSpan,
        ImmutableArray<CompletionItem> availableItems,
        ImmutableArray<CompletionItem> filteredItems)
    {
        var matchingShortcuts = StringComparer.Ordinal.Equals(language, LanguageNames.CSharp) &&
            filterText.Length > 0
            ? ExactSnippetShortcuts
                .Where(shortcut => shortcut.StartsWith(filterText, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static shortcut => shortcut, StringComparer.Ordinal)
                .ToArray()
            : [];
        var hasMatchingSnippet = matchingShortcuts.Any(shortcut =>
            SnippetCandidate(
                StringComparer.OrdinalIgnoreCase.Equals(shortcut, filterText)
                    ? filterText
                    : shortcut,
                filterSpan,
                availableItems) is not null);
        // CompletionService.FilterItems intentionally returns only the equally-best
        // matches. It is useful for selecting the first item, but it is not a
        // complete filtered list. For ordinary completions, keep every direct
        // prefix match from the full ItemsList, with Roslyn's best subset first.
        // Filtering the best subset by the same prefix is important because
        // Roslyn can return a fuzzy import (for example SveMaskPattern for
        // `svm`) even though it does not match the text the user actually typed.
        // If a context-aware semantic snippet is available, complex import
        // candidates are intentionally omitted so that a shortcut such as `svm`
        // cannot be hijacked by an unrelated auto-import.
        var candidateItems = MatchingCompletionItems(
            filterText,
            availableItems,
            filteredItems,
            hasMatchingSnippet);
        var filteredCandidates = candidateItems
            .Select(item => new CompletionCandidate(
                item,
                CompletionDisplayLabel(item),
                item.FilterText,
                null,
                item.Tags.Contains(WellKnownTags.Snippet) &&
                    StringComparer.Ordinal.Equals(language, LanguageNames.CSharp)
                    ? filterSpan
                    : null))
            .ToArray();

        if (!StringComparer.Ordinal.Equals(language, LanguageNames.CSharp))
            return filteredCandidates;

        if (filterText.Length == 0)
            return filteredCandidates;

        if (matchingShortcuts.Length == 0)
            return filteredCandidates;

        var prefixMatches = matchingShortcuts
            .Select(shortcut => SnippetCandidate(
                StringComparer.OrdinalIgnoreCase.Equals(shortcut, filterText)
                    ? filterText
                    : shortcut,
                filterSpan,
                availableItems))
            .Where(static candidate => candidate is not null)
            .Cast<CompletionCandidate>();
        var combined = new List<CompletionCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in filteredCandidates.Concat(prefixMatches))
        {
            var category = candidate.Item.Tags.Contains(WellKnownTags.Snippet)
                ? "snippet:"
                : "regular:";
            if (seen.Add(string.Concat(category, candidate.Label)))
                combined.Add(candidate);
        }
        return combined.ToArray();
    }

    private static ImmutableArray<CompletionItem> MatchingCompletionItems(
        string filterText,
        ImmutableArray<CompletionItem> availableItems,
        ImmutableArray<CompletionItem> filteredItems,
        bool hasMatchingSnippet)
    {
        if (filterText.Length == 0)
            return availableItems;

        // Use a reference set so that the best-match items retain their original
        // order and are not duplicated when they also have a direct prefix match.
        var seenItems = new HashSet<CompletionItem>(ReferenceEqualityComparer.Instance);
        var candidates = ImmutableArray.CreateBuilder<CompletionItem>(
            filteredItems.Length + Math.Min(availableItems.Length, 32));
        foreach (var item in filteredItems)
        {
            if (item.FilterText.StartsWith(filterText, StringComparison.OrdinalIgnoreCase) &&
                (!hasMatchingSnippet ||
                    item.Tags.Contains(WellKnownTags.Snippet) ||
                    !item.IsComplexTextEdit) &&
                seenItems.Add(item))
            {
                candidates.Add(item);
            }
        }

        if (hasMatchingSnippet)
            return candidates.ToImmutable();

        foreach (var item in availableItems)
        {
            if (!item.FilterText.StartsWith(filterText, StringComparison.OrdinalIgnoreCase) ||
                !seenItems.Add(item))
            {
                continue;
            }

            candidates.Add(item);
        }

        return candidates.ToImmutable();
    }

    private static CompletionCandidate? SnippetCandidate(
        string shortcut,
        TextSpan replacementSpan,
        ImmutableArray<CompletionItem> availableItems)
    {
        var canonicalShortcut = StringComparer.OrdinalIgnoreCase.Equals(shortcut, "props")
            ? "prop"
            : shortcut;
        var snippet = availableItems.FirstOrDefault(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.DisplayText, canonicalShortcut) &&
            item.Tags.Contains(WellKnownTags.Snippet));
        if (snippet is null)
            return null;

        var detail = StringComparer.OrdinalIgnoreCase.Equals(shortcut, "props")
            ? "Property snippet (prop alias)"
            : null;
        return new CompletionCandidate(snippet, shortcut, shortcut, detail, replacementSpan);
    }

    private static string CompletionDisplayLabel(CompletionItem item) =>
        string.Concat(item.DisplayTextPrefix, item.DisplayText, item.DisplayTextSuffix);

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static int FindPrimaryCompletionChange(
        ImmutableArray<TextChange> changes,
        TextSpan completionSpan)
    {
        for (var index = 0; index < changes.Length; index++)
        {
            if (changes[index].Span == completionSpan)
                return index;
        }

        for (var index = 0; index < changes.Length; index++)
        {
            var span = changes[index].Span;
            if (span.Contains(completionSpan) || completionSpan.Contains(span))
                return index;
        }

        return -1;
    }

    private static bool TryCoalescePostfixPrefixInsertion(
        SourceText source,
        TextChange primaryChange,
        ImmutableArray<TextChange> additionalChanges,
        string expectedPrefixText,
        out TextChange coalescedPrimaryChange,
        out ImmutableArray<TextChange> remainingAdditionalChanges)
    {
        coalescedPrimaryChange = primaryChange;
        remainingAdditionalChanges = additionalChanges;
        if (!string.IsNullOrEmpty(primaryChange.NewText) ||
            primaryChange.Span.IsEmpty ||
            source[primaryChange.Span.Start] != '.')
        {
            return false;
        }

        var primaryLine = source.Lines.GetLineFromPosition(primaryChange.Span.Start).LineNumber;
        var prefixChangeIndex = -1;
        var prefixChangeStart = -1;
        for (var index = 0; index < additionalChanges.Length; index++)
        {
            var change = additionalChanges[index];
            if (!change.Span.IsEmpty ||
                change.Span.Start >= primaryChange.Span.Start ||
                !StringComparer.Ordinal.Equals(change.NewText, expectedPrefixText) ||
                source.Lines.GetLineFromPosition(change.Span.Start).LineNumber != primaryLine ||
                change.Span.Start <= prefixChangeStart)
            {
                continue;
            }

            prefixChangeIndex = index;
            prefixChangeStart = change.Span.Start;
        }

        if (prefixChangeIndex < 0)
            return false;

        var prefixChange = additionalChanges[prefixChangeIndex];
        var preservedExpressionSpan = TextSpan.FromBounds(
            prefixChange.Span.Start,
            primaryChange.Span.Start);
        var hasExpressionContent = false;
        for (var position = preservedExpressionSpan.Start;
             position < preservedExpressionSpan.End;
             position++)
        {
            if (!char.IsWhiteSpace(source[position]))
            {
                hasExpressionContent = true;
                break;
            }
        }
        if (!hasExpressionContent)
            return false;

        coalescedPrimaryChange = new TextChange(
            TextSpan.FromBounds(prefixChange.Span.Start, primaryChange.Span.End),
            string.Concat(
                prefixChange.NewText,
                source.ToString(preservedExpressionSpan),
                primaryChange.NewText));
        remainingAdditionalChanges = additionalChanges.RemoveAt(prefixChangeIndex);
        return true;
    }

    private static async Task<bool> IsInlineSnippetInvocationAsync(
        Document document,
        SourceText source,
        CompletionItem item,
        TextChange aggregateChange,
        CancellationToken cancellationToken)
    {
        if (!item.Properties.TryGetValue("SnippetIdentifier", out var snippetIdentifier) ||
            snippetIdentifier is not ("do" or "if" or "while" or "for" or "forr" or "foreach"))
        {
            return false;
        }

        var position = item.Span.End;
        if (item.Properties.TryGetValue("Position", out var value) &&
            int.TryParse(value, out var invocationPosition))
        {
            position = invocationPosition;
        }

        if (position <= 0 ||
            position > source.Length ||
            aggregateChange.Span.Start >= item.Span.Start ||
            aggregateChange.Span.End < item.Span.End)
        {
            return false;
        }

        var identifierStart = position;
        while (identifierStart > 0 && SyntaxFacts.IsIdentifierPartCharacter(source[identifierStart - 1]))
            identifierStart--;
        var invocationText = TextSpan.FromBounds(identifierStart, position);
        var invocationSource = invocationText.IsEmpty
            ? source
            : source.WithChanges(new TextChange(invocationText, string.Empty));
        var root = await document
            .WithText(invocationSource)
            .GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false);
        if (root is null)
            return false;

        var precedingToken = root.DescendantTokens(descendIntoTrivia: true)
            .LastOrDefault(token => token.Span.End <= identifierStart);
        if (precedingToken.IsKind(SyntaxKind.DotToken))
            return true;

        var previousContent = identifierStart;
        while (previousContent > 0 && char.IsWhiteSpace(source[previousContent - 1]))
            previousContent--;
        return previousContent > 0 && source[previousContent - 1] == '.';
    }

    private static bool TryGetSnippetCursorOffset(
        TextChange primaryChange,
        ImmutableArray<TextChange> additionalChanges,
        int? newPosition,
        out int cursorOffset)
    {
        cursorOffset = 0;
        if (newPosition is not { } finalPosition)
            return false;

        var finalPrimaryStart = primaryChange.Span.Start;
        foreach (var change in additionalChanges)
        {
            if (change.Span.End <= primaryChange.Span.Start)
                finalPrimaryStart += (change.NewText?.Length ?? 0) - change.Span.Length;
        }

        cursorOffset = finalPosition - finalPrimaryStart;
        return cursorOffset >= 0 && cursorOffset <= (primaryChange.NewText?.Length ?? 0);
    }

    private static bool TryNarrowSnippetPrimaryChange(
        SourceText source,
        TextSpan completionSpan,
        TextChange primaryChange,
        int cursorOffset,
        out TextChange narrowedChange,
        out int narrowedCursorOffset)
    {
        narrowedChange = primaryChange;
        narrowedCursorOffset = cursorOffset;
        if (primaryChange.Span == completionSpan)
            return true;
        if (primaryChange.Span.Start > completionSpan.Start || primaryChange.Span.End < completionSpan.End)
            return false;

        var existingPrefix = source.ToString(TextSpan.FromBounds(
            primaryChange.Span.Start,
            completionSpan.Start));
        var existingSuffix = source.ToString(TextSpan.FromBounds(
            completionSpan.End,
            primaryChange.Span.End));
        var replacement = primaryChange.NewText ?? string.Empty;
        if (!TryConsumePreservedPrefix(existingPrefix, replacement, out var replacementPrefixLength) ||
            !TryConsumePreservedSuffix(
                existingSuffix,
                replacement.AsSpan(replacementPrefixLength),
                out var replacementSuffixLength))
        {
            return false;
        }

        var replacementContentEnd = replacement.Length - replacementSuffixLength;
        if (cursorOffset < replacementPrefixLength || cursorOffset > replacementContentEnd)
            return false;

        narrowedChange = new TextChange(
            completionSpan,
            replacement[replacementPrefixLength..replacementContentEnd]);
        narrowedCursorOffset = cursorOffset - replacementPrefixLength;
        return true;
    }

    private static bool TryConsumePreservedPrefix(
        ReadOnlySpan<char> existing,
        ReadOnlySpan<char> replacement,
        out int replacementLength)
    {
        var existingIndex = 0;
        var replacementIndex = 0;
        while (existingIndex < existing.Length)
        {
            var existingNewLineLength = NewLineLength(existing, existingIndex);
            var replacementNewLineLength = NewLineLength(replacement, replacementIndex);
            if (existingNewLineLength > 0 && replacementNewLineLength > 0)
            {
                existingIndex += existingNewLineLength;
                replacementIndex += replacementNewLineLength;
                continue;
            }
            if (IsHorizontalWhitespace(existing[existingIndex]))
            {
                while (existingIndex < existing.Length && IsHorizontalWhitespace(existing[existingIndex]))
                    existingIndex++;
                while (replacementIndex < replacement.Length && IsHorizontalWhitespace(replacement[replacementIndex]))
                    replacementIndex++;
                continue;
            }
            if (replacementIndex >= replacement.Length || existing[existingIndex] != replacement[replacementIndex])
            {
                replacementLength = 0;
                return false;
            }
            existingIndex++;
            replacementIndex++;
        }

        if (existing.Length > 0 && IsHorizontalWhitespace(existing[^1]))
        {
            while (replacementIndex < replacement.Length && IsHorizontalWhitespace(replacement[replacementIndex]))
                replacementIndex++;
        }

        replacementLength = replacementIndex;
        return true;
    }

    private static bool TryConsumePreservedSuffix(
        ReadOnlySpan<char> existing,
        ReadOnlySpan<char> replacement,
        out int replacementLength)
    {
        var existingIndex = existing.Length;
        var replacementIndex = replacement.Length;
        while (existingIndex > 0)
        {
            var existingNewLineLength = PreviousNewLineLength(existing, existingIndex);
            var replacementNewLineLength = PreviousNewLineLength(replacement, replacementIndex);
            if (existingNewLineLength > 0 && replacementNewLineLength > 0)
            {
                existingIndex -= existingNewLineLength;
                replacementIndex -= replacementNewLineLength;
                continue;
            }
            if (IsHorizontalWhitespace(existing[existingIndex - 1]))
            {
                var existingWhitespaceEnd = existingIndex;
                while (existingIndex > 0 && IsHorizontalWhitespace(existing[existingIndex - 1]))
                    existingIndex--;
                var replacementWhitespaceEnd = replacementIndex;
                while (replacementIndex > 0 && IsHorizontalWhitespace(replacement[replacementIndex - 1]))
                    replacementIndex--;
                var sameWhitespace = existing[existingIndex..existingWhitespaceEnd].SequenceEqual(
                    replacement[replacementIndex..replacementWhitespaceEnd]);
                var indentationBeforeMatchingNewLine =
                    PreviousNewLineLength(existing, existingIndex) > 0 &&
                    PreviousNewLineLength(replacement, replacementIndex) > 0;
                if (!sameWhitespace && !indentationBeforeMatchingNewLine)
                {
                    replacementLength = 0;
                    return false;
                }
                continue;
            }
            if (replacementIndex <= 0 || existing[existingIndex - 1] != replacement[replacementIndex - 1])
            {
                replacementLength = 0;
                return false;
            }
            existingIndex--;
            replacementIndex--;
        }

        replacementLength = replacement.Length - replacementIndex;
        return true;
    }

    private static bool IsHorizontalWhitespace(char value) => value is ' ' or '\t';

    private static int NewLineLength(ReadOnlySpan<char> text, int index)
    {
        if (index >= text.Length)
            return 0;
        if (text[index] == '\n')
            return 1;
        if (text[index] != '\r')
            return 0;
        return index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
    }

    private static int PreviousNewLineLength(ReadOnlySpan<char> text, int end)
    {
        if (end <= 0)
            return 0;
        if (text[end - 1] == '\n')
            return end > 1 && text[end - 2] == '\r' ? 2 : 1;
        return text[end - 1] == '\r' ? 1 : 0;
    }

    private static string LspSnippetText(
        string text,
        int cursorOffset,
        SourceText source,
        TextSpan completionSpan,
        ImmutableArray<SnippetTabStop> tabStops)
    {
        var snippetText = BuildLspSnippetText(text, cursorOffset, tabStops);
        return NormalizeSnippetIndentation(
            snippetText,
            cursorOffset: 0,
            LeadingIndentation(source, completionSpan.Start)).Text;
    }

    private static string BuildLspSnippetText(
        string text,
        int cursorOffset,
        ImmutableArray<SnippetTabStop> tabStops)
    {
        cursorOffset = Math.Clamp(cursorOffset, 0, text.Length);
        var spans = new List<(TextSpan Span, int TabStop)>();
        for (var index = 0; index < tabStops.Length; index++)
        {
            var tabStop = tabStops[index];
            if (tabStop.Spans.IsDefaultOrEmpty)
                return BuildLspSnippetText(text, cursorOffset, []);

            var first = tabStop.Spans[0];
            if (first.Start < 0 || first.End > text.Length || first.IsEmpty)
                return BuildLspSnippetText(text, cursorOffset, []);
            var expectedText = text.AsSpan(first.Start, first.Length);
            foreach (var span in tabStop.Spans)
            {
                if (span.Start < 0 ||
                    span.End > text.Length ||
                    span.IsEmpty ||
                    cursorOffset > span.Start && cursorOffset < span.End ||
                    !text.AsSpan(span.Start, span.Length).SequenceEqual(expectedText))
                {
                    return BuildLspSnippetText(text, cursorOffset, []);
                }
                spans.Add((span, index + 1));
            }
        }

        spans.Sort(static (left, right) => left.Span.Start.CompareTo(right.Span.Start));
        for (var index = 1; index < spans.Count; index++)
        {
            if (spans[index - 1].Span.End > spans[index].Span.Start)
                return BuildLspSnippetText(text, cursorOffset, []);
        }

        var builder = new System.Text.StringBuilder(text.Length + spans.Count * 8 + 4);
        var spanIndex = 0;
        var position = 0;
        while (position <= text.Length)
        {
            if (position == cursorOffset)
                builder.Append("${0}");

            if (spanIndex < spans.Count && spans[spanIndex].Span.Start == position)
            {
                var (span, tabStop) = spans[spanIndex++];
                builder.Append("${").Append(tabStop).Append(':');
                AppendEscapedLspSnippetText(builder, text.AsSpan(span.Start, span.Length));
                builder.Append('}');
                position = span.End;
                continue;
            }
            if (position == text.Length)
                break;

            AppendEscapedLspSnippetText(builder, text.AsSpan(position, 1));
            position++;
        }
        return builder.ToString();
    }

    private static async Task<ImmutableArray<SnippetTabStop>> TryGetCSharpSnippetTabStopsAsync(
        Document document,
        SourceText source,
        CompletionItem item,
        TextChange primaryChange,
        bool constructedFromInlineExpression,
        CancellationToken cancellationToken)
    {
        if (document.Project.Language != LanguageNames.CSharp ||
            !item.Properties.TryGetValue("SnippetIdentifier", out var snippetIdentifier) ||
            string.IsNullOrEmpty(primaryChange.NewText))
        {
            return [];
        }

        var generatedSpan = new TextSpan(primaryChange.Span.Start, primaryChange.NewText.Length);
        var updatedDocument = document.WithText(source.WithChanges(primaryChange));
        var root = await updatedDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return [];

        switch (snippetIdentifier)
        {
            case "class":
            case "enum":
            case "interface":
            case "struct":
            {
                var declaration = GeneratedSnippetNode<BaseTypeDeclarationSyntax>(root, generatedSpan);
                return declaration is null
                    ? []
                    : [TabStop(generatedSpan, declaration.Identifier.Span)];
            }
            case "prop":
            case "propg":
            case "propi":
            case "propr":
            {
                var property = GeneratedSnippetNode<PropertyDeclarationSyntax>(root, generatedSpan);
                return property is null
                    ? []
                    :
                    [
                        TabStop(generatedSpan, property.Type.Span),
                        TabStop(generatedSpan, property.Identifier.Span)
                    ];
            }
            case "if":
            {
                var statement = GeneratedSnippetNode<IfStatementSyntax>(root, generatedSpan);
                return statement is null || constructedFromInlineExpression
                    ? []
                    : [TabStop(generatedSpan, statement.Condition.Span)];
            }
            case "while":
            {
                var statement = GeneratedSnippetNode<WhileStatementSyntax>(root, generatedSpan);
                return statement is null || constructedFromInlineExpression
                    ? []
                    : [TabStop(generatedSpan, statement.Condition.Span)];
            }
            case "do":
            {
                var statement = GeneratedSnippetNode<DoStatementSyntax>(root, generatedSpan);
                return statement is null || constructedFromInlineExpression
                    ? []
                    : [TabStop(generatedSpan, statement.Condition.Span)];
            }
            case "lock":
            {
                var statement = GeneratedSnippetNode<LockStatementSyntax>(root, generatedSpan);
                return statement is null
                    ? []
                    : [TabStop(generatedSpan, statement.Expression.Span)];
            }
            case "using":
            {
                var statement = GeneratedSnippetNode<UsingStatementSyntax>(root, generatedSpan);
                return statement?.Expression is null
                    ? []
                    : [TabStop(generatedSpan, statement.Expression.Span)];
            }
            case "foreach":
            {
                var statement = GeneratedSnippetNode<ForEachStatementSyntax>(root, generatedSpan);
                if (statement is null)
                    return [];
                return constructedFromInlineExpression
                    ? [TabStop(generatedSpan, statement.Identifier.Span)]
                    :
                    [
                        TabStop(generatedSpan, statement.Identifier.Span),
                        TabStop(generatedSpan, statement.Expression.Span)
                    ];
            }
            case "for":
            case "forr":
                return ForSnippetTabStops(
                    GeneratedSnippetNode<ForStatementSyntax>(root, generatedSpan),
                    generatedSpan,
                    snippetIdentifier,
                    constructedFromInlineExpression);
            default:
                return [];
        }
    }

    private static TNode? GeneratedSnippetNode<TNode>(
        SyntaxNode root,
        TextSpan generatedSpan)
        where TNode : SyntaxNode
    {
        return root.DescendantNodes()
            .OfType<TNode>()
            .Where(candidate => candidate.Span.IntersectsWith(generatedSpan))
            .OrderBy(candidate => Math.Abs(candidate.SpanStart - generatedSpan.Start))
            .ThenByDescending(static candidate => candidate.Span.Length)
            .FirstOrDefault();
    }

    private static ImmutableArray<SnippetTabStop> ForSnippetTabStops(
        ForStatementSyntax? statement,
        TextSpan generatedSpan,
        string snippetIdentifier,
        bool constructedFromInlineExpression)
    {
        if (statement?.Declaration is not { Variables.Count: 1 } declaration ||
            declaration.Variables[0].Initializer?.Value is not { } initializer ||
            statement.Condition is not BinaryExpressionSyntax condition ||
            statement.Incrementors.Count != 1 ||
            statement.Incrementors[0] is not PostfixUnaryExpressionSyntax incrementor)
        {
            return [];
        }

        var variable = declaration.Variables[0];
        var iterator = TabStop(
            generatedSpan,
            variable.Identifier.Span,
            condition.Left.Span,
            incrementor.Operand.Span);
        if (constructedFromInlineExpression)
            return [iterator];

        if (StringComparer.Ordinal.Equals(snippetIdentifier, "for"))
            return [iterator, TabStop(generatedSpan, condition.Right.Span)];
        if (initializer is BinaryExpressionSyntax binaryInitializer)
            return [iterator, TabStop(generatedSpan, binaryInitializer.Left.Span)];
        return [iterator];
    }

    private static SnippetTabStop TabStop(TextSpan generatedSpan, params TextSpan[] spans) =>
        new(spans
            .Select(span => new TextSpan(span.Start - generatedSpan.Start, span.Length))
            .ToImmutableArray());

    private static string LeadingIndentation(SourceText source, int position)
    {
        var line = source.Lines.GetLineFromPosition(position);
        var end = line.Start;
        while (end < position && source[end] is ' ' or '\t')
            end++;
        return source.ToString(TextSpan.FromBounds(line.Start, end));
    }

    private static (string Text, int CursorOffset) NormalizeSnippetIndentation(
        string text,
        int cursorOffset,
        string baseIndentation)
    {
        var baseIndentationColumns = IndentationColumns(baseIndentation);
        if (baseIndentationColumns == 0 || text.IndexOfAny(['\r', '\n']) < 0)
            return (text, cursorOffset);

        var firstLineIndentationLength = 0;
        while (firstLineIndentationLength < text.Length &&
               IsHorizontalWhitespace(text[firstLineIndentationLength]))
        {
            firstLineIndentationLength++;
        }
        var desiredContinuationIndentation = text[..firstLineIndentationLength];
        if (!TryGetContinuationBaseIndentationColumns(
                text,
                out var continuationBaseIndentationColumns))
        {
            return (text, cursorOffset);
        }

        // Preflight every transformed line so mixed tabs/spaces cannot leave the
        // snippet partially reindented.
        var preflightIndex = FirstLineBreakEnd(text);
        while (preflightIndex < text.Length)
        {
            var lineEnd = LineContentEnd(text, preflightIndex);
            if (lineEnd > preflightIndex &&
                !TryConsumeIndentationColumns(
                    text.AsSpan(preflightIndex, lineEnd - preflightIndex),
                    continuationBaseIndentationColumns,
                    out _))
            {
                return (text, cursorOffset);
            }
            preflightIndex = NextLineStart(text, lineEnd);
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var mappedCursorOffset = cursorOffset;
        var index = 0;
        var firstLine = true;
        while (index < text.Length)
        {
            var lineEnd = LineContentEnd(text, index);
            if (!firstLine && lineEnd > index)
            {
                _ = TryConsumeIndentationColumns(
                    text.AsSpan(index, lineEnd - index),
                    continuationBaseIndentationColumns,
                    out var indentationLength);
                var outputLineStart = builder.Length;
                var removedEnd = index + indentationLength;
                builder.Append(desiredContinuationIndentation);
                if (cursorOffset >= removedEnd)
                {
                    mappedCursorOffset += desiredContinuationIndentation.Length - indentationLength;
                }
                else if (cursorOffset > index)
                {
                    mappedCursorOffset = outputLineStart + Math.Min(
                        cursorOffset - index,
                        desiredContinuationIndentation.Length);
                }
                index = removedEnd;
            }

            while (index < text.Length && text[index] is not '\r' and not '\n')
                builder.Append(text[index++]);
            if (index >= text.Length)
                break;

            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                builder.Append("\r\n");
                index += 2;
            }
            else
            {
                builder.Append(text[index++]);
            }
            firstLine = false;
        }

        return (builder.ToString(), mappedCursorOffset);
    }

    private static bool TryGetContinuationBaseIndentationColumns(
        string text,
        out int indentationColumns)
    {
        indentationColumns = int.MaxValue;
        var index = FirstLineBreakEnd(text);
        while (index < text.Length)
        {
            var lineEnd = LineContentEnd(text, index);
            if (lineEnd > index)
            {
                var indentationLength = 0;
                while (index + indentationLength < lineEnd &&
                       IsHorizontalWhitespace(text[index + indentationLength]))
                {
                    indentationLength++;
                }
                indentationColumns = Math.Min(
                    indentationColumns,
                    IndentationColumns(text.AsSpan(index, indentationLength)));
            }
            index = NextLineStart(text, lineEnd);
        }
        return indentationColumns != int.MaxValue;
    }

    private static int FirstLineBreakEnd(string text)
    {
        var lineEnd = LineContentEnd(text, 0);
        return NextLineStart(text, lineEnd);
    }

    private static int LineContentEnd(string text, int start)
    {
        var end = start;
        while (end < text.Length && text[end] is not '\r' and not '\n')
            end++;
        return end;
    }

    private static int NextLineStart(string text, int lineEnd)
    {
        if (lineEnd >= text.Length)
            return text.Length;
        return text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n'
            ? lineEnd + 2
            : lineEnd + 1;
    }

    private static int IndentationColumns(ReadOnlySpan<char> indentation)
    {
        var columns = 0;
        foreach (var character in indentation)
            columns = character == '\t' ? ((columns / 4) + 1) * 4 : columns + 1;
        return columns;
    }

    private static bool TryConsumeIndentationColumns(
        ReadOnlySpan<char> text,
        int requiredColumns,
        out int length)
    {
        var columns = 0;
        length = 0;
        while (length < text.Length && IsHorizontalWhitespace(text[length]) && columns < requiredColumns)
        {
            var nextColumns = text[length] == '\t' ? ((columns / 4) + 1) * 4 : columns + 1;
            if (nextColumns > requiredColumns)
                return false;
            columns = nextColumns;
            length++;
        }
        return columns == requiredColumns;
    }

    private static void AppendEscapedLspSnippetText(
        System.Text.StringBuilder builder,
        ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (character is '\\' or '$' or '}')
                builder.Append('\\');
            builder.Append(character);
        }
    }

    private readonly record struct SnippetTabStop(ImmutableArray<TextSpan> Spans);

    private void AddCompletion(string id, CachedCompletion completion)
    {
        _completionCache[id] = completion;
        _completionOrder.Enqueue(id);
        while (_completionCache.Count > limits.MaxCompletionCacheItems && _completionOrder.TryDequeue(out var expiredId))
            _completionCache.TryRemove(expiredId, out _);
    }

    private static int? CompletionKind(ImmutableArray<string> tags)
    {
        if (tags.Contains(WellKnownTags.Snippet))
            return 15;
        if (tags.Contains(WellKnownTags.Method))
            return 2;
        if (tags.Contains(WellKnownTags.ExtensionMethod))
            return 2;
        if (tags.Contains(WellKnownTags.Property))
            return 10;
        if (tags.Contains(WellKnownTags.Field))
            return 5;
        if (tags.Contains(WellKnownTags.Event))
            return 23;
        if (tags.Contains(WellKnownTags.Class))
            return 7;
        if (tags.Contains(WellKnownTags.Structure))
            return 22;
        if (tags.Contains(WellKnownTags.Interface))
            return 8;
        if (tags.Contains(WellKnownTags.Enum))
            return 13;
        if (tags.Contains(WellKnownTags.Namespace))
            return 9;
        if (tags.Contains(WellKnownTags.Keyword))
            return 14;
        if (tags.Contains(WellKnownTags.Local) || tags.Contains(WellKnownTags.Parameter))
            return 6;
        return null;
    }

    private static LspDiagnostic ConvertDiagnostic(Microsoft.CodeAnalysis.Diagnostic diagnostic, LspDocumentSnapshot snapshot)
    {
        var tags = new List<int>(2);
        if (diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary, StringComparer.Ordinal))
            tags.Add(1);
        if (diagnostic.Descriptor.CustomTags.Contains("Deprecated", StringComparer.Ordinal))
            tags.Add(2);

        return new LspDiagnostic(
            RoslynLanguageSession.ToRange(snapshot.Text, diagnostic.Location.SourceSpan),
            diagnostic.Severity switch
            {
                Microsoft.CodeAnalysis.DiagnosticSeverity.Error => 1,
                Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => 2,
                Microsoft.CodeAnalysis.DiagnosticSeverity.Info => 3,
                _ => 4
            },
            diagnostic.Id,
            "roslyn",
            diagnostic.GetMessage(CultureInfo.InvariantCulture),
            tags.Count == 0 ? null : tags,
            new LspDiagnosticData(
                snapshot.WorkspaceRevision,
                snapshot.SelectionRevision,
                snapshot.Version));
    }

    private static IMethodSymbol[] GetCandidateMethods(
        ArgumentListSyntax argumentList,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        IEnumerable<IMethodSymbol> methods = argumentList.Parent switch
        {
            InvocationExpressionSyntax invocation => semanticModel
                .GetMemberGroup(invocation.Expression, cancellationToken)
                .OfType<IMethodSymbol>()
                .Concat(GetSymbolMethods(semanticModel.GetSymbolInfo(invocation.Expression, cancellationToken))),
            ObjectCreationExpressionSyntax creation => GetSymbolMethods(semanticModel.GetSymbolInfo(creation, cancellationToken)),
            _ => []
        };

        return methods
            .GroupBy(static method => method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static method => method.Parameters.Length)
            .ThenBy(static method => method.ToDisplayString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<IMethodSymbol> GetSymbolMethods(SymbolInfo symbolInfo)
    {
        if (symbolInfo.Symbol is IMethodSymbol method)
            yield return method;
        foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
            yield return candidate;
    }

    private static LspSignatureInformation CreateSignature(IMethodSymbol method, int activeParameter)
    {
        var format = new SymbolDisplayFormat(
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType |
                SymbolDisplayMemberOptions.IncludeExplicitInterface |
                SymbolDisplayMemberOptions.IncludeParameters |
                SymbolDisplayMemberOptions.IncludeType,
            parameterOptions: SymbolDisplayParameterOptions.IncludeExtensionThis |
                SymbolDisplayParameterOptions.IncludeParamsRefOut |
                SymbolDisplayParameterOptions.IncludeType |
                SymbolDisplayParameterOptions.IncludeName |
                SymbolDisplayParameterOptions.IncludeDefaultValue,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
        var parameters = method.Parameters
            .Select(parameter => new LspParameterInformation(
                parameter.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                null))
            .ToArray();
        return new LspSignatureInformation(
            method.ToDisplayString(format),
            null,
            parameters,
            activeParameter < parameters.Length ? activeParameter : null);
    }

    private static List<int> EncodeSemanticTokens(
        SourceText text,
        IEnumerable<ClassifiedSpan> classifiedSpans,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var grouped = classifiedSpans
            .GroupBy(static span => span.TextSpan)
            .OrderBy(static group => group.Key.Start);
        var absoluteTokens = new List<AbsoluteSemanticToken>(Math.Min(maxTokens, 1024));
        foreach (var group in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var typeIndex = group
                .Select(static classified => SemanticTokenType(classified.ClassificationType))
                .FirstOrDefault(static index => index >= 0, -1);
            if (typeIndex < 0)
                continue;

            var modifiers = 0;
            foreach (var classified in group)
                modifiers |= SemanticTokenModifier(classified.ClassificationType);
            var lineSpan = text.Lines.GetLinePositionSpan(group.Key);
            for (var lineNumber = lineSpan.Start.Line; lineNumber <= lineSpan.End.Line; lineNumber++)
            {
                var line = text.Lines[lineNumber];
                var start = Math.Max(group.Key.Start, line.Start);
                var end = Math.Min(group.Key.End, line.End);
                if (end <= start)
                    continue;
                absoluteTokens.Add(new AbsoluteSemanticToken(
                    lineNumber,
                    start - line.Start,
                    end - start,
                    typeIndex,
                    modifiers));
                if (absoluteTokens.Count >= maxTokens)
                    break;
            }

            if (absoluteTokens.Count >= maxTokens)
                break;
        }

        var encoded = new List<int>(absoluteTokens.Count * 5);
        var previousLine = 0;
        var previousCharacter = 0;
        var previousEnd = -1;
        foreach (var token in absoluteTokens.OrderBy(static token => token.Line).ThenBy(static token => token.Character))
        {
            if (token.Line == previousLine && token.Character < previousEnd)
                continue;
            var deltaLine = token.Line - previousLine;
            var deltaCharacter = deltaLine == 0 ? token.Character - previousCharacter : token.Character;
            encoded.Add(deltaLine);
            encoded.Add(deltaCharacter);
            encoded.Add(token.Length);
            encoded.Add(token.Type);
            encoded.Add(token.Modifiers);
            previousLine = token.Line;
            previousCharacter = token.Character;
            previousEnd = token.Character + token.Length;
        }

        return encoded;
    }

    private static List<TextSpan> FindCSharpStringEscapes(SyntaxNode root)
    {
        var escapes = new List<TextSpan>();
        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            var kind = token.Kind().ToString();
            if (kind is not ("StringLiteralToken" or "Utf8StringLiteralToken" or "CharacterLiteralToken"))
                continue;

            var tokenText = token.Text;
            if (tokenText.StartsWith("@\"", StringComparison.Ordinal))
                continue;

            for (var index = 0; index < tokenText.Length - 1; index++)
            {
                if (tokenText[index] != '\\')
                    continue;
                var length = CSharpEscapeLength(tokenText, index);
                escapes.Add(new TextSpan(token.SpanStart + index, length));
                index += length - 1;
            }
        }
        return escapes;
    }

    private static int CSharpEscapeLength(string text, int start)
    {
        var marker = text[start + 1];
        var maximumHexDigits = marker switch
        {
            'u' => 4,
            'U' => 8,
            'x' => 4,
            _ => 0
        };
        if (maximumHexDigits == 0)
            return 2;

        var length = 2;
        while (length < maximumHexDigits + 2 &&
               start + length < text.Length &&
               Uri.IsHexDigit(text[start + length]))
        {
            length++;
        }
        return length;
    }

    private static IEnumerable<ClassifiedSpan> SplitCSharpStringEscapes(
        IEnumerable<ClassifiedSpan> classifications,
        List<TextSpan> escapes)
    {
        if (escapes.Count == 0)
            return classifications;

        var result = new List<ClassifiedSpan>();
        var firstCandidateEscape = 0;
        foreach (var classification in classifications)
        {
            if (classification.ClassificationType is not ("string" or "utf8 string"))
            {
                result.Add(classification);
                continue;
            }

            var position = classification.TextSpan.Start;
            while (firstCandidateEscape < escapes.Count &&
                   escapes[firstCandidateEscape].End <= position)
            {
                firstCandidateEscape++;
            }
            for (var index = firstCandidateEscape; index < escapes.Count; index++)
            {
                var escape = escapes[index];
                if (escape.End <= position)
                    continue;
                if (escape.Start >= classification.TextSpan.End)
                    break;
                if (!classification.TextSpan.Contains(escape))
                    continue;

                if (escape.Start > position)
                {
                    result.Add(new ClassifiedSpan(
                        classification.ClassificationType,
                        TextSpan.FromBounds(position, escape.Start)));
                }
                result.Add(new ClassifiedSpan("string escape character", escape));
                position = escape.End;
            }
            if (position < classification.TextSpan.End)
            {
                result.Add(new ClassifiedSpan(
                    classification.ClassificationType,
                    TextSpan.FromBounds(position, classification.TextSpan.End)));
            }
        }
        return result;
    }

    private static int SemanticTokenType(string classificationType)
    {
        if (classificationType.StartsWith("xml doc comment", StringComparison.Ordinal) ||
            classificationType == "excluded code")
        {
            return 17;
        }
        if (classificationType.StartsWith("xml literal", StringComparison.Ordinal))
            return 18;
        if (classificationType.StartsWith("regex", StringComparison.Ordinal))
            return 20;

        return classificationType switch
        {
            "namespace name" or "module name" => 0,
            "type name" => 1,
            "class name" or "record class name" => 2,
            "enum name" => 3,
            "interface name" => 4,
            "struct name" or "record struct name" => 5,
            "type parameter name" => 6,
            "parameter name" => 7,
            "local name" => 8,
            "property name" => 9,
            "enum member name" => 10,
            "event name" => 11,
            "method name" or "extension method name" => 13,
            "preprocessor keyword" or "preprocessor text" => 14,
            "keyword" or "control keyword" => 15,
            "comment" => 17,
            "string" or "verbatim string" or "utf8 string" => 18,
            "numeric literal" => 19,
            "operator" or "operator - overloaded" => 21,
            "delegate name" => 22,
            "field name" or "constant name" => 23,
            "label name" => 24,
            "string escape character" => 25,
            _ => -1
        };
    }

    private static int SemanticTokenModifier(string classificationType) => classificationType switch
    {
        "static symbol" => 1 << 0,
        "obsolete symbol" => 1 << 1,
        _ => 0
    };

    private static List<LspDocumentSymbol> CreateSymbols(
        SyntaxNode node,
        SourceText text,
        ref int remaining,
        CancellationToken cancellationToken)
    {
        var symbols = new List<LspDocumentSymbol>();
        foreach (var child in node.ChildNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0)
                break;

            var created = TryCreateSymbol(child, text, ref remaining, cancellationToken);
            if (created.Count > 0)
                symbols.AddRange(created);
            else
                symbols.AddRange(CreateSymbols(child, text, ref remaining, cancellationToken));
        }

        return symbols;
    }

    private static List<LspDocumentSymbol> TryCreateSymbol(
        SyntaxNode node,
        SourceText text,
        ref int remaining,
        CancellationToken cancellationToken)
    {
        if (remaining <= 0)
            return [];

        switch (node)
        {
            case BaseNamespaceDeclarationSyntax declaration:
                remaining--;
                return [CreateSymbol(declaration.Name.ToString(), "namespace", 3, declaration, declaration.Name.Span, text, ref remaining, cancellationToken)];
            case TypeDeclarationSyntax declaration:
                remaining--;
                return [CreateSymbol(
                    declaration.Identifier.ValueText,
                    declaration.Keyword.ValueText,
                    declaration.Kind() switch
                    {
                        SyntaxKind.InterfaceDeclaration => 11,
                        SyntaxKind.StructDeclaration or SyntaxKind.RecordStructDeclaration => 23,
                        _ => 5
                    },
                    declaration,
                    declaration.Identifier.Span,
                    text,
                    ref remaining,
                    cancellationToken)];
            case EnumDeclarationSyntax declaration:
                remaining--;
                return [CreateSymbol(declaration.Identifier.ValueText, "enum", 10, declaration, declaration.Identifier.Span, text, ref remaining, cancellationToken)];
            case DelegateDeclarationSyntax declaration:
                remaining--;
                return [CreateLeafSymbol(declaration.Identifier.ValueText, "delegate", 12, declaration, declaration.Identifier.Span, text)];
            case MethodDeclarationSyntax declaration:
                remaining--;
                return [CreateSymbol(declaration.Identifier.ValueText, declaration.ReturnType.ToString(), 6, declaration, declaration.Identifier.Span, text, ref remaining, cancellationToken)];
            case ConstructorDeclarationSyntax declaration:
                remaining--;
                return [CreateSymbol(declaration.Identifier.ValueText, "constructor", 9, declaration, declaration.Identifier.Span, text, ref remaining, cancellationToken)];
            case PropertyDeclarationSyntax declaration:
                remaining--;
                return [CreateLeafSymbol(declaration.Identifier.ValueText, declaration.Type.ToString(), 7, declaration, declaration.Identifier.Span, text)];
            case EventDeclarationSyntax declaration:
                remaining--;
                return [CreateLeafSymbol(declaration.Identifier.ValueText, declaration.Type.ToString(), 24, declaration, declaration.Identifier.Span, text)];
            case LocalFunctionStatementSyntax declaration:
                remaining--;
                return [CreateSymbol(declaration.Identifier.ValueText, declaration.ReturnType.ToString(), 12, declaration, declaration.Identifier.Span, text, ref remaining, cancellationToken)];
            case EnumMemberDeclarationSyntax declaration:
                remaining--;
                return [CreateLeafSymbol(declaration.Identifier.ValueText, null, 22, declaration, declaration.Identifier.Span, text)];
            case FieldDeclarationSyntax declaration:
                return CreateVariableSymbols(declaration, declaration.Declaration, 8, text, ref remaining);
            case EventFieldDeclarationSyntax declaration:
                return CreateVariableSymbols(declaration, declaration.Declaration, 24, text, ref remaining);
            default:
                return [];
        }
    }

    private static LspDocumentSymbol CreateSymbol(
        string name,
        string? detail,
        int kind,
        SyntaxNode node,
        TextSpan selectionSpan,
        SourceText text,
        ref int remaining,
        CancellationToken cancellationToken) =>
        new(
            name,
            detail,
            kind,
            RoslynLanguageSession.ToRange(text, node.Span),
            RoslynLanguageSession.ToRange(text, selectionSpan),
            CreateSymbols(node, text, ref remaining, cancellationToken));

    private static LspDocumentSymbol CreateLeafSymbol(
        string name,
        string? detail,
        int kind,
        SyntaxNode node,
        TextSpan selectionSpan,
        SourceText text) =>
        new(
            name,
            detail,
            kind,
            RoslynLanguageSession.ToRange(text, node.Span),
            RoslynLanguageSession.ToRange(text, selectionSpan),
            []);

    private static List<LspDocumentSymbol> CreateVariableSymbols(
        SyntaxNode parent,
        VariableDeclarationSyntax declaration,
        int kind,
        SourceText text,
        ref int remaining)
    {
        var symbols = new List<LspDocumentSymbol>();
        foreach (var variable in declaration.Variables)
        {
            if (remaining-- <= 0)
                break;
            symbols.Add(CreateLeafSymbol(
                variable.Identifier.ValueText,
                declaration.Type.ToString(),
                kind,
                parent,
                variable.Identifier.Span,
                text));
        }

        return symbols;
    }

    private async Task AddMissingSemicolonActionsAsync(
        LspDocumentSnapshot snapshot,
        LspCodeActionParams parameters,
        List<LspCodeAction> actions,
        CancellationToken cancellationToken)
    {
        var syntaxTree = await snapshot.Document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var compilation = await snapshot.Document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (syntaxTree is null || compilation is null)
            return;

        foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken).Where(diagnostic => diagnostic.Id == "CS1002" && diagnostic.Location.SourceTree == syntaxTree))
        {
            if (actions.Count >= limits.MaxCodeActions)
                return;
            var position = diagnostic.Location.SourceSpan.Start;
            var edit = new LspTextEdit(
                RoslynLanguageSession.ToRange(snapshot.Text, new TextSpan(position, 0)),
                ";");
            actions.Add(new LspCodeAction(
                "Insert missing ';'",
                "quickfix",
                [ConvertDiagnostic(diagnostic, snapshot)],
                true,
                new LspWorkspaceEdit(new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.Ordinal)
                {
                    [snapshot.Uri] = [edit]
                })));
        }
    }

    private static async Task AddWholeDocumentActionAsync(
        LspDocumentSnapshot snapshot,
        Document updatedDocument,
        string title,
        string kind,
        List<LspCodeAction> actions,
        CancellationToken cancellationToken)
    {
        var updatedText = await updatedDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Text.ContentEquals(updatedText))
            return;

        actions.Add(new LspCodeAction(
            title,
            kind,
            null,
            null,
            new LspWorkspaceEdit(new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.Ordinal)
            {
                [snapshot.Uri] =
                [
                    new LspTextEdit(
                        RoslynLanguageSession.ToRange(snapshot.Text, new TextSpan(0, snapshot.Text.Length)),
                        updatedText.ToString())
                ]
            })));
    }

    private static bool AllowsKind(IReadOnlyList<string>? only, string kind) =>
        only is null || only.Count == 0 || only.Any(requested => kind.StartsWith(requested, StringComparison.Ordinal));

    private static string ConcatTaggedText(IEnumerable<TaggedText> parts) =>
        string.Concat(parts.Select(static part => part.Text));

    private static string Truncate(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : string.Concat(value.AsSpan(0, maxCharacters), "...");

    private sealed record CachedCompletion(
        string Path,
        CompletionItem Item,
        LspCompletionItemData Data,
        ResolvedCompletionEdits? EagerEdits,
        TextSpan? ReplacementSpan);

    private sealed record ResolvedCompletionEdits(
        LspTextEdit TextEdit,
        int? InsertTextFormat,
        IReadOnlyList<LspTextEdit>? AdditionalTextEdits);

    private sealed record CompletionCandidate(
        CompletionItem Item,
        string Label,
        string FilterText,
        string? Detail,
        TextSpan? ReplacementSpan);

    private sealed record AbsoluteSemanticToken(
        int Line,
        int Character,
        int Length,
        int Type,
        int Modifiers);
}

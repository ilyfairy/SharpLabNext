using System.Text;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.FSharp.Compiler;

namespace SharpLabNext.Worker.FSharp;

public sealed class FSharpLanguageSession : IAsyncDisposable
{
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
        "operator"
    ];

    internal static readonly string[] SemanticTokenModifiers = [];

    private readonly FSharpCompilerFacade _compiler;
    private readonly LoadedFSharpReferenceSet _referenceSet;
    private readonly FSharpCompilationLimits _compilationLimits;
    private readonly FSharpLspLimits _lspLimits;
    private readonly BuildOptions _options;
    private readonly TemporaryFSharpWorkspace _temporary;
    private readonly Dictionary<string, SessionDocument> _documents;
    private readonly string[] _sourceOrder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _workspaceRevision;
    private readonly long _selectionRevision;
    private int _connectionAttached;
    private bool _disposed;

    internal FSharpLanguageSession(
        string sessionId,
        ValidatedFSharpWorkspace workspace,
        LoadedFSharpReferenceSet referenceSet,
        TemporaryFSharpWorkspace temporary,
        FSharpCompilerFacade compiler,
        FSharpCompilationLimits compilationLimits,
        FSharpLspLimits lspLimits,
        DateTimeOffset expiresAtUtc)
    {
        SessionId = sessionId;
        ExpiresAtUtc = expiresAtUtc;
        _compiler = compiler;
        _referenceSet = referenceSet;
        _temporary = temporary;
        _compilationLimits = compilationLimits;
        _lspLimits = lspLimits;
        _options = workspace.Options;
        _workspaceRevision = workspace.Snapshot.Revision;
        _selectionRevision = workspace.Snapshot.SelectionRevision;
        _sourceOrder = workspace.OrderedFiles.Select(static file => file.Path).ToArray();
        _documents = workspace.OrderedFiles.ToDictionary(
            static file => file.Path,
            static file => new SessionDocument(file.Path, file.Version, file.Text),
            StringComparer.Ordinal);
    }

    public string SessionId { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public bool IsExpired => _disposed || DateTimeOffset.UtcNow >= ExpiresAtUtc;

    public async Task<FSharpLspDocumentState> DidOpenAsync(
        FSharpLspDidOpenParams parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.TextDocument.LanguageId != "fsharp")
            throw new FSharpLspInvalidParamsException("This session only accepts languageId 'fsharp'.");
        var uri = ValidateUri(parameters.TextDocument.Uri);
        var path = PathFromUri(uri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var document = GetDocument(path);
            if (parameters.TextDocument.Version < document.Version)
                throw new FSharpLspContentModifiedException("didOpen version is older than the session document.");
            await ReplaceTextAsync(document, parameters.TextDocument.Version, parameters.TextDocument.Text, cancellationToken);
            return State(uri, document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FSharpLspDocumentState> DidChangeAsync(
        FSharpLspDidChangeParams parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.ContentChanges.Count is 0 or > 100)
            throw new FSharpLspInvalidParamsException("didChange must contain between 1 and 100 changes.");
        var uri = ValidateUri(parameters.TextDocument.Uri);
        var path = PathFromUri(uri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var document = GetDocument(path);
            if (parameters.TextDocument.Version <= document.Version || parameters.TextDocument.Version > int.MaxValue)
                throw new FSharpLspContentModifiedException("didChange version must increase and fit a 32-bit integer.");
            var text = document.Text;
            foreach (var change in parameters.ContentChanges)
            {
                if (change.Range is null)
                {
                    text = change.Text;
                    continue;
                }
                var start = ToOffset(text, change.Range.Start);
                var end = ToOffset(text, change.Range.End);
                if (end < start || change.RangeLength is not null && change.RangeLength != end - start)
                    throw new FSharpLspContentModifiedException("didChange range is inconsistent with the current document.");
                text = string.Concat(text.AsSpan(0, start), change.Text, text.AsSpan(end));
            }
            await ReplaceTextAsync(document, parameters.TextDocument.Version, text, cancellationToken);
            return State(uri, document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FSharpLspDocumentState> DidCloseAsync(
        FSharpLspDidCloseParams parameters,
        CancellationToken cancellationToken)
    {
        var uri = ValidateUri(parameters.TextDocument.Uri);
        var path = PathFromUri(uri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            return State(uri, GetDocument(path));
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<FSharpLspDiagnosticsReport> GetDiagnosticsAsync(string uri, CancellationToken cancellationToken) =>
        WithDocumentAsync(uri, async (document, input, token) =>
        {
            var analysis = await _compiler.AnalyzeAsync(
                input,
                _temporary.Paths[document.Path],
                checked((int)document.Version),
                document.Text,
                token).ConfigureAwait(false);
            var diagnostics = analysis.ParseDiagnostics
                .Concat(analysis.Diagnostics)
                .Where(item => IsForDocument(item.FilePath, document.Path))
                .DistinctBy(static item => (
                    item.Code,
                    item.Message,
                    item.Range.StartLine,
                    item.Range.StartCharacter,
                    item.Range.EndLine,
                    item.Range.EndCharacter))
                .Take(_lspLimits.MaxDiagnostics)
                .Select(item => new FSharpLspDiagnostic(
                    ToRange(item.Range),
                    Severity(item.Severity),
                    item.Code,
                    "fsharp",
                    item.Message,
                    new FSharpLspDiagnosticData(_workspaceRevision, _selectionRevision, document.Version)))
                .ToArray();
            return new FSharpLspDiagnosticsReport(
                uri,
                document.Version,
                _workspaceRevision,
                _selectionRevision,
                diagnostics);
        }, cancellationToken);

    public Task<FSharpLspCompletionList> GetCompletionsAsync(
        FSharpLspCompletionParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync(parameters.TextDocument.Uri, async (document, input, token) =>
        {
            ValidatePosition(document.Text, parameters.Position);
            var items = await _compiler.GetCompletionsAsync(
                input,
                _temporary.Paths[document.Path],
                checked((int)document.Version),
                document.Text,
                parameters.Position.Line,
                parameters.Position.Character,
                _lspLimits.MaxCompletionItems,
                token).ConfigureAwait(false);
            return new FSharpLspCompletionList(
                items.Length >= _lspLimits.MaxCompletionItems,
                items.Select(item => new FSharpLspCompletionItem(
                    item.Name,
                    CompletionKind(item.Kind),
                    EmptyToNull(item.Detail),
                    string.IsNullOrWhiteSpace(item.Documentation)
                        ? null
                        : new FSharpLspMarkupContent("markdown", item.Documentation),
                    item.Name,
                    item.Name,
                    item.NameInCode)).ToArray());
        }, cancellationToken);

    public Task<FSharpLspHover?> GetHoverAsync(
        FSharpLspTextDocumentPositionParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync<FSharpLspHover?>(parameters.TextDocument.Uri, async (document, input, token) =>
        {
            ValidatePosition(document.Text, parameters.Position);
            var hover = await _compiler.GetHoverAsync(
                input,
                _temporary.Paths[document.Path],
                checked((int)document.Version),
                document.Text,
                parameters.Position.Line,
                parameters.Position.Character,
                token).ConfigureAwait(false);
            if (hover is null)
                return null;
            var markdown = hover.Markdown.Length <= _lspLimits.MaxHoverCharacters
                ? hover.Markdown
                : hover.Markdown[.._lspLimits.MaxHoverCharacters];
            return new FSharpLspHover(new FSharpLspMarkupContent("markdown", markdown), ToRange(hover.Range));
        }, cancellationToken);

    public Task<FSharpLspSignatureHelp?> GetSignatureHelpAsync(
        FSharpLspSignatureHelpParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync<FSharpLspSignatureHelp?>(parameters.TextDocument.Uri, async (document, input, token) =>
        {
            ValidatePosition(document.Text, parameters.Position);
            var help = await _compiler.GetSignatureHelpAsync(
                input,
                _temporary.Paths[document.Path],
                checked((int)document.Version),
                document.Text,
                parameters.Position.Line,
                parameters.Position.Character,
                token).ConfigureAwait(false);
            if (help is null)
                return null;
            var signatures = help.Signatures.Select(signature => new FSharpLspSignatureInformation(
                signature.Label,
                string.IsNullOrWhiteSpace(signature.Documentation)
                    ? null
                    : new FSharpLspMarkupContent("markdown", signature.Documentation),
                signature.Parameters.Select(parameter => new FSharpLspParameterInformation(parameter.Label, null)).ToArray(),
                Math.Min(help.ActiveParameter, Math.Max(0, signature.Parameters.Length - 1)))).ToArray();
            return new FSharpLspSignatureHelp(signatures, 0, help.ActiveParameter);
        }, cancellationToken);

    public Task<IReadOnlyList<FSharpLspDocumentSymbol>> GetDocumentSymbolsAsync(
        FSharpLspDocumentSymbolParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync<IReadOnlyList<FSharpLspDocumentSymbol>>(
            parameters.TextDocument.Uri,
            async (document, input, token) =>
            {
                var symbols = await _compiler.GetDocumentSymbolsAsync(
                    input,
                    _temporary.Paths[document.Path],
                    document.Text,
                    token).ConfigureAwait(false);
                return symbols
                    .Take(_lspLimits.MaxDocumentSymbols)
                    .Select(ConvertSymbol)
                    .ToArray();
            },
            cancellationToken);

    public Task<FSharpLspSemanticTokens> GetSemanticTokensAsync(
        FSharpLspSemanticTokensParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync(parameters.TextDocument.Uri, async (document, input, token) =>
        {
            var classifications = await _compiler.GetSemanticClassificationAsync(
                input,
                _temporary.Paths[document.Path],
                checked((int)document.Version),
                document.Text,
                token).ConfigureAwait(false);
            var data = EncodeSemanticTokens(document.Text, classifications, _lspLimits.MaxSemanticTokens, token);
            return new FSharpLspSemanticTokens($"{document.Version}:{_workspaceRevision}", data);
        }, cancellationToken);

    public Task<IReadOnlyList<FSharpLspCodeAction>> GetCodeActionsAsync(
        FSharpLspCodeActionParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync<IReadOnlyList<FSharpLspCodeAction>>(
            parameters.TextDocument.Uri,
            async (document, input, token) =>
            {
                var requestedStart = ToOffset(document.Text, parameters.Range.Start);
                var requestedEnd = ToOffset(document.Text, parameters.Range.End);
                if (requestedEnd < requestedStart)
                    throw new FSharpLspInvalidParamsException("codeAction range end must not precede its start.");

                var compilerEdits = await _compiler.GetUnusedOpenEditsAsync(
                    input,
                    _temporary.Paths[document.Path],
                    checked((int)document.Version),
                    document.Text,
                    token).ConfigureAwait(false);
                var edits = compilerEdits
                    .Select(edit => TryConvertEdit(document.Text, edit))
                    .Where(static edit => edit is not null)
                    .Cast<FSharpLspTextEdit>()
                    .DistinctBy(static edit => edit.Range)
                    .OrderBy(static edit => edit.Range.Start.Line)
                    .ThenBy(static edit => edit.Range.Start.Character)
                    .Take(_lspLimits.MaxCodeActionEdits)
                    .ToArray();
                if (edits.Length == 0)
                    return [];

                var actions = new List<FSharpLspCodeAction>();
                if (AllowsKind(parameters.Context.Only, "quickfix"))
                {
                    foreach (var edit in edits)
                    {
                        var editStart = ToOffset(document.Text, edit.Range.Start);
                        var editEnd = ToOffset(document.Text, edit.Range.End);
                        if (!RangesIntersect(requestedStart, requestedEnd, editStart, editEnd))
                            continue;
                        var diagnostics = parameters.Context.Diagnostics
                            .Where(diagnostic => RangesIntersect(
                                editStart,
                                editEnd,
                                ToOffset(document.Text, diagnostic.Range.Start),
                                ToOffset(document.Text, diagnostic.Range.End)))
                            .ToArray();
                        actions.Add(new FSharpLspCodeAction(
                            "Remove unused open",
                            "quickfix",
                            diagnostics.Length == 0 ? null : diagnostics,
                            true,
                            WorkspaceEdit(parameters.TextDocument.Uri, [edit])));
                    }
                }

                if (AllowsKind(parameters.Context.Only, "source.organizeImports"))
                {
                    actions.Add(new FSharpLspCodeAction(
                        "Remove unused opens",
                        "source.organizeImports",
                        null,
                        true,
                        WorkspaceEdit(parameters.TextDocument.Uri, edits)));
                }

                return actions;
            },
            cancellationToken);

    public IDisposable AttachConnection()
    {
        ThrowIfUnavailable();
        if (Interlocked.CompareExchange(ref _connectionAttached, 1, 0) != 0)
            throw new FSharpLspSessionUnavailableException("The session already has an active LSP connection.");
        return new ConnectionLease(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _temporary.DisposeAsync();
        _gate.Dispose();
    }

    private async Task<T> WithDocumentAsync<T>(
        string uri,
        Func<SessionDocument, FSharpProjectInput, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        uri = ValidateUri(uri);
        var path = PathFromUri(uri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var document = GetDocument(path);
            var sourcePaths = _sourceOrder.Select(item => _temporary.Paths[item]).ToArray();
            var input = FSharpBuildService.CreateProjectInput(
                _temporary.Root,
                sourcePaths,
                _referenceSet,
                _options,
                _workspaceRevision);
            return await action(document, input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReplaceTextAsync(
        SessionDocument document,
        long version,
        string text,
        CancellationToken cancellationToken)
    {
        if (version < 0 || version > int.MaxValue)
            throw new FSharpLspContentModifiedException("Document version must fit a non-negative 32-bit integer.");
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > _compilationLimits.MaxFileUtf8Bytes)
            throw new FSharpLspLimitExceededException("Document exceeds the per-file source limit.");
        var totalBytes = _documents.Values.Sum(static item => Encoding.UTF8.GetByteCount(item.Text))
            - Encoding.UTF8.GetByteCount(document.Text)
            + bytes;
        if (totalBytes > _compilationLimits.MaxTotalSourceUtf8Bytes)
            throw new FSharpLspLimitExceededException("Session exceeds the total source limit.");
        var sourcePaths = _sourceOrder.Select(item => _temporary.Paths[item]).ToArray();
        var input = FSharpBuildService.CreateProjectInput(
            _temporary.Root,
            sourcePaths,
            _referenceSet,
            _options,
            _workspaceRevision + 1);
        var rejected = await FSharpSourceSafety.FindRejectedDirectiveAsync(
            _compiler,
            input,
            _temporary.Paths[document.Path],
            text,
            cancellationToken).ConfigureAwait(false);
        if (rejected is not null)
            throw new FSharpLspInvalidParamsException($"F# directive '#{rejected}' is not allowed in managed workspaces.");
        if (document.Text != text)
        {
            document.Text = text;
            _workspaceRevision++;
            await _temporary.WriteAsync(document.Path, text, cancellationToken);
        }
        document.Version = version;
    }

    private SessionDocument GetDocument(string path) =>
        _documents.TryGetValue(path, out var document)
            ? document
            : throw new FSharpLspInvalidParamsException($"Document '{path}' is not part of the immutable source order.");

    private FSharpLspDocumentState State(string uri, SessionDocument document) =>
        new(uri, document.Path, document.Version, _workspaceRevision, _selectionRevision);

    private bool IsForDocument(string compilerPath, string path) =>
        string.IsNullOrWhiteSpace(compilerPath) ||
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(compilerPath), _temporary.Paths[path]);

    private void ThrowIfUnavailable()
    {
        if (IsExpired)
            throw new FSharpLspSessionUnavailableException("The language session is closed or expired.");
    }

    private static string ValidateUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("file" or "sharplabnext" or "inmemory"))
        {
            throw new FSharpLspInvalidParamsException("Document URI must use file, sharplabnext or inmemory scheme.");
        }
        return parsed.AbsoluteUri;
    }

    private static string PathFromUri(string uri)
    {
        var parsed = new Uri(uri, UriKind.Absolute);
        return FSharpWorkspaceValidator.NormalizeRelativePath(Uri.UnescapeDataString(parsed.AbsolutePath).TrimStart('/'));
    }

    private static int ToOffset(string text, FSharpLspPosition position)
    {
        ValidatePosition(text, position);
        var (start, _) = GetLineBounds(text, position.Line);
        return start + position.Character;
    }

    private static void ValidatePosition(string text, FSharpLspPosition position)
    {
        if (position.Line < 0 || position.Character < 0)
            throw new FSharpLspInvalidParamsException("LSP positions cannot be negative.");
        var (start, end) = GetLineBounds(text, position.Line);
        if (position.Character > end - start)
            throw new FSharpLspInvalidParamsException("LSP UTF-16 character is outside the line.");
    }

    private static (int Start, int End) GetLineBounds(string text, int requestedLine)
    {
        var line = 0;
        var start = 0;
        while (line < requestedLine)
        {
            var next = text.IndexOf('\n', start);
            if (next < 0)
                throw new FSharpLspInvalidParamsException("LSP line is outside the document.");
            start = next + 1;
            line++;
        }
        var end = text.IndexOf('\n', start);
        if (end < 0)
            end = text.Length;
        if (end > start && text[end - 1] == '\r')
            end--;
        return (start, end);
    }

    private static FSharpLspRange ToRange(FSharpTextRange range) => new(
        new FSharpLspPosition(range.StartLine, range.StartCharacter),
        new FSharpLspPosition(range.EndLine, range.EndCharacter));

    private static List<int> EncodeSemanticTokens(
        string text,
        IEnumerable<FSharpSemanticClassification> classifications,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var lineCount = 1 + text.Count(static character => character == '\n');
        var absolute = new List<AbsoluteSemanticToken>(Math.Min(maxTokens, 1024));
        foreach (var classification in classifications
            .OrderBy(static item => item.Range.StartLine)
            .ThenBy(static item => item.Range.StartCharacter)
            .ThenBy(static item => item.Range.EndLine)
            .ThenBy(static item => item.Range.EndCharacter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokenType = SemanticTokenType(classification.Kind);
            if (tokenType < 0 ||
                classification.Range.StartLine < 0 ||
                classification.Range.EndLine < classification.Range.StartLine ||
                classification.Range.StartLine >= lineCount ||
                classification.Range.EndLine >= lineCount)
            {
                continue;
            }

            for (var line = classification.Range.StartLine; line <= classification.Range.EndLine; line++)
            {
                var (lineStart, lineEnd) = GetLineBounds(text, line);
                var lineLength = lineEnd - lineStart;
                var start = line == classification.Range.StartLine
                    ? Math.Clamp(classification.Range.StartCharacter, 0, lineLength)
                    : 0;
                var end = line == classification.Range.EndLine
                    ? Math.Clamp(classification.Range.EndCharacter, 0, lineLength)
                    : lineLength;
                if (end <= start)
                    continue;
                absolute.Add(new AbsoluteSemanticToken(line, start, end - start, tokenType));
                if (absolute.Count >= maxTokens)
                    break;
            }

            if (absolute.Count >= maxTokens)
                break;
        }

        var encoded = new List<int>(absolute.Count * 5);
        var previousLine = 0;
        var previousCharacter = 0;
        var previousEnd = -1;
        foreach (var token in absolute
            .Distinct()
            .OrderBy(static item => item.Line)
            .ThenBy(static item => item.Character)
            .ThenBy(static item => item.Length))
        {
            if (token.Line == previousLine && token.Character < previousEnd)
                continue;
            var deltaLine = token.Line - previousLine;
            encoded.Add(deltaLine);
            encoded.Add(deltaLine == 0 ? token.Character - previousCharacter : token.Character);
            encoded.Add(token.Length);
            encoded.Add(token.Type);
            encoded.Add(0);
            previousLine = token.Line;
            previousCharacter = token.Character;
            previousEnd = token.Character + token.Length;
        }
        return encoded;
    }

    private static int SemanticTokenType(string kind) => kind switch
    {
        "Namespace" or "Module" => 0,
        "Type" or "TypeDef" or "DisposableType" or "Delegate" or "ComputationExpression" => 1,
        "ReferenceType" or "Exception" => 2,
        "Enumeration" => 3,
        "Interface" => 4,
        "ValueType" => 5,
        "TypeArgument" => 6,
        "NamedArgument" => 7,
        "MutableVar" or "DisposableTopLevelValue" or "DisposableLocalValue" or "Literal" or "Field" or
            "Value" or "LocalValue" => 8,
        "Property" or "UnionCaseField" or "RecordField" or "MutableRecordField" or "RecordFieldAsFunction" => 9,
        "UnionCase" => 10,
        "Event" => 11,
        "Function" or "Printf" or "IntrinsicFunction" => 12,
        "Method" or "ExtensionMethod" or "ConstructorForReferenceType" or "ConstructorForValueType" => 13,
        "Operator" => 21,
        _ => -1
    };

    private static FSharpLspTextEdit? TryConvertEdit(string text, FSharpSourceEdit edit)
    {
        var range = ToRange(edit.Range);
        try
        {
            var start = ToOffset(text, range.Start);
            var end = ToOffset(text, range.End);
            return end <= start ? null : new FSharpLspTextEdit(range, edit.NewText);
        }
        catch (FSharpLspInvalidParamsException)
        {
            return null;
        }
    }

    private static FSharpLspWorkspaceEdit WorkspaceEdit(
        string uri,
        IReadOnlyList<FSharpLspTextEdit> edits) =>
        new(new Dictionary<string, IReadOnlyList<FSharpLspTextEdit>>(StringComparer.Ordinal) { [uri] = edits });

    private static bool AllowsKind(IReadOnlyList<string>? only, string kind) =>
        only is null || only.Count == 0 || only.Any(requested =>
            StringComparer.Ordinal.Equals(kind, requested) ||
            kind.StartsWith(requested + ".", StringComparison.Ordinal));

    private static bool RangesIntersect(int leftStart, int leftEnd, int rightStart, int rightEnd) =>
        leftStart <= rightEnd && rightStart <= leftEnd;

    private static int Severity(CompilerDiagnosticSeverity severity) => severity switch
    {
        CompilerDiagnosticSeverity.Error => 1,
        CompilerDiagnosticSeverity.Warning => 2,
        CompilerDiagnosticSeverity.Information => 3,
        _ => 4
    };

    private static int CompletionKind(string kind) => kind switch
    {
        var value when value.Contains("Method", StringComparison.OrdinalIgnoreCase) => 2,
        var value when value.Contains("Property", StringComparison.OrdinalIgnoreCase) => 10,
        var value when value.Contains("Field", StringComparison.OrdinalIgnoreCase) => 5,
        var value when value.Contains("Class", StringComparison.OrdinalIgnoreCase) => 7,
        var value when value.Contains("Interface", StringComparison.OrdinalIgnoreCase) => 8,
        var value when value.Contains("Module", StringComparison.OrdinalIgnoreCase) => 9,
        var value when value.Contains("Keyword", StringComparison.OrdinalIgnoreCase) => 14,
        var value when value.Contains("Type", StringComparison.OrdinalIgnoreCase) => 25,
        _ => 6
    };

    private static FSharpLspDocumentSymbol ConvertSymbol(FSharpDocumentSymbol symbol) => new(
        symbol.Name,
        EmptyToNull(symbol.Detail),
        SymbolKind(symbol.Kind),
        ToRange(symbol.Range),
        ToRange(symbol.SelectionRange),
        symbol.Children.Select(ConvertSymbol).ToArray());

    private static int SymbolKind(string kind) => kind switch
    {
        "Namespace" => 3,
        "Module" or "ModuleFile" => 2,
        "Type" or "Exception" => 5,
        "Method" => 6,
        "Property" => 7,
        "Field" => 8,
        _ => 13
    };

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record AbsoluteSemanticToken(int Line, int Character, int Length, int Type);

    private sealed class SessionDocument(string path, long version, string text)
    {
        public string Path { get; } = path;
        public long Version { get; set; } = version;
        public string Text { get; set; } = text;
    }

    private sealed class ConnectionLease(FSharpLanguageSession owner) : IDisposable
    {
        private FSharpLanguageSession? _owner = owner;

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is not null)
                Interlocked.Exchange(ref value._connectionAttached, 0);
        }
    }
}

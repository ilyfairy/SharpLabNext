using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Roslyn;

internal static class RoslynAstConverter
{
    private static readonly TextRange EmptyRange = new(0, 0, 0, 0);

    public static AstDocument Convert(
        ValidatedWorkspace workspace,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        string toolchainId,
        AstLimits limits,
        CancellationToken cancellationToken)
    {
        if (workspace.OrderedFiles.Count != syntaxTrees.Count)
            throw new InvalidOperationException("The syntax tree count does not match the validated workspace.");

        var state = new ConversionState(limits, cancellationToken);
        state.ReserveRequired("Workspace", workspace.OrderedFiles.Count + 1);

        var documentInputs = new List<DocumentInput>(syntaxTrees.Count);
        for (var index = 0; index < syntaxTrees.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = syntaxTrees[index];
            var file = workspace.OrderedFiles[index];
            var text = tree.GetText(cancellationToken);
            state.ReserveRequired("Document", Encoding.UTF8.GetByteCount(file.Path) + 64);
            documentInputs.Add(new DocumentInput(file, tree, text));
        }

        var documents = new List<AstNode>(documentInputs.Count);
        foreach (var input in documentInputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["path"] = input.File.Path,
                ["version"] = input.File.Version.ToString(CultureInfo.InvariantCulture),
                ["isActive"] = StringComparer.Ordinal.Equals(input.File.Path, workspace.ActiveFile) ? "true" : "false"
            };

            var children = new List<AstNode>(1);
            var syntaxRoot = input.Tree.GetRoot(cancellationToken);
            var convertedRoot = ConvertNode(syntaxRoot, input.Text, depth: 2, state);
            if (convertedRoot is not null)
                children.Add(convertedRoot);
            else
                properties["childrenTruncated"] = "true";

            documents.Add(new AstNode(
                "Document",
                ToRange(input.Text, new TextSpan(0, input.Text.Length)),
                null,
                properties,
                children));
        }

        var workspaceProperties = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["activeFile"] = workspace.ActiveFile,
            ["fileCount"] = workspace.OrderedFiles.Count.ToString(CultureInfo.InvariantCulture),
            ["coordinateEncoding"] = ContractConventions.TextCoordinateEncoding
        };
        if (state.Truncated)
            workspaceProperties["childrenTruncated"] = "true";

        return new AstDocument(
            workspace.Snapshot.LanguageId,
            toolchainId,
            workspace.Snapshot.Revision,
            new AstNode("Workspace", EmptyRange, null, workspaceProperties, documents),
            state.Truncated);
    }

    private static AstNode? ConvertNode(
        SyntaxNode node,
        SourceText text,
        int depth,
        ConversionState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        var properties = CreateCommonProperties(
            node.RawKind,
            node.GetType().Name,
            node.Language,
            isNode: true,
            isToken: false,
            isTrivia: false,
            Preview(text, node.Span, state.Limits.MaxTextPreviewCharacters));
        properties["containsDiagnostics"] = Boolean(node.ContainsDiagnostics);
        properties["containsDirectives"] = Boolean(node.ContainsDirectives);
        properties["containsSkippedText"] = Boolean(node.ContainsSkippedText);
        properties["hasLeadingTrivia"] = Boolean(node.HasLeadingTrivia);
        properties["hasTrailingTrivia"] = Boolean(node.HasTrailingTrivia);
        properties["isMissing"] = Boolean(node.IsMissing);
        var kind = KindName(node);
        if (!state.TryReserve(kind, properties))
            return null;

        var children = new List<AstNode>();
        if (depth >= state.Limits.MaxDepth)
        {
            if (node.ChildNodesAndTokens().Count > 0)
            {
                properties["childrenTruncated"] = "true";
                state.MarkTruncated();
            }
        }
        else
        {
            foreach (var child in node.ChildNodesAndTokens())
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var converted = child.IsNode
                    ? ConvertNode(child.AsNode()!, text, depth + 1, state)
                    : ConvertToken(child.AsToken(), text, depth + 1, state);
                if (converted is null)
                {
                    properties["childrenTruncated"] = "true";
                    break;
                }

                children.Add(converted);
            }
        }

        return new AstNode(
            kind,
            ToRange(text, node.Span),
            ToRange(text, node.FullSpan),
            properties,
            children);
    }

    private static AstNode? ConvertToken(
        SyntaxToken token,
        SourceText text,
        int depth,
        ConversionState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        var properties = CreateCommonProperties(
            token.RawKind,
            nameof(SyntaxToken),
            token.Language,
            isNode: false,
            isToken: true,
            isTrivia: false,
            Preview(text, token.Span, state.Limits.MaxTextPreviewCharacters));
        properties["valueText"] = TruncateAndEscape(token.ValueText, state.Limits.MaxTextPreviewCharacters);
        properties["value"] = TruncateAndEscape(
            System.Convert.ToString(token.Value, CultureInfo.InvariantCulture) ?? string.Empty,
            state.Limits.MaxTextPreviewCharacters);
        properties["containsDiagnostics"] = Boolean(token.ContainsDiagnostics);
        properties["containsDirectives"] = Boolean(token.ContainsDirectives);
        properties["hasLeadingTrivia"] = Boolean(token.HasLeadingTrivia);
        properties["hasTrailingTrivia"] = Boolean(token.HasTrailingTrivia);
        properties["isKeyword"] = Boolean(IsKeyword(token));
        properties["isMissing"] = Boolean(token.IsMissing);
        properties["leadingTriviaCount"] = token.LeadingTrivia.Count.ToString(CultureInfo.InvariantCulture);
        properties["trailingTriviaCount"] = token.TrailingTrivia.Count.ToString(CultureInfo.InvariantCulture);

        var kind = KindName(token);
        if (!state.TryReserve(kind, properties))
            return null;

        var children = new List<AstNode>();
        if (depth < state.Limits.MaxDepth)
        {
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                var converted = ConvertTrivia(trivia, text, depth + 1, state);
                if (converted is null)
                {
                    properties["childrenTruncated"] = "true";
                    break;
                }

                children.Add(converted);
            }
        }
        else if (token.HasLeadingTrivia || token.HasTrailingTrivia)
        {
            properties["childrenTruncated"] = "true";
            state.MarkTruncated();
        }

        return new AstNode(
            kind,
            ToRange(text, token.Span),
            ToRange(text, token.FullSpan),
            properties,
            children);
    }

    private static AstNode? ConvertTrivia(
        SyntaxTrivia trivia,
        SourceText text,
        int depth,
        ConversionState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        var properties = CreateCommonProperties(
            trivia.RawKind,
            nameof(SyntaxTrivia),
            trivia.Language,
            isNode: false,
            isToken: false,
            isTrivia: true,
            Preview(text, trivia.Span, state.Limits.MaxTextPreviewCharacters));
        properties["containsDiagnostics"] = Boolean(trivia.ContainsDiagnostics);
        properties["hasStructure"] = Boolean(trivia.HasStructure);

        var kind = KindName(trivia);
        if (!state.TryReserve(kind, properties))
            return null;

        var children = new List<AstNode>(1);
        if (trivia.HasStructure)
        {
            if (depth < state.Limits.MaxDepth)
            {
                var structure = trivia.GetStructure();
                if (structure is not null)
                {
                    var converted = ConvertNode(structure, text, depth + 1, state);
                    if (converted is not null)
                        children.Add(converted);
                    else
                        properties["childrenTruncated"] = "true";
                }
            }
            else
            {
                properties["childrenTruncated"] = "true";
                state.MarkTruncated();
            }
        }

        return new AstNode(
            kind,
            ToRange(text, trivia.Span),
            ToRange(text, trivia.FullSpan),
            properties,
            children);
    }

    private static Dictionary<string, string?> CreateCommonProperties(
        int rawKind,
        string type,
        string language,
        bool isNode,
        bool isToken,
        bool isTrivia,
        string textPreview) =>
        new(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["rawKind"] = rawKind.ToString(CultureInfo.InvariantCulture),
            ["language"] = language,
            ["isNode"] = Boolean(isNode),
            ["isToken"] = Boolean(isToken),
            ["isTrivia"] = Boolean(isTrivia),
            ["textPreview"] = textPreview
        };

    private static bool IsKeyword(SyntaxToken token) => token.Language switch
    {
        LanguageNames.CSharp => SyntaxFacts.IsKeywordKind((SyntaxKind)token.RawKind),
        LanguageNames.VisualBasic => Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts.IsKeywordKind(
            (Microsoft.CodeAnalysis.VisualBasic.SyntaxKind)token.RawKind),
        _ => false
    };

    private static string Boolean(bool value) => value ? "true" : "false";

    private static string KindName(SyntaxNode node) => node.Language switch
    {
        LanguageNames.CSharp => Microsoft.CodeAnalysis.CSharp.CSharpExtensions.Kind(node).ToString(),
        LanguageNames.VisualBasic => Microsoft.CodeAnalysis.VisualBasic.VisualBasicExtensions.Kind(node).ToString(),
        _ => node.GetType().Name
    };

    private static string KindName(SyntaxToken token) => token.Language switch
    {
        LanguageNames.CSharp => Microsoft.CodeAnalysis.CSharp.CSharpExtensions.Kind(token).ToString(),
        LanguageNames.VisualBasic => Microsoft.CodeAnalysis.VisualBasic.VisualBasicExtensions.Kind(token).ToString(),
        _ => token.GetType().Name
    };

    private static string KindName(SyntaxTrivia trivia) => trivia.Language switch
    {
        LanguageNames.CSharp => Microsoft.CodeAnalysis.CSharp.CSharpExtensions.Kind(trivia).ToString(),
        LanguageNames.VisualBasic => Microsoft.CodeAnalysis.VisualBasic.VisualBasicExtensions.Kind(trivia).ToString(),
        _ => trivia.GetType().Name
    };

    private static string Preview(SourceText text, TextSpan span, int maxCharacters)
    {
        var length = Math.Min(span.Length, maxCharacters);
        var preview = length == 0 ? string.Empty : text.ToString(new TextSpan(span.Start, length));
        if (length < span.Length)
            preview += "...";
        return Escape(preview);
    }

    private static string TruncateAndEscape(string value, int maxCharacters)
    {
        var truncated = value.Length <= maxCharacters
            ? value
            : string.Concat(value.AsSpan(0, maxCharacters), "...");
        return Escape(truncated);
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static TextRange ToRange(SourceText text, TextSpan span)
    {
        var lineSpan = text.Lines.GetLinePositionSpan(span);
        return new TextRange(
            lineSpan.Start.Line,
            lineSpan.Start.Character,
            lineSpan.End.Line,
            lineSpan.End.Character);
    }

    private sealed record DocumentInput(
        ValidatedWorkspaceFile File,
        SyntaxTree Tree,
        SourceText Text);

    private sealed class ConversionState(AstLimits limits, CancellationToken cancellationToken)
    {
        private int _nodeCount;
        private int _utf8Bytes;

        public AstLimits Limits { get; } = limits;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public bool Truncated { get; private set; }

        public void ReserveRequired(string kind, int estimatedPropertyBytes)
        {
            _nodeCount++;
            _utf8Bytes = checked(_utf8Bytes + Encoding.UTF8.GetByteCount(kind) + estimatedPropertyBytes);
            if (_nodeCount > Limits.MaxNodes || _utf8Bytes > Limits.MaxUtf8Bytes)
                Truncated = true;
        }

        public bool TryReserve(string kind, IReadOnlyDictionary<string, string?> properties)
        {
            var estimatedBytes = Encoding.UTF8.GetByteCount(kind) + 64;
            foreach (var property in properties)
            {
                estimatedBytes = checked(estimatedBytes + Encoding.UTF8.GetByteCount(property.Key));
                if (property.Value is not null)
                    estimatedBytes = checked(estimatedBytes + Encoding.UTF8.GetByteCount(property.Value));
            }

            if (_nodeCount >= Limits.MaxNodes || _utf8Bytes > Limits.MaxUtf8Bytes - estimatedBytes)
            {
                Truncated = true;
                return false;
            }

            _nodeCount++;
            _utf8Bytes += estimatedBytes;
            return true;
        }

        public void MarkTruncated() => Truncated = true;
    }
}

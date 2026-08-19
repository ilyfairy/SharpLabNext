using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace SharpLabNext.Worker.Roslyn;

internal static class VisualBasicLspFeatureAdapter
{
    public static LspSignatureHelp? CreateSignatureHelp(
        SyntaxNode root,
        SemanticModel semanticModel,
        SourceText text,
        int position,
        CancellationToken cancellationToken)
    {
        if (text.Length == 0)
            return null;

        var tokenPosition = Math.Clamp(position == 0 ? 0 : position - 1, 0, text.Length - 1);
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
        var boundMethod = argumentList.Parent switch
        {
            InvocationExpressionSyntax invocation =>
                semanticModel.GetSymbolInfo(invocation.Expression, cancellationToken).Symbol as IMethodSymbol,
            ObjectCreationExpressionSyntax creation =>
                semanticModel.GetSymbolInfo(creation, cancellationToken).Symbol as IMethodSymbol,
            _ => null
        };
        var signatures = methods
            .Take(50)
            .Select(method => CreateSignature(method, activeParameter))
            .ToArray();
        var activeSignature = boundMethod is null
            ? 0
            : Array.FindIndex(methods, method => SymbolEqualityComparer.Default.Equals(method, boundMethod));
        if (activeSignature < 0 || activeSignature >= signatures.Length)
            activeSignature = 0;

        return new LspSignatureHelp(signatures, activeSignature, activeParameter);
    }

    public static IReadOnlyList<LspDocumentSymbol> CreateDocumentSymbols(
        SyntaxNode root,
        SourceText text,
        int maxSymbols,
        CancellationToken cancellationToken)
    {
        var remaining = maxSymbols;
        return CreateSymbols(root, text, ref remaining, cancellationToken);
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
            ObjectCreationExpressionSyntax creation =>
                GetSymbolMethods(semanticModel.GetSymbolInfo(creation, cancellationToken)),
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
            case NamespaceBlockSyntax declaration:
                remaining--;
                return [CreateSymbol(
                    declaration.NamespaceStatement.Name.ToString(),
                    "Namespace",
                    3,
                    declaration,
                    declaration.NamespaceStatement.Name.Span,
                    text,
                    ref remaining,
                    cancellationToken)];
            case ClassBlockSyntax declaration:
                return CreateTypeSymbol(declaration, declaration.ClassStatement, 5, text, ref remaining, cancellationToken);
            case StructureBlockSyntax declaration:
                return CreateTypeSymbol(declaration, declaration.StructureStatement, 23, text, ref remaining, cancellationToken);
            case InterfaceBlockSyntax declaration:
                return CreateTypeSymbol(declaration, declaration.InterfaceStatement, 11, text, ref remaining, cancellationToken);
            case ModuleBlockSyntax declaration:
                return CreateTypeSymbol(declaration, declaration.ModuleStatement, 2, text, ref remaining, cancellationToken);
            case EnumBlockSyntax declaration:
                remaining--;
                return [CreateSymbol(
                    declaration.EnumStatement.Identifier.ValueText,
                    "Enum",
                    10,
                    declaration,
                    declaration.EnumStatement.Identifier.Span,
                    text,
                    ref remaining,
                    cancellationToken)];
            case MethodBlockSyntax declaration:
                remaining--;
                return [CreateSymbol(
                    declaration.SubOrFunctionStatement.Identifier.ValueText,
                    declaration.SubOrFunctionStatement.AsClause?.ToString(),
                    6,
                    declaration,
                    declaration.SubOrFunctionStatement.Identifier.Span,
                    text,
                    ref remaining,
                    cancellationToken)];
            case ConstructorBlockSyntax declaration:
                remaining--;
                return [CreateSymbol(
                    "New",
                    "Constructor",
                    9,
                    declaration,
                    declaration.SubNewStatement.NewKeyword.Span,
                    text,
                    ref remaining,
                    cancellationToken)];
            case PropertyBlockSyntax declaration:
                remaining--;
                return [CreateLeafSymbol(
                    declaration.PropertyStatement.Identifier.ValueText,
                    declaration.PropertyStatement.AsClause?.ToString(),
                    7,
                    declaration,
                    declaration.PropertyStatement.Identifier.Span,
                    text)];
            case EventBlockSyntax declaration:
                remaining--;
                return [CreateLeafSymbol(
                    declaration.EventStatement.Identifier.ValueText,
                    declaration.EventStatement.AsClause?.ToString(),
                    24,
                    declaration,
                    declaration.EventStatement.Identifier.Span,
                    text)];
            case MethodStatementSyntax declaration when declaration.Parent is not MethodBlockSyntax:
                remaining--;
                return [CreateLeafSymbol(
                    declaration.Identifier.ValueText,
                    declaration.AsClause?.ToString(),
                    6,
                    declaration,
                    declaration.Identifier.Span,
                    text)];
            case PropertyStatementSyntax declaration when declaration.Parent is not PropertyBlockSyntax:
                remaining--;
                return [CreateLeafSymbol(
                    declaration.Identifier.ValueText,
                    declaration.AsClause?.ToString(),
                    7,
                    declaration,
                    declaration.Identifier.Span,
                    text)];
            case EventStatementSyntax declaration when declaration.Parent is not EventBlockSyntax:
                remaining--;
                return [CreateLeafSymbol(
                    declaration.Identifier.ValueText,
                    declaration.AsClause?.ToString(),
                    24,
                    declaration,
                    declaration.Identifier.Span,
                    text)];
            case DelegateStatementSyntax declaration:
                remaining--;
                return [CreateLeafSymbol(
                    declaration.Identifier.ValueText,
                    declaration.AsClause?.ToString(),
                    12,
                    declaration,
                    declaration.Identifier.Span,
                    text)];
            case EnumMemberDeclarationSyntax declaration:
                remaining--;
                return [CreateLeafSymbol(
                    declaration.Identifier.ValueText,
                    null,
                    22,
                    declaration,
                    declaration.Identifier.Span,
                    text)];
            case FieldDeclarationSyntax declaration:
                return CreateFieldSymbols(declaration, text, ref remaining);
            default:
                return [];
        }
    }

    private static List<LspDocumentSymbol> CreateTypeSymbol(
        SyntaxNode block,
        TypeStatementSyntax statement,
        int kind,
        SourceText text,
        ref int remaining,
        CancellationToken cancellationToken)
    {
        remaining--;
        return [CreateSymbol(
            statement.Identifier.ValueText,
            statement.DeclarationKeyword.ValueText,
            kind,
            block,
            statement.Identifier.Span,
            text,
            ref remaining,
            cancellationToken)];
    }

    private static List<LspDocumentSymbol> CreateFieldSymbols(
        FieldDeclarationSyntax declaration,
        SourceText text,
        ref int remaining)
    {
        var symbols = new List<LspDocumentSymbol>();
        foreach (var declarator in declaration.Declarators)
        {
            foreach (var name in declarator.Names)
            {
                if (remaining-- <= 0)
                    return symbols;
                symbols.Add(CreateLeafSymbol(
                    name.Identifier.ValueText,
                    declarator.AsClause?.ToString(),
                    8,
                    declaration,
                    name.Identifier.Span,
                    text));
            }
        }

        return symbols;
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
}

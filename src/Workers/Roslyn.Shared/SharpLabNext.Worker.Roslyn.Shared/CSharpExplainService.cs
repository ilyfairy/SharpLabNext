using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Roslyn;

public sealed class CSharpExplainService(RoslynWorkerIdentity identity, CompilationLimits compilationLimits, AstLimits explainLimits)
{
    public async Task<ExplainResult> ExecuteAsync(ExplainRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RoslynCompilerIdentity.Ensure(identity, "C# compiler", CSharpBuildService.GetLoadedCompilerVersion(), CSharpBuildService.GetLoadedCompilerCommit());

        var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new BuildDeadlineExceededException("The explain deadline has already elapsed.", cancellationToken);
        var workerLimit = TimeSpan.FromMilliseconds(compilationLimits.MaxBuildMilliseconds);
        if (remaining > workerLimit)
            remaining = workerLimit;

        using var deadlineCancellation = new CancellationTokenSource(remaining);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCancellation.Token);
        try
        {
            return await Task.Run(() => ExecuteCore(request, linkedCancellation.Token), linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new BuildDeadlineExceededException("The explain deadline elapsed.", deadlineCancellation.Token);
        }
    }

    private ExplainResult ExecuteCore(ExplainRequest request, CancellationToken cancellationToken)
    {
        var workspace = WorkspaceValidator.Validate(request, compilationLimits);
        var state = new ExplainConversionState(explainLimits, cancellationToken);
        var files = new List<ExplanationFile>(workspace.OrderedFiles.Count);
        foreach (var file in workspace.OrderedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = SourceText.From(file.Text, Encoding.UTF8, SourceHashAlgorithm.Sha256);
            var tree = CSharpSyntaxTree.ParseText(text, CSharpBuildService.CreateParseOptions(workspace.Options), file.Path, cancellationToken);
            var nodes = new List<ExplanationNode>();
            AppendNode(tree.GetRoot(cancellationToken), text, depth: 0, nodes, state);
            files.Add(new ExplanationFile(file.Path, nodes));
        }

        return new ExplainResult(new ExplanationDocument("csharp", identity.ToolchainId, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision, files, state.Truncated), new BuildIdentity(identity.ReleaseId, "csharp", identity.ToolchainId, identity.CompilerVersion, CSharpBuildService.GetLoadedCompilerCommit(), workspace.Snapshot.ReferenceSetId, identity.WorkerImageId));
    }

    private static void AppendNode(SyntaxNode node, SourceText text, int depth, List<ExplanationNode> output, ExplainConversionState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        if (depth > state.Limits.MaxDepth)
        {
            state.MarkTruncated();
            return;
        }

        var kind = node.Kind().ToString();
        var title = CreateTitle(node, kind);
        var description = CreateDescription(node, kind);
        if (!state.TryReserve(kind, title, description))
            return;
        output.Add(new ExplanationNode(kind, title, description, ToRange(text, node.Span), depth));

        foreach (var child in node.ChildNodes())
        {
            if (state.Truncated)
                return;
            AppendNode(child, text, depth + 1, output, state);
        }
    }

    private static string CreateTitle(SyntaxNode node, string kind) => node switch
    {
        BaseTypeDeclarationSyntax type => $"{Humanize(kind)}: {type.Identifier.ValueText}",
        DelegateDeclarationSyntax declaration => $"Delegate declaration: {declaration.Identifier.ValueText}",
        MethodDeclarationSyntax method => $"Method declaration: {method.Identifier.ValueText}",
        ConstructorDeclarationSyntax constructor => $"Constructor declaration: {constructor.Identifier.ValueText}",
        DestructorDeclarationSyntax destructor => $"Destructor declaration: {destructor.Identifier.ValueText}",
        PropertyDeclarationSyntax property => $"Property declaration: {property.Identifier.ValueText}",
        EventDeclarationSyntax @event => $"Event declaration: {@event.Identifier.ValueText}",
        VariableDeclaratorSyntax variable => $"Variable: {variable.Identifier.ValueText}",
        ParameterSyntax parameter when !parameter.Identifier.IsMissing => $"Parameter: {parameter.Identifier.ValueText}",
        TypeParameterSyntax parameter => $"Type parameter: {parameter.Identifier.ValueText}",
        LocalFunctionStatementSyntax function => $"Local function: {function.Identifier.ValueText}",
        LabeledStatementSyntax label => $"Label: {label.Identifier.ValueText}",
        _ => Humanize(kind)
    };

    private static string CreateDescription(SyntaxNode node, string kind) => node switch
    {
        CompilationUnitSyntax => "The root syntax node for this C# source file.",
        UsingDirectiveSyntax => "Imports names from a namespace or type into the current source scope.",
        BaseNamespaceDeclarationSyntax => "Declares a namespace that groups related types and members.",
        ClassDeclarationSyntax => "Declares a reference type whose members share object-oriented behavior and state.",
        StructDeclarationSyntax => "Declares a value type whose value contains its fields directly.",
        InterfaceDeclarationSyntax => "Declares a contract that implementing types can satisfy.",
        RecordDeclarationSyntax => "Declares a record type with value-oriented equality and concise data syntax.",
        EnumDeclarationSyntax => "Declares a named set of integral constants.",
        DelegateDeclarationSyntax => "Declares a type-safe callable signature.",
        MethodDeclarationSyntax => "Declares a named member that can receive arguments and produce a result.",
        ConstructorDeclarationSyntax => "Declares initialization logic that runs when an instance is created.",
        PropertyDeclarationSyntax => "Declares a member accessed through get and optional set or init accessors.",
        FieldDeclarationSyntax => "Declares storage associated with a type or one of its instances.",
        EventDeclarationSyntax or EventFieldDeclarationSyntax => "Declares a notification member based on a delegate type.",
        ParameterSyntax => "Declares an input, output, or reference value accepted by a callable member.",
        TypeParameterSyntax => "Declares a placeholder type supplied when constructing a generic symbol.",
        BlockSyntax => "Groups an ordered sequence of statements into a lexical scope.",
        LocalDeclarationStatementSyntax => "Declares one or more local variables in the current scope.",
        VariableDeclarationSyntax => "Specifies the type and variable declarators in a declaration.",
        VariableDeclaratorSyntax => "Introduces a variable name and its optional initializer.",
        ExpressionStatementSyntax => "Evaluates an expression for its side effects.",
        ReturnStatementSyntax => "Stops the current callable member and optionally supplies its result.",
        IfStatementSyntax => "Conditionally executes a statement when its Boolean condition is true.",
        ElseClauseSyntax => "Provides the alternative branch of an if statement.",
        SwitchStatementSyntax or SwitchExpressionSyntax => "Selects behavior or a value by matching an input against cases or patterns.",
        ForStatementSyntax => "Repeats a statement with initializer, condition, and increment expressions.",
        ForEachStatementSyntax or ForEachVariableStatementSyntax => "Repeats a statement for each element produced by an enumerable value.",
        WhileStatementSyntax => "Repeats a statement while its condition remains true.",
        DoStatementSyntax => "Repeats a statement and tests its condition after each iteration.",
        TryStatementSyntax => "Defines protected code with optional exception handlers and cleanup logic.",
        CatchClauseSyntax => "Handles exceptions matching an optional type and filter.",
        FinallyClauseSyntax => "Runs cleanup logic when control leaves the associated try statement.",
        ThrowStatementSyntax or ThrowExpressionSyntax => "Raises an exception or rethrows the current exception.",
        UsingStatementSyntax => "Scopes a disposable value and disposes it when control leaves the statement.",
        LockStatementSyntax => "Executes a statement while holding a mutual-exclusion lock.",
        InvocationExpressionSyntax => "Invokes a method, delegate, or other callable expression.",
        ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax => "Creates and initializes a new object instance.",
        MemberAccessExpressionSyntax => "Accesses a member through a receiver expression.",
        ElementAccessExpressionSyntax => "Accesses an indexed element through an argument list.",
        AssignmentExpressionSyntax => "Assigns a computed value to a writable target.",
        BinaryExpressionSyntax => "Combines two operand expressions with a binary operator.",
        PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax => "Applies a unary operator to one operand.",
        LiteralExpressionSyntax => "Represents a literal value written directly in source code.",
        IdentifierNameSyntax => "Refers to a symbol by its identifier.",
        GenericNameSyntax => "Refers to a generic symbol and supplies type arguments.",
        AwaitExpressionSyntax => "Asynchronously waits for an awaitable operation without blocking the current thread.",
        LambdaExpressionSyntax => "Creates an anonymous function that can capture values from its enclosing scope.",
        ConditionalExpressionSyntax => "Selects one of two expressions based on a Boolean condition.",
        PatternSyntax => "Tests a value against a C# pattern and may introduce variables.",
        AttributeSyntax => "Attaches declarative metadata to a program element.",
        _ => $"Represents the C# {Humanize(kind).ToLowerInvariant()} syntax construct."
    };

    private static string Humanize(string kind)
    {
        const string suffix = "Syntax";
        var value = kind.EndsWith(suffix, StringComparison.Ordinal)
            ? kind[..^suffix.Length] : kind;
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(value[index - 1]))
                builder.Append(' ');
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static TextRange ToRange(SourceText text, TextSpan span)
    {
        var lineSpan = text.Lines.GetLinePositionSpan(span);
        return new TextRange(lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character);
    }

    private sealed class ExplainConversionState(AstLimits limits, CancellationToken cancellationToken)
    {
        private int _nodes;
        private int _utf8Bytes;

        public AstLimits Limits { get; } = limits;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public bool Truncated { get; private set; }

        public bool TryReserve(string kind, string title, string description)
        {
            var bytes = Encoding.UTF8.GetByteCount(kind) + Encoding.UTF8.GetByteCount(title) + Encoding.UTF8.GetByteCount(description) + 64;
            if (_nodes >= Limits.MaxNodes || _utf8Bytes > Limits.MaxUtf8Bytes - bytes)
            {
                Truncated = true;
                return false;
            }
            _nodes++;
            _utf8Bytes += bytes;
            return true;
        }

        public void MarkTruncated() => Truncated = true;
    }
}

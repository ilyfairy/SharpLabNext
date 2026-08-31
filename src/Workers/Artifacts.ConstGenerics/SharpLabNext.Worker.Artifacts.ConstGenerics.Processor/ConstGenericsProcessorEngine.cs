using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using SharpLabNext.Worker.Artifacts.ConstGenerics.Protocol;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics.Processing;

internal static class ConstGenericsProcessorEngine
{
    public static Task<ConstGenericsProcessorResponse> ExecuteAsync(ConstGenericsProcessorRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        return request.Operation switch
        {
            ConstGenericsProcessorOperation.Il => RenderIlAsync(request, cancellationToken),
            ConstGenericsProcessorOperation.DecompiledCSharp => RenderCSharpAsync(request, cancellationToken),
            ConstGenericsProcessorOperation.Verify => ConstGenericsVerificationRunner.VerifyAsync(request, cancellationToken),
            _ => throw new InvalidDataException("The processor operation is unsupported.")
        };
    }

    public static ConstGenericsProcessorResponse ToFailureResponse(Exception exception, ConstGenericsProcessorOperation operation)
    {
        var (processorId, processorVersion) = operation == ConstGenericsProcessorOperation.Verify
            ? ("ilverification-const-generics", ConstGenericsProcessorProtocol.VerificationProcessorVersion) : (operation == ConstGenericsProcessorOperation.DecompiledCSharp ? "ilspy-const-generics-csharp" : "ilspy-const-generics-il", ConstGenericsProcessorProtocol.IlSpyProcessorVersion);
        return exception switch
        {
            ProcessorLimitExceededException => Response(ConstGenericsProcessorOutcome.LimitExceeded, processorId, processorVersion, "text/plain", publicMessage: "Artifact processing exceeded its configured limit.", truncated: true),
            BadImageFormatException or InvalidDataException => Response(ConstGenericsProcessorOutcome.InvalidArtifact, processorId, processorVersion, "text/plain", publicMessage: "The managed PE or metadata is invalid."),
            OutOfMemoryException => Response(ConstGenericsProcessorOutcome.LimitExceeded, processorId, processorVersion, "text/plain", publicMessage: "Artifact processing exceeded its memory limit.", truncated: true),
            _ => Response(ConstGenericsProcessorOutcome.Failed, processorId, processorVersion, "text/plain", publicMessage: "Artifact processing failed inside the isolated processor.")
        };
    }

    private static async Task<ConstGenericsProcessorResponse> RenderIlAsync(ConstGenericsProcessorRequest request, CancellationToken cancellationToken)
    {
        long charactersWritten;
        await using (var file = new FileStream(request.OutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var streamWriter = new StreamWriter(file, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" })
        {
            var writer = new LimitedTextWriter(streamWriter, request.MaxCharacters);
            writer.WriteLine($"// ConstGenerics ILSpy {ConstGenericsProcessorProtocol.IlSpyCommit}");
            using var module = new PEFile(request.AssemblyPath, PEStreamOptions.PrefetchEntireImage);
            using var resolver = new BoundedAssemblyResolver(Path.GetDirectoryName(request.AssemblyPath)!, request.ReferenceRoots);
            using var debugInfo = PortablePdbDebugInfoProvider.TryOpen(request.PortablePdbPath);
            var output = new PlainTextOutput(writer)
            {
                IndentationString = "    "
            };
            var disassembler = new ReflectionDisassembler(output, cancellationToken)
            {
                AssemblyResolver = resolver,
                DebugInfo = debugInfo,
                ShowSequencePoints = request.IncludeSequencePoints && debugInfo is not null,
                ShowMetadataTokens = request.IncludeMetadataTokens,
                ExpandMemberDefinitions = true
            };
            disassembler.WriteAssemblyHeader(module);
            output.WriteLine();
            disassembler.WriteModuleContents(module);
            await writer.FlushAsync(cancellationToken);
            await file.FlushAsync(cancellationToken);
            charactersWritten = writer.CharactersWritten;
        }

        IReadOnlyList<ConstGenericsProcessorLinkedRange> linkedRanges = [];
        if (request.IncludeSequencePoints)
        {
            var linkedDocument = await IlLinkedRangeParser.ParseAndStripAsync(request.OutputPath, cancellationToken);
            linkedRanges = linkedDocument.LinkedRanges;
            charactersWritten = linkedDocument.CharactersWritten;
        }
        return Response(ConstGenericsProcessorOutcome.Succeeded, "ilspy-const-generics-il", ConstGenericsProcessorProtocol.IlSpyProcessorVersion, "text/x-il", charactersWritten, linkedRanges);
    }

    private static async Task<ConstGenericsProcessorResponse> RenderCSharpAsync(ConstGenericsProcessorRequest request, CancellationToken cancellationToken)
    {
        using var resolver = new BoundedAssemblyResolver(Path.GetDirectoryName(request.AssemblyPath)!, request.ReferenceRoots);
        using var debugInfo = PortablePdbDebugInfoProvider.TryOpen(request.PortablePdbPath);
        var decompiler = new CSharpDecompiler(request.AssemblyPath, resolver, CreateCSharpDecompilerSettings(request.IncludeCompilerGeneratedMembers))
        {
            DebugInfoProvider = debugInfo
        };
        cancellationToken.ThrowIfCancellationRequested();
        var syntaxTree = decompiler.DecompileWholeModuleAsSingleFile();
        if (!request.IncludeCompilerGeneratedMembers)
            RemoveCompilerGeneratedMembers(syntaxTree);
        var source = FormatCSharp(syntaxTree);
        cancellationToken.ThrowIfCancellationRequested();
        var header = $"// Decompiled with ConstGenerics ILSpy {ConstGenericsProcessorProtocol.IlSpyCommit}\n";
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (header.Length + source.Length > request.MaxCharacters)
            throw new ProcessorLimitExceededException();
        await File.WriteAllTextAsync(request.OutputPath, header + source, new UTF8Encoding(false), cancellationToken);
        return Response(ConstGenericsProcessorOutcome.Succeeded, "ilspy-const-generics-csharp", ConstGenericsProcessorProtocol.IlSpyProcessorVersion, "text/x-csharp", header.Length + source.Length);
    }

    private static void RemoveCompilerGeneratedMembers(SyntaxTree syntaxTree)
    {
        var generated = syntaxTree.Descendants.OfType<EntityDeclaration>().Where(IsCompilerGenerated).ToArray();
        foreach (var declaration in generated)
        {
            if (!declaration.Ancestors.OfType<EntityDeclaration>().Any(IsCompilerGenerated))
                declaration.Remove();
        }
    }

    private static CSharpFormattingOptions CreateCSharpFormattingOptions()
    {
        var formatting = FormattingOptionsFactory.CreateAllman();
        // Keep generated source compatible with the workbench's four-space C# style.
        // ILSpy defaults to a tab indentation string for this formatting profile.
        formatting.IndentationString = "    ";
        formatting.SpaceBeforeMethodDeclarationParentheses = false;
        formatting.SpaceBetweenEmptyMethodDeclarationParentheses = false;
        formatting.SpaceBeforeConstructorDeclarationParentheses = false;
        formatting.SpaceBetweenEmptyConstructorDeclarationParentheses = false;
        formatting.SpaceBeforeMethodCallParentheses = false;
        formatting.SpaceBetweenEmptyMethodCallParentheses = false;
        return formatting;
    }

    private static string FormatCSharp(SyntaxTree syntaxTree)
    {
        using var writer = new StringWriter();
        syntaxTree.AcceptVisitor(new DecompiledCSharpOutputVisitor(writer, CreateCSharpFormattingOptions()));
        return writer.ToString();
    }

    private sealed class DecompiledCSharpOutputVisitor(TextWriter writer, CSharpFormattingOptions formatting) : CSharpOutputVisitor(writer, formatting)
    {
        protected override void StartNode(AstNode node)
        {
            if (node is NamespaceDeclaration or EntityDeclaration && node.Parent is SyntaxTree or NamespaceDeclaration && node.PrevSibling is NamespaceDeclaration or EntityDeclaration)
                NewLine();
            base.StartNode(node);
        }
    }

    private static bool IsCompilerGenerated(EntityDeclaration declaration) =>
        declaration.Attributes.SelectMany(static section => section.Attributes).Any(static attribute =>
            {
                var typeName = attribute.Type.ToString();
                return typeName.EndsWith("CompilerGenerated", StringComparison.Ordinal) ||
                       typeName.EndsWith("CompilerGeneratedAttribute", StringComparison.Ordinal);
            });

    private static DecompilerSettings CreateCSharpDecompilerSettings(bool includeCompilerGeneratedMembers)
    {
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        if (!includeCompilerGeneratedMembers)
            return settings;

        settings.AnonymousMethods = false;
        settings.AnonymousTypes = false;
        settings.AsyncAwait = false;
        settings.AsyncEnumerator = false;
        settings.AutomaticEvents = false;
        settings.AutomaticProperties = false;
        settings.ExpandMemberDefinitions = true;
        settings.LocalFunctions = false;
        settings.RemoveDeadCode = false;
        settings.RemoveDeadStores = false;
        settings.UseLambdaSyntax = false;
        settings.YieldReturn = false;
        return settings;
    }

    internal static ConstGenericsProcessorResponse Response(ConstGenericsProcessorOutcome outcome, string processorId, string processorVersion, string mediaType, long outputCharacters = 0, IReadOnlyList<ConstGenericsProcessorLinkedRange>? linkedRanges = null, IReadOnlyList<ConstGenericsProcessorFinding>? findings = null, bool truncated = false, string? publicMessage = null) => new(
            ConstGenericsProcessorProtocol.Version,
            outcome,
            processorId,
            processorVersion,
            mediaType,
            outputCharacters,
            linkedRanges ?? [],
            findings ?? [],
            truncated,
            publicMessage);

    private static void Validate(ConstGenericsProcessorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != ConstGenericsProcessorProtocol.Version)
            throw new InvalidDataException("The processor protocol version is unsupported.");
        if (!Path.IsPathFullyQualified(request.AssemblyPath) || !File.Exists(request.AssemblyPath))
            throw new InvalidDataException("The input assembly is unavailable.");
        if (!Path.IsPathFullyQualified(request.OutputPath))
            throw new InvalidDataException("The output path is invalid.");
        if (request.PortablePdbPath is not null && (!Path.IsPathFullyQualified(request.PortablePdbPath) || !File.Exists(request.PortablePdbPath)))
        {
            throw new InvalidDataException("The portable PDB is unavailable.");
        }
        if (request.ReferenceRoots.Count is 0 or > 4 || request.ReferenceRoots.Any(path => !Path.IsPathFullyQualified(path) || !Directory.Exists(path)))
        {
            throw new InvalidDataException("A reference root is invalid.");
        }
        if (request.MaxCharacters is <= 0 or > 8_000_000 || request.MaxFindings is <= 0 or > ConstGenericsProcessorProtocol.MaximumFindings)
        {
            throw new ProcessorLimitExceededException();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
    }
}

internal sealed class ProcessorLimitExceededException : Exception;

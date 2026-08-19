using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using SharpLabNext.ArtifactProcessing.Protocol;

namespace SharpLabNext.ArtifactProcessing;

internal static class ProcessorEngine
{
    private const string NetFxMixedPeArtifactFormat = "dotnet-framework-mixed-pe-v1";

    public static async Task<ProcessorResponse> ExecuteAsync(
        ProcessorRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        return request.Operation switch
        {
            ProcessorOperation.Il => await RenderIlAsync(request, cancellationToken),
            ProcessorOperation.DecompiledCSharp => await RenderCSharpAsync(request, cancellationToken),
            ProcessorOperation.Verify => await VerificationRunner.VerifyAsync(request, cancellationToken),
            ProcessorOperation.RuntimeInstrumentationV1 => RewriteRuntimeInstrumentation(request),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    public static ProcessorResponse ToFailureResponse(Exception exception) => exception switch
    {
        ProcessorLimitExceededException => Response(
            ProcessorOutcome.LimitExceeded,
            "artifacts-default",
            ProcessorProtocol.IlSpyVersion,
            "text/plain",
            publicMessage: "Artifact processing exceeded a configured limit."),
        BadImageFormatException or InvalidDataException => Response(
            ProcessorOutcome.InvalidArtifact,
            "artifacts-default",
            ProcessorProtocol.IlSpyVersion,
            "text/plain",
            publicMessage: "The artifact is not a supported managed PE or portable PDB."),
        OutOfMemoryException => Response(
            ProcessorOutcome.LimitExceeded,
            "artifacts-default",
            ProcessorProtocol.IlSpyVersion,
            "text/plain",
            publicMessage: "Artifact processing exceeded its memory limit."),
        _ => Response(
            ProcessorOutcome.Failed,
            "artifacts-default",
            ProcessorProtocol.IlSpyVersion,
            "text/plain",
            publicMessage: $"The artifact processor failed ({exception.GetType().Name}).")
    };

    private static async Task<ProcessorResponse> RenderIlAsync(
        ProcessorRequest request,
        CancellationToken cancellationToken)
    {
        long charactersWritten;
        {
            await using var file = new FileStream(
                request.OutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var streamWriter = new StreamWriter(
                file,
                new UTF8Encoding(false),
                64 * 1024,
                leaveOpen: true)
            {
                NewLine = "\n"
            };
            var writer = new LimitedTextWriter(streamWriter, request.MaxCharacters);
            writer.WriteLine($"// ICSharpCode.Decompiler {ProcessorProtocol.IlSpyVersion}");
            using var module = new PEFile(request.AssemblyPath, PEStreamOptions.PrefetchEntireImage);
            using var resolver = new BoundedAssemblyResolver(
                Path.GetDirectoryName(request.AssemblyPath)!,
                request.ReferenceRoots);
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
            if (IsCppCliMixedPe(request))
                WriteCppCliUserIl(disassembler, module);
            else
            {
                disassembler.WriteAssemblyHeader(module);
                output.WriteLine();
                disassembler.WriteModuleContents(module);
            }
            await writer.FlushAsync(cancellationToken);
            await file.FlushAsync(cancellationToken);
            charactersWritten = writer.CharactersWritten;
        }

        IReadOnlyList<ProcessorLinkedRange> linkedRanges = [];
        if (request.IncludeSequencePoints)
        {
            var linkedDocument = await IlLinkedRangeParser.ParseAndStripAsync(
                request.OutputPath,
                cancellationToken);
            linkedRanges = linkedDocument.LinkedRanges;
            charactersWritten = linkedDocument.CharactersWritten;
        }
        return Response(
            ProcessorOutcome.Succeeded,
            "icsharpcode-decompiler-il",
            ProcessorProtocol.IlSpyVersion,
            "text/x-il",
            charactersWritten,
            linkedRanges);
    }

    private static async Task<ProcessorResponse> RenderCSharpAsync(
        ProcessorRequest request,
        CancellationToken cancellationToken)
    {
        using var resolver = new BoundedAssemblyResolver(
            Path.GetDirectoryName(request.AssemblyPath)!,
            request.ReferenceRoots);
        using var debugInfo = PortablePdbDebugInfoProvider.TryOpen(request.PortablePdbPath);
        var settings = CreateCSharpDecompilerSettings(request.IncludeCompilerGeneratedMembers);
        var decompiler = new CSharpDecompiler(request.AssemblyPath, resolver, settings)
        {
            DebugInfoProvider = debugInfo
        };
        cancellationToken.ThrowIfCancellationRequested();
        var peachPieAssembly = IsPeachPieAssembly(request.AssemblyPath);
        var cppCliMixedPe = IsCppCliMixedPe(request);
        var syntaxTree = peachPieAssembly
            ? DecompilePeachPieUserTypes(decompiler, request.AssemblyPath)
            : cppCliMixedPe
                ? DecompileCppCliUserSurface(decompiler, request.AssemblyPath)
                : decompiler.DecompileWholeModuleAsSingleFile();
        if (!request.IncludeCompilerGeneratedMembers)
            RemoveCompilerGeneratedMembers(syntaxTree);
        var source = syntaxTree.ToString(CreateCSharpFormattingOptions());
        cancellationToken.ThrowIfCancellationRequested();

        var header = $"// Decompiled with ICSharpCode.Decompiler {ProcessorProtocol.IlSpyVersion}\n";
        if (peachPieAssembly)
            header += "// PeachPie compiler infrastructure and bootstrap types are omitted.\n";
        if (cppCliMixedPe)
            header += "// MSVC C++/CLI CRT and compiler bootstrap members are omitted.\n";
        if (header.Length + source.Length > request.MaxCharacters)
            throw new ProcessorLimitExceededException();
        await File.WriteAllTextAsync(
            request.OutputPath,
            header + source.Replace("\r\n", "\n", StringComparison.Ordinal),
            new UTF8Encoding(false),
            cancellationToken);
        return Response(
            ProcessorOutcome.Succeeded,
            "icsharpcode-decompiler-csharp",
            ProcessorProtocol.IlSpyVersion,
            "text/x-csharp",
            header.Length + source.Length);
    }

    private static bool IsPeachPieAssembly(string assemblyPath)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        if (!peReader.HasMetadata)
            return false;
        var metadata = peReader.GetMetadataReader();
        return metadata.AssemblyReferences.Any(handle =>
            StringComparer.Ordinal.Equals(
                metadata.GetString(metadata.GetAssemblyReference(handle).Name),
                "Peachpie.Runtime"));
    }

    private static SyntaxTree DecompilePeachPieUserTypes(
        CSharpDecompiler decompiler,
        string assemblyPath)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        var metadata = peReader.GetMetadataReader();
        var userTypes = metadata.TypeDefinitions.Where(handle =>
        {
            var definition = metadata.GetTypeDefinition(handle);
            if (!definition.GetDeclaringType().IsNil)
                return false;
            var name = metadata.GetString(definition.Name);
            return name is not "<Module>" and not "<Script>" and not "__sharplabnext_bootstrap_php";
        });
        return decompiler.DecompileTypes(userTypes);
    }

    private static SyntaxTree DecompileCppCliUserSurface(
        CSharpDecompiler decompiler,
        string assemblyPath)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        var metadata = peReader.GetMetadataReader();
        var entities = CppCliUserEntities(metadata).ToArray();
        return entities.Length == 0
            ? decompiler.DecompileWholeModuleAsSingleFile()
            : decompiler.Decompile(entities);
    }

    private static IEnumerable<EntityHandle> CppCliUserEntities(MetadataReader metadata)
    {
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            if (!type.GetDeclaringType().IsNil)
                continue;
            var name = metadata.GetString(type.Name);
            if (StringComparer.Ordinal.Equals(name, "<Module>"))
            {
                foreach (var methodHandle in type.GetMethods())
                {
                    if (IsCppCliUserMethod(metadata, methodHandle))
                        yield return methodHandle;
                }
                continue;
            }
            if (!IsCppCliBootstrapType(metadata, type))
                yield return typeHandle;
        }
    }

    private static bool IsCppCliUserMethod(
        MetadataReader metadata,
        MethodDefinitionHandle methodHandle)
    {
        var method = metadata.GetMethodDefinition(methodHandle);
        var name = metadata.GetString(method.Name);
        if (name is "main" or "wmain")
            return true;
        if ((method.Attributes & (System.Reflection.MethodAttributes.SpecialName |
                                  System.Reflection.MethodAttributes.RTSpecialName |
                                  System.Reflection.MethodAttributes.PinvokeImpl)) != 0 ||
            name.Length == 0 ||
            name[0] is '_' or '?' or '<' ||
            name.Contains("CRT", StringComparison.Ordinal) ||
            name.Contains("<CrtImplementationDetails>", StringComparison.Ordinal) ||
            name.Contains("<CppImplementationDetails>", StringComparison.Ordinal))
        {
            return false;
        }
        if (!char.IsAsciiLetter(name[0]))
            return false;
        foreach (var character in name.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
                return false;
        }
        return true;
    }

    private static bool IsCppCliBootstrapType(
        MetadataReader metadata,
        TypeDefinition type)
    {
        var name = metadata.GetString(type.Name);
        var @namespace = metadata.GetString(type.Namespace);
        var identity = @namespace.Length == 0 ? name : $"{@namespace}.{name}";
        return identity.Contains("<CrtImplementationDetails>", StringComparison.Ordinal) ||
               identity.Contains("<CppImplementationDetails>", StringComparison.Ordinal) ||
               identity.StartsWith("vc.cppcli.", StringComparison.Ordinal) ||
               identity.StartsWith("gcroot<", StringComparison.Ordinal) ||
               identity.StartsWith("__scrt_", StringComparison.Ordinal) ||
               IsCppCliNativeBootstrapType(@namespace, name) ||
               identity is "_GUID" or "__s_GUID" or "_EXCEPTION_POINTERS" or
                   "IUnknown" or "ICLRRuntimeHost" or "ICorRuntimeHost";
    }

    private static bool IsCppCliNativeBootstrapType(string @namespace, string name)
    {
        // These exact global tags are emitted by the locked x64 MSVC /clr startup.
        // Keep this narrow so ordinary user C++/CLI types remain visible.
        return @namespace.Length == 0 &&
               name is "_crt_argv_mode" or "_crt_app_type" or "HINSTANCE__" or
                   "_IMAGE_DOS_HEADER" or "_IMAGE_NT_HEADERS64";
    }

    private static void WriteCppCliUserIl(
        ReflectionDisassembler disassembler,
        PEFile module)
    {
        disassembler.WriteAssemblyHeader(module);
        disassembler.WriteAssemblyReferences(module.Metadata);
        disassembler.WriteModuleHeader(module, skipMVID: false);
        foreach (var entity in CppCliUserEntities(module.Metadata))
        {
            switch (entity.Kind)
            {
                case HandleKind.MethodDefinition:
                    disassembler.DisassembleMethod(module, (MethodDefinitionHandle)entity);
                    break;
                case HandleKind.TypeDefinition:
                    disassembler.DisassembleType(module, (TypeDefinitionHandle)entity);
                    break;
            }
        }
    }

    private static bool IsCppCliMixedPe(ProcessorRequest request) =>
        StringComparer.Ordinal.Equals(request.ArtifactFormat, NetFxMixedPeArtifactFormat);

    private static DecompilerSettings CreateCSharpDecompilerSettings(bool includeCompilerGeneratedMembers)
    {
        var settings = new DecompilerSettings
        {
            ThrowOnAssemblyResolveErrors = false
        };
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

    private static void RemoveCompilerGeneratedMembers(SyntaxTree syntaxTree)
    {
        var generated = syntaxTree.Descendants
            .OfType<EntityDeclaration>()
            .Where(IsCompilerGenerated)
            .ToArray();
        foreach (var declaration in generated)
        {
            if (!declaration.Ancestors.OfType<EntityDeclaration>().Any(IsCompilerGenerated))
                declaration.Remove();
        }
    }

    private static bool IsCompilerGenerated(EntityDeclaration declaration) =>
        declaration.Attributes
            .SelectMany(static section => section.Attributes)
            .Any(static attribute =>
            {
                var typeName = attribute.Type.ToString();
                return typeName.EndsWith("CompilerGenerated", StringComparison.Ordinal) ||
                       typeName.EndsWith("CompilerGeneratedAttribute", StringComparison.Ordinal);
            });

    private static ProcessorResponse RewriteRuntimeInstrumentation(ProcessorRequest request)
    {
        var result = RuntimeInstrumentationRewriter.Rewrite(request);
        return Response(
            ProcessorOutcome.Succeeded,
            "runtime-instrumentation-v1",
            ProcessorProtocol.RuntimeInstrumentationVersion,
            "application/vnd.sharplabnext.managed-pe",
            publicMessage: result.PublicMessage,
            rewriteApplied: result.RewriteApplied,
            instrumentationPointCount: result.InstrumentationPointCount);
    }

    private static ProcessorResponse Response(
        ProcessorOutcome outcome,
        string processorId,
        string processorVersion,
        string mediaType,
        long outputCharacters = 0,
        IReadOnlyList<ProcessorLinkedRange>? linkedRanges = null,
        IReadOnlyList<ProcessorFinding>? findings = null,
        bool truncated = false,
        string? publicMessage = null,
        bool? rewriteApplied = null,
        int? instrumentationPointCount = null) => new(
            ProcessorProtocol.Version,
            outcome,
            processorId,
            processorVersion,
            mediaType,
            outputCharacters,
            linkedRanges ?? [],
            findings ?? [],
            truncated,
            publicMessage,
            rewriteApplied,
            instrumentationPointCount);

    private static void Validate(ProcessorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != ProcessorProtocol.Version)
            throw new InvalidDataException("The processor protocol version is unsupported.");
        if (request.ArtifactFormat is not ("dotnet-managed-pe-v1" or
            "dotnet-framework-managed-pe-v1" or NetFxMixedPeArtifactFormat))
        {
            throw new InvalidDataException("The artifact format is unsupported.");
        }
        if (StringComparer.Ordinal.Equals(request.ArtifactFormat, NetFxMixedPeArtifactFormat) &&
            request.Operation is not (ProcessorOperation.Il or ProcessorOperation.DecompiledCSharp))
        {
            throw new InvalidDataException(
                "C++/CLI mixed PE artifacts support only IL and Decompiled C# rendering.");
        }
        if (!Path.IsPathFullyQualified(request.AssemblyPath) || !File.Exists(request.AssemblyPath))
            throw new InvalidDataException("The input assembly is unavailable.");
        if (!Path.IsPathFullyQualified(request.OutputPath))
            throw new InvalidDataException("The output path is invalid.");
        if (request.PortablePdbOutputPath is not null && !Path.IsPathFullyQualified(request.PortablePdbOutputPath))
            throw new InvalidDataException("The portable PDB output path is invalid.");
        if (request.PortablePdbPath is not null &&
            (!Path.IsPathFullyQualified(request.PortablePdbPath) || !File.Exists(request.PortablePdbPath)))
        {
            throw new InvalidDataException("The portable PDB is unavailable.");
        }
        if (request.ReferenceRoots.Count > 16 || request.ReferenceRoots.Any(path =>
                !Path.IsPathFullyQualified(path) || !Directory.Exists(path)))
        {
            throw new InvalidDataException("A reference root is invalid.");
        }
        if (request.MaxCharacters is <= 0 or > 4_000_000 || request.MaxFindings is <= 0 or > 5_000)
            throw new ProcessorLimitExceededException();

        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
        if (request.PortablePdbOutputPath is not null)
            Directory.CreateDirectory(Path.GetDirectoryName(request.PortablePdbOutputPath)!);
    }
}

internal sealed class ProcessorLimitExceededException : Exception;

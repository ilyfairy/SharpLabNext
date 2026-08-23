using Mono.Cecil;
using Mono.Cecil.Cil;
using SharpLabNext.ArtifactProcessing.Protocol;

namespace SharpLabNext.ArtifactProcessing;

internal sealed record RuntimeInstrumentationResult(
    bool RewriteApplied,
    int InstrumentationPointCount,
    string? PublicMessage);

internal static class RuntimeInstrumentationRewriter
{
    private const string RuntimeAssemblyName = "SharpLab.Runtime";
    private const string FlowTypeNamespace = "SharpLab.Runtime.Internal";
    private const string FlowTypeName = "Flow";
    private const string NoRewriteAttributeName = "SharpLab.Runtime.NoILRewritingAttribute";
    private const int MaximumInstrumentationStackDepth = 5;

    public static RuntimeInstrumentationResult Rewrite(ProcessorRequest request)
    {
        if (!StringComparer.Ordinal.Equals(
                request.RewriterProfileId,
                ProcessorProtocol.RuntimeInstrumentationProfileId))
            throw new InvalidDataException("The runtime instrumentation profile is unsupported.");
        if (request.PortablePdbPath is not null && request.PortablePdbOutputPath is null)
            throw new InvalidDataException("A rewritten portable PDB output path is required.");

        using var symbolInput = request.PortablePdbPath is null
            ? null
            : new FileStream(request.PortablePdbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new ReaderParameters
        {
            InMemory = true,
            ReadingMode = ReadingMode.Immediate,
            ReadSymbols = symbolInput is not null,
            SymbolReaderProvider = symbolInput is null ? null : new PortablePdbReaderProvider(),
            SymbolStream = symbolInput
        };
        using var assembly = AssemblyDefinition.ReadAssembly(request.AssemblyPath, reader);
        if (HasNoRewriteAttribute(assembly))
        {
            CopyUnchanged(request);
            return new RuntimeInstrumentationResult(
                false,
                0,
                "The assembly opted out of IL rewriting with NoILRewritingAttribute.");
        }

        var module = assembly.MainModule;
        var flow = ImportFlowMethods(module);
        var count = 0;
        foreach (var type in EnumerateTypes(module.Types))
        {
            foreach (var method in type.Methods)
                count += RewriteMethod(method, flow);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
        using var symbolOutput = request.PortablePdbOutputPath is null
            ? null
            : new FileStream(
                request.PortablePdbOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
        var writer = new WriterParameters
        {
            WriteSymbols = symbolOutput is not null,
            SymbolWriterProvider = symbolOutput is null ? null : new PortablePdbWriterProvider(),
            SymbolStream = symbolOutput
        };
        assembly.Write(request.OutputPath, writer);
        return new RuntimeInstrumentationResult(true, count, null);
    }

    private static int RewriteMethod(MethodDefinition method, FlowMethods flow)
    {
        if (!method.HasBody || method.Body.Instructions.Count == 0)
            return 0;

        WidenShortBranches(method);
        var original = method.Body.Instructions.ToArray();
        var processor = method.Body.GetILProcessor();
        var previousLocation = (Document: (string?)null, StartLine: -1, StartColumn: -1, EndLine: -1, EndColumn: -1);
        var points = 0;
        for (var index = 0; index < original.Length; index++)
        {
            var instruction = original[index];
            var injected = new List<Instruction>();
            if (index == 0)
            {
                injected.Add(processor.Create(OpCodes.Ldstr, MethodDisplayName(method)));
                injected.Add(processor.Create(OpCodes.Call, flow.ReportMethod));
                points++;
            }

            var sequencePoint = method.DebugInformation.GetSequencePoint(instruction);
            if (sequencePoint is { IsHidden: false })
            {
                var document = NormalizeDocumentPath(sequencePoint.Document?.Url);
                var location = (
                    document,
                    sequencePoint.StartLine,
                    sequencePoint.StartColumn,
                    sequencePoint.EndLine,
                    sequencePoint.EndColumn);
                if (location != previousLocation)
                {
                    AddSourceRangeCall(processor, injected, flow.ReportSequencePoint, location);
                    previousLocation = location;
                    points++;
                }
            }

            if (instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)
            {
                var branchLocation = previousLocation.StartLine < 0
                    ? ((string?)null, -1, 0, -1, 0)
                    : previousLocation;
                AddSourceRangeCall(processor, injected, flow.ReportBranch, branchLocation);
                points++;
            }

            if (injected.Count == 0)
                continue;
            foreach (var item in injected)
                processor.InsertBefore(instruction, item);
            RetargetControlFlow(method, instruction, injected[0]);
        }

        method.Body.MaxStackSize = checked(
            method.Body.MaxStackSize + MaximumInstrumentationStackDepth);
        return points;
    }

    private static void WidenShortBranches(MethodDefinition method)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            instruction.OpCode = instruction.OpCode.Code switch
            {
                Code.Br_S => OpCodes.Br,
                Code.Brfalse_S => OpCodes.Brfalse,
                Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq,
                Code.Bge_S => OpCodes.Bge,
                Code.Bgt_S => OpCodes.Bgt,
                Code.Ble_S => OpCodes.Ble,
                Code.Blt_S => OpCodes.Blt,
                Code.Bne_Un_S => OpCodes.Bne_Un,
                Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_Un_S => OpCodes.Bgt_Un,
                Code.Ble_Un_S => OpCodes.Ble_Un,
                Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Leave_S => OpCodes.Leave,
                _ => instruction.OpCode
            };
        }
    }

    private static void AddSourceRangeCall(
        ILProcessor processor,
        List<Instruction> target,
        MethodReference method,
        (string? Document, int StartLine, int StartColumn, int EndLine, int EndColumn) location)
    {
        target.Add(location.Document is null
            ? processor.Create(OpCodes.Ldnull)
            : processor.Create(OpCodes.Ldstr, location.Document));
        target.Add(processor.Create(OpCodes.Ldc_I4, location.StartLine));
        target.Add(processor.Create(OpCodes.Ldc_I4, location.StartColumn));
        target.Add(processor.Create(OpCodes.Ldc_I4, location.EndLine));
        target.Add(processor.Create(OpCodes.Ldc_I4, location.EndColumn));
        target.Add(processor.Create(OpCodes.Call, method));
    }

    private static void RetargetControlFlow(MethodDefinition method, Instruction from, Instruction to)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (ReferenceEquals(instruction, to))
                continue;
            if (ReferenceEquals(instruction.Operand, from))
            {
                instruction.Operand = to;
            }
            else if (instruction.Operand is Instruction[] targets)
            {
                for (var index = 0; index < targets.Length; index++)
                {
                    if (ReferenceEquals(targets[index], from))
                        targets[index] = to;
                }
            }
        }

        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (ReferenceEquals(handler.TryStart, from)) handler.TryStart = to;
            if (ReferenceEquals(handler.TryEnd, from)) handler.TryEnd = to;
            if (ReferenceEquals(handler.HandlerStart, from)) handler.HandlerStart = to;
            if (ReferenceEquals(handler.HandlerEnd, from)) handler.HandlerEnd = to;
            if (ReferenceEquals(handler.FilterStart, from)) handler.FilterStart = to;
        }
    }

    private static FlowMethods ImportFlowMethods(ModuleDefinition module)
    {
        var runtime = module.AssemblyReferences.FirstOrDefault(static reference =>
            StringComparer.Ordinal.Equals(reference.Name, RuntimeAssemblyName));
        if (runtime is null)
        {
            runtime = new AssemblyNameReference(RuntimeAssemblyName, new Version(1, 0, 0, 0));
            module.AssemblyReferences.Add(runtime);
        }

        var flowType = new TypeReference(FlowTypeNamespace, FlowTypeName, module, runtime);
        return new FlowMethods(
            Method(module, flowType, "ReportMethod", module.TypeSystem.String),
            Method(
                module,
                flowType,
                "ReportSequencePoint",
                module.TypeSystem.String,
                module.TypeSystem.Int32,
                module.TypeSystem.Int32,
                module.TypeSystem.Int32,
                module.TypeSystem.Int32),
            Method(
                module,
                flowType,
                "ReportBranch",
                module.TypeSystem.String,
                module.TypeSystem.Int32,
                module.TypeSystem.Int32,
                module.TypeSystem.Int32,
                module.TypeSystem.Int32));
    }

    private static MethodReference Method(
        ModuleDefinition module,
        TypeReference declaringType,
        string name,
        params TypeReference[] parameters)
    {
        var method = new MethodReference(name, module.TypeSystem.Void, declaringType)
        {
            HasThis = false,
            ExplicitThis = false,
            CallingConvention = MethodCallingConvention.Default
        };
        foreach (var parameter in parameters)
            method.Parameters.Add(new ParameterDefinition(parameter));
        return module.ImportReference(method);
    }

    private static bool HasNoRewriteAttribute(AssemblyDefinition assembly) =>
        assembly.CustomAttributes.Any(static attribute =>
            StringComparer.Ordinal.Equals(attribute.AttributeType.FullName, NoRewriteAttributeName));

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (var type in roots)
        {
            yield return type;
            foreach (var nested in EnumerateTypes(type.NestedTypes))
                yield return nested;
        }
    }

    private static void CopyUnchanged(ProcessorRequest request)
    {
        File.Copy(request.AssemblyPath, request.OutputPath, overwrite: true);
        if (request.PortablePdbPath is not null && request.PortablePdbOutputPath is not null)
            File.Copy(request.PortablePdbPath, request.PortablePdbOutputPath, overwrite: true);
    }

    private static string MethodDisplayName(MethodDefinition method)
    {
        var value = $"{method.DeclaringType.FullName}::{method.Name}";
        return value.Length <= 512 ? value : value[..512];
    }

    private static string? NormalizeDocumentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var normalized = path.Replace('\\', '/').Replace('\0', ' ');
        if (normalized.Length > 1_024)
            normalized = normalized[^1_024..];
        return normalized;
    }

    private sealed record FlowMethods(
        MethodReference ReportMethod,
        MethodReference ReportSequencePoint,
        MethodReference ReportBranch);
}

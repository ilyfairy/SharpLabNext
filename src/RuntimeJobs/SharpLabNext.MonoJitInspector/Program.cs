using System.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Enumeration;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using SharpLabNext.RuntimeProtocol;

namespace SharpLabNext.MonoJitInspector;

internal static class Program
{
    private const int MaximumMethods = 128;
    private const int MaximumChildOutputBytes = 8 * 1024 * 1024;
    private const int JitFrameChunkSize = 64 * 1024;
    private const int MaximumExceptionDepth = 32;
    private const string MonoExecutable = "/usr/bin/mono";

    public static int Main(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new RuntimeFrameWriter(Console.OpenStandardOutput(), RuntimeFrameTransport.Base64Line);
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        var started = Stopwatch.StartNew();

        try
        {
            if (args is ["self-test"])
            {
                RunSelfTest();
                await WriteExitAsync(writer, "completed", 0, started.Elapsed.TotalMilliseconds);
                return 0;
            }

            var options = MonoJitInspectorArguments.Parse(args);
            var inspection = MonoAssemblyInspection.Read(options.AssemblyPath, options.MethodFilter);
            var runtimeVersion = await ReadMonoVersionAsync();
            var methodResults = new List<MonoJitMethodResult>(inspection.Methods.Count);
            var assemblyText = new StringBuilder();

            foreach (var method in inspection.Methods)
            {
                MonoJitMethodResult result;
                try
                {
                    var raw = await CompileMethodAsync(options.AssemblyPath, method.Selector);
                    var section = MonoJitOutputParser.Parse(raw, method, runtimeVersion);
                    if (assemblyText.Length > 0)
                        assemblyText.AppendLine().AppendLine();
                    assemblyText.Append(section.Text);
                    result = new MonoJitMethodResult(method.Identity, method.DisplayName, "prepared", section.Address, null, section.NativeCodeSize, section.InstructionCount, [], "none");
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    result = new MonoJitMethodResult(method.Identity, method.DisplayName, "failed", null, BoundedError(exception), 0, 0, [], "none");
                }
                methodResults.Add(result);
            }

            await WriteChunksAsync(writer, RuntimeFrameKind.JitAssembly, Encoding.UTF8.GetBytes(assemblyText.ToString()));
            await writer.WriteAsync(RuntimeFrameKind.JitSummary, RuntimeStructuredPayloadCodec.Serialize(new { RuntimeVersion = runtimeVersion, Assembly = inspection.AssemblyName, MethodFilter = options.MethodFilter, Methods = methodResults }));

            var preparedAny = methodResults.Any(static result => result.Status == "prepared");
            var exitCode = preparedAny && assemblyText.Length > 0 ? 0 : methodResults.Count == 0 ? 2 : 1;
            await WriteExitAsync(
                writer,
                exitCode switch
                {
                    0 => "completed",
                    2 => "no-matching-methods",
                    _ => "inspection-failed"
                },
                exitCode,
                started.Elapsed.TotalMilliseconds);
            return exitCode;
        }
        catch (OutOfMemoryException)
        {
            await WriteExitAsync(writer, "out-of-memory", 137, started.Elapsed.TotalMilliseconds);
            return 137;
        }
        catch (Exception exception)
        {
            await writer.WriteAsync(RuntimeFrameKind.Exception, RuntimeStructuredPayloadCodec.Serialize(new { TypeName = exception.GetType().FullName ?? exception.GetType().Name, Message = exception.Message, StackTrace = exception.StackTrace, InnerException = CreateInnerExceptionPayload(exception.InnerException), ElapsedMilliseconds = started.Elapsed.TotalMilliseconds }));
            await WriteExitAsync(writer, "inspection-failed", 1, started.Elapsed.TotalMilliseconds);
            return 1;
        }
    }

    private static async Task<string> ReadMonoVersionAsync()
    {
        var capture = await RunProcessAsync(MonoExecutable, ["--version"], workingDirectory: "/tmp", environment: null);
        if (capture.ExitCode != 0)
            throw new InvalidOperationException("The exact Mono runtime did not report its version.");
        return MonoJitOutputParser.ParseRuntimeVersion(capture.StandardOutput + capture.StandardError);
    }

    private static async Task<string> CompileMethodAsync(string assemblyPath, string selector)
    {
        var capture = await RunProcessAsync(
            MonoExecutable,
            ["--compile", selector, assemblyPath],
            Path.GetDirectoryName(assemblyPath)!,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["MONO_VERBOSE_METHOD"] = selector,
                ["MONO_LOG_LEVEL"] = "error"
            });
        if (capture.ExitCode != 0)
            throw new InvalidOperationException($"Mono --compile exited with code {capture.ExitCode}.");
        return capture.StandardOutput + capture.StandardError;
    }

    private static async Task<ProcessCapture> RunProcessAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{executable}'.");
        var outputBudget = new ProcessOutputBudget(process, MaximumChildOutputBytes);
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, outputBudget);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, outputBudget);
        try
        {
            await process.WaitForExitAsync();
            var standardOutput = await stdout;
            var standardError = await stderr;
            if (outputBudget.Overflowed)
                throw new InvalidDataException("Mono JIT diagnostic output exceeds the helper limit.");
            return new ProcessCapture(process.ExitCode, standardOutput, standardError);
        }
        catch
        {
            TryKill(process);
            await process.WaitForExitAsync();
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, ProcessOutputBudget budget)
    {
        using var output = new MemoryStream(64 * 1024);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                break;
            if (budget.TryReserve(read))
                output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    private static async Task WriteChunksAsync(RuntimeFrameWriter writer, RuntimeFrameKind kind, byte[] content)
    {
        for (var offset = 0; offset < content.Length; offset += JitFrameChunkSize)
        {
            var length = Math.Min(JitFrameChunkSize, content.Length - offset);
            await writer.WriteAsync(kind, content.AsMemory(offset, length));
        }
    }

    private static ValueTask WriteExitAsync(RuntimeFrameWriter writer, string status, int exitCode, double elapsedMilliseconds) =>
        writer.WriteAsync(RuntimeFrameKind.Exit, RuntimeStructuredPayloadCodec.Serialize(new { Status = status, ExitCode = exitCode, ElapsedMilliseconds = elapsedMilliseconds }));

    private static object? CreateInnerExceptionPayload(Exception? exception, int depth = 1)
    {
        if (exception is null || depth > MaximumExceptionDepth)
            return null;
        return new { TypeName = exception.GetType().FullName ?? exception.GetType().Name, Message = exception.Message, StackTrace = exception.StackTrace, InnerException = CreateInnerExceptionPayload(exception.InnerException, depth + 1) };
    }

    private static string BoundedError(Exception exception)
    {
        var value = $"{exception.GetType().Name}: {exception.Message}";
        return value.Length <= 512 ? value : value[..512];
    }

    private static void RunSelfTest()
    {
        const string sample = """
            Mono JIT compiler version 6.12.0.182 (tarball Tue Jun 14 22:52:21 UTC 2022)
            Method int Example.Program:Calculate (int) emitted at 0x40c82fd0 to 0x40c82fd4 (code length 4) [Example.exe]

            *** ASM for Example.Program:Calculate (int) ***

            /tmp/mono-jit:     file format elf64-x86-64

            Disassembly of section .text:

            0000000000000000 <Example_Program_Calculate__int_>:
            <BB>:3
               0:  8d 47 01              lea    0x1(%rdi),%eax
               3:  c3                    retq
            ***
            """;
        var method = new MonoMethodCandidate("0x06000001", "Example.Program.Calculate", "Example.Program:Calculate(int)");
        var parsed = MonoJitOutputParser.Parse(sample, method, "6.12.0.182");
        if (parsed.NativeCodeSize != 4 || parsed.InstructionCount != 2 || parsed.Address != "0x40c82fd0" || !parsed.Text.Contains("lea", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Mono JIT parser self-test failed.");
        }
        if (MonoJitOutputParser.ParseRuntimeVersion(sample) != "6.12.0.182")
            throw new InvalidOperationException("Mono runtime version parser self-test failed.");
    }
}

internal sealed record MonoJitInspectorArguments(string AssemblyPath, string? MethodFilter)
{
    public static MonoJitInspectorArguments Parse(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            throw new ArgumentException("Usage: SharpLabNext.MonoJitInspector <absolute-assembly-path> [method-filter]");
        }
        var assemblyPath = Path.GetFullPath(args[0]);
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("User entry assembly was not found.", assemblyPath);
        var filter = args.Length == 2 ? args[1] : null;
        if (filter is { Length: > 256 } || filter?.Any(char.IsControl) == true)
            throw new ArgumentException("Method filter is invalid.", nameof(args));
        return new MonoJitInspectorArguments(assemblyPath, string.IsNullOrWhiteSpace(filter) ? null : filter);
    }
}

internal static class MonoAssemblyInspection
{
    public static MonoAssemblyMethods Read(string assemblyPath, string? methodFilter)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata)
            throw new BadImageFormatException("The user assembly has no managed metadata.");
        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly)
            throw new BadImageFormatException("The managed image is not an assembly.");

        var assemblyName = SafeMetadataName(reader.GetString(reader.GetAssemblyDefinition().Name));
        var raw = new List<RawMethodCandidate>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typeName = GetTypeName(reader, typeHandle);
            if (typeName == "<Module>")
                continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0 || method.Attributes.HasFlag(MethodAttributes.Abstract) || method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
                {
                    continue;
                }
                var methodName = SafeMetadataName(reader.GetString(method.Name));
                var displayName = $"{typeName}.{methodName}";
                if (!MatchesFilter(displayName, methodFilter))
                    continue;
                raw.Add(new RawMethodCandidate(typeName, methodName, displayName, methodHandle, method));
            }
        }

        if (raw.Count > MaximumMethods)
        {
            throw new InvalidDataException($"Mono JIT inspection matched more than {MaximumMethods} methods; select a narrower method filter.");
        }

        var overloadCounts = raw.GroupBy(static method => (method.TypeName, method.MethodName)).ToDictionary(static group => group.Key, static group => group.Count());
        var provider = new MonoSignatureTypeProvider(reader);
        var methods = new List<MonoMethodCandidate>(raw.Count);
        foreach (var method in raw)
        {
            var selector = $"{method.TypeName}:{method.MethodName}";
            if (overloadCounts[(method.TypeName, method.MethodName)] > 1)
            {
                var signature = method.Definition.DecodeSignature(provider, genericContext: null);
                selector += $"({string.Join(',', signature.ParameterTypes)})";
            }
            if (selector.Length > 1024 || selector.Any(char.IsControl))
                throw new InvalidDataException("A Mono method selector is invalid or too long.");
            methods.Add(new MonoMethodCandidate($"0x{MetadataTokens.GetToken(method.Handle):x8}", method.DisplayName, selector));
        }
        return new MonoAssemblyMethods(assemblyName, methods);
    }

    private const int MaximumMethods = 128;

    private static string GetTypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = SafeMetadataName(reader.GetString(type.Name));
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return $"{GetTypeName(reader, declaring)}/{name}";
        var @namespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace)
            ? name : $"{SafeMetadataName(@namespace)}.{name}";
    }

    private static string SafeMetadataName(string value)
    {
        if (value.Length is 0 or > 512 || value.Any(char.IsControl))
            throw new InvalidDataException("Managed metadata contains an invalid name.");
        return value;
    }

    private static bool MatchesFilter(string displayName, string? methodFilter)
    {
        if (string.IsNullOrWhiteSpace(methodFilter))
            return true;
        return methodFilter.IndexOfAny(['*', '?']) >= 0
            ? FileSystemName.MatchesSimpleExpression(methodFilter, displayName, ignoreCase: true) : displayName.Contains(methodFilter, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RawMethodCandidate(string TypeName, string MethodName, string DisplayName, MethodDefinitionHandle Handle, MethodDefinition Definition);
}

internal sealed class MonoSignatureTypeProvider(MetadataReader reader) : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape) =>
        $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";

    public string GetByReferenceType(string elementType) => $"{elementType}&";

    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        $"{genericType}<{string.Join(',', typeArguments)}>";

    public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

    public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

    public string GetModifiedType(string modifierType, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetPinnedType(string elementType) => elementType;

    public string GetPointerType(string elementType) => $"{elementType}*";

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.IntPtr => "intptr",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.TypedReference => "typedbyref",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.UIntPtr => "uintptr",
        PrimitiveTypeCode.Void => "void",
        _ => throw new BadImageFormatException($"Unsupported signature primitive '{typeCode}'.")
    };

    public string GetSZArrayType(string elementType) => $"{elementType}[]";

    public string GetTypeFromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        TypeName(metadataReader.GetTypeDefinition(handle).Namespace, metadataReader.GetTypeDefinition(handle).Name);

    public string GetTypeFromReference(MetadataReader metadataReader, TypeReferenceHandle handle, byte rawTypeKind) =>
        TypeName(metadataReader.GetTypeReference(handle).Namespace, metadataReader.GetTypeReference(handle).Name);

    public string GetTypeFromSpecification(MetadataReader metadataReader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        metadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    private string TypeName(StringHandle namespaceHandle, StringHandle nameHandle)
    {
        var name = reader.GetString(nameHandle);
        var @namespace = reader.GetString(namespaceHandle);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }
}

internal static partial class MonoJitOutputParser
{
    public static string ParseRuntimeVersion(string text)
    {
        var match = RuntimeVersionRegex().Match(NormalizeLineEndings(text));
        return match.Success
            ? match.Groups[1].Value : throw new InvalidDataException("Mono runtime version output is not recognized.");
    }

    public static MonoJitSection Parse(string rawOutput, MonoMethodCandidate method, string runtimeVersion)
    {
        var text = NormalizeLineEndings(rawOutput);
        var header = AssemblyHeaderRegex().Match(text);
        if (!header.Success || !HeaderMatchesSelector(header.Groups[1].Value, method.Selector))
            throw new InvalidDataException("Mono JIT output does not match the requested method.");
        var end = text.IndexOf("\n***", header.Index + header.Length, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidDataException("Mono JIT assembly section is incomplete.");
        var section = text[header.Index..end];
        var disassembly = section.IndexOf("Disassembly of section .text:", StringComparison.Ordinal);
        if (disassembly < 0)
            throw new InvalidDataException("Mono JIT assembly section has no text disassembly.");

        var output = new StringBuilder();
        output.Append("; Assembly listing for method ").AppendLine(method.DisplayName);
        output.Append("; Mono JIT version ").AppendLine(runtimeVersion);
        var instructionCount = 0;
        var computedSize = 0;
        var block = 0;
        foreach (var line in section[disassembly..].Split('\n'))
        {
            if (line.TrimStart().StartsWith("<BB>:", StringComparison.Ordinal))
            {
                output.Append("G_M000_IG").Append(block++.ToString("00", CultureInfo.InvariantCulture)).AppendLine(":");
                continue;
            }
            if (!TryParseObjdumpLine(line, out var offset, out var byteCount, out var instruction))
                continue;
            computedSize = Math.Max(computedSize, checked(offset + byteCount));
            if (instruction is null)
                continue;
            instructionCount++;
            output.Append("       ").AppendLine(instruction);
        }
        if (instructionCount == 0)
            throw new InvalidDataException("Mono JIT assembly section contains no instructions.");

        var emitted = EmittedMethodRegex().Match(text);
        var nativeCodeSize = emitted.Success &&
            int.TryParse(emitted.Groups[2].Value, out var declaredSize)
            ? declaredSize : computedSize;
        if (nativeCodeSize <= 0 || nativeCodeSize != computedSize)
            throw new InvalidDataException("Mono JIT native code size is inconsistent with its disassembly.");
        output.Append("; Total bytes of code ").Append(nativeCodeSize);
        return new MonoJitSection(output.ToString(), emitted.Success ? emitted.Groups[1].Value : null, nativeCodeSize, instructionCount);
    }

    private static bool HeaderMatchesSelector(string header, string selector)
    {
        static string Canonical(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal);
        var actual = Canonical(header);
        var expected = Canonical(selector);
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            !expected.Contains('(') && actual.StartsWith(expected + "(", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseObjdumpLine(string line, out int offset, out int byteCount, out string? instruction)
    {
        offset = 0;
        byteCount = 0;
        instruction = null;
        var colon = line.IndexOf(':');
        if (colon < 0 || !int.TryParse(line.AsSpan(0, colon).Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out offset))
        {
            return false;
        }

        var index = colon + 1;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        while (index < line.Length)
        {
            var tokenStart = index;
            while (index < line.Length && !char.IsWhiteSpace(line[index]))
                index++;
            var token = line.AsSpan(tokenStart, index - tokenStart);
            if (token.Length == 2 && IsHex(token[0]) && IsHex(token[1]))
            {
                byteCount++;
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                    index++;
                continue;
            }

            instruction = line[tokenStart..].Trim();
            break;
        }
        return byteCount > 0;
    }

    private static bool IsHex(char value) => value is
        >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    [GeneratedRegex(
        @"Mono JIT compiler version ([0-9]+(?:\.[0-9]+){2,3})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeVersionRegex();

    [GeneratedRegex(@"^\*\*\* ASM for (.+?) \*\*\*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AssemblyHeaderRegex();

    [GeneratedRegex(@"\bemitted at (0x[0-9a-fA-F]+) to 0x[0-9a-fA-F]+ \(code length ([0-9]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex EmittedMethodRegex();
}

internal sealed record MonoAssemblyMethods(string AssemblyName, IReadOnlyList<MonoMethodCandidate> Methods);

internal sealed record MonoMethodCandidate(string Identity, string DisplayName, string Selector);

internal sealed record MonoJitSection(string Text, string? Address, int NativeCodeSize, int InstructionCount);

internal sealed record MonoJitMethodResult(string Method, string DisplayName, string Status, string? Address, string? Error, int NativeCodeSize, int InstructionCount, IReadOnlyList<object> LinkedRanges, string MappingSource);

internal sealed record ProcessCapture(int ExitCode, string StandardOutput, string StandardError);

internal sealed class ProcessOutputBudget(Process process, long maximumBytes)
{
    private long _observedBytes;
    private int _overflowed;

    public bool Overflowed => Volatile.Read(ref _overflowed) != 0;

    public bool TryReserve(int count)
    {
        var observed = Interlocked.Add(ref _observedBytes, count);
        if (observed <= maximumBytes && !Overflowed)
            return true;

        if (Interlocked.Exchange(ref _overflowed, 1) == 0)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        }
        return false;
    }
}

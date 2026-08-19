using System.IO.Enumeration;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SharpLab.Runtime;
using SharpLabNext.RuntimeProtocol;

// Keep the inspector entry point in a distinct named type. The inspected user
// assembly may also contain a top-level `Program.<Main>$`; a top-level entry
// here would produce the same name (and, because it awaits, a misleading
// `Task<int>` section) in the CoreCLR JIT listing.
internal static class JitInspectorBootstrap
{
    public static int Main(string[] args) =>
        JitInspectorProgram.RunAsync(args).GetAwaiter().GetResult();
}

internal static partial class JitInspectorProgram
{
    private const int MaximumMethods = 1_000;
    private const int JitFrameChunkSize = 64 * 1024;
    private const int MaximumExceptionDepth = 32;
    public static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new RuntimeFrameWriter(
            Console.OpenStandardOutput(),
            RuntimeFrameTransport.Base64Line);
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        var started = DateTimeOffset.UtcNow;
        try
        {
            var options = JitInspectorArguments.Parse(args);
            var loadContext = new RuntimeArtifactLoadContext(
                options.AssemblyPath,
                typeof(JitGenericAttribute).Assembly);
            var assembly = loadContext.LoadFromAssemblyPath(options.AssemblyPath);
            var results = InspectAssembly(assembly, options.MethodFilter);
            var sourcePoints = JitSourceMapping.LoadSiblingPortablePdb(options.AssemblyPath);
            FlushJitOutput();
            var nativeMaps = JitNativeMapLog.Load(
                Environment.GetEnvironmentVariable("SHARPLABNEXT_JIT_MAP_PATH"));
            var richMaps = JitRichMapLog.Load(
                Environment.GetEnvironmentVariable("SHARPLABNEXT_JIT_RICH_MAP_PATH"));
            var assemblyText = ApplyAssemblyStatistics(
                results,
                ReadJitAssembly(),
                sourcePoints,
                nativeMaps,
                richMaps);
            await WriteChunksAsync(writer, RuntimeFrameKind.JitAssembly, Encoding.UTF8.GetBytes(assemblyText));
            await writer.WriteAsync(RuntimeFrameKind.JitSummary, RuntimeStructuredPayloadCodec.Serialize(new
            {
                runtimeVersion = Environment.Version.ToString(),
                assembly = assembly.GetName().Name,
                methodFilter = options.MethodFilter,
                methods = results
            }));
            var preparedAny = results.Any(static result => result.Status == "prepared");
            var exitCode = preparedAny && assemblyText.Length > 0 ? 0 : preparedAny ? 1 : 2;
            await writer.WriteAsync(RuntimeFrameKind.Exit, RuntimeStructuredPayloadCodec.Serialize(new
            {
                status = exitCode switch
                {
                    0 => "completed",
                    2 => "no-matching-methods",
                    _ => "inspection-failed"
                },
                exitCode,
                elapsedMilliseconds = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            }));
            return exitCode;
        }
        catch (OutOfMemoryException)
        {
            await writer.WriteAsync(RuntimeFrameKind.Exit, RuntimeStructuredPayloadCodec.Serialize(new
            {
                status = "out-of-memory",
                exitCode = 137,
                elapsedMilliseconds = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            }));
            return 137;
        }
        catch (Exception exception)
        {
            await writer.WriteAsync(RuntimeFrameKind.Exception, RuntimeStructuredPayloadCodec.Serialize(new
            {
                typeName = exception.GetType().FullName ?? exception.GetType().Name,
                message = exception.Message,
                stackTrace = exception.StackTrace,
                innerException = CreateInnerExceptionPayload(exception.InnerException),
                elapsedMilliseconds = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            }));
            await writer.WriteAsync(RuntimeFrameKind.Exit, RuntimeStructuredPayloadCodec.Serialize(new
            {
                status = "inspection-failed",
                exitCode = 1,
                elapsedMilliseconds = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            }));
            return 1;
        }
    }

    private static object? CreateInnerExceptionPayload(Exception? exception, int depth = 1)
    {
        if (exception is null || depth > MaximumExceptionDepth)
            return null;

        return new
        {
            typeName = exception.GetType().FullName ?? exception.GetType().Name,
            message = exception.Message,
            stackTrace = exception.StackTrace,
            innerException = CreateInnerExceptionPayload(exception.InnerException, depth + 1)
        };
    }

    private static List<JitMethodResult> InspectAssembly(Assembly assembly, string? methodFilter)
    {
        var results = new List<JitMethodResult>();
        foreach (var type in ExpandTypes(assembly))
        {
            foreach (var declaredMethod in DeclaredMethods(type))
            {
                foreach (var method in ExpandMethods(declaredMethod))
                {
                    if (results.Count >= MaximumMethods)
                    {
                        return results;
                    }

                    if (method.IsAbstract ||
                        method.ContainsGenericParameters ||
                        method.GetMethodImplementationFlags().HasFlag(MethodImplAttributes.InternalCall))
                    {
                        continue;
                    }

                    var displayName = $"{type.FullName}.{method.Name}";
                    if (!MatchesFilter(displayName, methodFilter))
                    {
                        continue;
                    }

                    try
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        var address = method.MethodHandle.GetFunctionPointer();
                        results.Add(new JitMethodResult(
                            MethodIdentity(method),
                            method.MetadataToken,
                            (nuint)method.MethodHandle.Value,
                            (nuint)address,
                            displayName,
                            "prepared",
                            $"0x{address:x}",
                            null,
                            0,
                            0,
                            []));
                    }
                    catch (Exception exception)
                    {
                        results.Add(new JitMethodResult(
                            MethodIdentity(method),
                            method.MetadataToken,
                            0,
                            0,
                            displayName,
                            "failed",
                            null,
                            $"{exception.GetType().Name}: {exception.Message}",
                            0,
                            0,
                            []));
                    }
                }
            }
        }

        return results;
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static
            | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(flags))
            yield return method;
        foreach (var constructor in type.GetConstructors(flags))
            yield return constructor;
        if (type.TypeInitializer is { } typeInitializer)
            yield return typeInitializer;
    }

    private static IEnumerable<MethodBase> ExpandMethods(MethodBase method)
    {
        if (method is not MethodInfo { IsGenericMethodDefinition: true } genericMethod)
        {
            if (!method.ContainsGenericParameters)
                yield return method;
            yield break;
        }

        foreach (var attribute in genericMethod.GetCustomAttributes<JitGenericAttribute>(false))
        {
            if (attribute.ArgumentTypes.Length != genericMethod.GetGenericArguments().Length ||
                attribute.ArgumentTypes.Any(static argument => argument.ContainsGenericParameters))
            {
                continue;
            }

            MethodInfo? constructed = null;
            try
            {
                constructed = genericMethod.MakeGenericMethod(attribute.ArgumentTypes);
            }
            catch (ArgumentException)
            {
            }
            if (constructed is not null)
                yield return constructed;
        }
    }

    private static string MethodIdentity(MethodBase method)
    {
        var metadataToken = $"0x{method.MetadataToken:x8}";
        if (!method.IsGenericMethod || method.IsGenericMethodDefinition)
            return metadataToken;
        var arguments = string.Join(",", method.GetGenericArguments().Select(static argument =>
            argument.FullName ?? argument.Name));
        return $"{metadataToken}[{arguments}]";
    }

    private static bool MatchesFilter(string displayName, string? methodFilter)
    {
        if (string.IsNullOrWhiteSpace(methodFilter))
            return true;
        return methodFilter.IndexOfAny(['*', '?']) >= 0
            ? FileSystemName.MatchesSimpleExpression(methodFilter, displayName, ignoreCase: true)
            : displayName.Contains(methodFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Type> ExpandTypes(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (!type.ContainsGenericParameters)
            {
                yield return type;
                continue;
            }

            foreach (var attribute in type.GetCustomAttributes<JitGenericAttribute>(false))
            {
                if (attribute.ArgumentTypes.Length != type.GetGenericArguments().Length)
                {
                    continue;
                }

                Type? constructed = null;
                try
                {
                    constructed = type.MakeGenericType(attribute.ArgumentTypes);
                }
                catch (ArgumentException)
                {
                }

                if (constructed is not null)
                {
                    yield return constructed;
                }
            }
        }
    }

    private static string ReadJitAssembly()
    {
        var path = Environment.GetEnvironmentVariable("SHARPLABNEXT_JIT_OUTPUT_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void FlushJitOutput()
    {
        if (OperatingSystem.IsLinux() && FlushNativeStreams(IntPtr.Zero) != 0)
        {
            throw new IOException("CoreCLR JIT output could not be flushed.");
        }
    }

    [DllImport("libc", EntryPoint = "fflush")]
    private static extern int FlushNativeStreams(IntPtr stream);

    internal static string ApplyAssemblyStatistics(
        List<JitMethodResult> results,
        string assemblyText,
        IReadOnlyDictionary<int, IReadOnlyList<JitSourcePoint>> sourcePoints,
        IReadOnlyDictionary<nuint, IReadOnlyList<JitNativeMethodMap>> nativeMaps,
        IReadOnlyDictionary<nuint, IReadOnlyList<JitRichMethodMap>>? richMaps = null)
    {
        if (assemblyText.Length == 0)
        {
            return string.Empty;
        }

        var sections = AssemblyHeaderRegex().Matches(assemblyText);
        var unmatchedResultIndices = Enumerable.Range(0, results.Count).ToList();
        var filteredAssembly = new StringBuilder();
        for (var index = 0; index < sections.Count; index++)
        {
            var header = sections[index];
            var end = index + 1 < sections.Count ? sections[index + 1].Index : assemblyText.Length;
            var section = assemblyText.AsSpan(header.Index, end - header.Index);
            var jitName = header.Groups[1].Value.Replace(':', '.');
            var unmatchedIndex = unmatchedResultIndices.FindIndex(
                resultIndex => MethodNamesMatch(jitName, results[resultIndex].DisplayName));
            if (unmatchedIndex < 0)
            {
                continue;
            }

            var resultIndex = unmatchedResultIndices[unmatchedIndex];
            unmatchedResultIndices.RemoveAt(unmatchedIndex);
            var rawSectionText = section.ToString().TrimEnd();
            sourcePoints.TryGetValue(results[resultIndex].MetadataToken, out var points);
            var nativeMap = SelectNativeMap(results[resultIndex], nativeMaps);
            var richNativeMap = SelectRichNativeMap(nativeMap, richMaps);
            var mappedSection = JitSourceMapping.MapNativeSection(
                rawSectionText,
                points ?? [],
                nativeMap);
            var richRanges = richNativeMap is null
                ? []
                : JitSourceMapping.MapNativeSection(
                    rawSectionText,
                    points ?? [],
                    richNativeMap).LinkedRanges;
            var sectionText = mappedSection.Text;
            var markerRanges = points is not null
                ? JitSourceMapping.MapTextMarkers(sectionText, points)
                : [];
            var methodFallbackRanges = points is not null
                ? JitSourceMapping.MapMethodFallback(sectionText, points)
                : [];
            var sizeMatch = TotalBytesRegex().Match(sectionText);
            var nativeSize = sizeMatch.Success && int.TryParse(sizeMatch.Groups[1].Value, out var parsedSize)
                ? parsedSize
                : 0;
            var instructionCount = 0;
            foreach (var line in sectionText.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 &&
                    !trimmed.StartsWith(';') &&
                    !trimmed.EndsWith(':') &&
                    !trimmed.StartsWith("G_M", StringComparison.Ordinal))
                {
                    instructionCount++;
                }
            }

            var linkedRangeSelection = SelectLinkedRanges(
                nativeMap is not null,
                mappedSection.LinkedRanges,
                richRanges,
                markerRanges,
                methodFallbackRanges);
            results[resultIndex] = results[resultIndex] with
            {
                NativeCodeSize = nativeSize,
                InstructionCount = instructionCount,
                LinkedRanges = linkedRangeSelection.Ranges,
                MappingSource = linkedRangeSelection.Source
            };

            if (filteredAssembly.Length > 0)
            {
                filteredAssembly.AppendLine().AppendLine();
            }
            filteredAssembly.Append(sectionText);
        }

        return filteredAssembly.ToString();
    }

    private static JitLinkedRangeSelection SelectLinkedRanges(
        bool hasProfilerMap,
        IReadOnlyList<JitSourceLinkedRange> nativeRanges,
        IReadOnlyList<JitSourceLinkedRange> richRanges,
        IReadOnlyList<JitSourceLinkedRange> markerRanges,
        IReadOnlyList<JitSourceLinkedRange> methodFallbackRanges)
    {
        if (CountDistinctSequencePoints(richRanges) > CountDistinctSequencePoints(nativeRanges))
            return new JitLinkedRangeSelection(richRanges, "rich");

        if (nativeRanges.Count > 0)
            return new JitLinkedRangeSelection(nativeRanges, "ordinary");

        // A matching profiler record is authoritative even when it cannot be
        // aligned to instructions. Text emitted by JitDisasm must never make
        // that record appear richer or replace its provenance.
        if (hasProfilerMap)
            return CreateFallbackSelection(methodFallbackRanges);

        if (markerRanges.Count > 0)
            return new JitLinkedRangeSelection(markerRanges, "marker");

        return CreateFallbackSelection(methodFallbackRanges);
    }

    private static JitLinkedRangeSelection CreateFallbackSelection(
        IReadOnlyList<JitSourceLinkedRange> methodFallbackRanges) =>
        methodFallbackRanges.Count > 0
            ? new JitLinkedRangeSelection(methodFallbackRanges, "method")
            : new JitLinkedRangeSelection([], "none");

    private static int CountDistinctSequencePoints(IReadOnlyList<JitSourceLinkedRange> ranges) =>
        ranges
            .Where(static range => range.Precision == "sequence-point")
            .Select(static range => (
                range.SourceFilePath,
                range.SourceRange.StartLine,
                range.SourceRange.StartCharacter,
                range.SourceRange.EndLine,
                range.SourceRange.EndCharacter))
            .Distinct()
            .Count();

    internal static JitNativeMethodMap? SelectRichNativeMap(
        JitNativeMethodMap? ordinaryMap,
        IReadOnlyDictionary<nuint, IReadOnlyList<JitRichMethodMap>>? richMaps)
    {
        if (ordinaryMap is null ||
            richMaps is null ||
            !richMaps.TryGetValue(ordinaryMap.MethodHandle, out var versions) ||
            versions.Count != 1)
        {
            return null;
        }

        var codeEnd = ordinaryMap.Ranges.Count == 0
            ? 0
            : ordinaryMap.Ranges.Max(static range => range.NativeEnd);
        if (codeEnd == 0)
            return null;

        var rootOffsets = new Dictionary<uint, int>();
        foreach (var point in versions[0].Points)
        {
            if (point.Inlinee != 0)
                continue;
            if (point.NativeOffset > codeEnd)
                return null;
            rootOffsets[point.NativeOffset] = point.IlOffset;
        }
        if (rootOffsets.Count == 0)
            return null;

        var ordered = rootOffsets
            .OrderBy(static pair => pair.Key)
            .ToArray();
        var ranges = new List<JitNativeIlRange>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var start = ordered[index].Key;
            var end = index + 1 < ordered.Length
                ? ordered[index + 1].Key
                : codeEnd;
            if (end > start)
                ranges.Add(new JitNativeIlRange(ordered[index].Value, start, end));
        }
        if (ranges.Count == 0)
            return null;

        return new JitNativeMethodMap(
            ordinaryMap.MethodHandle,
            ordinaryMap.MetadataToken,
            ordinaryMap.NativeCodeStart,
            ranges);
    }

    internal static JitNativeMethodMap? SelectNativeMap(
        JitMethodResult result,
        IReadOnlyDictionary<nuint, IReadOnlyList<JitNativeMethodMap>> nativeMaps)
    {
        if (result.MethodHandle != 0 &&
            nativeMaps.TryGetValue(result.MethodHandle, out var handleVersions))
        {
            var handleMatch = SelectCandidate(result, handleVersions, out var handleCandidateCount);
            if (handleCandidateCount > 0)
                return handleMatch;
        }

        JitNativeMethodMap? soleTokenCandidate = null;
        var tokenCandidateCount = 0;
        foreach (var versions in nativeMaps.Values)
        {
            foreach (var version in versions)
            {
                if (version.MetadataToken != result.MetadataToken)
                    continue;

                soleTokenCandidate = version;
                tokenCandidateCount++;
            }
        }

        return tokenCandidateCount == 1
            ? soleTokenCandidate
            : null;
    }

    private static JitNativeMethodMap? SelectCandidate(
        JitMethodResult result,
        IReadOnlyList<JitNativeMethodMap> versions,
        out int candidateCount)
    {
        JitNativeMethodMap? soleCandidate = null;
        JitNativeMethodMap? exactNativeCandidate = null;
        candidateCount = 0;
        var exactNativeCandidateCount = 0;
        foreach (var version in versions)
        {
            if (version.MetadataToken != result.MetadataToken)
                continue;

            soleCandidate = version;
            candidateCount++;
            if (result.NativeCodeStart != 0 && version.NativeCodeStart == result.NativeCodeStart)
            {
                exactNativeCandidate = version;
                exactNativeCandidateCount++;
            }
        }

        if (exactNativeCandidateCount == 1)
            return exactNativeCandidate;
        return exactNativeCandidateCount == 0 && candidateCount == 1
            ? soleCandidate
            : null;
    }

    private static bool MethodNamesMatch(string jitName, string resultDisplayName)
    {
        var jitMethodName = RemoveConstructedMethodArguments(RemoveSignature(jitName));
        return jitMethodName.Equals(resultDisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveSignature(string name)
    {
        var signatureStart = name.IndexOf('(');
        return signatureStart < 0 ? name : name[..signatureStart];
    }

    private static string RemoveConstructedMethodArguments(string name)
    {
        if (!name.EndsWith(']'))
            return name;

        var argumentsStart = name.LastIndexOf('[');
        return argumentsStart > 0 ? name[..argumentsStart] : name;
    }

    private static async Task WriteChunksAsync(
        RuntimeFrameWriter writer,
        RuntimeFrameKind kind,
        byte[] content)
    {
        for (var offset = 0; offset < content.Length; offset += JitFrameChunkSize)
        {
            var length = Math.Min(JitFrameChunkSize, content.Length - offset);
            await writer.WriteAsync(kind, content.AsMemory(offset, length));
        }
    }

    [GeneratedRegex(@"^; Assembly listing for method (.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AssemblyHeaderRegex();

    [GeneratedRegex(@"; Total bytes of code\s+(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TotalBytesRegex();
}

internal sealed record JitInspectorArguments(string AssemblyPath, string? MethodFilter)
{
    public static JitInspectorArguments Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("Usage: SharpLabNext.JitInspector <absolute-assembly-path> [method-filter]");
        }

        var assemblyPath = Path.GetFullPath(args[0]);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("User entry assembly was not found.", assemblyPath);
        }

        var filter = args.Length > 1 ? args[1] : null;
        if (filter is { Length: > 256 })
        {
            throw new ArgumentException("Method filter exceeds 256 characters.", nameof(args));
        }

        return new JitInspectorArguments(assemblyPath, string.IsNullOrWhiteSpace(filter) ? null : filter);
    }
}

internal sealed record JitMethodResult(
    string Method,
    [property: JsonIgnore] int MetadataToken,
    [property: JsonIgnore] nuint MethodHandle,
    [property: JsonIgnore] nuint NativeCodeStart,
    string DisplayName,
    string Status,
    string? Address,
    string? Error,
    int NativeCodeSize,
    int InstructionCount,
    IReadOnlyList<JitSourceLinkedRange> LinkedRanges)
{
    public string MappingSource { get; init; } = "none";

    public IReadOnlyList<JitEvidenceRange> EvidenceRanges => LinkedRanges
        .Select(static range => range.EvidenceRange)
        .OfType<JitEvidenceRange>()
        .ToArray();
}

internal sealed record JitLinkedRangeSelection(
    IReadOnlyList<JitSourceLinkedRange> Ranges,
    string Source);

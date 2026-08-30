using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace SharpLabNext.CheckedJitBridge;

internal static class CheckedJitSourceMapping
{
    private const int MaximumSequencePointsPerMethod = 20_000;
    private static readonly char[] DocumentPathSeparators = { '/' };
    private static readonly Regex RootOffsetMarker = new(
        @"\bINLRT\s+@\s+(?:(?:0x)?(?<offset>[0-9A-Fa-f]+)(?:\[[^\]]*\])?|(?<unknown>\?{3}))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnyInlineMarker = new(@"^\s*;\s*INL\d+\s+@\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnknownDebugMarker = new(
        @"^\s*;\s*(?:(?:INL\d+|INLRT)\s+@\s+)?\?{3}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InstructionGroupLabel = new(
        @"^\s*G_M[0-9A-Za-z]+_IG\d+:\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NativeOffsetLabel = new(
        @"^\s*G_M[0-9A-Za-z]+_IG\d+:\s*;;\s*offset=0x(?<offset>[0-9A-Fa-f]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CodeBytesInstruction = new(
        @"^(?<indent>\s*)(?<bytes>(?:[0-9A-Fa-f]{2})+)(?<spacing>\s{2,})(?<instruction>\S.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<int, CheckedMethodSourceMap> LoadForDeclaredKind(string assemblyPath, string? declaredSourceMappingKind)
    {
        if (declaredSourceMappingKind is "" or "none")
            return new Dictionary<int, CheckedMethodSourceMap>();
        if (declaredSourceMappingKind is not null && !string.Equals(declaredSourceMappingKind, CheckedJitBridgeContract.SourceMappingKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported Checked JIT source mapping kind '{declaredSourceMappingKind}'.");
        }

        return LoadSiblingPortablePdb(assemblyPath);
    }

    public static IReadOnlyDictionary<int, CheckedMethodSourceMap> LoadSiblingPortablePdb(string assemblyPath)
    {
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(pdbPath))
            return new Dictionary<int, CheckedMethodSourceMap>();

        try
        {
            using var peStream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var peReader = new PEReader(peStream, PEStreamOptions.PrefetchEntireImage);
            if (!peReader.HasMetadata)
                return new Dictionary<int, CheckedMethodSourceMap>();
            var peMetadata = peReader.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
            using var pdbStream = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream, MetadataStreamOptions.PrefetchMetadata);
            var pdbReader = pdbProvider.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
            if (!PortablePdbMatchesPe(peReader, pdbReader))
                return new Dictionary<int, CheckedMethodSourceMap>();
            var methodCount = Math.Min(peMetadata.GetTableRowCount(TableIndex.MethodDef), pdbReader.GetTableRowCount(TableIndex.MethodDebugInformation));
            var result = new Dictionary<int, CheckedMethodSourceMap>();
            for (var row = 1; row <= methodCount; row++)
            {
                var methodHandle = MetadataTokens.MethodDefinitionHandle(row);
                var method = peMetadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue;
                var ilBytes = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                var ilLength = ilBytes?.Length ?? 0;
                if (ilLength <= 0)
                    continue;

                var debug = pdbReader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(row));
                var points = new List<CheckedSourcePoint>();
                foreach (var point in debug.GetSequencePoints())
                {
                    if (points.Count >= MaximumSequencePointsPerMethod)
                        break;
                    var documentHandle = point.Document.IsNil ? debug.Document : point.Document;
                    if (point.IsHidden || documentHandle.IsNil)
                    {
                        points.Add(new CheckedSourcePoint(point.Offset, null, null));
                        continue;
                    }

                    var document = pdbReader.GetDocument(documentHandle);
                    points.Add(new CheckedSourcePoint(point.Offset, SanitizeDocumentPath(pdbReader.GetString(document.Name)), new JitTextRange(ToZeroBased(point.StartLine), ToZeroBased(point.StartColumn), ToZeroBased(point.EndLine), ToZeroBased(point.EndColumn))));
                }

                if (points.Count > 0)
                {
                    result[MetadataTokens.GetToken(methodHandle)] = new CheckedMethodSourceMap(ilLength, points.OrderBy(point => point.IlOffset).ToArray());
                }
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return new Dictionary<int, CheckedMethodSourceMap>();
        }
    }

    public static CheckedJitMappingSelection MapSection(string sectionText, CheckedMethodSourceMap sourceMap)
    {
        if (sectionText is null)
            throw new ArgumentNullException(nameof(sectionText));
        if (sourceMap is null)
            throw new ArgumentNullException(nameof(sourceMap));

        var fallback = MapMethodFallback(sectionText, sourceMap.Points);
        if (TryMapTextMarkers(sectionText, sourceMap, out var checkedRanges))
        {
            return new CheckedJitMappingSelection(checkedRanges, "checked-jit-debug-info");
        }

        return fallback.Length > 0
            ? new CheckedJitMappingSelection(fallback, "method") : new CheckedJitMappingSelection(Array.Empty<JitLinkedRange>(), "none");
    }

    private static bool TryMapTextMarkers(string sectionText, CheckedMethodSourceMap sourceMap, out IReadOnlyList<JitLinkedRange> linkedRanges)
    {
        var lines = NormalizeLineEndings(sectionText).Split('\n');
        var ranges = new List<JitLinkedRange>();
        CheckedSourcePoint? currentPoint = null;
        var firstInstructionLine = -1;
        var lastInstructionLine = -1;
        var nativeOffset = 0u;
        var nativeStart = 0u;
        var nativeEnd = 0u;
        var hasNativeRange = false;
        var nativeRangeComplete = true;
        var sawRootMarker = false;
        var invalid = false;

        void CompleteRange()
        {
            if (currentPoint?.DocumentPath is not null && currentPoint.SourceRange is not null && firstInstructionLine >= 0 && lastInstructionLine >= firstInstructionLine)
            {
                ranges.Add(new JitLinkedRange(
                    currentPoint.DocumentPath,
                    currentPoint.SourceRange,
                    new JitTextRange(firstInstructionLine, 0, lastInstructionLine, lines[lastInstructionLine].Length),
                    "sequence-point",
                    hasNativeRange && nativeRangeComplete && nativeEnd > nativeStart
                        ? new JitEvidenceRange(currentPoint.IlOffset, checked((int)nativeStart), checked((int)nativeEnd), currentPoint.DocumentPath, checked(currentPoint.SourceRange.StartLine + 1), checked(currentPoint.SourceRange.StartCharacter + 1), checked(currentPoint.SourceRange.EndLine + 1), checked(currentPoint.SourceRange.EndCharacter + 1)) : null));
            }
            firstInstructionLine = -1;
            lastInstructionLine = -1;
            nativeStart = 0;
            nativeEnd = 0;
            hasNativeRange = false;
            nativeRangeComplete = true;
        }

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var nativeLabel = NativeOffsetLabel.Match(line);
            if (nativeLabel.Success && uint.TryParse(nativeLabel.Groups["offset"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var labelOffset))
            {
                CompleteRange();
                currentPoint = null;
                nativeOffset = labelOffset;
                continue;
            }

            var rootMarker = RootOffsetMarker.Match(line);
            if (rootMarker.Success)
            {
                sawRootMarker = true;
                CompleteRange();
                currentPoint = null;
                if (rootMarker.Groups["unknown"].Success || !int.TryParse(rootMarker.Groups["offset"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var ilOffset) || ilOffset < 0 || ilOffset >= sourceMap.IlLength)
                {
                    invalid = true;
                    break;
                }

                currentPoint = FindSequencePoint(sourceMap.Points, ilOffset);
                if (currentPoint?.DocumentPath is null || currentPoint.SourceRange is null)
                {
                    invalid = true;
                    break;
                }
                continue;
            }

            if ((line.Contains("INLRT", StringComparison.Ordinal) && line.Contains('@')) || UnknownDebugMarker.IsMatch(line))
            {
                invalid = true;
                break;
            }

            if (InstructionGroupLabel.IsMatch(line))
            {
                CompleteRange();
                currentPoint = null;
                continue;
            }

            if (AnyInlineMarker.IsMatch(line))
            {
                CompleteRange();
                currentPoint = null;
                continue;
            }

            var isInstruction = IsInstructionLine(line);
            var instruction = CodeBytesInstruction.Match(line);
            var hasCodeBytes = instruction.Success &&
                instruction.Groups["bytes"].Length + instruction.Groups["spacing"].Length >= 16;
            var instructionStart = nativeOffset;
            if (hasCodeBytes)
            {
                var byteCount = checked((uint)(instruction.Groups["bytes"].Length / 2));
                nativeOffset = checked(nativeOffset + byteCount);
            }

            if (currentPoint is not null && isInstruction)
            {
                if (firstInstructionLine < 0)
                    firstInstructionLine = lineIndex;
                lastInstructionLine = lineIndex;

                if (hasCodeBytes)
                {
                    if (!hasNativeRange)
                    {
                        nativeStart = instructionStart;
                        hasNativeRange = true;
                    }
                    nativeEnd = nativeOffset;
                }
                else
                {
                    nativeRangeComplete = false;
                }
            }
        }

        CompleteRange();
        linkedRanges = invalid || !sawRootMarker || ranges.Count == 0
            ? Array.Empty<JitLinkedRange>() : ranges;
        return !invalid && sawRootMarker && ranges.Count > 0;
    }

    private static JitLinkedRange[] MapMethodFallback(string sectionText, IReadOnlyList<CheckedSourcePoint> sourcePoints)
    {
        var firstPoint = sourcePoints.FirstOrDefault(point => point.DocumentPath is not null && point.SourceRange is not null);
        if (firstPoint?.DocumentPath is null || firstPoint.SourceRange is null)
            return Array.Empty<JitLinkedRange>();

        var sourceRanges = sourcePoints.Where(point => string.Equals(point.DocumentPath, firstPoint.DocumentPath, StringComparison.Ordinal) && point.SourceRange is not null).Select(point => point.SourceRange!).ToArray();
        if (sourceRanges.Length == 0)
            return Array.Empty<JitLinkedRange>();

        var lines = NormalizeLineEndings(sectionText).Split('\n');
        var firstInstruction = -1;
        var lastInstruction = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!IsInstructionLine(lines[index]))
                continue;
            if (firstInstruction < 0)
                firstInstruction = index;
            lastInstruction = index;
        }
        if (firstInstruction < 0)
            return Array.Empty<JitLinkedRange>();

        var start = sourceRanges.OrderBy(range => range.StartLine).ThenBy(range => range.StartCharacter).First();
        var end = sourceRanges.OrderByDescending(range => range.EndLine).ThenByDescending(range => range.EndCharacter).First();
        return new[]
        {
            new JitLinkedRange(firstPoint.DocumentPath, new JitTextRange(start.StartLine, start.StartCharacter, end.EndLine, end.EndCharacter), new JitTextRange(firstInstruction, 0, lastInstruction, lines[lastInstruction].Length), "method")
        };
    }

    private static CheckedSourcePoint? FindSequencePoint(IReadOnlyList<CheckedSourcePoint> sourcePoints, int ilOffset)
    {
        var low = 0;
        var high = sourcePoints.Count - 1;
        var match = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (sourcePoints[middle].IlOffset <= ilOffset)
            {
                match = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return match >= 0 ? sourcePoints[match] : null;
    }

    private static bool IsInstructionLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 &&
            !trimmed.StartsWith(';') &&
            !trimmed.EndsWith(':') &&
            !trimmed.StartsWith("G_M", StringComparison.Ordinal);
    }

    private static int ToZeroBased(int coordinate) => Math.Max(0, coordinate - 1);

    private static bool PortablePdbMatchesPe(PEReader peReader, MetadataReader pdbReader)
    {
        var header = pdbReader.DebugMetadataHeader;
        if (header is null || header.Id.IsDefaultOrEmpty)
            return false;

        BlobContentId pdbId;
        try
        {
            pdbId = new BlobContentId(header.Id);
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach (var entry in peReader.ReadDebugDirectory())
        {
            if (entry.Type != DebugDirectoryEntryType.CodeView || entry.Stamp != pdbId.Stamp)
                continue;
            var codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
            if (codeView.Age == 1 && codeView.Guid == pdbId.Guid)
                return true;
        }
        return false;
    }

    private static string SanitizeDocumentPath(string path)
    {
        var segments = path.Replace('\\', '/').Split(DocumentPathSeparators, StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != "." && segment != ".." && !segment.EndsWith(':')).ToArray();
        var start = Math.Max(0, segments.Length - 8);
        var sanitized = segments.Length == 0
            ? "source" : string.Join("/", segments, start, segments.Length - start);
        return sanitized.Length <= 512
            ? sanitized : sanitized.Substring(sanitized.Length - 512);
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');
}

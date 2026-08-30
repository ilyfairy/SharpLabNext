using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

internal static partial class JitSourceMapping
{
    private const int MaximumSequencePointsPerMethod = 20_000;
    private const string SequencePointPrecision = "sequence-point";
    private const string MethodPrecision = "method";

    public static IReadOnlyDictionary<int, IReadOnlyList<JitSourcePoint>> LoadSiblingPortablePdb(string assemblyPath)
    {
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(pdbPath))
            return new Dictionary<int, IReadOnlyList<JitSourcePoint>>();

        try
        {
            using var stream = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(stream, MetadataStreamOptions.PrefetchMetadata);
            var reader = provider.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
            var result = new Dictionary<int, IReadOnlyList<JitSourcePoint>>();
            var methodCount = reader.GetTableRowCount(TableIndex.MethodDebugInformation);
            for (var row = 1; row <= methodCount; row++)
            {
                var information = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(row));
                var points = new List<JitSourcePoint>();
                foreach (var point in information.GetSequencePoints())
                {
                    if (points.Count >= MaximumSequencePointsPerMethod)
                        break;

                    var documentHandle = point.Document.IsNil ? information.Document : point.Document;
                    if (point.IsHidden || documentHandle.IsNil)
                    {
                        points.Add(new JitSourcePoint(point.Offset, null, null));
                        continue;
                    }

                    var document = reader.GetDocument(documentHandle);
                    points.Add(new JitSourcePoint(point.Offset, SanitizeDocumentPath(reader.GetString(document.Name)), new JitSourceTextRange(ToZeroBased(point.StartLine), ToZeroBased(point.StartColumn), ToZeroBased(point.EndLine), ToZeroBased(point.EndColumn))));
                }

                if (points.Count > 0)
                {
                    result[MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(row))] = points;
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return new Dictionary<int, IReadOnlyList<JitSourcePoint>>();
        }
    }

    public static IReadOnlyList<JitSourceLinkedRange> MapSection(string sectionText, IReadOnlyList<JitSourcePoint> sequencePoints)
    {
        var markerRanges = MapTextMarkers(sectionText, sequencePoints, out var sawRootMarker);
        return markerRanges.Count > 0 || sawRootMarker
            ? markerRanges : MapMethodFallback(sectionText, sequencePoints);
    }

    internal static IReadOnlyList<JitSourceLinkedRange> MapTextMarkers(string sectionText, IReadOnlyList<JitSourcePoint> sequencePoints) =>
        MapTextMarkers(sectionText, sequencePoints, out _);

    private static List<JitSourceLinkedRange> MapTextMarkers(string sectionText, IReadOnlyList<JitSourcePoint> sequencePoints, out bool sawRootMarker)
    {
        sawRootMarker = false;
        if (sectionText.Length == 0 || sequencePoints.Count == 0)
            return [];

        var lines = sectionText.Split('\n').Select(static line => line.TrimEnd('\r')).ToArray();
        var ranges = new List<JitSourceLinkedRange>();
        JitSourcePoint? currentPoint = null;
        var firstInstructionLine = -1;
        var lastInstructionLine = -1;

        void CompleteRange()
        {
            if (currentPoint?.SourceRange is not { } sourceRange ||
                currentPoint.DocumentPath is null ||
                firstInstructionLine < 0 ||
                lastInstructionLine < firstInstructionLine)
            {
                firstInstructionLine = -1;
                lastInstructionLine = -1;
                return;
            }

            ranges.Add(new JitSourceLinkedRange(currentPoint.DocumentPath, sourceRange, new JitSourceTextRange(firstInstructionLine, 0, lastInstructionLine, lines[lastInstructionLine].Length), SequencePointPrecision));
            firstInstructionLine = -1;
            lastInstructionLine = -1;
        }

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var marker = RootOffsetMarker().Match(line);
            if (marker.Success)
            {
                sawRootMarker = true;
                CompleteRange();
                currentPoint = marker.Groups["unknown"].Success ||
                    !int.TryParse(marker.Groups["offset"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset)
                    ? null : FindSequencePoint(sequencePoints, offset);
                continue;
            }

            if (UnknownDebugMarker().IsMatch(line) || InstructionGroupLabel().IsMatch(line))
            {
                CompleteRange();
                currentPoint = null;
                continue;
            }

            if (currentPoint is not null && IsInstructionLine(line))
            {
                if (firstInstructionLine < 0)
                    firstInstructionLine = lineIndex;
                lastInstructionLine = lineIndex;
            }
        }

        CompleteRange();
        return ranges;
    }

    internal static IReadOnlyList<JitSourceLinkedRange> MapMethodFallback(string sectionText, IReadOnlyList<JitSourcePoint> sequencePoints)
    {
        if (sectionText.Length == 0 || sequencePoints.Count == 0)
            return [];

        var lines = sectionText.Split('\n').Select(static line => line.TrimEnd('\r')).ToArray();
        var fallback = CreateMethodFallback(lines, sequencePoints);
        return fallback is null ? [] : [fallback];
    }

    internal static IReadOnlyList<JitSourceLinkedRange> MapSection(string assemblyPath, int metadataToken, string sectionText)
    {
        var methods = LoadSiblingPortablePdb(assemblyPath);
        return methods.TryGetValue(metadataToken, out var points)
            ? MapSection(sectionText, points) : [];
    }

    private static JitSourcePoint? FindSequencePoint(IReadOnlyList<JitSourcePoint> sequencePoints, int offset)
    {
        var low = 0;
        var high = sequencePoints.Count - 1;
        var match = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (sequencePoints[middle].Offset <= offset)
            {
                match = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return match >= 0 && sequencePoints[match].SourceRange is not null
            ? sequencePoints[match] : null;
    }

    private static JitSourceLinkedRange? CreateMethodFallback(string[] lines, IReadOnlyList<JitSourcePoint> sequencePoints)
    {
        var firstPoint = sequencePoints.FirstOrDefault(static point => point.DocumentPath is not null && point.SourceRange is not null);
        if (firstPoint?.DocumentPath is not { } documentPath || firstPoint.SourceRange is null)
            return null;

        var points = sequencePoints.Where(point => point.DocumentPath == documentPath && point.SourceRange is not null).Select(static point => point.SourceRange!).ToArray();
        if (points.Length == 0)
            return null;

        var firstInstruction = -1;
        var lastInstruction = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!IsInstructionLine(lines[index]) || InstructionGroupLabel().IsMatch(lines[index]))
                continue;
            if (firstInstruction < 0)
                firstInstruction = index;
            lastInstruction = index;
        }
        if (firstInstruction < 0)
            return null;

        var start = points.MinBy(static range => (range.StartLine, range.StartCharacter))!;
        var end = points.MaxBy(static range => (range.EndLine, range.EndCharacter))!;
        return new JitSourceLinkedRange(documentPath, new JitSourceTextRange(start.StartLine, start.StartCharacter, end.EndLine, end.EndCharacter), new JitSourceTextRange(firstInstruction, 0, lastInstruction, lines[lastInstruction].Length), MethodPrecision);
    }

    private static bool IsInstructionLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 &&
            !trimmed.StartsWith(';') &&
            !trimmed.EndsWith(':');
    }

    private static int ToZeroBased(int coordinate) => Math.Max(0, coordinate - 1);

    private static string SanitizeDocumentPath(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(static segment => segment is not "." and not ".." && !segment.EndsWith(':')).TakeLast(8).ToArray();
        var sanitized = segments.Length == 0 ? "source" : string.Join('/', segments);
        return sanitized.Length <= 512 ? sanitized : sanitized[^512..];
    }

    [GeneratedRegex(
        @"\bINLRT\s+@\s+(?:(?:0x)?(?<offset>[0-9A-Fa-f]+)(?:\[[^\]]*\])?|(?<unknown>\?{3}))",
        RegexOptions.CultureInvariant)]
    private static partial Regex RootOffsetMarker();

    [GeneratedRegex(
        @"^\s*;\s*(?:(?:INL\d+|INLRT)\s+@\s+)?\?{3}",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnknownDebugMarker();

    [GeneratedRegex(@"^\s*G_M[0-9A-Za-z]+_IG\d+:\s*", RegexOptions.CultureInvariant)]
    private static partial Regex InstructionGroupLabel();
}

internal sealed record JitSourcePoint(int Offset, string? DocumentPath, JitSourceTextRange? SourceRange);

internal sealed record JitSourceTextRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter);

internal sealed record JitSourceLinkedRange(string SourceFilePath, JitSourceTextRange SourceRange, JitSourceTextRange OutputRange, string Precision, [property: JsonIgnore] JitEvidenceRange? EvidenceRange = null);

internal sealed record JitEvidenceRange(int IlOffset, int NativeStartOffset, int NativeEndOffset, string Document, int StartLine, int StartColumn, int EndLine, int EndColumn);

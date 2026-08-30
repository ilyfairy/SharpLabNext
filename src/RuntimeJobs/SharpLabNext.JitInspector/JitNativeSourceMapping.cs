using System.Globalization;
using System.Text.RegularExpressions;

internal static partial class JitSourceMapping
{
    internal static JitNativeMappedSection MapNativeSection(string sectionText, IReadOnlyList<JitSourcePoint> sequencePoints, JitNativeMethodMap? methodMap)
    {
        var rawLines = sectionText.Split('\n').Select(static line => line.TrimEnd('\r')).ToArray();
        var cleanedLines = new string[rawLines.Length];
        var instructions = new List<JitNativeInstruction>();
        uint nativeOffset = 0;

        for (var lineIndex = 0; lineIndex < rawLines.Length; lineIndex++)
        {
            var line = rawLines[lineIndex];
            var label = NativeOffsetLabel().Match(line);
            if (label.Success && uint.TryParse(label.Groups["offset"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var labelOffset))
            {
                nativeOffset = labelOffset;
                cleanedLines[lineIndex] = label.Groups["label"].Value;
                continue;
            }

            var instruction = CodeBytesInstruction().Match(line);
            if (!instruction.Success || instruction.Groups["bytes"].Length + instruction.Groups["spacing"].Length < 16)
            {
                cleanedLines[lineIndex] = line;
                continue;
            }

            var codeBytes = instruction.Groups["bytes"].Value;
            var byteCount = checked((uint)(codeBytes.Length / 2));
            var cleaned = $"{instruction.Groups["indent"].Value}{instruction.Groups["instruction"].Value}";
            cleanedLines[lineIndex] = cleaned;
            instructions.Add(new JitNativeInstruction(lineIndex, nativeOffset, checked(nativeOffset + byteCount)));
            nativeOffset = checked(nativeOffset + byteCount);
        }

        var cleanedText = string.Join('\n', cleanedLines);
        if (methodMap is null || sequencePoints.Count == 0 || instructions.Count == 0)
            return new JitNativeMappedSection(cleanedText, []);

        var linkedRanges = CreateNativeLinkedRanges(instructions, cleanedLines, sequencePoints, NormalizeNativeRanges(methodMap.Ranges));
        return new JitNativeMappedSection(cleanedText, linkedRanges);
    }

    private static List<JitSourceLinkedRange> CreateNativeLinkedRanges(IReadOnlyList<JitNativeInstruction> instructions, string[] cleanedLines, IReadOnlyList<JitSourcePoint> sequencePoints, IReadOnlyList<JitNativeIlRange> nativeRanges)
    {
        var result = new List<JitSourceLinkedRange>();
        JitSourcePoint? currentPoint = null;
        var firstLine = -1;
        var lastLine = -1;
        uint nativeStart = 0;
        uint nativeEnd = 0;

        void CompleteRange()
        {
            if (currentPoint?.DocumentPath is { } documentPath &&
                currentPoint.SourceRange is { } sourceRange &&
                firstLine >= 0 &&
                lastLine >= firstLine)
            {
                result.Add(new JitSourceLinkedRange(documentPath, sourceRange, new JitSourceTextRange(firstLine, 0, lastLine, cleanedLines[lastLine].Length), SequencePointPrecision, new JitEvidenceRange(currentPoint.Offset, checked((int)nativeStart), checked((int)nativeEnd), documentPath, checked(sourceRange.StartLine + 1), checked(sourceRange.StartCharacter + 1), checked(sourceRange.EndLine + 1), checked(sourceRange.EndCharacter + 1))));
            }
            firstLine = -1;
            lastLine = -1;
            nativeStart = 0;
            nativeEnd = 0;
        }

        foreach (var instruction in instructions)
        {
            var nativeRange = FindNativeRange(nativeRanges, instruction.NativeStart, instruction.NativeEnd);
            var point = nativeRange is null
                ? null : FindSequencePoint(sequencePoints, nativeRange.IlOffset);
            if (!Equals(point, currentPoint))
            {
                CompleteRange();
                currentPoint = point;
            }
            if (currentPoint?.SourceRange is null || currentPoint.DocumentPath is null)
                continue;
            if (firstLine < 0)
            {
                firstLine = instruction.LineIndex;
                nativeStart = instruction.NativeStart;
            }
            lastLine = instruction.LineIndex;
            nativeEnd = instruction.NativeEnd;
        }
        CompleteRange();
        return result;
    }

    private static JitNativeIlRange[] NormalizeNativeRanges(IReadOnlyList<JitNativeIlRange> ranges)
    {
        var ordered = ranges.Where(static range => range.IlOffset >= 0 && range.NativeEnd > range.NativeStart).OrderBy(static range => range.NativeStart).ThenBy(static range => range.NativeEnd).ToArray();
        if (ordered.Length <= 1)
            return ordered;

        var normalized = new List<JitNativeIlRange>(ordered.Length);
        uint previousEnd = 0;
        foreach (var range in ordered)
        {
            if (normalized.Count > 0 && range.NativeStart < previousEnd)
                continue;
            normalized.Add(range);
            previousEnd = range.NativeEnd;
        }
        return normalized.ToArray();
    }

    private static JitNativeIlRange? FindNativeRange(IReadOnlyList<JitNativeIlRange> ranges, uint instructionStart, uint instructionEnd)
    {
        var low = 0;
        var high = ranges.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (ranges[middle].NativeEnd <= instructionStart)
                low = middle + 1;
            else
                high = middle;
        }
        if (low >= ranges.Count)
            return null;

        var candidate = ranges[low];
        return candidate.NativeStart < instructionEnd
            ? candidate : null;
    }

    [GeneratedRegex(
        @"^(?<label>\s*G_M[0-9A-Za-z]+_IG\d+:)\s*;;\s*offset=0x(?<offset>[0-9A-Fa-f]+)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NativeOffsetLabel();

    [GeneratedRegex(
        @"^(?<indent>\s*)(?<bytes>(?:[0-9A-Fa-f]{2})+)(?<spacing>\s{2,})(?<instruction>\S.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CodeBytesInstruction();
}

internal sealed record JitNativeMappedSection(string Text, IReadOnlyList<JitSourceLinkedRange> LinkedRanges);

internal sealed record JitNativeInstruction(int LineIndex, uint NativeStart, uint NativeEnd);

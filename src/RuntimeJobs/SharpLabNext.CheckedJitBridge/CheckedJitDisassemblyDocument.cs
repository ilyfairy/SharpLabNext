using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SharpLabNext.CheckedJitBridge;

internal static class CheckedJitDisassemblyDocument
{
    private const string HeaderPrefix = "; Assembly listing for method ";
    private const string BareHeaderPrefix = "Assembly listing for method ";
    private const string SizePrefix = "; Total bytes of code";

    public static string SelectPreparedMethods(string assemblyText, IReadOnlyList<JitMethodResult> methods, IReadOnlyDictionary<int, CheckedMethodSourceMap> sourceMaps)
    {
        if (assemblyText is null)
            throw new ArgumentNullException(nameof(assemblyText));
        if (methods is null)
            throw new ArgumentNullException(nameof(methods));
        if (sourceMaps is null)
            throw new ArgumentNullException(nameof(sourceMaps));
        if (assemblyText.Length == 0 || methods.Count == 0)
            return string.Empty;

        var lines = NormalizeLineEndings(assemblyText).Split('\n');
        var sections = ParseSections(lines);
        var preparedMethods = methods.Where(static method => string.Equals(method.Status, "prepared", StringComparison.Ordinal)).ToArray();
        var preparedHeaders = preparedMethods.Where(static method => method.SignatureIdentity.HeaderKey is not null).GroupBy(static method => method.SignatureIdentity.HeaderKey!, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var namespaceShortenedPreparedHeaders = preparedMethods.Where(static method => method.SignatureIdentity.NamespaceShortenedHeaderKey is not null).GroupBy(static method => method.SignatureIdentity.NamespaceShortenedHeaderKey!, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var unsupportedPreparedNames = preparedMethods.Where(static method => method.SignatureIdentity.HeaderKey is null).SelectMany(static method => GetNameKeys(method.SignatureIdentity)).ToHashSet(StringComparer.Ordinal);
        var retainedSections = sections.Where(section =>
                section.SignatureIdentity.HeaderKey is { } headerKey &&
                (preparedHeaders.ContainsKey(headerKey) || namespaceShortenedPreparedHeaders.ContainsKey(headerKey) || unsupportedPreparedNames.Contains(section.SignatureIdentity.NameKey))).ToArray();
        var sectionsByHeader = retainedSections.Where(static section => section.SignatureIdentity.HeaderKey is not null).GroupBy(static section => section.SignatureIdentity.HeaderKey!, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var bindings = CreateBindings(preparedHeaders, namespaceShortenedPreparedHeaders, sectionsByHeader);

        var output = new StringBuilder();
        var outputLine = 0;
        foreach (var section in retainedSections)
        {
            if (output.Length > 0)
            {
                output.Append('\n').Append('\n');
                outputLine += 1;
            }

            var sectionOutputStart = outputLine;
            var sectionText = string.Join("\n", lines, section.Start, section.End - section.Start);
            output.Append(sectionText);
            outputLine += section.End - section.Start;

            if (!bindings.TryGetValue(section, out var result))
            {
                continue;
            }

            result.NativeCodeSize = ParseNativeCodeSize(lines, section.Start, section.End);
            result.InstructionCount = CountInstructions(lines, section.Start, section.End);
            if (sourceMaps.TryGetValue(result.MetadataToken, out var sourceMap))
            {
                var mapping = CheckedJitSourceMapping.MapSection(sectionText, sourceMap);
                result.LinkedRanges = mapping.Ranges.Select(range => OffsetOutputRange(range, sectionOutputStart)).ToList();
                result.MappingSource = mapping.Source;
            }
        }

        return output.ToString();
    }

    private static Dictionary<JitAssemblySection, JitMethodResult> CreateBindings(Dictionary<string, JitMethodResult[]> preparedHeaders, Dictionary<string, JitMethodResult[]> namespaceShortenedPreparedHeaders, Dictionary<string, JitAssemblySection[]> sectionsByHeader)
    {
        var bindings = new Dictionary<JitAssemblySection, JitMethodResult>();
        var exactlyBoundMethods = new HashSet<JitMethodResult>();

        foreach (var (headerKey, candidates) in preparedHeaders)
        {
            if (candidates.Length != 1 || !sectionsByHeader.TryGetValue(headerKey, out var matchingSections) || matchingSections.Length != 1)
            {
                continue;
            }

            bindings.Add(matchingSections[0], candidates[0]);
            exactlyBoundMethods.Add(candidates[0]);
        }

        foreach (var (headerKey, candidates) in namespaceShortenedPreparedHeaders)
        {
            if (preparedHeaders.ContainsKey(headerKey) || candidates.Length != 1 || exactlyBoundMethods.Contains(candidates[0]) || !sectionsByHeader.TryGetValue(headerKey, out var matchingSections) || matchingSections.Length != 1 || bindings.ContainsKey(matchingSections[0]))
            {
                continue;
            }

            bindings.Add(matchingSections[0], candidates[0]);
        }

        return bindings;
    }

    private static IEnumerable<string> GetNameKeys(JitMethodSignatureIdentity identity)
    {
        yield return identity.NameKey;
        if (identity.NamespaceShortenedNameKey is not null)
            yield return identity.NamespaceShortenedNameKey;
    }

    private static List<JitAssemblySection> ParseSections(string[] lines)
    {
        var sections = new List<JitAssemblySection>();
        for (var start = 0; start < lines.Length; start++)
        {
            if (!TryGetHeaderName(lines[start], out var header) || !JitMethodSignatures.TryParseHeader(header, out var signatureIdentity))
            {
                continue;
            }

            var end = start + 1;
            while (end < lines.Length && !TryGetHeaderName(lines[end], out _))
                end++;
            while (end > start && lines[end - 1].Length == 0)
                end--;
            if (end > start)
                sections.Add(new JitAssemblySection(start, end, signatureIdentity));
        }
        return sections;
    }

    private static JitLinkedRange OffsetOutputRange(JitLinkedRange range, int lineOffset) =>
        new(range.SourceFilePath, range.SourceRange, new JitTextRange(checked(range.OutputRange.StartLine + lineOffset), range.OutputRange.StartCharacter, checked(range.OutputRange.EndLine + lineOffset), range.OutputRange.EndCharacter), range.Precision, range.EvidenceRange);

    private static bool TryGetHeaderName(string line, out string name)
    {
        string? prefix = line.StartsWith(HeaderPrefix, StringComparison.Ordinal)
            ? HeaderPrefix : line.StartsWith(BareHeaderPrefix, StringComparison.Ordinal)
                ? BareHeaderPrefix : null;
        if (prefix is null)
        {
            name = string.Empty;
            return false;
        }

        name = line.Substring(prefix.Length).Trim();
        return name.Length > 0;
    }

    private static int CountInstructions(string[] lines, int start, int end)
    {
        var count = 0;
        for (var index = start; index < end; index++)
        {
            if (IsInstructionLine(lines[index]))
                count++;
        }
        return count;
    }

    private static bool IsInstructionLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 &&
            !trimmed.StartsWith(';') &&
            !trimmed.EndsWith(':') &&
            !trimmed.StartsWith("G_M", StringComparison.Ordinal);
    }

    private static int ParseNativeCodeSize(string[] lines, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (!trimmed.StartsWith(SizePrefix, StringComparison.Ordinal))
                continue;
            var digitStart = SizePrefix.Length;
            while (digitStart < trimmed.Length && !char.IsDigit(trimmed[digitStart]))
                digitStart++;
            var digitEnd = digitStart;
            while (digitEnd < trimmed.Length && char.IsDigit(trimmed[digitEnd]))
                digitEnd++;
            if (digitEnd > digitStart && int.TryParse(trimmed.AsSpan(digitStart, digitEnd - digitStart), NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                return size;
            }
        }
        return 0;
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private sealed record JitAssemblySection(int Start, int End, JitMethodSignatureIdentity SignatureIdentity);
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SharpLabNext.LegacyJitInspector
{
    internal static class JitDisassemblyDocument
    {
        private const int MaximumOutputBytes = 32 * 1024 * 1024;
        private const string HeaderPrefix = "; Assembly listing for method ";
        private const string BareHeaderPrefix = "Assembly listing for method ";
        private const string SizePrefix = "; Total bytes of code";

        public static string ReadOutput()
        {
            string path = Environment.GetEnvironmentVariable("SHARPLABNEXT_JIT_OUTPUT_PATH");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;

            var information = new FileInfo(path);
            if (information.Length > MaximumOutputBytes)
                throw new InvalidDataException("CoreCLR JIT output exceeds the helper limit.");

            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string text = reader.ReadToEnd();
                if (Encoding.UTF8.GetByteCount(text) > MaximumOutputBytes)
                    throw new InvalidDataException("CoreCLR JIT output exceeds the helper limit.");
                return NormalizeLineEndings(text);
            }
        }

        public static string SelectPreparedMethods(
            string assemblyText,
            IList<JitMethodResult> methods,
            IReadOnlyDictionary<int, MethodSourceSpan> sourceSpans)
        {
            if (string.IsNullOrEmpty(assemblyText) || methods.Count == 0)
                return string.Empty;

            string[] lines = NormalizeLineEndings(assemblyText).Split('\n');
            var sectionStarts = new List<int>();
            for (int index = 0; index < lines.Length; index++)
            {
                if (TryGetHeaderName(lines[index], out _))
                    sectionStarts.Add(index);
            }

            var unmatched = Enumerable.Range(0, methods.Count).ToList();
            var output = new StringBuilder();
            int outputLine = 0;
            for (int sectionIndex = 0; sectionIndex < sectionStarts.Count; sectionIndex++)
            {
                int start = sectionStarts[sectionIndex];
                int end = sectionIndex + 1 < sectionStarts.Count
                    ? sectionStarts[sectionIndex + 1]
                    : lines.Length;
                if (!TryGetHeaderName(lines[start], out string jitName))
                    continue;

                int unmatchedIndex = unmatched.FindIndex(
                    resultIndex => MethodNamesMatch(jitName, methods[resultIndex].DisplayName));
                if (unmatchedIndex < 0)
                    continue;

                int resultIndex = unmatched[unmatchedIndex];
                unmatched.RemoveAt(unmatchedIndex);
                while (end > start && lines[end - 1].Length == 0)
                    end--;
                if (end <= start)
                    continue;

                if (output.Length > 0)
                {
                    output.Append('\n').Append('\n');
                    outputLine += 2;
                }

                int sectionOutputStart = outputLine;
                int firstInstruction = -1;
                int lastInstruction = -1;
                for (int lineIndex = start; lineIndex < end; lineIndex++)
                {
                    string line = lines[lineIndex];
                    output.Append(line);
                    if (lineIndex + 1 < end)
                        output.Append('\n');
                    if (IsInstructionLine(line))
                    {
                        if (firstInstruction < 0)
                            firstInstruction = lineIndex - start;
                        lastInstruction = lineIndex - start;
                    }
                }

                JitMethodResult result = methods[resultIndex];
                result.NativeCodeSize = ParseNativeCodeSize(lines, start, end);
                result.InstructionCount = CountInstructions(lines, start, end);
                if (firstInstruction >= 0 &&
                    sourceSpans.TryGetValue(result.MetadataToken, out MethodSourceSpan sourceSpan))
                {
                    result.LinkedRanges.Add(new JitLinkedRange(
                        sourceSpan.SourceFilePath,
                        sourceSpan.Range,
                        new JitTextRange(
                            sectionOutputStart + firstInstruction,
                            0,
                            sectionOutputStart + lastInstruction,
                            lines[start + lastInstruction].Length),
                        "method"));
                    result.MappingSource = "method";
                }

                outputLine += end - start;
            }

            return output.ToString();
        }

        private static bool TryGetHeaderName(string line, out string name)
        {
            string prefix = line.StartsWith(HeaderPrefix, StringComparison.Ordinal)
                ? HeaderPrefix
                : line.StartsWith(BareHeaderPrefix, StringComparison.Ordinal) ? BareHeaderPrefix : null;
            if (prefix == null)
            {
                name = null;
                return false;
            }

            name = line.Substring(prefix.Length).Trim();
            return name.Length > 0;
        }

        private static bool MethodNamesMatch(string jitName, string displayName)
        {
            string normalized = RemoveConstructedMethodArguments(RemoveSignature(jitName.Replace(':', '.')));
            return string.Equals(normalized, displayName, StringComparison.OrdinalIgnoreCase);
        }

        private static string RemoveSignature(string name)
        {
            int signatureStart = name.IndexOf('(');
            return signatureStart < 0 ? name : name.Substring(0, signatureStart);
        }

        private static string RemoveConstructedMethodArguments(string name)
        {
            if (!name.EndsWith(']'))
                return name;
            int argumentsStart = name.LastIndexOf('[');
            return argumentsStart > 0 ? name.Substring(0, argumentsStart) : name;
        }

        private static bool IsInstructionLine(string line)
        {
            string trimmed = line.Trim();
            return trimmed.Length > 0 &&
                !trimmed.StartsWith(';') &&
                !trimmed.EndsWith(':') &&
                !(trimmed.StartsWith("G_M", StringComparison.Ordinal) && trimmed.IndexOf(':') >= 0);
        }

        private static int CountInstructions(string[] lines, int start, int end)
        {
            int count = 0;
            for (int index = start; index < end; index++)
            {
                if (IsInstructionLine(lines[index]))
                    count++;
            }
            return count;
        }

        private static int ParseNativeCodeSize(string[] lines, int start, int end)
        {
            for (int index = start; index < end; index++)
            {
                string trimmed = lines[index].TrimStart();
                if (!trimmed.StartsWith(SizePrefix, StringComparison.Ordinal))
                    continue;
                int digitStart = SizePrefix.Length;
                while (digitStart < trimmed.Length && !char.IsDigit(trimmed[digitStart]))
                    digitStart++;
                int digitEnd = digitStart;
                while (digitEnd < trimmed.Length && char.IsDigit(trimmed[digitEnd]))
                    digitEnd++;
                if (digitEnd > digitStart && int.TryParse(
                    trimmed.Substring(digitStart, digitEnd - digitStart),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int size))
                {
                    return size;
                }
            }
            return 0;
        }

        private static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }
    }
}

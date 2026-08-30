using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace SharpLabNext.LegacyJitInspector
{
    internal static class PortablePdbMethodMap
    {
        private const int MaximumSequencePointsPerMethod = 20_000;
        private static readonly char[] PathSeparators = { '/' };

        public static IReadOnlyDictionary<int, MethodSourceSpan> Load(string assemblyPath)
        {
            string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (!File.Exists(pdbPath))
                return new Dictionary<int, MethodSourceSpan>();

            try
            {
                using (var stream = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var provider = MetadataReaderProvider.FromPortablePdbStream(stream, MetadataStreamOptions.PrefetchMetadata))
                {
                    MetadataReader reader = provider.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
                    int count = reader.GetTableRowCount(TableIndex.MethodDebugInformation);
                    var result = new Dictionary<int, MethodSourceSpan>();
                    for (int row = 1; row <= count; row++)
                    {
                        MethodSourceSpan span = ReadMethodSpan(reader, row);
                        if (span != null)
                        {
                            int token = MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(row));
                            result[token] = span;
                        }
                    }
                    return result;
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is BadImageFormatException || exception is InvalidOperationException || exception is ArgumentException)
            {
                return new Dictionary<int, MethodSourceSpan>();
            }
        }

        private static MethodSourceSpan ReadMethodSpan(MetadataReader reader, int row)
        {
            MethodDebugInformation information = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(row));
            string selectedPath = null;
            int startLine = int.MaxValue;
            int startCharacter = int.MaxValue;
            int endLine = -1;
            int endCharacter = -1;
            int count = 0;
            foreach (SequencePoint point in information.GetSequencePoints())
            {
                if (++count > MaximumSequencePointsPerMethod)
                    break;
                DocumentHandle documentHandle = point.Document.IsNil ? information.Document : point.Document;
                if (point.IsHidden || documentHandle.IsNil)
                    continue;

                string path = SanitizeDocumentPath(reader.GetString(reader.GetDocument(documentHandle).Name));
                if (selectedPath == null)
                    selectedPath = path;
                if (!string.Equals(path, selectedPath, StringComparison.Ordinal))
                    continue;

                int pointStartLine = ToZeroBased(point.StartLine);
                int pointStartCharacter = ToZeroBased(point.StartColumn);
                if (pointStartLine < startLine || (pointStartLine == startLine && pointStartCharacter < startCharacter))
                {
                    startLine = pointStartLine;
                    startCharacter = pointStartCharacter;
                }

                int pointEndLine = ToZeroBased(point.EndLine);
                int pointEndCharacter = ToZeroBased(point.EndColumn);
                if (pointEndLine > endLine || (pointEndLine == endLine && pointEndCharacter > endCharacter))
                {
                    endLine = pointEndLine;
                    endCharacter = pointEndCharacter;
                }
            }

            return selectedPath == null || endLine < 0
                ? null : new MethodSourceSpan(selectedPath, new JitTextRange(startLine, startCharacter, endLine, endCharacter));
        }

        private static int ToZeroBased(int coordinate)
        {
            return Math.Max(0, coordinate - 1);
        }

        private static string SanitizeDocumentPath(string path)
        {
            string[] rawSegments = path.Replace('\\', '/').Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
            var segments = new List<string>(8);
            int start = Math.Max(0, rawSegments.Length - 8);
            for (int index = start; index < rawSegments.Length; index++)
            {
                string segment = rawSegments[index];
                if (segment == "." || segment == ".." || segment.EndsWith(':'))
                    continue;
                segments.Add(segment);
            }

            string sanitized = segments.Count == 0 ? "source" : string.Join("/", segments);
            return sanitized.Length <= 512
                ? sanitized : sanitized.Substring(sanitized.Length - 512);
        }
    }
}

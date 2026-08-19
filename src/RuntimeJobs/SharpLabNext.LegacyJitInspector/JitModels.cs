using System;
using System.Collections.Generic;

namespace SharpLabNext.LegacyJitInspector
{
    internal sealed class JitMethodResult
    {
        public JitMethodResult(
            string method,
            int metadataToken,
            string displayName,
            string status,
            string address,
            string error)
        {
            Method = method;
            MetadataToken = metadataToken;
            DisplayName = displayName;
            Status = status;
            Address = address;
            Error = error;
            LinkedRanges = new List<JitLinkedRange>();
            MappingSource = "none";
        }

        public string Method { get; private set; }

        public int MetadataToken { get; private set; }

        public string DisplayName { get; private set; }

        public string Status { get; private set; }

        public string Address { get; private set; }

        public string Error { get; private set; }

        public int NativeCodeSize { get; set; }

        public int InstructionCount { get; set; }

        public List<JitLinkedRange> LinkedRanges { get; private set; }

        public string MappingSource { get; set; }
    }

    internal sealed class JitLinkedRange
    {
        public JitLinkedRange(
            string sourceFilePath,
            JitTextRange sourceRange,
            JitTextRange outputRange,
            string precision)
        {
            SourceFilePath = sourceFilePath;
            SourceRange = sourceRange;
            OutputRange = outputRange;
            Precision = precision;
        }

        public string SourceFilePath { get; private set; }

        public JitTextRange SourceRange { get; private set; }

        public JitTextRange OutputRange { get; private set; }

        public string Precision { get; private set; }
    }

    internal sealed class JitTextRange
    {
        public JitTextRange(int startLine, int startCharacter, int endLine, int endCharacter)
        {
            StartLine = startLine;
            StartCharacter = startCharacter;
            EndLine = endLine;
            EndCharacter = endCharacter;
        }

        public int StartLine { get; private set; }

        public int StartCharacter { get; private set; }

        public int EndLine { get; private set; }

        public int EndCharacter { get; private set; }
    }

    internal sealed class MethodSourceSpan
    {
        public MethodSourceSpan(string sourceFilePath, JitTextRange range)
        {
            SourceFilePath = sourceFilePath;
            Range = range;
        }

        public string SourceFilePath { get; private set; }

        public JitTextRange Range { get; private set; }
    }
}

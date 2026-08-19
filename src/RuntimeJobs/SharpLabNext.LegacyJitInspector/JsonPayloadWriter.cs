using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SharpLabNext.LegacyJitInspector
{
    internal static class JsonPayloadWriter
    {
        private const int MaximumExceptionDepth = 32;

        public static byte[] WriteJitSummary(
            string runtimeVersion,
            string assembly,
            string methodFilter,
            IList<JitMethodResult> methods)
        {
            var json = new StringBuilder();
            json.Append('{');
            WritePropertyName(json, "RuntimeVersion");
            WriteString(json, runtimeVersion);
            json.Append(',');
            WritePropertyName(json, "Assembly");
            WriteString(json, assembly);
            json.Append(',');
            WritePropertyName(json, "MethodFilter");
            WriteString(json, methodFilter);
            json.Append(',');
            WritePropertyName(json, "Methods");
            json.Append('[');
            for (int index = 0; index < methods.Count; index++)
            {
                if (index > 0)
                    json.Append(',');
                WriteMethod(json, methods[index]);
            }
            json.Append(']').Append('}');
            return Encoding.UTF8.GetBytes(json.ToString());
        }

        public static byte[] WriteExit(string status, int exitCode, double elapsedMilliseconds)
        {
            var json = new StringBuilder();
            json.Append('{');
            WritePropertyName(json, "Status");
            WriteString(json, status);
            json.Append(',');
            WritePropertyName(json, "ExitCode");
            json.Append(exitCode.ToString(CultureInfo.InvariantCulture));
            json.Append(',');
            WritePropertyName(json, "ElapsedMilliseconds");
            WriteFiniteDouble(json, elapsedMilliseconds);
            json.Append('}');
            return Encoding.UTF8.GetBytes(json.ToString());
        }

        public static byte[] WriteException(Exception exception, double elapsedMilliseconds)
        {
            if (exception == null)
                throw new ArgumentNullException(nameof(exception));
            var json = new StringBuilder();
            WriteExceptionObject(json, exception, 1, true, elapsedMilliseconds);
            return Encoding.UTF8.GetBytes(json.ToString());
        }

        private static void WriteMethod(StringBuilder json, JitMethodResult method)
        {
            json.Append('{');
            WritePropertyName(json, "Method");
            WriteString(json, method.Method);
            json.Append(',');
            WritePropertyName(json, "DisplayName");
            WriteString(json, method.DisplayName);
            json.Append(',');
            WritePropertyName(json, "Status");
            WriteString(json, method.Status);
            json.Append(',');
            WritePropertyName(json, "Address");
            WriteString(json, method.Address);
            json.Append(',');
            WritePropertyName(json, "Error");
            WriteString(json, method.Error);
            json.Append(',');
            WritePropertyName(json, "NativeCodeSize");
            json.Append(method.NativeCodeSize.ToString(CultureInfo.InvariantCulture));
            json.Append(',');
            WritePropertyName(json, "InstructionCount");
            json.Append(method.InstructionCount.ToString(CultureInfo.InvariantCulture));
            json.Append(',');
            WritePropertyName(json, "LinkedRanges");
            json.Append('[');
            for (int index = 0; index < method.LinkedRanges.Count; index++)
            {
                if (index > 0)
                    json.Append(',');
                WriteLinkedRange(json, method.LinkedRanges[index]);
            }
            json.Append(']');
            json.Append(',');
            WritePropertyName(json, "MappingSource");
            WriteString(json, method.MappingSource);
            json.Append('}');
        }

        private static void WriteLinkedRange(StringBuilder json, JitLinkedRange range)
        {
            json.Append('{');
            WritePropertyName(json, "SourceFilePath");
            WriteString(json, range.SourceFilePath);
            json.Append(',');
            WritePropertyName(json, "SourceRange");
            WriteRange(json, range.SourceRange);
            json.Append(',');
            WritePropertyName(json, "OutputRange");
            WriteRange(json, range.OutputRange);
            json.Append(',');
            WritePropertyName(json, "Precision");
            WriteString(json, range.Precision);
            json.Append('}');
        }

        private static void WriteRange(StringBuilder json, JitTextRange range)
        {
            json.Append('{');
            WritePropertyName(json, "StartLine");
            json.Append(range.StartLine.ToString(CultureInfo.InvariantCulture));
            json.Append(',');
            WritePropertyName(json, "StartCharacter");
            json.Append(range.StartCharacter.ToString(CultureInfo.InvariantCulture));
            json.Append(',');
            WritePropertyName(json, "EndLine");
            json.Append(range.EndLine.ToString(CultureInfo.InvariantCulture));
            json.Append(',');
            WritePropertyName(json, "EndCharacter");
            json.Append(range.EndCharacter.ToString(CultureInfo.InvariantCulture));
            json.Append('}');
        }

        private static void WriteExceptionObject(
            StringBuilder json,
            Exception exception,
            int depth,
            bool includeElapsed,
            double elapsedMilliseconds)
        {
            json.Append('{');
            WritePropertyName(json, "TypeName");
            WriteString(json, exception.GetType().FullName ?? exception.GetType().Name);
            json.Append(',');
            WritePropertyName(json, "Message");
            WriteString(json, exception.Message);
            json.Append(',');
            WritePropertyName(json, "StackTrace");
            WriteString(json, exception.StackTrace);
            json.Append(',');
            WritePropertyName(json, "InnerException");
            if (exception.InnerException == null || depth >= MaximumExceptionDepth)
                json.Append("null");
            else
                WriteExceptionObject(json, exception.InnerException, depth + 1, false, 0);
            if (includeElapsed)
            {
                json.Append(',');
                WritePropertyName(json, "ElapsedMilliseconds");
                WriteFiniteDouble(json, elapsedMilliseconds);
            }
            json.Append('}');
        }

        private static void WriteFiniteDouble(StringBuilder json, double value)
        {
            json.Append(double.IsNaN(value) || double.IsInfinity(value)
                ? "0"
                : value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WritePropertyName(StringBuilder json, string name)
        {
            WriteString(json, name);
            json.Append(':');
        }

        private static void WriteString(StringBuilder json, string value)
        {
            if (value == null)
            {
                json.Append("null");
                return;
            }

            json.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"':
                        json.Append("\\\"");
                        break;
                    case '\\':
                        json.Append("\\\\");
                        break;
                    case '\b':
                        json.Append("\\b");
                        break;
                    case '\f':
                        json.Append("\\f");
                        break;
                    case '\n':
                        json.Append("\\n");
                        break;
                    case '\r':
                        json.Append("\\r");
                        break;
                    case '\t':
                        json.Append("\\t");
                        break;
                    default:
                        if (character < ' ' || char.IsSurrogate(character))
                        {
                            json.Append("\\u");
                            json.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            json.Append(character);
                        }
                        break;
                }
            }
            json.Append('"');
        }
    }
}

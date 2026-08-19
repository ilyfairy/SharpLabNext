using System;
using System.Globalization;
using System.Text;

namespace SharpLabNext.TargetRuntimeRunner
{
    internal static class JsonPayloadWriter
    {
        private const int MaximumExceptionDepth = 32;

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
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\b': json.Append("\\b"); break;
                    case '\f': json.Append("\\f"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
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

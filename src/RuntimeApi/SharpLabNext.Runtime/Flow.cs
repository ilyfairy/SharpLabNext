namespace SharpLab.Runtime.Internal;

public static class Flow
{
    public const int UnknownLineNumber = -1;

    public static void ReportSequencePoint(
        string? documentPath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        RuntimeServices.Write(new FlowRecord(
            FlowEventKind.SequencePoint,
            documentPath,
            startLine,
            startColumn,
            endLine,
            endColumn));
    }

    public static void ReportBranch(
        string? documentPath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        RuntimeServices.Write(new FlowRecord(
            FlowEventKind.Branch,
            documentPath,
            startLine,
            startColumn,
            endLine,
            endColumn));
    }

    public static void ReportMethod(string? name)
    {
        RuntimeServices.Write(new FlowRecord(
            FlowEventKind.Method,
            null,
            UnknownLineNumber,
            0,
            UnknownLineNumber,
            0,
            name: name));
    }

    public static void ReportMethodArea(int startLineNumber, int endLineNumber) =>
        WriteLegacy(FlowEventKind.Method, startLineNumber, endLineNumber);

    public static void ReportLoopArea(int startLineNumber, int endLineNumber) =>
        WriteLegacy(FlowEventKind.Loop, startLineNumber, endLineNumber);

    public static void ReportLineStart(int lineNumber) =>
        WriteLegacy(FlowEventKind.SequencePoint, lineNumber, lineNumber);

    public static void ReportJump() =>
        WriteLegacy(FlowEventKind.Jump, UnknownLineNumber, UnknownLineNumber);

    public static void ReportLoopStart() =>
        WriteLegacy(FlowEventKind.Loop, UnknownLineNumber, UnknownLineNumber);

    public static void ReportLoopEnd() =>
        WriteLegacy(FlowEventKind.Loop, UnknownLineNumber, UnknownLineNumber);

    public static void ReportRefValue<T>(ref T value, string? name, int lineNumber) =>
        ReportValue(value, name, lineNumber);

    public static void ReportValue<T>(T value, string? name, int lineNumber)
    {
        RuntimeServices.Write(new FlowRecord(
            FlowEventKind.Value,
            null,
            lineNumber,
            0,
            lineNumber,
            0,
            value,
            name));
    }

    public static void ReportRefSpanValue<T>(ref Span<T> value, string? name, int lineNumber) =>
        ReportReadOnlySpanValue((ReadOnlySpan<T>)value, name, lineNumber);

    public static void ReportSpanValue<T>(Span<T> value, string? name, int lineNumber) =>
        ReportReadOnlySpanValue((ReadOnlySpan<T>)value, name, lineNumber);

    public static void ReportRefReadOnlySpanValue<T>(ref ReadOnlySpan<T> value, string? name, int lineNumber) =>
        ReportReadOnlySpanValue(value, name, lineNumber);

    public static void ReportReadOnlySpanValue<T>(ReadOnlySpan<T> value, string? name, int lineNumber) =>
        ReportValue(value.ToArray(), name, lineNumber);

    public static void ReportException(object exception)
    {
        RuntimeServices.Write(new FlowRecord(
            FlowEventKind.Exception,
            null,
            UnknownLineNumber,
            0,
            UnknownLineNumber,
            0,
            exception));
    }

    private static void WriteLegacy(FlowEventKind kind, int startLine, int endLine)
    {
        RuntimeServices.Write(new FlowRecord(kind, null, startLine, 0, endLine, 0));
    }
}

[Obsolete("Only preserved for binary compatibility with older SharpLab artifacts.", error: true)]
public static class ContainerFlow
{
    public static void ReportLineStart(int lineNumber) => Flow.ReportLineStart(lineNumber);
    public static void ReportRefValue<T>(ref T value, string? name, int lineNumber) => Flow.ReportRefValue(ref value, name, lineNumber);
    public static void ReportValue<T>(T value, string? name, int lineNumber) => Flow.ReportValue(value, name, lineNumber);
    public static void ReportRefSpanValue<T>(ref Span<T> value, string? name, int lineNumber) => Flow.ReportRefSpanValue(ref value, name, lineNumber);
    public static void ReportSpanValue<T>(Span<T> value, string? name, int lineNumber) => Flow.ReportSpanValue(value, name, lineNumber);
    public static void ReportRefReadOnlySpanValue<T>(ref ReadOnlySpan<T> value, string? name, int lineNumber) => Flow.ReportRefReadOnlySpanValue(ref value, name, lineNumber);
    public static void ReportReadOnlySpanValue<T>(ReadOnlySpan<T> value, string? name, int lineNumber) => Flow.ReportReadOnlySpanValue(value, name, lineNumber);
    public static void ReportException(object exception) => Flow.ReportException(exception);
}

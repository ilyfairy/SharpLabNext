using System.Collections.Generic;
using System.Threading;

namespace SharpLab.Runtime;

public enum InspectionKind
{
    Value,
    Heap,
    Stack,
    MemoryGraph,
    Allocations,
    Warning
}

public sealed class InspectionRecord
{
    public InspectionRecord(
        InspectionKind kind,
        string title,
        object? value,
        IReadOnlyList<object?>? values = null)
    {
        Kind = kind;
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Value = value;
        Values = values ?? Array.Empty<object?>();
    }

    public InspectionKind Kind { get; }
    public string Title { get; }
    public object? Value { get; }
    public IReadOnlyList<object?> Values { get; }
}

public sealed class AllocationInspection
{
    public AllocationInspection(long allocatedBytes)
    {
        AllocatedBytes = allocatedBytes;
    }

    public long AllocatedBytes { get; }
}

public interface IInspectionSink
{
    void Write(InspectionRecord inspection);
}

public enum FlowEventKind
{
    SequencePoint,
    Branch,
    Method,
    Loop,
    Jump,
    Value,
    Exception
}

public sealed class FlowRecord
{
    public FlowRecord(
        FlowEventKind kind,
        string? documentPath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        object? value = null,
        string? name = null)
    {
        Kind = kind;
        DocumentPath = documentPath;
        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
        Value = value;
        Name = name;
    }

    public FlowEventKind Kind { get; }
    public string? DocumentPath { get; }
    public int StartLine { get; }
    public int StartColumn { get; }
    public int EndLine { get; }
    public int EndColumn { get; }
    public object? Value { get; }
    public string? Name { get; }
}

public interface IFlowSink
{
    void Write(FlowRecord flow);
}

public static class RuntimeServices
{
    private static readonly AsyncLocal<IInspectionSink?> CurrentSink = new();
    private static readonly AsyncLocal<IFlowSink?> CurrentFlowSink = new();

    public static IDisposable PushInspectionSink(IInspectionSink sink)
    {
        if (sink is null)
        {
            throw new ArgumentNullException(nameof(sink));
        }
        var previous = CurrentSink.Value;
        CurrentSink.Value = sink;
        return new RestoreScope(previous);
    }

    public static IDisposable PushFlowSink(IFlowSink sink)
    {
        if (sink is null)
        {
            throw new ArgumentNullException(nameof(sink));
        }
        var previous = CurrentFlowSink.Value;
        CurrentFlowSink.Value = sink;
        return new RestoreFlowScope(previous);
    }

    internal static void Write(InspectionRecord inspection)
    {
        CurrentSink.Value?.Write(inspection);
    }

    internal static void Write(FlowRecord flow)
    {
        CurrentFlowSink.Value?.Write(flow);
    }

    private sealed class RestoreScope(IInspectionSink? previous) : IDisposable
    {
        private IInspectionSink? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CurrentSink.Value = _previous;
            _previous = null;
            _disposed = true;
        }
    }


    private sealed class RestoreFlowScope(IFlowSink? previous) : IDisposable
    {
        private IFlowSink? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CurrentFlowSink.Value = _previous;
            _previous = null;
            _disposed = true;
        }
    }
}

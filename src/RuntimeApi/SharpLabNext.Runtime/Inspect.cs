using System.ComponentModel;
using SharpLab.Runtime;

public static partial class Inspect
{
    public static void Heap(object @object)
    {
        RuntimeServices.Write(new InspectionRecord(InspectionKind.Heap, "Heap", @object));
    }

    public static void Stack<T>(in T value)
    {
        RuntimeServices.Write(new InspectionRecord(InspectionKind.Stack, "Stack", value));
    }

    public static void MemoryGraph<T>(in T value)
    {
        WriteMemoryGraph([value]);
    }

    public static void MemoryGraph<T1, T2>(in T1 value1, in T2 value2)
    {
        WriteMemoryGraph([value1, value2]);
    }

    public static void MemoryGraph<T1, T2, T3>(in T1 value1, in T2 value2, in T3 value3)
    {
        WriteMemoryGraph([value1, value2, value3]);
    }

    public static void MemoryGraph<T1, T2, T3, T4>(in T1 value1, in T2 value2, in T3 value3, in T4 value4)
    {
        WriteMemoryGraph([value1, value2, value3, value4]);
    }

    public static void MemoryGraph<T1, T2, T3, T4, T5>(in T1 value1, in T2 value2, in T3 value3, in T4 value4, in T5 value5)
    {
        WriteMemoryGraph([value1, value2, value3, value4, value5]);
    }

    public static void Allocations<T>(Func<T> action)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));
        Allocations((Action)(() =>
        {
            _ = action();
        }));
    }

    public static void Allocations(Action action)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            action();
        }
        finally
        {
            var allocated = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before);
            RuntimeServices.Write(new InspectionRecord(InspectionKind.Allocations, "Allocations", new AllocationInspection(allocated)));
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static new bool Equals(object? a, object? b)
    {
        throw new NotSupportedException();
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static new bool ReferenceEquals(object? objA, object? objB)
    {
        throw new NotSupportedException();
    }

    private static void WriteMemoryGraph(IReadOnlyList<object?> values)
    {
        RuntimeServices.Write(new InspectionRecord(InspectionKind.MemoryGraph, "Memory Graph", null, values));
    }
}

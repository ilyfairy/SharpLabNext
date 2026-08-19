using System.Runtime.CompilerServices;
using SharpLab.Runtime;

namespace SharpLabNext.JitProfilerSmoke;

public static class MappingSmoke
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int MultipleSequencePoints(int input)
    {
        var value = input + 1;
        if (value > 10)
            value *= 2;
        else
            value -= 3;
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int SameLineFor(int input)
    {
        var total = 0;
        for (var i = input; i < input + 3; i++) total += i;
        return total;
    }

    [JitGeneric(typeof(int))]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ConstructedGeneric<T>(int input)
    {
        var value = input + 2;
        if (value > 10)
            value += 3;
        else
            value -= 4;
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int OrdinarySingleSequencePoint(int input)
    {
        return input + 1;
    }
}

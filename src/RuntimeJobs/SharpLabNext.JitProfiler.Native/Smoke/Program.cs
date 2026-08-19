namespace SharpLabNext.JitProfilerSmoke;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["hold"])
            Thread.Sleep(TimeSpan.FromSeconds(10));

        return MappingSmoke.MultipleSequencePoints(1);
    }
}

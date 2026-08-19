namespace SharpLabNext.ArtifactProcessing.Fixture;

using System.Runtime.CompilerServices;

public static class Program
{
    public static void Main()
    {
        var value = Add(20, 22);
        Console.WriteLine(value == 42 ? value : -1);
    }

    public static int Add(int left, int right) => left + right;

    [CompilerGenerated]
    public static int GeneratedHelper() => 1;
}

using System.Diagnostics;

if (args is ["compiler-child-exit"])
    return 134;

if (args is ["compiler-child-hang"])
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}

if (args is ["compiler-child-memory"])
{
    var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(128 * 1024 * 1024);
    try
    {
        for (var offset = 0; offset < 128 * 1024 * 1024; offset += 4096)
            System.Runtime.InteropServices.Marshal.WriteByte(memory, offset, 1);
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 0;
    }
    finally
    {
        System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);
    }
}

if (args is ["compiler-child-stderr"])
{
    var chunk = new string('x', 4096);
    for (var index = 0; index < 512; index++)
        Console.Error.Write(chunk);
    return 134;
}

if (args is ["compiler-child-stdout"])
{
    var chunk = new string('x', 4096);
    for (var index = 0; index < 512; index++)
        Console.Out.Write(chunk);
    return 0;
}

if (args is ["runtime-user-exception"])
{
    try
    {
        throw new ArgumentException("inner runtime failure");
    }
    catch (ArgumentException exception)
    {
        throw new InvalidOperationException("outer runtime failure", exception);
    }
}

if (args is ["process-bridge", var fixedArgument, var userArgument])
{
    var stdin = await Console.In.ReadToEndAsync();
    Console.Write($"bridge-stdout:{fixedArgument}:{userArgument}:{stdin}");
    Console.Error.Write(
        "wineserver: could not save registry branch to user.reg : Read-only file system\n" +
        "bridge-stderr");
    return 23;
}

if (args is ["runner-descendant-parent", var pidPath])
{
    var windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
    var startInfo = new ProcessStartInfo
    {
        FileName = windows ? "cmd.exe" : "/bin/sh",
        Arguments = windows
            ? "/d /s /c \"ping -t 127.0.0.1 >NUL 2>NUL\""
            : "-c \"while :; do sleep 60; done >/dev/null 2>&1\"",
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var descendant = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Runner descendant did not start.");
    File.WriteAllText(pidPath, descendant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Console.Write("runner-descendant-parent-output");
    var outputChunk = new string('x', 64 * 1024);
    for (var index = 0; index < 16; index++)
        Console.Write(outputChunk);
    Console.Out.Flush();
    await Task.Delay(100);
    return 0;
}

Console.Write("fixture-stdout");
Console.Error.Write("fixture-stderr");
42.Dump();
var node = new FixtureNode { Name = "root" };
node.Next = node;
var values = new[] { 1, 2, 3 };
Inspect.MemoryGraph(node, values);
return 7;

internal sealed class FixtureNode
{
    public string Name { get; init; } = string.Empty;
    public FixtureNode? Next { get; set; }
}

internal static class GenericFixture
{
    [SharpLab.Runtime.JitGeneric(typeof(int))]
    public static T Identity<T>(T value) => value;

    public static T Unspecified<T>(T value) => value;
}

namespace SharpLabNext.RunnerFixture
{
    public sealed class RunnerFixtureMarker;
}

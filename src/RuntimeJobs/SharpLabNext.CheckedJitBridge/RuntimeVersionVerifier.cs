using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace SharpLabNext.CheckedJitBridge;

internal static class RuntimeVersionVerifier
{
    public const string Switch = "--verify-runtime-version";

    public static int Run(string[] args, TextWriter error, Func<string>? getRuntimeVersion = null)
    {
        if (args.Length != 2 || !string.Equals(args[0], Switch, StringComparison.Ordinal))
        {
            error.WriteLine($"Usage: SharpLabNext.CheckedJitBridge {Switch} <exact-runtime-version>");
            return 64;
        }

        var expected = args[1];
        var actual = (getRuntimeVersion ?? ReadRuntimeVersion)();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            error.WriteLine($"Checked JIT runtime identity '{actual}' does not match '{expected}'.");
            return 1;
        }

        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ReadRuntimeVersion() => Environment.Version.ToString();
}

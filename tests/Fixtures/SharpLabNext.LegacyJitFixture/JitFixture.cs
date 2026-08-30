using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SharpLabNext.LegacyJitFixture
{
    public static class JitFixture
    {
        public static long WindowsAbi(long first, long second)
        {
            return (first * 31) + (second * 17) + (first ^ second);
        }

        public static int Add(int first, int second)
        {
            return first + second;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Overload(int value)
        {
            return value + 1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long Overload(long value)
        {
            return value + 2;
        }

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

        [SharpLab.Runtime.JitGeneric(typeof(int))]
        public static T Identity<T>(T value)
        {
            return value;
        }

        [SharpLab.Runtime.JitGeneric(typeof(int))]
        [SharpLab.Runtime.JitGeneric(typeof(string))]
        public static T Generic<T>(T value)
        {
            return value;
        }

        [SharpLab.Runtime.JitGeneric(typeof(string))]
        [SharpLab.Runtime.JitGeneric(typeof(object))]
        public static T SharedReference<T>(T value)
        {
            return value;
        }
    }

    [SharpLab.Runtime.JitGeneric(typeof(int))]
    [SharpLab.Runtime.JitGeneric(typeof(string))]
    public static class GenericType<T>
    {
        [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Checked JIT fixture")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T Echo(T value)
        {
            return value;
        }
    }
}

namespace SharpLab.Runtime
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public sealed class JitGenericAttribute : Attribute
    {
        public JitGenericAttribute(params Type[] argumentTypes)
        {
            ArgumentTypes = argumentTypes;
        }

        public Type[] ArgumentTypes { get; private set; }
    }
}

internal static class Program
{
    [DllImport("libc", EntryPoint = "write", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr WriteLinux(int descriptor, byte[] buffer, UIntPtr count);

    [DllImport("ucrtbase.dll", EntryPoint = "_write", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WriteWindows(int descriptor, byte[] buffer, uint count);

    public static int Main(string[] args)
    {
        if (args != null && args.Length == 1 && args[0] == "stream")
        {
            Console.Out.Write("stream-first");
            Console.Out.Flush();
            Thread.Sleep(1500);
            Console.Error.Write("stream-second");
            return 0;
        }

        if (args != null && args.Length == 1 && args[0] == "output-limit")
        {
            var block = new string('x', 4096);
            for (var index = 0; index < 32; index++)
                Console.Out.Write(block);
            Console.Out.Flush();
            Thread.Sleep(30000);
            return 0;
        }

        if (args != null && args.Length == 1 && args[0] == "interleaved-output")
        {
            WriteInterleaved(1, Console.Out, "stdout-managed-a|", "stdout-native-b|", "stdout-managed-c");
            WriteInterleaved(2, Console.Error, "stderr-managed-a|", "stderr-native-b|", "stderr-managed-c");
            return 0;
        }

        return 0;
    }

    private static void WriteInterleaved(int descriptor, System.IO.TextWriter writer, string first, string native, string last)
    {
        writer.Write(first);
        writer.Flush();

        var bytes = Encoding.UTF8.GetBytes(native);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (WriteWindows(descriptor, bytes, (uint)bytes.Length) != bytes.Length)
                throw new InvalidOperationException("The native Windows output write was incomplete.");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (WriteLinux(descriptor, bytes, new UIntPtr((uint)bytes.Length)).ToInt64() != bytes.Length)
                throw new InvalidOperationException("The native Linux output write was incomplete.");
        }
        else
        {
            throw new PlatformNotSupportedException("The interleaved output fixture supports Windows and Linux only.");
        }

        writer.Write(last);
        writer.Flush();
    }
}

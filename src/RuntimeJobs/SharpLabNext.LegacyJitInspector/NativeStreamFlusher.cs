using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SharpLabNext.LegacyJitInspector
{
    internal static class NativeStreamFlusher
    {
        public static void FlushAll()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                FlushWindows(
                    () => FlushUcrt(IntPtr.Zero),
                    () => FlushMsvcrt(IntPtr.Zero));
                return;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (FlushLibc(IntPtr.Zero) != 0)
                    throw new IOException("CoreCLR JIT output could not be flushed through libc.");
                return;
            }

            throw new PlatformNotSupportedException("The legacy JIT helper supports Linux and Windows only.");
        }

        internal static void FlushWindows(Func<int> flushUcrt, Func<int> flushMsvcrt)
        {
            if (flushUcrt == null)
                throw new ArgumentNullException(nameof(flushUcrt));
            if (flushMsvcrt == null)
                throw new ArgumentNullException(nameof(flushMsvcrt));

            var failures = new List<Exception>(2);
            bool ucrtFlushed = TryFlush(flushUcrt, "ucrtbase", failures);
            bool msvcrtFlushed = TryFlush(flushMsvcrt, "msvcrt", failures);
            if (ucrtFlushed || msvcrtFlushed)
                return;

            throw new AggregateException(
                "CoreCLR JIT output could not be flushed through ucrtbase or msvcrt.",
                failures);
        }

        private static bool TryFlush(
            Func<int> flush,
            string libraryName,
            List<Exception> failures)
        {
            try
            {
                int result = flush();
                if (result == 0)
                    return true;
                failures.Add(new IOException(libraryName + "!fflush(NULL) returned " + result + "."));
            }
            catch (Exception exception) when (
                exception is DllNotFoundException ||
                exception is EntryPointNotFoundException ||
                exception is BadImageFormatException)
            {
                failures.Add(exception);
            }
            return false;
        }

        [DllImport("libc", EntryPoint = "fflush", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FlushLibc(IntPtr stream);

        [DllImport("ucrtbase.dll", EntryPoint = "fflush", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FlushUcrt(IntPtr stream);

        [DllImport("msvcrt.dll", EntryPoint = "fflush", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FlushMsvcrt(IntPtr stream);
    }
}

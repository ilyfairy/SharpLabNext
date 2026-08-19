using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpLabNext.LegacyJitInspector
{
    internal static class FrameOutput
    {
        private const int StandardOutputHandle = -11;
        private const uint DuplicateSameAccess = 0x00000002;

        public static Stream Open()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                int descriptor = DuplicateLinux(1);
                if (descriptor < 0)
                    throw new IOException("The runtime frame stdout descriptor could not be duplicated.");
                return new FileStream(
                    new SafeFileHandle(new IntPtr(descriptor), true),
                    FileAccess.Write);
            }
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("Runtime frames support Linux and Windows only.");

            IntPtr currentProcess = GetCurrentProcess();
            IntPtr standardOutput = GetStdHandle(StandardOutputHandle);
            if (standardOutput == IntPtr.Zero || standardOutput == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The runtime frame stdout handle is invalid.");

            if (!DuplicateHandle(
                currentProcess,
                standardOutput,
                currentProcess,
                out IntPtr duplicate,
                0,
                false,
                DuplicateSameAccess))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The runtime frame stdout handle could not be duplicated.");
            }

            return new FileStream(new SafeFileHandle(duplicate, true), FileAccess.Write);
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(
            IntPtr sourceProcess,
            IntPtr sourceHandle,
            IntPtr targetProcess,
            out IntPtr targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [DllImport("libc", EntryPoint = "dup", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DuplicateLinux(int descriptor);
    }

    internal static class WindowsJitOutputRedirector
    {
        private const int StandardOutputHandle = -11;
        private const int WriteOnly = 0x0001;
        private const int Create = 0x0100;
        private const int Truncate = 0x0200;
        private const int Binary = 0x8000;
        private const int OwnerRead = 0x0100;
        private const int OwnerWrite = 0x0080;

        public static void RedirectIfNeeded()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            string path = Environment.GetEnvironmentVariable("SHARPLABNEXT_JIT_OUTPUT_PATH");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            {
                throw new InvalidOperationException(
                    "Windows JIT inspection requires an absolute SHARPLABNEXT_JIT_OUTPUT_PATH.");
            }

            int descriptor = OpenUcrt(
                path,
                WriteOnly | Create | Truncate | Binary,
                OwnerRead | OwnerWrite);
            if (descriptor < 0)
                throw new IOException("The Windows JIT output file could not be opened through ucrtbase.");

            try
            {
                if (DuplicateDescriptorUcrt(descriptor, 1) != 0)
                    throw new IOException("ucrtbase could not redirect native stdout to the JIT output file.");

                IntPtr outputHandle = GetOsFileHandleUcrt(1);
                if (outputHandle == IntPtr.Zero || outputHandle == new IntPtr(-1))
                    throw new IOException("ucrtbase returned an invalid redirected stdout handle.");
                if (!SetStdHandle(StandardOutputHandle, outputHandle))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The Windows standard output handle could not be redirected.");
                }
            }
            catch
            {
                _ = CloseUcrt(descriptor);
                throw;
            }
            if (CloseUcrt(descriptor) != 0)
                throw new IOException("ucrtbase could not close the temporary JIT output descriptor.");
        }

        [DllImport("ucrtbase.dll", EntryPoint = "_wopen", CallingConvention = CallingConvention.Cdecl)]
        private static extern int OpenUcrt(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            int flags,
            int permissionMode);

        [DllImport("ucrtbase.dll", EntryPoint = "_dup2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DuplicateDescriptorUcrt(int source, int destination);

        [DllImport("ucrtbase.dll", EntryPoint = "_get_osfhandle", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetOsFileHandleUcrt(int descriptor);

        [DllImport("ucrtbase.dll", EntryPoint = "_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CloseUcrt(int descriptor);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetStdHandle(int standardHandle, IntPtr handle);
    }
}

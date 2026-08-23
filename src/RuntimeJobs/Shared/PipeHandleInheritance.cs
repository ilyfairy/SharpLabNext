using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpLabNext.RuntimeJobs;

internal static class PipeHandleInheritance
{
    private const uint HandleFlagInherit = 0x00000001;
    private const int FileDescriptorCloseOnExec = 1;
    private const int GetFileDescriptorFlags = 1;
    private const int SetFileDescriptorFlags = 2;

    public static void Disable(SafePipeHandle handle)
    {
        if (handle is null || handle.IsInvalid)
            throw new ArgumentException("The anonymous pipe handle is invalid.", nameof(handle));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!SetHandleInformation(handle, HandleFlagInherit, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var flags = GetDescriptorFlags(handle, GetFileDescriptorFlags);
            if (flags < 0 || SetDescriptorFlags(handle, SetFileDescriptorFlags, flags | FileDescriptorCloseOnExec) < 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        throw new PlatformNotSupportedException("Runtime jobs support Linux and Windows only.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        SafePipeHandle handle,
        uint mask,
        uint flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int GetDescriptorFlags(
        SafePipeHandle descriptor,
        int command);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int SetDescriptorFlags(
        SafePipeHandle descriptor,
        int command,
        int flags);
}

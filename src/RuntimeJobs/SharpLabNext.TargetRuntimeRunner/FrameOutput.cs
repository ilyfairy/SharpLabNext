using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace SharpLabNext.TargetRuntimeRunner
{
    internal static class RuntimePlatform
    {
        public static bool IsWindows
        {
            get
            {
                PlatformID platform = Environment.OSVersion.Platform;
                return platform == PlatformID.Win32NT ||
                    platform == PlatformID.Win32Windows ||
                    platform == PlatformID.Win32S ||
                    platform == PlatformID.WinCE;
            }
        }

        public static bool IsUnix
        {
            get
            {
                int platform = (int)Environment.OSVersion.Platform;
                return platform == 4 || platform == 6 || platform == 128;
            }
        }
    }

    internal static class FrameOutput
    {
        private const int StandardOutputHandle = -11;
        private const uint DuplicateSameAccess = 0x00000002;

        public static Stream Open()
        {
            if (RuntimePlatform.IsUnix)
            {
                int descriptor = DuplicateUnix(1);
                if (descriptor < 0)
                    throw new IOException("The runtime frame stdout descriptor could not be duplicated.");
                return NativeFrameStream.ForUnixDescriptor(descriptor);
            }
            if (!RuntimePlatform.IsWindows)
                throw new PlatformNotSupportedException("Runtime frames support Linux and Windows only.");

            IntPtr currentProcess = GetCurrentProcess();
            IntPtr standardOutput = GetStdHandle(StandardOutputHandle);
            if (standardOutput == IntPtr.Zero || standardOutput == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The runtime frame stdout handle is invalid.");
            IntPtr duplicate;
            if (!DuplicateHandle(
                currentProcess,
                standardOutput,
                currentProcess,
                out duplicate,
                0,
                false,
                DuplicateSameAccess))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The runtime frame stdout handle could not be duplicated.");
            }
            return NativeFrameStream.ForWindowsHandle(duplicate);
        }

        [DllImport("libc", EntryPoint = "dup", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DuplicateUnix(int descriptor);

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
    }

    internal sealed class NativeFrameStream : Stream
    {
        private readonly bool _windows;
        private int _unixDescriptor;
        private IntPtr _windowsHandle;
        private bool _disposed;

        private NativeFrameStream(int unixDescriptor, IntPtr windowsHandle, bool windows)
        {
            _unixDescriptor = unixDescriptor;
            _windowsHandle = windowsHandle;
            _windows = windows;
        }

        public static Stream ForUnixDescriptor(int descriptor)
        {
            return new NativeFrameStream(descriptor, IntPtr.Zero, false);
        }

        public static Stream ForWindowsHandle(IntPtr handle)
        {
            return new NativeFrameStream(-1, handle, true);
        }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return !_disposed; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
            ThrowIfDisposed();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset));

            int written = 0;
            while (written < count)
            {
                int remaining = count - written;
                var segment = new byte[remaining];
                Buffer.BlockCopy(buffer, offset + written, segment, 0, remaining);
                int result;
                if (_windows)
                {
                    if (!WriteFile(_windowsHandle, segment, remaining, out result, IntPtr.Zero))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "The runtime frame write failed.");
                    }
                }
                else
                {
                    long nativeResult = WriteUnix(
                        _unixDescriptor,
                        segment,
                        new UIntPtr((uint)remaining)).ToInt64();
                    result = nativeResult > int.MaxValue ? -1 : (int)nativeResult;
                }

                if (result <= 0 || result > remaining)
                    throw new IOException("The runtime frame write was incomplete.");
                written += result;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_windows)
                {
                    if (_windowsHandle != IntPtr.Zero && _windowsHandle != new IntPtr(-1))
                        _ = CloseHandle(_windowsHandle);
                    _windowsHandle = IntPtr.Zero;
                }
                else if (_unixDescriptor >= 0)
                {
                    _ = CloseUnix(_unixDescriptor);
                    _unixDescriptor = -1;
                }
            }
            base.Dispose(disposing);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        [DllImport("libc", EntryPoint = "write", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern IntPtr WriteUnix(int descriptor, byte[] buffer, UIntPtr count);

        [DllImport("libc", EntryPoint = "close", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern int CloseUnix(int descriptor);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(
            IntPtr handle,
            byte[] buffer,
            int bytesToWrite,
            out int bytesWritten,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

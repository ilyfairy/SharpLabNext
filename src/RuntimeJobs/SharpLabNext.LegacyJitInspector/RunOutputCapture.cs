using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SharpLabNext.LegacyJitInspector
{
    internal sealed class RunOutputCapture : IDisposable
    {
        private const int DefaultMaximumOutputBytes = 16 * 1024 * 1024;
        private const int PumpBufferSize = 64 * 1024;
        private const int PumpIntervalMilliseconds = 10;
        // libc open(2) flags are different from the UCRT _O_* flags below.
        // Keep them separate: O_CREAT is 0x40 and O_APPEND is 0x400 on
        // Linux, while the Windows values are 0x100 and 0x8 respectively.
        private const int LinuxWriteOnly = 0x0001;
        private const int LinuxCreate = 0x0040;
        private const int LinuxTruncate = 0x0200;
        private const int LinuxAppend = 0x0400;
        private const int UcrtWriteOnly = 0x0001;
        private const int UcrtCreate = 0x0100;
        private const int UcrtTruncate = 0x0200;
        private const int UcrtAppend = 0x0008;
        private const int Binary = 0x8000;
        private const int OwnerRead = 0x0100;
        private const int OwnerWrite = 0x0080;
        private const int StandardOutputHandle = -11;
        private const int StandardErrorHandle = -12;

        private readonly string _stdoutPath;
        private readonly string _stderrPath;
        private readonly StreamWriter _stdoutWriter;
        private readonly StreamWriter _stderrWriter;
        private readonly RuntimeFrameWriter _frameWriter;
        private readonly long _maximumOutputBytes;
        private readonly Thread _pumpThread;
        private readonly object _pumpGate = new object();
        private long _stdoutOffset;
        private long _stderrOffset;
        private long _observedOutputBytes;
        private int _stopRequested;
        private bool _outputLimitReached;
        private bool _emitted;

        private RunOutputCapture(string stdoutPath, string stderrPath, RuntimeFrameWriter frameWriter)
        {
            _stdoutPath = stdoutPath;
            _stderrPath = stderrPath;
            _frameWriter = frameWriter;
            _maximumOutputBytes = ReadMaximumOutputBytes();
            // Route managed writes through the redirected descriptors too. A
            // second FileMode.Append handle has an independent file position,
            // while Console.OpenStandardOutput can retain the process-startup
            // pipe on older CoreCLR. Writing fd 1/2 directly gives managed,
            // CRT and Win32 output one shared capture position.
            _stdoutWriter = CreateManagedWriter(new NativeDescriptorStream(1));
            _stderrWriter = CreateManagedWriter(new NativeDescriptorStream(2));
            Console.SetOut(_stdoutWriter);
            Console.SetError(_stderrWriter);
            _pumpThread = new Thread(PumpOutput)
            {
                IsBackground = true,
                Name = "SharpLabNext.LegacyJitInspector.OutputPump"
            };
            _pumpThread.Start();
        }

        public static RunOutputCapture Start(RuntimeFrameWriter frameWriter)
        {
            if (frameWriter == null)
                throw new ArgumentNullException(nameof(frameWriter));
            string token = Guid.NewGuid().ToString("N");
            string directory = ResolveCaptureDirectory(Environment.GetEnvironmentVariable("SHARPLABNEXT_CAPTURE_DIRECTORY"), RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            string stdoutPath = Path.Combine(directory, "sharplabnext-run-" + token + ".stdout");
            string stderrPath = Path.Combine(directory, "sharplabnext-run-" + token + ".stderr");
            RedirectNativeOutput(stdoutPath, stderrPath);
            return new RunOutputCapture(stdoutPath, stderrPath, frameWriter);
        }

        internal static string ResolveCaptureDirectory(string configuredDirectory, bool isWindows)
        {
            if (string.IsNullOrWhiteSpace(configuredDirectory))
                return Path.GetTempPath();
            if (!isWindows || !string.Equals(configuredDirectory, @"Z:\tmp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SHARPLABNEXT_CAPTURE_DIRECTORY must be the fixed Wine path 'Z:\\tmp'.");
            }
            return configuredDirectory;
        }

        public void Emit(RuntimeFrameWriter writer)
        {
            if (_emitted)
                return;

            // Stop the tailer only after the user entry point has returned.
            // The final drain below closes the small race with a native write
            // that completed just before the entry point returned.
            Interlocked.Exchange(ref _stopRequested, 1);
            _pumpThread.Join();
            _stdoutWriter.Flush();
            _stderrWriter.Flush();
            NativeStreamFlusher.FlushAll();
            DrainFile(RuntimeFrameKind.Stdout, _stdoutPath, ref _stdoutOffset);
            DrainFile(RuntimeFrameKind.Stderr, _stderrPath, ref _stderrOffset);
            _emitted = true;
        }

        public void Dispose()
        {
            try
            {
                Interlocked.Exchange(ref _stopRequested, 1);
                if (_pumpThread.IsAlive)
                    _pumpThread.Join();
                _stdoutWriter.Flush();
                _stderrWriter.Flush();
            }
            catch (ObjectDisposedException) { }

            // Do not restore fd 1/2 before process exit. A user thread that
            // keeps running after Main must not regain access to the frame
            // pipe and inject raw bytes into the supervisor protocol. Null
            // redirection is safe and also lets us release the temp files.
            TryRedirectNativeOutputToNull();
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            _stdoutWriter.Dispose();
            _stderrWriter.Dispose();
            TryDelete(_stdoutPath);
            TryDelete(_stderrPath);
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Windows CRT handles may not allow delete-sharing. The
                // helper is short-lived, so the process will release those
                // handles; never turn a completed user run into a cleanup
                // failure or expose a second unframed error.
            }
            catch (UnauthorizedAccessException) { }
        }

        private static void TryRedirectNativeOutputToNull()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    int descriptor = OpenLinux("/dev/null", LinuxWriteOnly, Convert.ToInt32("600", 8));
                    if (descriptor >= 0)
                    {
                        _ = DuplicateLinux(descriptor, 1);
                        _ = DuplicateLinux(descriptor, 2);
                        _ = CloseLinux(descriptor);
                    }
                    return;
                }

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return;

                int ucrtDescriptor = OpenUcrt("NUL", UcrtWriteOnly | UcrtCreate | Binary, OwnerRead | OwnerWrite);
                if (ucrtDescriptor >= 0)
                {
                    _ = DuplicateDescriptorUcrt(ucrtDescriptor, 1);
                    _ = DuplicateDescriptorUcrt(ucrtDescriptor, 2);
                    IntPtr nullHandle = GetOsFileHandleUcrt(ucrtDescriptor);
                    if (nullHandle != IntPtr.Zero && nullHandle != new IntPtr(-1))
                    {
                        _ = SetStdHandle(StandardOutputHandle, nullHandle);
                        _ = SetStdHandle(StandardErrorHandle, nullHandle);
                    }
                    _ = CloseUcrt(ucrtDescriptor);
                }

                // UCRT and the compatibility MSVCRT keep independent fd
                // tables. Redirect the latter as well when available.
                int msvcrtDescriptor = OpenMsvcrt("NUL", UcrtWriteOnly | Binary, OwnerRead | OwnerWrite);
                if (msvcrtDescriptor >= 0)
                {
                    _ = DuplicateDescriptorMsvcrt(msvcrtDescriptor, 1);
                    _ = DuplicateDescriptorMsvcrt(msvcrtDescriptor, 2);
                    _ = CloseMsvcrt(msvcrtDescriptor);
                }
            }
            catch
            {
                // Cleanup is best effort. The process boundary still closes
                // any remaining descriptors before a later job can observe
                // them.
            }
        }

        private static StreamWriter CreateManagedWriter(Stream stream)
        {
            return new StreamWriter(stream, new UTF8Encoding(false), 4 * 1024)
            {
                AutoFlush = true
            };
        }

        private sealed class NativeDescriptorStream : Stream
        {
            private readonly int _descriptor;

            public NativeDescriptorStream(int descriptor)
            {
                _descriptor = descriptor;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
                // write(2)/_write commit directly to the redirected file. The
                // StreamWriter owns the only managed buffer.
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (buffer == null)
                    throw new ArgumentNullException(nameof(buffer));
                if (offset < 0)
                    throw new ArgumentOutOfRangeException(nameof(offset));
                if (count < 0)
                    throw new ArgumentOutOfRangeException(nameof(count));
                if (offset > buffer.Length - count)
                    throw new ArgumentException("Offset and count exceed the buffer length.");
                if (count == 0)
                    return;

                byte[] bytes;
                if (offset == 0 && count == buffer.Length)
                {
                    bytes = buffer;
                }
                else
                {
                    bytes = new byte[count];
                    Buffer.BlockCopy(buffer, offset, bytes, 0, count);
                }

                int written = 0;
                while (written < count)
                {
                    int remaining = count - written;
                    byte[] segment;
                    if (written == 0)
                    {
                        segment = bytes;
                    }
                    else
                    {
                        segment = new byte[remaining];
                        Buffer.BlockCopy(bytes, written, segment, 0, remaining);
                    }

                    long result;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        result = WriteLinux(_descriptor, segment, new UIntPtr((uint)remaining)).ToInt64();
                    }
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        result = WriteUcrt(_descriptor, segment, (uint)remaining);
                    }
                    else
                    {
                        throw new PlatformNotSupportedException("Native descriptor output supports Linux and Windows only.");
                    }

                    if (result <= 0 || result > remaining)
                        throw new IOException("The redirected native output write failed.");
                    written += (int)result;
                }
            }
        }

        private void PumpOutput()
        {
            try
            {
                using (var stdout = OpenCaptureReader(_stdoutPath))
                using (var stderr = OpenCaptureReader(_stderrPath))
                {
                    while (Thread.VolatileRead(ref _stopRequested) == 0)
                    {
                        var progressed = DrainAvailable(stdout, RuntimeFrameKind.Stdout, ref _stdoutOffset) |
                            DrainAvailable(stderr, RuntimeFrameKind.Stderr, ref _stderrOffset);
                        if (!progressed)
                            Thread.Sleep(PumpIntervalMilliseconds);
                    }
                }
            }
            catch
            {
                // The supervisor owns the hard output/deadline limits.  If a
                // filesystem or pipe disappears while the user is running,
                // stop the pump and let the normal process/container result
                // report the terminal state.
                Interlocked.Exchange(ref _stopRequested, 1);
            }
        }

        private bool DrainAvailable(FileStream stream, RuntimeFrameKind kind, ref long offset, bool allowAfterStop = false)
        {
            bool progressed = false;
            while (allowAfterStop || Thread.VolatileRead(ref _stopRequested) == 0)
            {
                long length = stream.Length;
                if (length <= offset)
                    break;

                stream.Position = offset;
                int count = (int)Math.Min(PumpBufferSize, length - offset);
                var bytes = new byte[count];
                int read = stream.Read(bytes, 0, count);
                if (read <= 0)
                    break;

                offset += read;
                progressed = true;
                EmitChunk(kind, bytes, read);
                if (_outputLimitReached)
                    break;
            }
            return progressed;
        }

        private void DrainFile(RuntimeFrameKind kind, string path, ref long offset)
        {
            if (!File.Exists(path) || _outputLimitReached)
                return;

            using (var stream = OpenCaptureReader(path))
            {
                DrainAvailable(stream, kind, ref offset, allowAfterStop: true);
            }
        }

        private void EmitChunk(RuntimeFrameKind kind, byte[] bytes, int count)
        {
            lock (_pumpGate)
            {
                if (_outputLimitReached || count <= 0)
                    return;

                long remaining = _maximumOutputBytes - _observedOutputBytes;
                if (remaining <= 0)
                {
                    // A previous pump read can end exactly on the budget
                    // boundary. This call proves that more source bytes exist,
                    // so surface one of them instead of silently stopping at
                    // exactly the configured limit.
                    _frameWriter.Write(kind, bytes, 0, 1);
                    _observedOutputBytes++;
                    MarkOutputLimitReached();
                    return;
                }

                // Emit one byte beyond the configured budget when the source
                // crosses it.  Supervisor then observes the overflow and can
                // terminate the container immediately instead of waiting for
                // the user entry point to return.
                long overflowBoundary = remaining == long.MaxValue
                    ? long.MaxValue : remaining + 1;
                long requested = Math.Min((long)count, overflowBoundary);
                int emitCount = (int)Math.Min(requested, int.MaxValue);
                _frameWriter.Write(kind, bytes, 0, emitCount);
                _observedOutputBytes += emitCount;
                if (_observedOutputBytes > _maximumOutputBytes)
                    MarkOutputLimitReached();
            }
        }

        private void MarkOutputLimitReached()
        {
            if (_outputLimitReached)
                return;
            _outputLimitReached = true;
            TryRedirectNativeOutputToNull();
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            Interlocked.Exchange(ref _stopRequested, 1);
        }

        private static FileStream OpenCaptureReader(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, PumpBufferSize, FileOptions.SequentialScan);

        private static long ReadMaximumOutputBytes()
        {
            var value = Environment.GetEnvironmentVariable("SHARPLABNEXT_MAX_OUTPUT_BYTES");
            if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return parsed;
            }
            return DefaultMaximumOutputBytes;
        }

        private static void RedirectNativeOutput(string stdoutPath, string stderrPath)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                RedirectLinux(stdoutPath, 1, false);
                RedirectLinux(stderrPath, 2, false);
                return;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                RedirectWindows(stdoutPath, StandardOutputHandle, false);
                RedirectWindows(stderrPath, StandardErrorHandle, false);
                return;
            }
            throw new PlatformNotSupportedException("Run output capture supports Linux and Windows only.");
        }

        private static void RedirectLinux(string path, int destination, bool append)
        {
            int flags = LinuxWriteOnly | LinuxCreate | (append ? LinuxAppend : LinuxTruncate);
            int descriptor = OpenLinux(path, flags, Convert.ToInt32("600", 8));
            if (descriptor < 0)
                throw new IOException("The Linux run output file could not be opened (errno " + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
            try
            {
                // POSIX dup2 returns the destination descriptor on success,
                // not zero (unlike the Windows _dup2 wrappers).
                if (DuplicateLinux(descriptor, destination) < 0)
                    throw new IOException("The Linux native output descriptor could not be redirected (errno " + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
            }
            catch
            {
                _ = CloseLinux(descriptor);
                throw;
            }
            if (CloseLinux(descriptor) != 0)
                throw new IOException("The Linux run output descriptor could not be closed.");
        }

        private static void RedirectWindows(string path, int standardHandle, bool append)
        {
            int ucrtFlags = UcrtWriteOnly | UcrtCreate | Binary | (append ? UcrtAppend : UcrtTruncate);
            int ucrtDescriptor = OpenUcrt(path, ucrtFlags, OwnerRead | OwnerWrite);
            if (ucrtDescriptor < 0)
                throw new IOException("The Windows run output file could not be opened through ucrtbase.");
            int msvcrtDescriptor = -1;
            try
            {
                if (DuplicateDescriptorUcrt(ucrtDescriptor, standardHandle == StandardOutputHandle ? 1 : 2) != 0)
                    throw new IOException("ucrtbase could not redirect the Windows native output descriptor.");

                // CoreCLR uses UCRT, while user P/Invokes can use MSVCRT.
                // Duplicate the already-open kernel handle instead of opening
                // the path a second time (Windows sharing modes can reject it).
                IntPtr ucrtHandle = GetOsFileHandleUcrt(standardHandle == StandardOutputHandle ? 1 : 2);
                if (ucrtHandle == IntPtr.Zero || ucrtHandle == new IntPtr(-1) || !DuplicateHandle(GetCurrentProcess(), ucrtHandle, GetCurrentProcess(), out IntPtr msvcrtHandle, 0, false, DuplicateSameAccess))
                {
                    throw new IOException("The Windows output file handle could not be duplicated for msvcrt.");
                }
                msvcrtDescriptor = OpenOsHandleMsvcrt(msvcrtHandle, Binary | UcrtWriteOnly);
                if (msvcrtDescriptor < 0)
                {
                    _ = CloseHandle(msvcrtHandle);
                }
                else if (DuplicateDescriptorMsvcrt(msvcrtDescriptor, standardHandle == StandardOutputHandle ? 1 : 2) != 0)
                {
                    // Some modern compatibility msvcrt builds expose no live
                    // fd 1/2 table. In that case _write on those descriptors
                    // fails; Win32 and UCRT output remain redirected below.
                    _ = CloseMsvcrt(msvcrtDescriptor);
                    msvcrtDescriptor = -1;
                }

                IntPtr outputHandle = GetOsFileHandleUcrt(standardHandle == StandardOutputHandle ? 1 : 2);
                if (outputHandle == IntPtr.Zero || outputHandle == new IntPtr(-1) || !SetStdHandle(standardHandle, outputHandle))
                {
                    throw new IOException("The Windows standard output handle could not be redirected.");
                }
            }
            catch
            {
                if (msvcrtDescriptor >= 0)
                    _ = CloseMsvcrt(msvcrtDescriptor);
                _ = CloseUcrt(ucrtDescriptor);
                throw;
            }
            if (msvcrtDescriptor >= 0)
            {
                // Wine's msvcrt compatibility layer can report EBADF after
                // _dup2 has installed the descriptor in fd 1/2. The target
                // descriptor is still redirected, so a failed cleanup close
                // must not turn a successful run into a protocol exception.
                _ = CloseMsvcrt(msvcrtDescriptor);
            }
            if (CloseUcrt(ucrtDescriptor) != 0)
                throw new IOException("ucrtbase could not close the run output descriptor.");
        }

        [DllImport("libc", EntryPoint = "open", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
        private static extern int OpenLinux([MarshalAs(UnmanagedType.LPStr)] string path, int flags, int mode);

        [DllImport("libc", EntryPoint = "dup2", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern int DuplicateLinux(int source, int destination);

        [DllImport("libc", EntryPoint = "close", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern int CloseLinux(int descriptor);

        [DllImport("libc", EntryPoint = "write", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern IntPtr WriteLinux(int descriptor, byte[] buffer, UIntPtr count);

        [DllImport("ucrtbase.dll", EntryPoint = "_wopen", CallingConvention = CallingConvention.Cdecl)]
        private static extern int OpenUcrt([MarshalAs(UnmanagedType.LPWStr)] string path, int flags, int permissionMode);

        [DllImport("ucrtbase.dll", EntryPoint = "_dup2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DuplicateDescriptorUcrt(int source, int destination);

        [DllImport("ucrtbase.dll", EntryPoint = "_get_osfhandle", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetOsFileHandleUcrt(int descriptor);

        [DllImport("ucrtbase.dll", EntryPoint = "_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CloseUcrt(int descriptor);

        [DllImport("ucrtbase.dll", EntryPoint = "_write", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WriteUcrt(int descriptor, byte[] buffer, uint count);

        [DllImport("msvcrt.dll", EntryPoint = "_open_osfhandle", CallingConvention = CallingConvention.Cdecl)]
        private static extern int OpenOsHandleMsvcrt(IntPtr handle, int flags);

        [DllImport("msvcrt.dll", EntryPoint = "_wopen", CallingConvention = CallingConvention.Cdecl)]
        private static extern int OpenMsvcrt([MarshalAs(UnmanagedType.LPWStr)] string path, int flags, int permissionMode);

        [DllImport("msvcrt.dll", EntryPoint = "_dup2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DuplicateDescriptorMsvcrt(int source, int destination);

        [DllImport("msvcrt.dll", EntryPoint = "_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CloseMsvcrt(int descriptor);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetStdHandle(int standardHandle, IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint DuplicateSameAccess = 0x00000002;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);
    }
}

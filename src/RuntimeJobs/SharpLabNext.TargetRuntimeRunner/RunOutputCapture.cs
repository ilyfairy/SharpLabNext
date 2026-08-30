using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SharpLabNext.TargetRuntimeRunner
{
    internal sealed class RunOutputCapture : IDisposable
    {
        private const int DefaultMaximumOutputBytes = 16 * 1024 * 1024;
        private const int PumpBufferSize = 64 * 1024;
        private const int PumpIntervalMilliseconds = 10;
        private const int UnixWriteOnly = 0x0001;
        private const int UnixCreate = 0x0040;
        private const int UnixTruncate = 0x0200;
        private const int WindowsWriteOnly = 0x0001;
        private const int WindowsCreate = 0x0100;
        private const int WindowsTruncate = 0x0200;
        private const int WindowsAppend = 0x0008;
        private const int WindowsBinary = 0x8000;
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
            _stdoutWriter = CreateManagedWriter(new NativeDescriptorStream(1));
            _stderrWriter = CreateManagedWriter(new NativeDescriptorStream(2));
            Console.SetOut(_stdoutWriter);
            Console.SetError(_stderrWriter);
            _pumpThread = new Thread(PumpOutput);
            _pumpThread.IsBackground = true;
            _pumpThread.Name = "SharpLabNext.TargetRuntimeRunner.OutputPump";
            _pumpThread.Start();
        }

        public static RunOutputCapture Start(RuntimeFrameWriter frameWriter)
        {
            if (frameWriter == null)
                throw new ArgumentNullException(nameof(frameWriter));
            string token = Guid.NewGuid().ToString("N");
            string directory = ResolveCaptureDirectory(Environment.GetEnvironmentVariable("SHARPLABNEXT_CAPTURE_DIRECTORY"), RuntimePlatform.IsWindows);
            string stdoutPath = Path.Combine(directory, "sharplabnext-target-run-" + token + ".stdout");
            string stderrPath = Path.Combine(directory, "sharplabnext-target-run-" + token + ".stderr");
            RedirectNativeOutput(stdoutPath, stderrPath);
            return new RunOutputCapture(stdoutPath, stderrPath, frameWriter);
        }

        internal static string ResolveCaptureDirectory(string configuredDirectory, bool isWindows)
        {
            if (configuredDirectory == null || configuredDirectory.Trim().Length == 0)
                return Path.GetTempPath();
            if (!isWindows || !string.Equals(configuredDirectory, @"Z:\tmp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SHARPLABNEXT_CAPTURE_DIRECTORY must be the fixed Wine path 'Z:\\tmp'.");
            }
            return configuredDirectory;
        }

        public void Emit()
        {
            if (_emitted)
                return;
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
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            _stdoutWriter.Dispose();
            _stderrWriter.Dispose();
            TryDelete(_stdoutPath);
            TryDelete(_stderrPath);
        }

        private static StreamWriter CreateManagedWriter(Stream stream)
        {
            var writer = new StreamWriter(stream, new UTF8Encoding(false), 4 * 1024);
            writer.AutoFlush = true;
            return writer;
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
                        bool progressed = DrainAvailable(stdout, RuntimeFrameKind.Stdout, ref _stdoutOffset, false);
                        progressed = DrainAvailable(stderr, RuntimeFrameKind.Stderr, ref _stderrOffset, false) || progressed;
                        if (!progressed)
                            Thread.Sleep(PumpIntervalMilliseconds);
                    }
                }
            }
            catch
            {
                Interlocked.Exchange(ref _stopRequested, 1);
            }
        }

        private bool DrainAvailable(FileStream stream, RuntimeFrameKind kind, ref long offset, bool allowAfterStop)
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
                DrainAvailable(stream, kind, ref offset, true);
        }

        private void EmitChunk(RuntimeFrameKind kind, byte[] bytes, int count)
        {
            lock (_pumpGate)
            {
                if (_outputLimitReached || count <= 0)
                    return;
                long remaining = _maximumOutputBytes - _observedOutputBytes;
                long requested = remaining <= 0
                    ? 1 : Math.Min((long)count, remaining == long.MaxValue ? long.MaxValue : remaining + 1);
                int emitCount = (int)Math.Min(requested, int.MaxValue);
                _frameWriter.Write(kind, bytes, 0, emitCount);
                _observedOutputBytes += emitCount;
                if (_observedOutputBytes > _maximumOutputBytes)
                {
                    _outputLimitReached = true;
                    Console.SetOut(TextWriter.Null);
                    Console.SetError(TextWriter.Null);
                    Interlocked.Exchange(ref _stopRequested, 1);
                }
            }
        }

        private static FileStream OpenCaptureReader(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, PumpBufferSize, FileOptions.SequentialScan);
        }

        private static long ReadMaximumOutputBytes()
        {
            string value = Environment.GetEnvironmentVariable("SHARPLABNEXT_MAX_OUTPUT_BYTES");
            long parsed;
            if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parsed) && parsed > 0)
            {
                return parsed;
            }
            return DefaultMaximumOutputBytes;
        }

        private static void RedirectNativeOutput(string stdoutPath, string stderrPath)
        {
            if (RuntimePlatform.IsUnix)
            {
                RedirectUnix(stdoutPath, 1);
                RedirectUnix(stderrPath, 2);
                return;
            }
            if (RuntimePlatform.IsWindows)
            {
                RedirectWindows(stdoutPath, 1, StandardOutputHandle);
                RedirectWindows(stderrPath, 2, StandardErrorHandle);
                return;
            }
            throw new PlatformNotSupportedException("Run output capture supports Linux and Windows only.");
        }

        private static void RedirectUnix(string path, int destination)
        {
            int descriptor = OpenUnix(path, UnixWriteOnly | UnixCreate | UnixTruncate, Convert.ToInt32("600", 8));
            if (descriptor < 0)
                throw new IOException("The Unix run output file could not be opened.");
            try
            {
                if (DuplicateUnix(descriptor, destination) < 0)
                    throw new IOException("The Unix output descriptor could not be redirected.");
            }
            finally
            {
                _ = CloseUnix(descriptor);
            }
        }

        private static void RedirectWindows(string path, int destination, int standardHandle)
        {
            int flags = WindowsWriteOnly | WindowsCreate | WindowsTruncate | WindowsAppend | WindowsBinary;
            int descriptor = OpenMsvcrt(path, flags, OwnerRead | OwnerWrite);
            if (descriptor < 0)
                throw new IOException("The Windows run output file could not be opened through msvcrt.");
            try
            {
                if (DuplicateDescriptorMsvcrt(descriptor, destination) != 0)
                    throw new IOException("msvcrt could not redirect the Windows output descriptor.");
                IntPtr handle = GetOsFileHandleMsvcrt(destination);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !SetStdHandle(standardHandle, handle))
                    throw new IOException("The Windows standard output handle could not be redirected.");
            }
            finally
            {
                _ = CloseMsvcrt(descriptor);
            }
            TryRedirectUcrt(path, destination, standardHandle);
        }

        private static void TryRedirectUcrt(string path, int destination, int standardHandle)
        {
            try
            {
                int descriptor = OpenUcrt(path, WindowsWriteOnly | WindowsCreate | WindowsAppend | WindowsBinary, OwnerRead | OwnerWrite);
                if (descriptor < 0)
                    return;
                try
                {
                    if (DuplicateDescriptorUcrt(descriptor, destination) != 0)
                        return;
                    IntPtr handle = GetOsFileHandleUcrt(destination);
                    if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                        SetStdHandle(standardHandle, handle);
                }
                finally
                {
                    _ = CloseUcrt(descriptor);
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
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
                    if (RuntimePlatform.IsUnix)
                    {
                        long nativeResult = WriteUnix(_descriptor, segment, new UIntPtr((uint)remaining)).ToInt64();
                        result = nativeResult > int.MaxValue ? -1 : (int)nativeResult;
                    }
                    else
                    {
                        result = WriteMsvcrt(_descriptor, segment, (uint)remaining);
                    }
                    if (result <= 0 || result > remaining)
                        throw new IOException("The redirected native output write failed.");
                    written += result;
                }
            }
        }

        private static class NativeStreamFlusher
        {
            public static void FlushAll()
            {
                if (RuntimePlatform.IsUnix)
                {
                    _ = FlushUnix(IntPtr.Zero);
                    return;
                }
                _ = FlushMsvcrt(IntPtr.Zero);
                try
                {
                    _ = FlushUcrt(IntPtr.Zero);
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
        }

        [DllImport("libc", EntryPoint = "open", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
        private static extern int OpenUnix([MarshalAs(UnmanagedType.LPStr)] string path, int flags, int mode);
        [DllImport("libc", EntryPoint = "dup2", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern int DuplicateUnix(int source, int destination);
        [DllImport("libc", EntryPoint = "close", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern int CloseUnix(int descriptor);
        [DllImport("libc", EntryPoint = "write", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        private static extern IntPtr WriteUnix(int descriptor, byte[] buffer, UIntPtr count);
        [DllImport("libc", EntryPoint = "fflush", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FlushUnix(IntPtr stream);

        [DllImport("msvcrt.dll", EntryPoint = "_wopen", CallingConvention = CallingConvention.Cdecl)]
        private static extern int OpenMsvcrt([MarshalAs(UnmanagedType.LPWStr)] string path, int flags, int permissionMode);
        [DllImport("msvcrt.dll", EntryPoint = "_dup2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DuplicateDescriptorMsvcrt(int source, int destination);
        [DllImport("msvcrt.dll", EntryPoint = "_get_osfhandle", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetOsFileHandleMsvcrt(int descriptor);
        [DllImport("msvcrt.dll", EntryPoint = "_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CloseMsvcrt(int descriptor);
        [DllImport("msvcrt.dll", EntryPoint = "_write", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WriteMsvcrt(int descriptor, byte[] buffer, uint count);
        [DllImport("msvcrt.dll", EntryPoint = "fflush", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FlushMsvcrt(IntPtr stream);

        [DllImport("ucrtbase.dll", EntryPoint = "_wopen", CallingConvention = CallingConvention.Cdecl)]
        private static extern int OpenUcrt([MarshalAs(UnmanagedType.LPWStr)] string path, int flags, int permissionMode);
        [DllImport("ucrtbase.dll", EntryPoint = "_dup2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DuplicateDescriptorUcrt(int source, int destination);
        [DllImport("ucrtbase.dll", EntryPoint = "_get_osfhandle", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetOsFileHandleUcrt(int descriptor);
        [DllImport("ucrtbase.dll", EntryPoint = "_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CloseUcrt(int descriptor);
        [DllImport("ucrtbase.dll", EntryPoint = "fflush", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FlushUcrt(IntPtr stream);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetStdHandle(int standardHandle, IntPtr handle);
    }
}

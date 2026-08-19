using System;
using System.IO;
using System.Text;

namespace SharpLabNext.LegacyJitInspector
{
    internal enum RuntimeFrameKind : byte
    {
        Stdout = 1,
        Stderr = 2,
        Exception = 6,
        Exit = 7,
        JitAssembly = 9,
        JitSummary = 10
    }

    internal sealed class RuntimeFrameWriter : IDisposable
    {
        private const int HeaderSize = 18;
        private const int MaximumPayloadBytes = 4 * 1024 * 1024;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SLNR");
        private readonly Stream _stream;
        private readonly object _gate = new object();
        private long _sequence;

        public RuntimeFrameWriter(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public void Write(RuntimeFrameKind kind, byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            Write(kind, payload, 0, payload.Length);
        }

        public void Write(RuntimeFrameKind kind, byte[] payload, int offset, int count)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (offset < 0 || count < 0 || offset > payload.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count > MaximumPayloadBytes)
                throw new InvalidDataException("Runtime frame payload exceeds the protocol limit.");

            lock (_gate)
            {
                var frame = new byte[HeaderSize + count];
                Buffer.BlockCopy(Magic, 0, frame, 0, Magic.Length);
                frame[4] = 1;
                frame[5] = (byte)kind;
                WriteInt64LittleEndian(frame, 6, checked(++_sequence));
                WriteInt32LittleEndian(frame, 14, count);
                Buffer.BlockCopy(payload, offset, frame, HeaderSize, count);

                byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(frame));
                _stream.Write(encoded, 0, encoded.Length);
                _stream.WriteByte((byte)'\n');
                _stream.Flush();
            }
        }

        public void Dispose()
        {
            lock (_gate)
                _stream.Flush();
        }

        private static void WriteInt64LittleEndian(byte[] bytes, int offset, long value)
        {
            unchecked
            {
                for (int index = 0; index < 8; index++)
                    bytes[offset + index] = (byte)(value >> (index * 8));
            }
        }

        private static void WriteInt32LittleEndian(byte[] bytes, int offset, int value)
        {
            unchecked
            {
                for (int index = 0; index < 4; index++)
                    bytes[offset + index] = (byte)(value >> (index * 8));
            }
        }
    }
}

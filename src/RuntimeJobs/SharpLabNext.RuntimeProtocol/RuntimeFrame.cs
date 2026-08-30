using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace SharpLabNext.RuntimeProtocol;

public enum RuntimeFrameKind : byte
{
    Stdout = 1,
    Stderr = 2,
    Inspection = 3,
    MemoryGraph = 4,
    Flow = 5,
    Exception = 6,
    Exit = 7,
    ProtocolError = 8,
    JitAssembly = 9,
    JitSummary = 10
}

public sealed record RuntimeFrame(long Sequence, RuntimeFrameKind Kind, ReadOnlyMemory<byte> Payload);

public enum RuntimeFrameTransport
{
    Binary,
    Base64Line
}

public static class RuntimeFrameCodec
{
    private static ReadOnlySpan<byte> Magic => "SLNR"u8;
    private const byte ProtocolVersion = 1;
    public const int HeaderSize = 18;
    public const int DefaultMaximumPayloadBytes = 4 * 1024 * 1024;

    public static void Write(Stream stream, RuntimeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = CreateHeader(frame);
        stream.Write(header);
        stream.Write(frame.Payload.Span);
        stream.Flush();
    }

    public static async ValueTask WriteAsync(Stream stream, RuntimeFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = CreateHeader(frame);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(frame.Payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static byte[] CreateHeader(RuntimeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "Frame sequence must be positive.");
        }

        if (frame.Payload.Length > DefaultMaximumPayloadBytes)
        {
            throw new InvalidDataException("Runtime frame payload exceeds the protocol limit.");
        }

        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = ProtocolVersion;
        header[5] = (byte)frame.Kind;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6, 8), frame.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14, 4), frame.Payload.Length);
        return header;
    }

    public static async ValueTask<RuntimeFrame?> ReadAsync(Stream stream, int maximumPayloadBytes = DefaultMaximumPayloadBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);
        var header = new byte[HeaderSize];
        var firstRead = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (firstRead == 0)
        {
            return null;
        }

        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Runtime frame magic is invalid.");
        }

        if (header[4] != ProtocolVersion)
        {
            throw new InvalidDataException($"Runtime child protocol version {header[4]} is not supported.");
        }

        var kind = (RuntimeFrameKind)header[5];
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Runtime frame kind {(byte)kind} is not supported.");
        }

        var sequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6, 8));
        if (sequence <= 0)
        {
            throw new InvalidDataException("Runtime frame sequence must be positive.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(14, 4));
        if (length < 0 || length > maximumPayloadBytes)
        {
            throw new InvalidDataException($"Runtime frame payload length {length} is outside the 0..{maximumPayloadBytes} limit.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new RuntimeFrame(sequence, kind, payload);
    }
}

public sealed class RuntimeFrameLogReader(Stream stream)
{
    private const int ReadBufferSize = 8 * 1024;
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private int _readOffset;
    private int _readCount;

    public async ValueTask<RuntimeFrame?> ReadAsync(int maximumPayloadBytes = RuntimeFrameCodec.DefaultMaximumPayloadBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);
        var maximumEncodedBytes = checked(((maximumPayloadBytes + RuntimeFrameCodec.HeaderSize + 2) / 3) * 4);
        using var encoded = new MemoryStream(Math.Min(maximumEncodedBytes, ReadBufferSize));
        while (true)
        {
            if (_readOffset == _readCount)
            {
                _readCount = await stream.ReadAsync(_readBuffer, cancellationToken);
                _readOffset = 0;
                if (_readCount == 0)
                {
                    if (encoded.Length == 0)
                        return null;
                    throw new EndOfStreamException("Runtime log stream ended inside an encoded frame.");
                }
            }

            var available = _readBuffer.AsSpan(_readOffset, _readCount - _readOffset);
            var newline = available.IndexOf((byte)'\n');
            var length = newline >= 0 ? newline : available.Length;
            if (encoded.Length + length > maximumEncodedBytes)
                throw new InvalidDataException("Runtime encoded frame exceeds the protocol limit.");
            encoded.Write(available[..length]);
            _readOffset += length;
            if (newline >= 0)
            {
                _readOffset++;
                break;
            }
        }

        var encodedBytes = encoded.GetBuffer().AsSpan(0, checked((int)encoded.Length));
        if (encodedBytes.IsEmpty || encodedBytes.ContainsAnyExcept(Base64Characters))
            throw new InvalidDataException("Runtime encoded frame is not canonical base64.");
        byte[] frameBytes;
        try
        {
            frameBytes = Convert.FromBase64String(Encoding.ASCII.GetString(encodedBytes));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Runtime encoded frame is not valid base64.", exception);
        }

        var canonical = Convert.ToBase64String(frameBytes);
        if (!encodedBytes.SequenceEqual(Encoding.ASCII.GetBytes(canonical)))
            throw new InvalidDataException("Runtime encoded frame is not canonical base64.");

        if (frameBytes.Length > maximumPayloadBytes + RuntimeFrameCodec.HeaderSize)
            throw new InvalidDataException("Runtime decoded frame exceeds the protocol limit.");
        using var frameStream = new MemoryStream(frameBytes, writable: false);
        var frame = await RuntimeFrameCodec.ReadAsync(frameStream, maximumPayloadBytes, cancellationToken) ?? throw new InvalidDataException("Runtime encoded frame was empty.");
        if (frameStream.Position != frameStream.Length)
            throw new InvalidDataException("Runtime encoded line contains more than one frame.");
        return frame;
    }

    private static SearchValues<byte> Base64Characters { get; } =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/="u8);
}

public sealed class RuntimeFrameWriter(Stream stream, RuntimeFrameTransport transport = RuntimeFrameTransport.Binary) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequence;

    public void Write(RuntimeFrameKind kind, ReadOnlyMemory<byte> payload)
    {
        _gate.Wait();
        try
        {
            var sequence = checked(++_sequence);
            WriteFrame(new RuntimeFrame(sequence, kind, payload));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteAsync(RuntimeFrameKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sequence = checked(++_sequence);
            await WriteFrameAsync(new RuntimeFrame(sequence, kind, payload), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void WriteFrame(RuntimeFrame frame)
    {
        if (transport == RuntimeFrameTransport.Binary)
        {
            RuntimeFrameCodec.Write(stream, frame);
            return;
        }

        using var binary = new MemoryStream(RuntimeFrameCodec.HeaderSize + frame.Payload.Length);
        RuntimeFrameCodec.Write(binary, frame);
        var encoded = Convert.ToBase64String(binary.GetBuffer(), 0, checked((int)binary.Length));
        stream.Write(Encoding.ASCII.GetBytes(encoded));
        stream.WriteByte((byte)'\n');
        stream.Flush();
    }

    private async ValueTask WriteFrameAsync(RuntimeFrame frame, CancellationToken cancellationToken)
    {
        if (transport == RuntimeFrameTransport.Binary)
        {
            await RuntimeFrameCodec.WriteAsync(stream, frame, cancellationToken);
            return;
        }

        using var binary = new MemoryStream(RuntimeFrameCodec.HeaderSize + frame.Payload.Length);
        RuntimeFrameCodec.Write(binary, frame);
        var encoded = Convert.ToBase64String(binary.GetBuffer(), 0, checked((int)binary.Length));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(encoded), cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

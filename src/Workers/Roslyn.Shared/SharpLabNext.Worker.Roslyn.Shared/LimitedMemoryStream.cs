namespace SharpLabNext.Worker.Roslyn;

internal sealed class LimitedMemoryStream(int maxLength) : MemoryStream
{
    private readonly int _maxLength = maxLength > 0
        ? maxLength
        : throw new ArgumentOutOfRangeException(nameof(maxLength));

    public override void SetLength(long value)
    {
        EnsureLength(value);
        base.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureWrite(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWrite(buffer.Length);
        base.Write(buffer);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWrite(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWrite(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    private void EnsureWrite(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        long end;
        try
        {
            end = checked(Position + count);
        }
        catch (OverflowException)
        {
            throw new BuildOutputLimitExceededException("Compiler output exceeded the configured stream limit.");
        }

        EnsureLength(Math.Max(Length, end));
    }

    private void EnsureLength(long length)
    {
        if (length > _maxLength)
        {
            throw new BuildOutputLimitExceededException(
                $"Compiler output exceeds the configured {_maxLength} byte limit.");
        }
    }
}

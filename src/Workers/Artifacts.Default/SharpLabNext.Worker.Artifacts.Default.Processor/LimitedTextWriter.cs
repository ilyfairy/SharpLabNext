using System.Text;

namespace SharpLabNext.ArtifactProcessing;

internal sealed class LimitedTextWriter(TextWriter inner, long maximumCharacters) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public long CharactersWritten { get; private set; }

    public override void Write(char value)
    {
        Reserve(1);
        inner.Write(value);
    }

    public override void Write(string? value)
    {
        if (value is null)
            return;
        Reserve(value.Length);
        inner.Write(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        Reserve(count);
        inner.Write(buffer, index, count);
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        Reserve(buffer.Length);
        inner.Write(buffer);
    }

    public override Task FlushAsync() => inner.FlushAsync();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void Reserve(int count)
    {
        if (count < 0 || CharactersWritten + count > maximumCharacters)
            throw new ProcessorLimitExceededException();
        CharactersWritten += count;
    }
}

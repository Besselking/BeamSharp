namespace BeamSharp.Tests;

/// <summary>
/// Replays a canned byte sequence to the reader and throws away anything written.
/// <para>
/// A MemoryStream cannot stand in for a socket here: reads and writes share one position, so the
/// handshake would read back its own replies. This keeps the two directions separate.
/// </para>
/// </summary>
internal sealed class FakeDuplexStream(byte[] toRead) : Stream
{
    private int _position;

    /// <summary>What the handshake wrote, for tests that care.</summary>
    public MemoryStream Written { get; } = new();

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => toRead.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var available = Math.Min(buffer.Length, toRead.Length - _position);
        if (available <= 0) return 0;   // end of stream, which the handshake must handle

        toRead.AsSpan(_position, available).CopyTo(buffer);
        _position += available;
        return available;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => Written.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Written.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

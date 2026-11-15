namespace Gt5pOnlineServer.Services;

// Count traffic while forwarding immediately; movies must never accumulate in RAM.
public sealed class CountingWriteStream(Stream destination) : Stream
{
    public long BytesWritten { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken ct) => destination.FlushAsync(ct);
    public override void Write(byte[] buffer, int offset, int count)
    {
        destination.Write(buffer, offset, count);
        BytesWritten += count;
    }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await destination.WriteAsync(buffer, offset, count, ct);
        BytesWritten += count;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await destination.WriteAsync(buffer, ct);
        BytesWritten += buffer.Length;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

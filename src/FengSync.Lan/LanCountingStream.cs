namespace FengSync.Lan;

/// <summary>
/// A write-only wrapper that reports how much has gone through it, for the one direction a response body cannot be
/// counted on its own.
/// </summary>
/// <remarks>
/// <para>
/// Two routes need this and neither can use a framework helper. A file is handed over by copying it in chunks, so
/// the loop counts itself; a folder is compressed straight onto the response by <see cref="LanZipStream"/>, which
/// writes through whatever stream it is given — so giving it this one is the only place a byte can be seen without
/// staging the archive on disk, which is exactly what that class exists to avoid.
/// </para>
/// <para>
/// It never closes the stream underneath. The response body belongs to Kestrel and closing it here would end the
/// response before the headers were settled; <see cref="DisposeAsync"/> and <see cref="Dispose"/> are deliberately
/// no-ops for that reason, and the caller keeps ownership.
/// </para>
/// <para>
/// Synchronous writes are supported on purpose: <c>ZipArchive</c> has no asynchronous dispose and flushes its
/// central directory synchronously, which is why the archive route lifts Kestrel's synchronous-IO gate for itself.
/// </para>
/// </remarks>
public sealed class LanCountingStream(Stream inner, Action<long> moved) : Stream
{
    private long _total;

    /// <summary>How many bytes have gone through, which is what the last callback was told.</summary>
    public long Total => Interlocked.Read(ref _total);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => Total;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Count(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Count(buffer.Length);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Count(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Count(buffer.Length);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The response body outlives this wrapper; see the type's remarks.
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Count(int delta)
    {
        if (delta <= 0) return;
        moved(Interlocked.Add(ref _total, delta));
    }
}

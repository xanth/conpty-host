using System.Threading.Channels;

namespace ConPtyHost.Core.Pty;

/// <summary>Output chunks handed from a terminal's reader to whoever consumes them.</summary>
internal sealed class OutputQueue
{
    private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    private byte[]? pending;
    private int pendingOffset;

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty)
        {
            chunks.Writer.TryWrite(data.ToArray());
        }
    }

    public void Complete() => chunks.Writer.TryComplete();

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (pending is null)
        {
            if (!await chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                || !chunks.Reader.TryRead(out pending))
            {
                return 0;
            }

            pendingOffset = 0;
        }

        int count = Math.Min(buffer.Length, pending.Length - pendingOffset);
        pending.AsMemory(pendingOffset, count).CopyTo(buffer);
        pendingOffset += count;
        if (pendingOffset == pending.Length)
        {
            pending = null;
        }

        return count;
    }
}

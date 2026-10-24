using System.Text;
using System.Text.Json;

namespace ConPtyHost.Core.Ipc;

/// <summary>Newline-delimited JSON frames over a stream; writes may come from any thread.</summary>
internal sealed class FrameStream : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly StreamReader reader;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public FrameStream(Stream stream)
    {
        this.stream = stream;
        reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    }

    /// <summary>The next frame, or null once the other side closed.</summary>
    public async Task<Frame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line is null ? null : JsonSerializer.Deserialize(line, FrameJsonContext.Default.Frame);
    }

    public async Task WriteAsync(Frame frame, CancellationToken cancellationToken = default)
    {
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(frame, FrameJsonContext.Default.Frame);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            stream.WriteByte((byte)'\n');
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        reader.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
        writeGate.Dispose();
    }
}

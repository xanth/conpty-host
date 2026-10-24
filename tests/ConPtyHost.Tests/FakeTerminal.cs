using System.Text;
using System.Threading.Channels;
using ConPtyHost.Core.Pty;

namespace ConPtyHost.Tests;

/// <summary>A terminal the test drives: it records input and emits output on demand.</summary>
internal sealed class FakeTerminal(IReadOnlyList<string> argv, int columns, int rows) : IPseudoTerminal
{
    private readonly Channel<byte[]> output = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock gate = new();
    private readonly List<byte> written = [];

    public IReadOnlyList<string> Argv { get; } = argv;

    public (int Columns, int Rows) Size { get; private set; } = (columns, rows);

    public int ProcessId => 4242;

    public string Backend => "fake";

    public Task<int> Exited => exited.Task;

    public string Written
    {
        get
        {
            lock (gate)
            {
                return Encoding.UTF8.GetString([.. written]);
            }
        }
    }

    public void Emit(string text) => output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public void Exit(int code)
    {
        exited.TrySetResult(code);
        output.Writer.TryComplete();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!await output.Reader.WaitToReadAsync(cancellationToken) || !output.Reader.TryRead(out byte[]? chunk))
        {
            return 0;
        }

        chunk.CopyTo(buffer);
        return chunk.Length;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            written.AddRange(data.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows) => Size = (columns, rows);

    public ValueTask DisposeAsync()
    {
        Exit(-1);
        return ValueTask.CompletedTask;
    }
}

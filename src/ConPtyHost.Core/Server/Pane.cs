using ConPtyHost.Core.Pty;
using ConPtyHost.Core.Tmux;

namespace ConPtyHost.Core.Server;

/// <summary>The session's one pane: its terminal, its screen, and whether it has died.</summary>
internal sealed class Pane : IAsyncDisposable
{
    private readonly IPseudoTerminal terminal;
    private readonly Func<byte[], Task> broadcast;
    private readonly Task pump;

    private Pane(IPseudoTerminal terminal, PaneScreen screen, Func<byte[], Task> broadcast)
    {
        this.terminal = terminal;
        this.broadcast = broadcast;
        Screen = screen;
        pump = PumpAsync();
    }

    public PaneScreen Screen { get; }

    public int ProcessId => terminal.ProcessId;

    public string Backend => terminal.Backend;

    /// <summary>The child's exit code, once it has exited.</summary>
    public int? ExitStatus { get; private set; }

    public bool IsDead => ExitStatus is not null;

    /// <summary>Completes once the child exits and its last output was read.</summary>
    public Task Died => pump;

    /// <param name="broadcast">Receives each chunk of output as it arrives.</param>
    public static Pane Start(
        Func<IReadOnlyList<string>, string?, int, int, IPseudoTerminal> startTerminal,
        IReadOnlyList<string> argv,
        string? workingDirectory,
        int columns,
        int rows,
        int historyLimit,
        Func<byte[], Task> broadcast)
    {
        IPseudoTerminal terminal = startTerminal(argv, workingDirectory, columns, rows);

        // The emulator's answers to queries (cursor position, device
        // attributes) go back in as if typed; ConPTY waits on some of them.
        PaneScreen screen = new(columns, rows, historyLimit, reply => _ = terminal.WriteAsync(reply).AsTask());
        return new Pane(terminal, screen, broadcast);
    }

    public async ValueTask WriteAsync(byte[] data)
    {
        if (!IsDead)
        {
            await terminal.WriteAsync(data).ConfigureAwait(false);
        }
    }

    public void Resize(int columns, int rows)
    {
        Screen.Resize(columns, rows);
        if (!IsDead)
        {
            terminal.Resize(columns, rows);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await terminal.DisposeAsync().ConfigureAwait(false);
        await pump.ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        byte[] buffer = new byte[16 * 1024];
        Task<int> exited = terminal.Exited;
        Task<int> read = terminal.ReadAsync(buffer).AsTask();

        while (true)
        {
            Task finished = await Task.WhenAny(read, exited).ConfigureAwait(false);
            if (finished == read)
            {
                int count = await read.ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                Screen.Feed(buffer, count);
                await broadcast(buffer.AsSpan(0, count).ToArray()).ConfigureAwait(false);
                read = terminal.ReadAsync(buffer).AsTask();
                continue;
            }

            // The child exited. A pseudoconsole keeps its pipe open until it is
            // closed, so drain what is already there and then stop reading.
            using CancellationTokenSource drained = new(TimeSpan.FromMilliseconds(500));
            try
            {
                while (true)
                {
                    int count = await read.WaitAsync(drained.Token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    Screen.Feed(buffer, count);
                    await broadcast(buffer.AsSpan(0, count).ToArray()).ConfigureAwait(false);
                    read = terminal.ReadAsync(buffer).AsTask();
                }
            }
            catch (OperationCanceledException)
            {
                // Nothing more arrived; the pane is done producing output.
            }

            break;
        }

        ExitStatus = await exited.ConfigureAwait(false);
    }
}

using System.Net.Sockets;
using System.Text;
using ConPtyHost.Core.Server;
using ConPtyHost.Core.Tmux;

namespace ConPtyHost.Core.Ipc;

/// <summary>One <c>tmux</c> invocation, as a client of the server on its socket.</summary>
public sealed class TmuxClient(Func<string, Task> startServer)
{
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Run <paramref name="args"/> and return the process exit code.</summary>
    /// <param name="startServer">Starts a detached server listening on the given socket path.</param>
    public async Task<int> RunAsync(IReadOnlyList<string> args, Stream stdin, Stream stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        Invocation invocation;
        try
        {
            invocation = Invocation.Parse(args);
        }
        catch (TmuxException failure)
        {
            await stderr.WriteLineAsync(failure.Message).ConfigureAwait(false);
            return 1;
        }

        if (invocation.Options.PrintVersion)
        {
            await stdout.WriteAsync(Encoding.ASCII.GetBytes($"tmux {TmuxServer.Version}\n")).ConfigureAwait(false);
            return 0;
        }

        if (invocation.Commands.Count == 0)
        {
            await stderr.WriteLineAsync("no command given").ConfigureAwait(false);
            return 1;
        }

        string socketPath = invocation.Options.SocketPath ?? DefaultSocketPath();
        bool attach = invocation.Commands.Any(command => command[0] is "attach-session" or "attach");
        if (attach && !invocation.Options.ControlMode)
        {
            await stderr.WriteLineAsync("attach needs control mode (-C)").ConfigureAwait(false);
            return 1;
        }

        Socket? connection = await TryConnectAsync(socketPath).ConfigureAwait(false);
        // As in tmux, a sequence that creates a session starts the server,
        // wherever new-session falls in it; omnigent sets options first.
        if (connection is null && invocation.Commands.Any(command => command[0] is "new-session" or "new"))
        {
            await startServer(socketPath).ConfigureAwait(false);
            connection = await WaitForServerAsync(socketPath).ConfigureAwait(false);
        }

        if (connection is null)
        {
            await stderr.WriteLineAsync($"no server running on {socketPath}").ConfigureAwait(false);
            return 1;
        }

        await using FrameStream frames = new(new NetworkStream(connection, ownsSocket: true));
        if (attach)
        {
            await frames.WriteAsync(new Frame { Kind = "attach" }).ConfigureAwait(false);
            _ = RelayControlInputAsync(stdin, frames);
        }
        else
        {
            await frames.WriteAsync(new Frame
            {
                Kind = "command",
                Commands = [.. invocation.Commands.Select(command => command.ToList())],
                WorkingDirectory = Environment.CurrentDirectory,
                Data = ReadsStdin(invocation) ? await ReadAllAsync(stdin).ConfigureAwait(false) : null,
            }).ConfigureAwait(false);
        }

        while (await frames.ReadAsync().ConfigureAwait(false) is { } frame)
        {
            switch (frame.Kind)
            {
                case "out" when frame.Data is not null:
                    await stdout.WriteAsync(frame.Data).ConfigureAwait(false);
                    await stdout.FlushAsync().ConfigureAwait(false);
                    break;
                case "err" when frame.Text is not null:
                    await stderr.WriteLineAsync(frame.Text).ConfigureAwait(false);
                    break;
                case "exit":
                    return frame.Code;
            }
        }

        return 1;
    }

    /// <summary>tmux's default: <c>$TMUX_TMPDIR/tmux-&lt;uid&gt;/default</c>.</summary>
    private static string DefaultSocketPath()
    {
        string root = Environment.GetEnvironmentVariable("TMUX_TMPDIR") ?? Path.GetTempPath();
        string directory = Path.Combine(root, $"tmux-{Environment.UserName}");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "default");
    }

    private static bool ReadsStdin(Invocation invocation) =>
        invocation.Commands.Any(command =>
            command[0] is "load-buffer" or "loadb"
            && (command.Count == 1 || command[^1] == "-"));

    private static async Task<byte[]> ReadAllAsync(Stream stdin)
    {
        using MemoryStream buffer = new();
        await stdin.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    // Control-mode commands arrive one per line on stdin; end-of-file detaches.
    private static async Task RelayControlInputAsync(Stream stdin, FrameStream frames)
    {
        using StreamReader reader = new(stdin, new UTF8Encoding(false), leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                await frames.WriteAsync(new Frame { Kind = "line", Text = line }).ConfigureAwait(false);
            }

            await frames.WriteAsync(new Frame { Kind = "line", Text = "detach-client" }).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException)
        {
            // The server closed the connection first.
        }
    }

    private static async Task<Socket?> TryConnectAsync(string socketPath)
    {
        if (!File.Exists(socketPath))
        {
            return null;
        }

        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).ConfigureAwait(false);
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    private static async Task<Socket?> WaitForServerAsync(string socketPath)
    {
        DateTime deadline = DateTime.UtcNow + ServerStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await TryConnectAsync(socketPath).ConfigureAwait(false) is { } connection)
            {
                return connection;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return null;
    }
}

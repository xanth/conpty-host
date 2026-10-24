using System.Net.Sockets;
using System.Text;
using ConPtyHost.Core.Server;

namespace ConPtyHost.Core.Ipc;

/// <summary>Serves a <see cref="TmuxServer"/> on a Unix domain socket until it stops.</summary>
/// <remarks>Windows 10 1803 and later support Unix domain sockets, so <c>-S path</c> means the same everywhere.</remarks>
public static class ServerHost
{
    public static async Task RunAsync(string socketPath, TmuxServer server, Action<Exception>? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        ArgumentNullException.ThrowIfNull(server);

        // A leftover file from a server that died; the caller already failed to connect to it.
        File.Delete(socketPath);
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(16);

        using CancellationTokenSource stopping = new();
        _ = server.Stopped.ContinueWith(_ => stopping.Cancel(), TaskScheduler.Default);

        List<Task> connections = [];
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                Socket connection;
                try
                {
                    connection = await listener.AcceptAsync(stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                connections.Add(ServeAsync(connection, server, log, stopping.Token));
                connections.RemoveAll(task => task.IsCompleted);
            }
        }
        finally
        {
            await Task.WhenAll(connections).ConfigureAwait(false);
            File.Delete(socketPath);
        }
    }

    private static async Task ServeAsync(Socket connection, TmuxServer server, Action<Exception>? log, CancellationToken stopping)
    {
        await using FrameStream frames = new(new NetworkStream(connection, ownsSocket: true));
        try
        {
            Frame? request = await frames.ReadAsync(stopping).ConfigureAwait(false);
            switch (request?.Kind)
            {
                case "command":
                    // kill-server stops the server mid-request; the reply must still go out.
                    CommandResult result = await server.ExecuteAsync(
                        request.Commands ?? [],
                        new CommandContext(request.Data ?? [], request.WorkingDirectory)).ConfigureAwait(false);
                    if (result.Output.Length > 0)
                    {
                        await frames.WriteAsync(new Frame { Kind = "out", Data = Encoding.UTF8.GetBytes(result.Output) }, CancellationToken.None).ConfigureAwait(false);
                    }

                    if (result.Error is not null)
                    {
                        await frames.WriteAsync(new Frame { Kind = "err", Text = result.Error }, CancellationToken.None).ConfigureAwait(false);
                    }

                    await frames.WriteAsync(new Frame { Kind = "exit", Code = result.ExitCode }, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "attach":
                    await AttachAsync(frames, server, stopping).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception failure) when (failure is IOException or OperationCanceledException or SocketException)
        {
            // The client went away or the server is stopping.
        }
        catch (Exception failure)
        {
            log?.Invoke(failure);
        }
    }

    private static async Task AttachAsync(FrameStream frames, TmuxServer server, CancellationToken stopping)
    {
        SocketControlClient client = new(frames);
        try
        {
            await server.AttachAsync(client).ConfigureAwait(false);
        }
        catch (Tmux.TmuxException failure)
        {
            await frames.WriteAsync(new Frame { Kind = "err", Text = failure.Message }, stopping).ConfigureAwait(false);
            await frames.WriteAsync(new Frame { Kind = "exit", Code = 1 }, stopping).ConfigureAwait(false);
            return;
        }

        Task commands = RelayCommandsAsync(frames, server, client, stopping);
        await Task.WhenAny(commands, client.Detached).ConfigureAwait(false);
        server.Detach(client);

        await frames.WriteAsync(new Frame { Kind = "out", Data = "%exit\n"u8.ToArray() }, CancellationToken.None).ConfigureAwait(false);
        await frames.WriteAsync(new Frame { Kind = "exit", Code = 0 }, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task RelayCommandsAsync(FrameStream frames, TmuxServer server, SocketControlClient client, CancellationToken stopping)
    {
        try
        {
            while (await frames.ReadAsync(stopping).ConfigureAwait(false) is { } frame)
            {
                if (frame.Kind == "line" && frame.Text is not null)
                {
                    await server.ExecuteControlLineAsync(client, frame.Text).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (failure is IOException or OperationCanceledException)
        {
            // Client gone; detaching follows either way.
        }
    }

    private sealed class SocketControlClient(FrameStream frames) : IControlClient
    {
        private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Detached => detached.Task;

        public async ValueTask SendAsync(byte[] data)
        {
            if (detached.Task.IsCompleted)
            {
                return;
            }

            try
            {
                await frames.WriteAsync(new Frame { Kind = "out", Data = data }).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException)
            {
                detached.TrySetResult();
            }
        }

        public void Detach() => detached.TrySetResult();
    }
}

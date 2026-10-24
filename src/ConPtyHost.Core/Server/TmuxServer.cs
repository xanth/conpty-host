using System.Globalization;
using System.Text;
using ConPtyHost.Core.Pty;
using ConPtyHost.Core.Tmux;

namespace ConPtyHost.Core.Server;

/// <summary>Who ran a command sequence, and what it can read.</summary>
/// <param name="Input">The client's stdin, for <c>load-buffer -</c>.</param>
/// <param name="WorkingDirectory">The client's working directory.</param>
public sealed record CommandContext(byte[] Input, string? WorkingDirectory);

/// <summary>
/// A tmux server with one session, one window and one pane, implementing the
/// commands in docs/TMUX-CONTRACT.md.
/// </summary>
/// <remarks>
/// Commands are serialised, as in tmux. Pane output reaches control clients
/// as it arrives, independently of command execution.
/// </remarks>
public sealed class TmuxServer : IAsyncDisposable
{
    public const string Version = "3.4";

    private const int DefaultHistoryLimit = 2000;
    private const string PaneId = "%0";

    private readonly Func<IReadOnlyList<string>, string?, int, int, IPseudoTerminal> startTerminal;
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly Lock clientsGate = new();
    private readonly List<IControlClient> controlClients = [];
    private readonly Dictionary<string, string> options = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> buffers = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Pane? pane;
    private string sessionName = "main";
    private bool detachOnPaneDied;
    private long controlCommandNumber;

    public TmuxServer(Func<IReadOnlyList<string>, string?, int, int, IPseudoTerminal> startTerminal)
    {
        ArgumentNullException.ThrowIfNull(startTerminal);
        this.startTerminal = startTerminal;
    }

    /// <summary>Completes when the server should exit: killed, or its pane gone.</summary>
    public Task Stopped => stopped.Task;

    public bool HasSession => pane is not null;

    /// <summary>Run a command sequence, stopping at the first failure as tmux does.</summary>
    public async Task<CommandResult> ExecuteAsync(IReadOnlyList<IReadOnlyList<string>> commands, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(context);

        StringBuilder output = new();
        await commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (IReadOnlyList<string> tokens in commands)
            {
                try
                {
                    await ExecuteOneAsync(CommandArguments.Parse(tokens), context, output).ConfigureAwait(false);
                }
                catch (TmuxException failure)
                {
                    return CommandResult.From(output, failure.Message);
                }
            }

            return CommandResult.From(output, null);
        }
        finally
        {
            commandGate.Release();
        }
    }

    /// <summary>Attach a control client and send it the attach reply.</summary>
    public async Task AttachAsync(IControlClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (pane is null)
        {
            throw new TmuxException("no sessions");
        }

        lock (clientsGate)
        {
            controlClients.Add(client);
        }

        await client.SendAsync(ControlProtocol.Reply(Interlocked.Increment(ref controlCommandNumber), string.Empty, failed: false)).ConfigureAwait(false);
        await client.SendAsync(Encoding.ASCII.GetBytes($"%session-changed $0 {sessionName}\n")).ConfigureAwait(false);
    }

    /// <summary>Run one control-mode command line and reply in a <c>%begin</c> block.</summary>
    public async Task ExecuteControlLineAsync(IControlClient client, string line)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(line);

        CommandResult result;
        try
        {
            IReadOnlyList<string> words = ShellWords.SplitPosix(line);
            result = words.Count == 0
                ? new CommandResult(string.Empty, null)
                : await ExecuteAsync([words], new CommandContext([], null)).ConfigureAwait(false);
        }
        catch (TmuxException failure)
        {
            result = new CommandResult(string.Empty, failure.Message);
        }

        string body = result.Error is null ? result.Output : result.Error;
        await client.SendAsync(ControlProtocol.Reply(Interlocked.Increment(ref controlCommandNumber), body, result.Error is not null)).ConfigureAwait(false);
    }

    public void Detach(IControlClient client)
    {
        lock (clientsGate)
        {
            controlClients.Remove(client);
        }
    }

    public async ValueTask DisposeAsync()
    {
        DetachAll();
        if (pane is not null)
        {
            await pane.DisposeAsync().ConfigureAwait(false);
        }

        commandGate.Dispose();
        stopped.TrySetResult();
    }

    private async Task ExecuteOneAsync(CommandArguments command, CommandContext context, StringBuilder output)
    {
        switch (command.Name)
        {
            case "new-session":
                NewSession(command, context);
                break;
            case "set-option" or "set-window-option":
                SetOption(command);
                break;
            case "set-hook":
                SetHook(command);
                break;
            case "send-keys":
                await SendKeysAsync(command).ConfigureAwait(false);
                break;
            case "capture-pane":
                CapturePane(command, output);
                break;
            case "display-message":
                if (command.Has('p'))
                {
                    output.Append(FormatExpander.Expand(FirstPositional(command) ?? "#{session_name}", Variable(RequirePane(command.Value('t'))))).Append('\n');
                }

                break;
            case "list-panes":
                output.Append(FormatExpander.Expand(command.Value('F') ?? "#{pane_index}: [#{pane_width}x#{pane_height}] #{pane_id}", Variable(RequirePane(command.Value('t'))))).Append('\n');
                break;
            case "has-session":
                _ = RequirePane(command.Value('t'));
                break;
            case "detach-client":
                _ = RequirePane(command.Value('s') ?? command.Value('t'));
                DetachAll();
                break;
            case "kill-server" or "kill-session":
                DetachAll();
                stopped.TrySetResult();
                break;
            case "run-shell":
                // omnigent only round-trips ":"; running real shell commands is out of scope.
                if (command.Positional.Count > 0 && command.Positional[0].Trim() is not (":" or ""))
                {
                    throw new TmuxException("run-shell: only ':' is supported");
                }

                break;
            case "load-buffer":
                buffers[command.Value('b') ?? "buffer0"] = ReadSource(FirstPositional(command) ?? "-", context);
                break;
            case "save-buffer":
                string name = command.Value('b') ?? "buffer0";
                output.Append(Encoding.UTF8.GetString(buffers.TryGetValue(name, out byte[]? saved) ? saved : throw new TmuxException($"no buffer {name}")));
                break;
            case "refresh-client":
                if (command.Value('C') is { } size)
                {
                    (int columns, int rows) = ParseSize(size);
                    RequirePane(null).Resize(columns, rows);
                }

                break;
            case "attach-session":
                throw new TmuxException("attach-session needs control mode (-C)");
            default:
                throw new TmuxException($"unsupported command: {command.Name}");
        }
    }

    private void NewSession(CommandArguments command, CommandContext context)
    {
        if (pane is not null)
        {
            throw new TmuxException($"duplicate session: {sessionName}");
        }

        sessionName = command.Value('s') ?? "main";
        int columns = ParsePositive(command.Value('x'), 80, 'x');
        int rows = ParsePositive(command.Value('y'), 24, 'y');

        // One argument is a shell command, POSIX-quoted the way Python's
        // shlex.join writes it; several are already an argv, as in tmux.
        IReadOnlyList<string> argv = command.Positional.Count switch
        {
            0 => [DefaultShell()],
            1 => ShellWords.SplitPosix(command.Positional[0]),
            _ => command.Positional,
        };
        if (argv.Count == 0)
        {
            throw new TmuxException("new-session: empty command");
        }

        int historyLimit = int.TryParse(options.GetValueOrDefault("history-limit"), NumberStyles.None, CultureInfo.InvariantCulture, out int limit)
            ? limit
            : DefaultHistoryLimit;

        try
        {
            pane = Pane.Start(startTerminal, argv, command.Value('c') ?? context.WorkingDirectory, columns, rows, historyLimit, BroadcastOutputAsync);
        }
        catch (Exception failure) when (failure is not TmuxException)
        {
            throw new TmuxException($"new-session: could not start {argv[0]}: {failure.Message}", failure);
        }

        _ = WatchPaneAsync(pane);
    }

    private void SetOption(CommandArguments command)
    {
        if (command.Positional.Count == 0)
        {
            throw new TmuxException($"{command.Name}: no option given");
        }

        string name = command.Positional[0];
        if (command.Has('u'))
        {
            options.Remove(name);
            return;
        }

        string value = command.Positional.Count > 1 ? command.Positional[1] : string.Empty;
        if (name == "history-limit" && !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new TmuxException($"invalid history-limit: {value}");
        }

        options[name] = value;
    }

    private void SetHook(CommandArguments command)
    {
        if (command.Positional.Count == 0)
        {
            throw new TmuxException("set-hook: no hook given");
        }

        // The one hook omnigent sets; others are accepted and do nothing.
        if (command.Positional[0] == "pane-died")
        {
            detachOnPaneDied = !command.Has('u') && command.Positional.Count > 1
                && ShellWords.SplitPosix(command.Positional[1]) is ["detach-client", "-a"];
        }
    }

    private async Task SendKeysAsync(CommandArguments command)
    {
        Pane target = RequirePane(command.Value('t'));
        List<byte> bytes = [];
        foreach (string argument in command.Positional)
        {
            if (command.Has('H'))
            {
                bytes.Add(byte.TryParse(argument, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)
                    ? value
                    : throw new TmuxException($"invalid hex: {argument}"));
            }
            else if (command.Has('l'))
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(argument));
            }
            else
            {
                bytes.AddRange(KeyNames.Encode(argument, target.Screen.ApplicationCursor));
            }
        }

        if (bytes.Count > 0)
        {
            await target.WriteAsync([.. bytes]).ConfigureAwait(false);
        }
    }

    private void CapturePane(CommandArguments command, StringBuilder output)
    {
        Pane target = RequirePane(command.Value('t'));
        string captured = target.Screen.Capture(new CaptureRequest(command.Has('e'), command.Has('J'), command.Value('S'), command.Value('E')));
        if (command.Has('p'))
        {
            output.Append(captured);
        }
        else
        {
            buffers[command.Value('b') ?? "buffer0"] = Encoding.UTF8.GetBytes(captured);
        }
    }

    private Pane RequirePane(string? target)
    {
        if (pane is null || !TargetsSession(target))
        {
            throw new TmuxException($"can't find session: {target ?? sessionName}");
        }

        return pane;
    }

    // Accept the forms that name the one session, window and pane:
    // "main", "=main", "main:", "main:0", "main:0.0", and "%0".
    private bool TargetsSession(string? target)
    {
        if (target is null || target == PaneId)
        {
            return true;
        }

        string session = target.TrimStart('=');
        int colon = session.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            string window = session[(colon + 1)..];
            session = session[..colon];
            if (window is not ("" or "0" or "0.0" or ".0"))
            {
                return false;
            }
        }

        return session.Length == 0 || session == sessionName;
    }

    private Func<string, string?> Variable(Pane target) => name => name switch
    {
        "pane_dead" => target.IsDead ? "1" : "0",
        "pane_dead_status" => target.ExitStatus?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        "pane_dead_signal" => string.Empty,
        "pane_id" => PaneId,
        "pane_index" or "window_index" => "0",
        "pane_pid" => target.ProcessId.ToString(CultureInfo.InvariantCulture),
        "session_name" => sessionName,
        "pane_backend" => target.Backend,
        _ => target.Screen.Variable(name),
    };

    private async Task BroadcastOutputAsync(byte[] data)
    {
        byte[] line = ControlProtocol.OutputLine(data);
        foreach (IControlClient client in SnapshotClients())
        {
            await client.SendAsync(line).ConfigureAwait(false);
        }
    }

    private async Task WatchPaneAsync(Pane watched)
    {
        await watched.Died.ConfigureAwait(false);

        bool remainOnExit = options.GetValueOrDefault("remain-on-exit") == "on";
        if (!remainOnExit)
        {
            // The only pane is gone, so its window and the session go too.
            await SendToAllAsync("%window-close @0\n").ConfigureAwait(false);
            DetachAll();
            stopped.TrySetResult();
        }
        else if (detachOnPaneDied)
        {
            DetachAll();
        }
    }

    private async Task SendToAllAsync(string text)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        foreach (IControlClient client in SnapshotClients())
        {
            await client.SendAsync(data).ConfigureAwait(false);
        }
    }

    private void DetachAll()
    {
        foreach (IControlClient client in SnapshotClients())
        {
            Detach(client);
            client.Detach();
        }
    }

    private IControlClient[] SnapshotClients()
    {
        lock (clientsGate)
        {
            return [.. controlClients];
        }
    }

    private static string? FirstPositional(CommandArguments command) =>
        command.Positional.Count > 0 ? command.Positional[0] : null;

    private static byte[] ReadSource(string path, CommandContext context) =>
        path == "-" ? context.Input : File.ReadAllBytes(path);

    private static int ParsePositive(string? value, int fallback, char flag) =>
        value is null
            ? fallback
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed
                : throw new TmuxException($"invalid -{flag}: {value}");

    // refresh-client -C takes WxH, or W,H in older tmux.
    private static (int Columns, int Rows) ParseSize(string value)
    {
        string[] parts = value.Split('x', ',');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int columns) && columns > 0
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int rows) && rows > 0
            ? (columns, rows)
            : throw new TmuxException($"invalid size: {value}");
    }

    private static string DefaultShell() =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
            : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
}

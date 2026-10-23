namespace ConPtyHost.Core.Terminal;

/// <summary>
/// A platform's pseudo-terminal implementation.
/// </summary>
/// <remarks>
/// This is the seam that lets a host stop assuming tmux. A backend owns
/// session lifetime, byte transport in both directions, and resize; it does
/// not interpret the stream, so a harness TUI's escape sequences pass through
/// untouched.
/// </remarks>
public interface ITerminalBackend
{
    /// <summary>Start a session running <paramref name="command"/>.</summary>
    ValueTask<ITerminalSession> StartAsync(
        string command,
        string? workingDirectory,
        short width,
        short height,
        CancellationToken cancellationToken = default);
}

/// <summary>One live pseudo-terminal session.</summary>
public interface ITerminalSession : IAsyncDisposable
{
    /// <summary>Operating-system id of the attached child process.</summary>
    int ProcessId { get; }

    /// <summary>Bytes produced by the session: raw, including escape sequences.</summary>
    Stream Output { get; }

    /// <summary>Bytes sent to the session as if typed.</summary>
    Stream Input { get; }

    /// <summary>Tell the session its viewport changed.</summary>
    ValueTask ResizeAsync(short width, short height, CancellationToken cancellationToken = default);
}

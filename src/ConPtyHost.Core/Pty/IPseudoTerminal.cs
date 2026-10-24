namespace ConPtyHost.Core.Pty;

/// <summary>A child process attached to a terminal: bytes in, bytes out, a size, an exit.</summary>
public interface IPseudoTerminal : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>Which implementation runs the terminal, for diagnostics.</summary>
    /// <remarks><c>conpty-bundled</c>, <c>conpty-inbox</c> or <c>redirected-process</c>.</remarks>
    string Backend { get; }

    /// <summary>Completes with the child's exit code once it exits.</summary>
    Task<int> Exited { get; }

    /// <summary>Read output; returns 0 once the terminal has closed and everything was read.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    void Resize(int columns, int rows);
}

/// <summary>Starts the platform's pseudo-terminal.</summary>
public static class PseudoTerminal
{
    /// <summary>Start <paramref name="argv"/> attached to a new terminal.</summary>
    /// <remarks>
    /// Windows gets a real pseudoconsole. Elsewhere this falls back to a
    /// process with redirected stdio: enough to develop and test the tmux
    /// layer on, but not a terminal, so full-screen programs won't work.
    /// </remarks>
    public static IPseudoTerminal Start(IReadOnlyList<string> argv, string? workingDirectory, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentOutOfRangeException.ThrowIfZero(argv.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);

        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
            ? ConPtyPseudoTerminal.Start(argv, workingDirectory, columns, rows)
            : RedirectedProcessTerminal.Start(argv, workingDirectory);
    }
}

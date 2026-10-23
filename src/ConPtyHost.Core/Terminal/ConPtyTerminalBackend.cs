using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ConPtyHost.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace ConPtyHost.Core.Terminal;

/// <summary>
/// An <see cref="ITerminalBackend"/> over the Windows pseudoconsole API.
/// </summary>
/// <remarks>
/// CreatePseudoConsole requires Windows 10 1809 / Server 2019 or later.
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class ConPtyTerminalBackend : ITerminalBackend
{
    public ValueTask<ITerminalSession> StartAsync(
        string command,
        string? workingDirectory,
        short width,
        short height,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ITerminalSession>(
            ConPtyTerminalSession.Start(command, workingDirectory, width, height));
    }
}

[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class ConPtyTerminalSession : ITerminalSession
{
    private readonly PseudoConsolePipe inputPipe;
    private readonly PseudoConsolePipe outputPipe;
    private readonly PseudoConsole pseudoConsole;
    private readonly PseudoConsoleProcess process;

    private ConPtyTerminalSession(
        PseudoConsolePipe inputPipe,
        PseudoConsolePipe outputPipe,
        PseudoConsole pseudoConsole,
        PseudoConsoleProcess process,
        Stream input,
        Stream output)
    {
        this.inputPipe = inputPipe;
        this.outputPipe = outputPipe;
        this.pseudoConsole = pseudoConsole;
        this.process = process;
        Input = input;
        Output = output;
    }

    public int ProcessId => process.ProcessId;

    public Stream Output { get; }

    public Stream Input { get; }

    internal static ConPtyTerminalSession Start(
        string command,
        string? workingDirectory,
        short width,
        short height)
    {
        PseudoConsolePipe inputPipe = new();
        PseudoConsolePipe outputPipe = new();

        // ConPTY reads input from the pipe's read side and writes output to
        // the write side; the host keeps the opposite ends.
        PseudoConsole console = PseudoConsole.Create(
            inputPipe.ReadSide, outputPipe.WriteSide, width, height);

        PseudoConsoleProcess process = ProcessFactory.Start(command, workingDirectory, console.Handle);

        Stream input = new FileStream(
            new SafeFileHandle(inputPipe.WriteSide, ownsHandle: false), FileAccess.Write);
        Stream output = new FileStream(
            new SafeFileHandle(outputPipe.ReadSide, ownsHandle: false), FileAccess.Read);

        // ConPTY duplicated the ends it was given, so release our copies now
        // that the client exists. While we still hold the output write side,
        // our read side never observes EOF and a read blocks indefinitely
        // once the client stops producing.
        inputPipe.CloseReadSide();
        outputPipe.CloseWriteSide();

        return new ConPtyTerminalSession(inputPipe, outputPipe, console, process, input, output);
    }

    public ValueTask ResizeAsync(short width, short height, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        pseudoConsole.Resize(width, height);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // Order is load-bearing. ClosePseudoConsole blocks until the client
        // has drained, so it must run while the output pipe is still open;
        // closing the pipes first is the classic ConPTY teardown hang.
        await Input.DisposeAsync().ConfigureAwait(false);
        pseudoConsole.Dispose();
        await Output.DisposeAsync().ConfigureAwait(false);
        inputPipe.Dispose();
        outputPipe.Dispose();
        process.Dispose();
    }
}

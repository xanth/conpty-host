using System.Runtime.Versioning;
using ConPtyHost.Core.Native;
using ConPtyHost.Core.Tmux;
using Microsoft.Win32.SafeHandles;

namespace ConPtyHost.Core.Pty;

/// <summary>A child process attached to a Windows pseudoconsole.</summary>
/// <remarks>
/// A dedicated thread drains the output pipe for the console's whole life.
/// ClosePseudoConsole waits for its client to drain, so output must still be
/// being read when the console closes; owning the reader here guarantees it.
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class ConPtyPseudoTerminal : IPseudoTerminal
{
    private readonly PseudoConsolePipe inputPipe;
    private readonly PseudoConsolePipe outputPipe;
    private readonly PseudoConsole console;
    private readonly PseudoConsoleProcess process;
    private readonly FileStream input;
    private readonly FileStream output;
    private readonly OutputQueue queue = new();
    private readonly Thread reader;
    private int disposed;

    private ConPtyPseudoTerminal(
        PseudoConsolePipe inputPipe,
        PseudoConsolePipe outputPipe,
        PseudoConsole console,
        PseudoConsoleProcess process)
    {
        this.inputPipe = inputPipe;
        this.outputPipe = outputPipe;
        this.console = console;
        this.process = process;

        input = new FileStream(new SafeFileHandle(inputPipe.WriteSide, ownsHandle: false), FileAccess.Write, 1);
        output = new FileStream(new SafeFileHandle(outputPipe.ReadSide, ownsHandle: false), FileAccess.Read, 1);

        // ConPTY duplicated the ends it was given; while we hold our copy of
        // the output write side, the read side never sees end-of-file.
        inputPipe.CloseReadSide();
        outputPipe.CloseWriteSide();

        Exited = Task.Factory.StartNew(WaitForExit, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        reader = new Thread(Drain) { IsBackground = true, Name = $"conpty-output-{process.ProcessId}" };
        reader.Start();
    }

    public int ProcessId => process.ProcessId;

    public string Backend => console.Bundled ? "conpty-bundled" : "conpty-inbox";

    public Task<int> Exited { get; }

    internal static ConPtyPseudoTerminal Start(IReadOnlyList<string> argv, string? workingDirectory, int columns, int rows)
    {
        PseudoConsolePipe inputPipe = new();
        PseudoConsolePipe outputPipe = new();
        PseudoConsole? console = null;
        try
        {
            console = PseudoConsole.Create(inputPipe.ReadSide, outputPipe.WriteSide, (short)columns, (short)rows);
            PseudoConsoleProcess process = ProcessFactory.Start(ShellWords.JoinWindows(argv), workingDirectory, console.Handle);
            return new ConPtyPseudoTerminal(inputPipe, outputPipe, console, process);
        }
        catch
        {
            console?.Dispose();
            inputPipe.Dispose();
            outputPipe.Dispose();
            throw;
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        queue.ReadAsync(buffer, cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await input.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Resize(int columns, int rows) => console.Resize((short)columns, (short)rows);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (!Exited.IsCompleted)
        {
            PseudoConsoleApi.TerminateProcess(process.ProcessHandle, 1);
        }

        await input.DisposeAsync().ConfigureAwait(false);

        // Close the console while the reader still drains it, then let the
        // reader finish on the end-of-file that follows.
        await Task.Run(console.Dispose).ConfigureAwait(false);
        reader.Join();
        await output.DisposeAsync().ConfigureAwait(false);
        inputPipe.Dispose();
        outputPipe.Dispose();
        await Exited.ConfigureAwait(false);
        process.Dispose();
    }

    private void Drain()
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
            {
                queue.Write(buffer.AsSpan(0, read));
            }
        }
        catch (IOException)
        {
            // The pipe broke because the console closed; that is end-of-file.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            queue.Complete();
        }
    }

    private int WaitForExit()
    {
        _ = PseudoConsoleApi.WaitForSingleObject(process.ProcessHandle, PseudoConsoleApi.Infinite);
        return PseudoConsoleApi.GetExitCodeProcess(process.ProcessHandle, out uint code)
            ? unchecked((int)code)
            : -1;
    }
}

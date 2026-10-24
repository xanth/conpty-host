using System.Diagnostics;

namespace ConPtyHost.Core.Pty;

/// <summary>
/// A child process with redirected stdio, standing in for a terminal off Windows.
/// </summary>
/// <remarks>
/// For development and tests only: it is not a terminal. It translates the CR
/// that Enter sends into LF, as a terminal's line discipline would, so
/// line-reading programs behave, and merges stderr into the output.
/// </remarks>
internal sealed class RedirectedProcessTerminal : IPseudoTerminal
{
    private readonly Process process;
    private readonly OutputQueue queue = new();
    private readonly Task pumps;
    private int disposed;

    private RedirectedProcessTerminal(Process process)
    {
        this.process = process;
        Task stdout = PumpAsync(process.StandardOutput.BaseStream);
        Task stderr = PumpAsync(process.StandardError.BaseStream);
        pumps = Task.WhenAll(stdout, stderr).ContinueWith(_ => queue.Complete(), TaskScheduler.Default);
        Exited = WaitForExitAsync();
    }

    public int ProcessId => process.Id;

    public string Backend => "redirected-process";

    public Task<int> Exited { get; }

    internal static RedirectedProcessTerminal Start(IReadOnlyList<string> argv, string? workingDirectory)
    {
        ProcessStartInfo info = new(argv[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (string argument in argv.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }

        return new RedirectedProcessTerminal(Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{argv[0]}'."));
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        queue.ReadAsync(buffer, cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        byte[] translated = data.ToArray();
        for (int index = 0; index < translated.Length; index++)
        {
            if (translated[index] == (byte)'\r')
            {
                translated[index] = (byte)'\n';
            }
        }

        Stream stdin = process.StandardInput.BaseStream;
        await stdin.WriteAsync(translated, cancellationToken).ConfigureAwait(false);
        await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Resize(int columns, int rows)
    {
        // No terminal, so nothing to resize.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await pumps.ConfigureAwait(false);
        process.Dispose();
    }

    private async Task PumpAsync(Stream stream)
    {
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            queue.Write(buffer.AsSpan(0, read));
        }
    }

    private async Task<int> WaitForExitAsync()
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
}

using System.Diagnostics;
using System.Text;

namespace ConPtyHost.Tests;

/// <summary>
/// The built tmux executable, driven through omnigent's call sequence as
/// separate processes. On Windows this runs a real pseudoconsole.
/// </summary>
public class TmuxExecutableTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private static string Executable => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "tmux.exe" : "tmux");

    // The shell omnigent would start, quoted as Python's shlex.join writes it.
    private static string Shell => OperatingSystem.IsWindows() ? "cmd.exe" : "sh";

    [Test]
    public async Task A_shell_runs_through_omnigents_command_sequence()
    {
        string directory = Directory.CreateTempSubdirectory("cph-").FullName;
        string socket = Path.Combine(directory, "s.sock");
        try
        {
            // Run with stdout and stderr redirected, as omnigent does. A server
            // that inherited those pipes would keep them open, and this would hang.
            Result launch = await RunAsync(
                "-S", socket, "-f", OperatingSystem.IsWindows() ? "nul" : "/dev/null",
                "set-option", "-g", "history-limit", "5000", ";",
                "set-option", "-gq", "remain-on-exit", "on", ";",
                "new-session", "-d", "-s", "main", "-x", "80", "-y", "24", "-c", directory, Shell);
            await Assert.That(launch.ExitCode).IsEqualTo(0);
            await Assert.That((await RunAsync("-S", socket, "has-session", "-t", "main")).ExitCode).IsEqualTo(0);

            string backend = (await RunAsync("-S", socket, "list-panes", "-t", "main", "-F", "#{pane_backend}")).Output.Trim();
            Console.WriteLine($"pane backend: {backend}");
            if (OperatingSystem.IsWindows())
            {
                await Assert.That(backend).StartsWith("conpty-");
            }

            await RunAsync("-S", socket, "send-keys", "-t", "main", "-l", "echo hello-shim");
            await RunAsync("-S", socket, "send-keys", "-t", "main", "Enter");
            await EventuallyAsync(async () => (await RunAsync("-S", socket, "capture-pane", "-t", "main", "-p")).Output
                .Split('\n').Any(line => line.Trim() == "hello-shim"));

            await ControlModeEchoesAsync(socket);

            await RunAsync("-S", socket, "send-keys", "-t", "main", "-l", "exit 7");
            await RunAsync("-S", socket, "send-keys", "-t", "main", "Enter");
            await EventuallyAsync(async () => (await RunAsync("-S", socket, "list-panes", "-t", "main", "-F", "#{pane_dead}|#{pane_dead_status}")).Output.Trim() == "1|7");

            await Assert.That((await RunAsync("-S", socket, "kill-server")).ExitCode).IsEqualTo(0);
            await EventuallyAsync(() => Task.FromResult(!File.Exists(socket)));
            await Assert.That((await RunAsync("-S", socket, "has-session", "-t", "main")).ExitCode).IsEqualTo(1);
        }
        finally
        {
            await RunAsync("-S", socket, "kill-server");
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Version_satisfies_omnigents_minimum()
    {
        Result version = await RunAsync("-V");

        // omnigent's util/tmux_compat.py requires 3.3 or newer.
        await Assert.That(version.Output).IsEqualTo("tmux 3.4\n");
    }

    // control_bridge.py attaches with -C, sends input as send-keys -H and
    // reads pane output from %output lines.
    private static async Task ControlModeEchoesAsync(string socket)
    {
        using Process attach = Start(["-S", socket, "-f", "/dev/null", "-C", "attach"]);
        StringBuilder received = new();
        Task reading = Task.Run(async () =>
        {
            char[] buffer = new char[4096];
            int read;
            while ((read = await attach.StandardOutput.ReadAsync(buffer)) > 0)
            {
                lock (received)
                {
                    received.Append(buffer, 0, read);
                }
            }
        });

        string hex = string.Join(' ', Encoding.ASCII.GetBytes("echo ctl-mode\r").Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        await attach.StandardInput.WriteLineAsync($"send-keys -H -t main {hex}");
        await attach.StandardInput.FlushAsync();
        await EventuallyAsync(() =>
        {
            lock (received)
            {
                return Task.FromResult(received.ToString().Split('\n').Any(line => line.StartsWith("%output %0 ", StringComparison.Ordinal) && line.Contains("ctl-mode", StringComparison.Ordinal)));
            }
        });

        // Closing stdin detaches the client, which says %exit and exits cleanly.
        attach.StandardInput.Close();
        await attach.WaitForExitAsync().WaitAsync(CommandTimeout);
        await reading;
        await Assert.That(attach.ExitCode).IsEqualTo(0);
        await Assert.That(received.ToString()).Contains("%exit\n");
    }

    private static async Task<Result> RunAsync(params string[] args)
    {
        using Process process = Start(args);
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(CommandTimeout);
            await Task.WhenAll(output, error).WaitAsync(CommandTimeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: false);
            throw new TimeoutException($"tmux {string.Join(' ', args)} did not finish; did the server hold its pipes?");
        }

        return new Result(process.ExitCode, await output, await error);
    }

    private static Process Start(IEnumerable<string> args)
    {
        ProcessStartInfo info = new(Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("Could not start tmux.");
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within 20s.");
            }

            await Task.Delay(100);
        }
    }

    private sealed record Result(int ExitCode, string Output, string Error);
}

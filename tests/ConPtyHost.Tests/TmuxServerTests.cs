using System.Text;
using ConPtyHost.Core.Server;

namespace ConPtyHost.Tests;

public class TmuxServerTests
{
    private static readonly CommandContext NoInput = new([], null);

    [Test]
    public async Task Omnigents_launch_sequence_starts_the_split_command()
    {
        await using Harness harness = new();

        CommandResult result = await harness.LaunchAsync("'C:\\Program Files\\PowerShell\\7\\pwsh.exe' -NoLogo");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(harness.Terminal!.Argv.SequenceEqual(["C:\\Program Files\\PowerShell\\7\\pwsh.exe", "-NoLogo"])).IsTrue();
        await Assert.That(harness.Terminal.Size).IsEqualTo((80, 24));
        await Assert.That((await harness.RunAsync("display-message", "-p", "-t", "main", "#{history_limit}")).Output).IsEqualTo("5000\n");
    }

    [Test]
    public async Task A_second_session_is_refused()
    {
        await using Harness harness = new();
        await harness.LaunchAsync("sh");

        CommandResult again = await harness.RunAsync("new-session", "-d", "-s", "main", "sh");

        await Assert.That(again.Error).IsEqualTo("duplicate session: main");
    }

    [Test]
    public async Task A_failing_command_stops_the_sequence()
    {
        await using Harness harness = new();
        await harness.LaunchAsync("sh");

        CommandResult result = await harness.Server.ExecuteAsync(
            [["has-session", "-t", "other"], ["send-keys", "-t", "main", "-l", "never"]],
            NoInput);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(harness.Terminal!.Written).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Send_keys_types_literals_names_and_hex()
    {
        await using Harness harness = new();
        await harness.LaunchAsync("sh");

        await harness.RunAsync("send-keys", "-l", "-t", "main", "echo hi");
        await harness.RunAsync("send-keys", "-t", "main", "Enter");
        await harness.RunAsync("send-keys", "-H", "-t", "main", "03", "1b");

        await Assert.That(harness.Terminal!.Written).IsEqualTo("echo hi\r\u0003\u001b");
    }

    [Test]
    [Arguments("main", true)]
    [Arguments("=main", true)]
    [Arguments("main:0.0", true)]
    [Arguments("%0", true)]
    [Arguments("other", false)]
    [Arguments("main:1", false)]
    public async Task Targets_name_the_one_session(string target, bool found)
    {
        await using Harness harness = new();
        await harness.LaunchAsync("sh");

        await Assert.That((await harness.RunAsync("has-session", "-t", target)).ExitCode).IsEqualTo(found ? 0 : 1);
    }

    [Test]
    public async Task Output_is_captured_and_streamed_to_control_clients()
    {
        await using Harness harness = new();
        await harness.LaunchAsync("sh");
        RecordingClient client = new();
        await harness.Server.AttachAsync(client);

        harness.Terminal!.Emit("hello\r\n");

        await Eventually(() => client.Received.Contains("%output %0 hello\\015\\012\n", StringComparison.Ordinal));
        await Assert.That((await harness.RunAsync("capture-pane", "-p", "-t", "main")).Output).StartsWith("hello\n");
        await Assert.That(client.Received).Contains("%session-changed $0 main\n");
    }

    [Test]
    public async Task Control_lines_resize_and_reply()
    {
        await using Harness harness = new();
        await harness.LaunchAsync("sh");
        RecordingClient client = new();
        await harness.Server.AttachAsync(client);

        await harness.Server.ExecuteControlLineAsync(client, "refresh-client -C 120x40");
        await harness.Server.ExecuteControlLineAsync(client, "no-such-command");

        await Assert.That(harness.Terminal!.Size).IsEqualTo((120, 40));
        await Assert.That(client.Received).Contains("%error ");
    }

    [Test]
    public async Task A_dead_pane_remains_and_reports_its_exit()
    {
        await using Harness harness = new();
        await harness.LaunchAsync("sh");

        harness.Terminal!.Exit(7);

        await EventuallyAsync(async () => (await harness.RunAsync("list-panes", "-t", "main", "-F", "#{pane_dead}|#{pane_dead_status}|#{pane_dead_signal}")).Output == "1|7|\n");
        await Assert.That(harness.Server.Stopped.IsCompleted).IsFalse();
    }

    [Test]
    public async Task The_pane_died_hook_detaches_control_clients()
    {
        await using Harness harness = new();
        await harness.Server.ExecuteAsync([["set-option", "-gq", "remain-on-exit", "on"], ["set-hook", "-w", "pane-died", "detach-client -a"], ["new-session", "-d", "-s", "main", "sh"]], NoInput);
        RecordingClient client = new();
        await harness.Server.AttachAsync(client);

        harness.Terminal!.Exit(0);

        await Eventually(() => client.Detached);
        await Assert.That(harness.Server.Stopped.IsCompleted).IsFalse();
    }

    [Test]
    public async Task Without_remain_on_exit_the_server_stops_with_its_pane()
    {
        await using Harness harness = new();
        await harness.Server.ExecuteAsync([["new-session", "-d", "-s", "main", "sh"]], NoInput);
        RecordingClient client = new();
        await harness.Server.AttachAsync(client);

        harness.Terminal!.Exit(0);

        await harness.Server.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(client.Received).Contains("%window-close @0\n");
        await Assert.That(client.Detached).IsTrue();
    }

    [Test]
    public async Task Kill_server_stops_it()
    {
        await using Harness harness = new();
        await harness.LaunchAsync("sh");

        await harness.RunAsync("kill-server");

        await harness.Server.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Buffers_round_trip_through_stdin()
    {
        await using Harness harness = new();
        await harness.LaunchAsync("sh");

        await harness.Server.ExecuteAsync([["load-buffer", "-w", "-b", "clip", "-"]], new CommandContext("copied"u8.ToArray(), null));

        await Assert.That((await harness.RunAsync("save-buffer", "-b", "clip", "-")).Output).IsEqualTo("copied");
    }

    private static async Task Eventually(Func<bool> condition) =>
        await EventuallyAsync(() => Task.FromResult(condition()));

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within 5s.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness() => Server = new TmuxServer((argv, _, columns, rows) => Terminal = new FakeTerminal(argv, columns, rows));

        public TmuxServer Server { get; }

        public FakeTerminal? Terminal { get; private set; }

        // inner/terminal.py's launch, minus the options that change nothing here.
        public Task<CommandResult> LaunchAsync(string shellCommand) =>
            Server.ExecuteAsync(
                [
                    ["set-option", "-g", "history-limit", "5000"],
                    ["set-option", "-gq", "remain-on-exit", "on"],
                    ["set-option", "-sq", "exit-empty", "off"],
                    ["set-option", "-g", "mouse", "on"],
                    ["new-session", "-d", "-s", "main", "-x", "80", "-y", "24", "-c", ".", shellCommand],
                ],
                NoInput);

        public Task<CommandResult> RunAsync(params string[] command) => Server.ExecuteAsync([command], NoInput);

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    private sealed class RecordingClient : IControlClient
    {
        private readonly StringBuilder received = new();

        public bool Detached { get; private set; }

        public string Received
        {
            get
            {
                lock (received)
                {
                    return received.ToString();
                }
            }
        }

        public ValueTask SendAsync(byte[] data)
        {
            lock (received)
            {
                received.Append(Encoding.UTF8.GetString(data));
            }

            return ValueTask.CompletedTask;
        }

        public void Detach() => Detached = true;
    }
}

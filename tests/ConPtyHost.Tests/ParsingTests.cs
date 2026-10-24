using System.Text;
using ConPtyHost.Core.Tmux;

namespace ConPtyHost.Tests;

public class ParsingTests
{
    [Test]
    public async Task Invocation_splits_omnigents_launch_sequence()
    {
        // The shape of inner/terminal.py's launch: base flags, then options and
        // new-session joined by lone ";" arguments.
        Invocation invocation = Invocation.Parse(
            ["-S", "/tmp/t/tmux.sock", "-f", "/dev/null", "set-option", "-g", "history-limit", "5000", ";", "new-session", "-d", "-s", "main", "pwsh"]);

        await Assert.That(invocation.Options.SocketPath).IsEqualTo("/tmp/t/tmux.sock");
        await Assert.That(invocation.Commands.Count).IsEqualTo(2);
        await Assert.That(invocation.Commands[1][0]).IsEqualTo("new-session");
    }

    [Test]
    public async Task Invocation_reads_control_mode_and_version_flags()
    {
        await Assert.That(Invocation.Parse(["-S", "s", "-C", "attach"]).Options.ControlMode).IsTrue();
        await Assert.That(Invocation.Parse(["-V"]).Options.PrintVersion).IsTrue();
    }

    [Test]
    public async Task Invocation_rejects_an_unknown_global_flag() =>
        await Assert.That(() => Invocation.Parse(["-Z", "ls"])).Throws<TmuxException>();

    [Test]
    public async Task Command_flags_combine_and_take_dashed_values()
    {
        CommandArguments capture = CommandArguments.Parse(["capture-pane", "-t", "main", "-pe", "-J", "-S", "-2000"]);

        await Assert.That(capture.Has('p') && capture.Has('e') && capture.Has('J')).IsTrue();
        await Assert.That(capture.Value('S')).IsEqualTo("-2000");
        await Assert.That(capture.Value('t')).IsEqualTo("main");
    }

    [Test]
    public async Task Command_aliases_resolve_to_canonical_names()
    {
        await Assert.That(CommandArguments.Parse(["set", "-gq", "remain-on-exit", "on"]).Name).IsEqualTo("set-option");
        await Assert.That(CommandArguments.Parse(["display", "-p", "x"]).Name).IsEqualTo("display-message");
    }

    [Test]
    public async Task Command_rejects_unknown_commands_and_flags()
    {
        await Assert.That(() => CommandArguments.Parse(["split-window"])).Throws<TmuxException>();
        await Assert.That(() => CommandArguments.Parse(["send-keys", "-Z"])).Throws<TmuxException>();
    }

    [Test]
    public async Task Format_expands_variables_and_escapes()
    {
        string expanded = FormatExpander.Expand(
            "#{pane_dead}|#{pane_dead_status}|#{pane_dead_signal} ##",
            name => name switch { "pane_dead" => "1", "pane_dead_status" => "7", _ => null });

        await Assert.That(expanded).IsEqualTo("1|7| #");
    }

    [Test]
    [Arguments("pwsh -NoLogo", new[] { "pwsh", "-NoLogo" })]
    [Arguments("'C:\\Program Files\\PowerShell\\7\\pwsh.exe' -NoLogo", new[] { "C:\\Program Files\\PowerShell\\7\\pwsh.exe", "-NoLogo" })]
    [Arguments("echo 'it'\"'\"'s'", new[] { "echo", "it's" })]
    [Arguments("send-keys -H -t main 61 0d", new[] { "send-keys", "-H", "-t", "main", "61", "0d" })]
    public async Task Posix_split_reverses_shlex_join(string text, string[] expected) =>
        await Assert.That(ShellWords.SplitPosix(text).SequenceEqual(expected)).IsTrue();

    [Test]
    public async Task Posix_split_rejects_an_unterminated_quote() =>
        await Assert.That(() => ShellWords.SplitPosix("echo 'oops")).Throws<TmuxException>();

    [Test]
    public async Task Windows_join_quotes_only_when_needed()
    {
        await Assert.That(ShellWords.JoinWindows(["pwsh", "-NoLogo"])).IsEqualTo("pwsh -NoLogo");
        await Assert.That(ShellWords.JoinWindows(["C:\\Program Files\\x.exe", "a\"b", "end\\"]))
            .IsEqualTo("\"C:\\Program Files\\x.exe\" \"a\\\"b\" end\\");
        await Assert.That(ShellWords.JoinWindows(["", "dir\\ with space\\"])).IsEqualTo("\"\" \"dir\\ with space\\\\\"");
    }

    [Test]
    [Arguments("Enter", false, "\r")]
    [Arguments("C-c", false, "\u0003")]
    [Arguments("C-d", false, "\u0004")]
    [Arguments("Escape", false, "\u001b")]
    [Arguments("Tab", false, "\t")]
    [Arguments("Up", false, "\u001b[A")]
    [Arguments("Up", true, "\u001bOA")]
    [Arguments("M-x", false, "\u001bx")]
    [Arguments("F5", false, "\u001b[15~")]
    [Arguments("not-a-key", false, "not-a-key")]
    public async Task Key_names_encode_as_a_terminal_sends_them(string key, bool applicationCursor, string expected) =>
        await Assert.That(Encoding.UTF8.GetString(KeyNames.Encode(key, applicationCursor))).IsEqualTo(expected);

    [Test]
    public async Task Output_lines_escape_like_tmux()
    {
        // control_bridge.unescape_control_output must turn this back into the input.
        byte[] line = ControlProtocol.OutputLine("\u001b[31mR\\\r\n\u00e9"u8.ToArray());

        await Assert.That(Encoding.UTF8.GetString(line)).IsEqualTo("%output %0 \\033[31mR\\134\\015\\012\u00e9\n");
    }

    [Test]
    public async Task Replies_wrap_output_and_errors()
    {
        string ok = Encoding.UTF8.GetString(ControlProtocol.Reply(3, "x", failed: false));
        string failed = Encoding.UTF8.GetString(ControlProtocol.Reply(4, "bad", failed: true));

        await Assert.That(ok).StartsWith("%begin ");
        await Assert.That(ok).Contains("\nx\n%end ");
        await Assert.That(failed).Contains("\nbad\n%error ");
    }
}

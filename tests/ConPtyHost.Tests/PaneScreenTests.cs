using System.Text;
using ConPtyHost.Core.Tmux;

namespace ConPtyHost.Tests;

public class PaneScreenTests
{
    private static readonly CaptureRequest Plain = new(Escapes: false, JoinWrapped: false, Start: null, End: null);

    [Test]
    public async Task Capture_prints_every_screen_row()
    {
        PaneScreen screen = Feed(new PaneScreen(20, 3, 100, _ => { }), "one\r\ntwo");

        await Assert.That(screen.Capture(Plain)).IsEqualTo("one\ntwo\n\n");
    }

    [Test]
    public async Task Capture_with_escapes_keeps_attributes_and_resets()
    {
        PaneScreen screen = Feed(new PaneScreen(20, 1, 100, _ => { }), "\u001b[1;31mRED\u001b[0m ok");

        string captured = screen.Capture(Plain with { Escapes = true });

        await Assert.That(captured).IsEqualTo("\u001b[0;1;31mRED\u001b[0m ok\n");
    }

    [Test]
    public async Task Capture_joins_wrapped_lines_only_when_asked()
    {
        // 15 characters into a 10-column pane wrap onto a second row.
        PaneScreen screen = Feed(new PaneScreen(10, 2, 100, _ => { }), "abcdefghijklmno");

        await Assert.That(screen.Capture(Plain)).IsEqualTo("abcdefghij\nklmno\n");
        await Assert.That(screen.Capture(Plain with { JoinWrapped = true })).IsEqualTo("abcdefghijklmno\n");
    }

    [Test]
    public async Task Capture_reaches_into_scrollback()
    {
        PaneScreen screen = Feed(new PaneScreen(10, 2, 100, _ => { }), "1\r\n2\r\n3\r\n4");

        await Assert.That(screen.Variable("history_size")).IsEqualTo("2");
        await Assert.That(screen.Capture(Plain)).IsEqualTo("3\n4\n");
        await Assert.That(screen.Capture(Plain with { Start = "-1" })).IsEqualTo("2\n3\n4\n");
        await Assert.That(screen.Capture(Plain with { Start = "-" })).IsEqualTo("1\n2\n3\n4\n");
    }

    [Test]
    public async Task Variables_follow_the_terminal_modes()
    {
        PaneScreen screen = Feed(new PaneScreen(20, 4, 100, _ => { }), "ab\u001b[?1000h\u001b[?1006h\u001b[?2004h\u001b[?1h\u001b[?25l");

        await Assert.That(screen.Variable("cursor_x")).IsEqualTo("2");
        await Assert.That(screen.Variable("cursor_y")).IsEqualTo("0");
        await Assert.That(screen.Variable("cursor_flag")).IsEqualTo("0");
        await Assert.That(screen.Variable("mouse_standard_flag")).IsEqualTo("1");
        await Assert.That(screen.Variable("mouse_sgr_flag")).IsEqualTo("1");
        await Assert.That(screen.Variable("bracket_paste_flag")).IsEqualTo("1");
        await Assert.That(screen.Variable("keypad_cursor_flag")).IsEqualTo("1");
        await Assert.That(screen.Variable("alternate_on")).IsEqualTo("0");

        Feed(screen, "\u001b[?1049h");
        await Assert.That(screen.Variable("alternate_on")).IsEqualTo("1");
    }

    [Test]
    public async Task Terminal_answers_reach_the_reply_callback()
    {
        List<string> replies = [];
        Feed(new PaneScreen(20, 4, 100, data => replies.Add(Encoding.UTF8.GetString(data))), "\u001b[6n");

        await Assert.That(replies.Count).IsEqualTo(1);
        await Assert.That(replies[0]).IsEqualTo("\u001b[1;1R");
    }

    private static PaneScreen Feed(PaneScreen screen, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        screen.Feed(bytes, bytes.Length);
        return screen;
    }
}

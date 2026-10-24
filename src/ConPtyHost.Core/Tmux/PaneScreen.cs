using System.Globalization;
using System.Text;
using XtermSharp;
using XtermTerminal = XtermSharp.Terminal;

namespace ConPtyHost.Core.Tmux;

/// <summary>What <c>capture-pane</c> was asked for.</summary>
/// <param name="Escapes"><c>-e</c>: keep colours and attributes as SGR sequences.</param>
/// <param name="JoinWrapped"><c>-J</c>: join lines the pane wrapped back into one.</param>
/// <param name="Start"><c>-S</c>: a line number, or <c>-</c> for the start of history.</param>
/// <param name="End"><c>-E</c>: a line number, or <c>-</c> for the bottom of the screen.</param>
public sealed record CaptureRequest(bool Escapes, bool JoinWrapped, string? Start, string? End);

/// <summary>
/// The pane's screen and scrollback, kept by a VT emulator fed the pane's output.
/// </summary>
/// <remarks>
/// XtermSharp is a port of xterm.js's core, so what is captured here matches
/// what the browser's xterm.js renders from the same bytes. The emulator is not
/// thread-safe; every member takes the same lock.
/// </remarks>
public sealed class PaneScreen
{
    private readonly Lock gate = new();
    private readonly XtermTerminal terminal;

    /// <param name="reply">
    /// Receives the bytes the terminal answers with, such as a cursor-position
    /// report. They must be written back to the pane's input.
    /// </param>
    public PaneScreen(int columns, int rows, int historyLimit, Action<byte[]> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        HistoryLimit = historyLimit;
        terminal = new XtermTerminal(
            new ReplyDelegate(reply),
            new TerminalOptions { Cols = columns, Rows = rows, Scrollback = historyLimit });
    }

    public int HistoryLimit { get; }

    public void Feed(byte[] data, int count)
    {
        lock (gate)
        {
            terminal.Feed(data, count);
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (gate)
        {
            terminal.Resize(columns, rows);
        }
    }

    /// <summary>Render the requested lines as <c>capture-pane -p</c> prints them.</summary>
    public string Capture(CaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            XtermSharp.Buffer buffer = terminal.Buffer;
            int screenTop = buffer.YBase;
            int screenBottom = buffer.YBase + terminal.Rows - 1;
            int first = Math.Clamp(ResolveLine(request.Start, screenTop, absent: screenTop, dash: 0), 0, screenBottom);
            int last = Math.Clamp(ResolveLine(request.End, screenTop, absent: screenBottom, dash: screenBottom), first, screenBottom);

            StringBuilder output = new();
            for (int index = first; index <= last; index++)
            {
                BufferLine line = buffer.Lines[index];
                bool joinsNext = request.JoinWrapped && index < last && buffer.Lines[index + 1].IsWrapped;

                // A wrapped line's trailing blanks are content once it is joined.
                output.Append(request.Escapes ? RenderWithAttributes(line, trimRight: !joinsNext) : line.TranslateToString(!joinsNext, 0, terminal.Cols).ToString());
                if (!joinsNext)
                {
                    output.Append('\n');
                }
            }

            return output.ToString();
        }
    }

    /// <summary>One format variable, or null when this pane doesn't define it.</summary>
    public string? Variable(string name)
    {
        lock (gate)
        {
            return name switch
            {
                "cursor_x" => Number(terminal.Buffer.X),
                "cursor_y" => Number(terminal.Buffer.Y),
                "cursor_flag" => Flag(!terminal.CursorHidden),
                "alternate_on" => Flag(terminal.Buffers.IsAlternateBuffer),
                "mouse_standard_flag" => Flag(terminal.MouseMode == MouseMode.VT200),
                "mouse_button_flag" => Flag(terminal.MouseMode == MouseMode.ButtonEventTracking),
                "mouse_all_flag" => Flag(terminal.MouseMode == MouseMode.AnyEvent),
                "mouse_sgr_flag" => Flag(terminal.MouseProtocol == MouseProtocolEncoding.SGR),
                "mouse_utf8_flag" => Flag(terminal.MouseProtocol == MouseProtocolEncoding.UTF8),
                "keypad_cursor_flag" => Flag(terminal.ApplicationCursor),
                "bracket_paste_flag" => Flag(terminal.BracketedPasteMode),
                "history_size" => Number(terminal.Buffer.YBase),
                "history_limit" => Number(HistoryLimit),
                "pane_width" => Number(terminal.Cols),
                "pane_height" => Number(terminal.Rows),
                _ => null,
            };
        }
    }

    public bool ApplicationCursor
    {
        get
        {
            lock (gate)
            {
                return terminal.ApplicationCursor;
            }
        }
    }

    // tmux numbers screen lines from 0 at the top and negative numbers reach
    // into history. Without the flag a capture covers the screen; "-" means
    // the start of history for -S and the bottom of the screen for -E.
    private static int ResolveLine(string? value, int screenTop, int absent, int dash) => value switch
    {
        null => absent,
        "-" => dash,
        _ when int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int line) => screenTop + line,
        _ => throw new TmuxException($"invalid line: {value}"),
    };

    private string RenderWithAttributes(BufferLine line, bool trimRight)
    {
        int width = trimRight ? line.GetTrimmedLength() : Math.Min(line.Length, terminal.Cols);
        StringBuilder text = new();
        int current = CharData.DefaultAttr;
        for (int column = 0; column < width; column++)
        {
            CharData cell = line[column];
            if (cell.Width == 0)
            {
                continue;
            }

            if (cell.Attribute != current)
            {
                text.Append(Sgr(cell.Attribute));
                current = cell.Attribute;
            }

            text.Append(cell.IsNullChar() ? " " : cell.Rune.ToString());
        }

        if (current != CharData.DefaultAttr)
        {
            text.Append("\u001b[0m");
        }

        return text.ToString();
    }

    // XtermSharp packs a cell's attributes as xterm.js does: background in the
    // low 9 bits, foreground in the next 9, style flags above; 256 is the
    // default colour. Each change resets first, so no state carries over.
    private static string Sgr(int attribute)
    {
        int background = attribute & 0x1ff;
        int foreground = (attribute >> 9) & 0x1ff;
        int flags = attribute >> 18;

        StringBuilder sgr = new("\u001b[0");
        foreach ((FLAGS flag, int code) in StyleCodes)
        {
            if ((flags & (int)flag) != 0)
            {
                sgr.Append(';').Append(code);
            }
        }

        AppendColour(sgr, foreground, normal: 30, bright: 90, extended: 38);
        AppendColour(sgr, background, normal: 40, bright: 100, extended: 48);
        return sgr.Append('m').ToString();
    }

    private static readonly (FLAGS Flag, int Code)[] StyleCodes =
    [
        (FLAGS.BOLD, 1),
        (FLAGS.DIM, 2),
        (FLAGS.ITALIC, 3),
        (FLAGS.UNDERLINE, 4),
        (FLAGS.BLINK, 5),
        (FLAGS.INVERSE, 7),
        (FLAGS.INVISIBLE, 8),
        (FLAGS.CrossedOut, 9),
    ];

    private static void AppendColour(StringBuilder sgr, int colour, int normal, int bright, int extended)
    {
        if (colour >= 256)
        {
            return;
        }

        if (colour < 8)
        {
            sgr.Append(';').Append(normal + colour);
        }
        else if (colour < 16)
        {
            sgr.Append(';').Append(bright + colour - 8);
        }
        else
        {
            sgr.Append(CultureInfo.InvariantCulture, $";{extended};5;{colour}");
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "1" : "0";

    private sealed class ReplyDelegate(Action<byte[]> reply) : ITerminalDelegate
    {
        public void Send(byte[] data) => reply(data);

        public bool IsProcessTrusted() => true;

        public void SetTerminalTitle(XtermTerminal source, string title)
        {
        }

        public void SetTerminalIconTitle(XtermTerminal source, string title)
        {
        }

        public void ShowCursor(XtermTerminal source)
        {
        }

        public void SizeChanged(XtermTerminal source)
        {
        }

        public string WindowCommand(XtermTerminal source, WindowManipulationCommand command, params int[] args) => string.Empty;
    }
}

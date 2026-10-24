using System.Text;

namespace ConPtyHost.Core.Tmux;

/// <summary>Translates tmux key names into the bytes a terminal sends.</summary>
public static class KeyNames
{
    private static readonly Dictionary<string, string> Plain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = "\r",
        ["Tab"] = "\t",
        ["BTab"] = "\u001b[Z",
        ["BSpace"] = "\u007f",
        ["Escape"] = "\u001b",
        ["Space"] = " ",
        ["PageUp"] = "\u001b[5~",
        ["PPage"] = "\u001b[5~",
        ["PageDown"] = "\u001b[6~",
        ["NPage"] = "\u001b[6~",
        ["IC"] = "\u001b[2~",
        ["Insert"] = "\u001b[2~",
        ["DC"] = "\u001b[3~",
        ["Delete"] = "\u001b[3~",
        ["F1"] = "\u001bOP",
        ["F2"] = "\u001bOQ",
        ["F3"] = "\u001bOR",
        ["F4"] = "\u001bOS",
        ["F5"] = "\u001b[15~",
        ["F6"] = "\u001b[17~",
        ["F7"] = "\u001b[18~",
        ["F8"] = "\u001b[19~",
        ["F9"] = "\u001b[20~",
        ["F10"] = "\u001b[21~",
        ["F11"] = "\u001b[23~",
        ["F12"] = "\u001b[24~",
    };

    // Cursor keys switch between CSI and SS3 forms with DECCKM.
    private static readonly Dictionary<string, char> Cursor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Up"] = 'A',
        ["Down"] = 'B',
        ["Right"] = 'C',
        ["Left"] = 'D',
        ["Home"] = 'H',
        ["End"] = 'F',
    };

    /// <summary>Encode one key name, as <c>send-keys</c> without <c>-l</c> does.</summary>
    /// <remarks>
    /// Supports <c>C-</c> and <c>M-</c> prefixes. A name that is not a key is
    /// typed literally, which is also what tmux does.
    /// </remarks>
    /// <param name="key">A key name such as <c>Enter</c>, <c>C-c</c> or <c>Up</c>.</param>
    /// <param name="applicationCursor">Whether the pane enabled application cursor keys.</param>
    public static byte[] Encode(string key, bool applicationCursor)
    {
        ArgumentNullException.ThrowIfNull(key);

        bool control = false;
        bool meta = false;
        string name = key;
        while (name.Length > 2 && name[1] == '-' && name[0] is 'C' or 'M' or 'c' or 'm')
        {
            if (name[0] is 'C' or 'c')
            {
                control = true;
            }
            else
            {
                meta = true;
            }

            name = name[2..];
        }

        string? bytes = Plain.TryGetValue(name, out string? plain)
            ? plain
            : Cursor.TryGetValue(name, out char final)
                ? (applicationCursor ? "\u001bO" : "\u001b[") + final
                : null;

        if (bytes is null)
        {
            if (!control && !meta)
            {
                return Encoding.UTF8.GetBytes(key);
            }

            bytes = name;
        }

        if (control && bytes.Length == 1)
        {
            bytes = ControlCharacter(bytes[0]).ToString();
        }

        return Encoding.UTF8.GetBytes(meta ? "\u001b" + bytes : bytes);
    }

    private static char ControlCharacter(char character) => character switch
    {
        ' ' or '@' or '2' => '\0',
        '?' => '\u007f',
        >= 'a' and <= 'z' => (char)(character - 'a' + 1),
        >= '@' and <= '_' => (char)(character - '@'),
        _ => character,
    };
}

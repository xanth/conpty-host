using System.Globalization;
using System.Text;

namespace ConPtyHost.Core.Tmux;

/// <summary>Encodes the lines of tmux's control-mode protocol.</summary>
public static class ControlProtocol
{
    /// <summary>The <c>%output</c> line for one chunk of pane output.</summary>
    /// <remarks>
    /// Bytes below space and the backslash become three-digit octal
    /// <c>\ooo</c>; everything else, including non-ASCII bytes, passes
    /// through, exactly as tmux escapes them.
    /// </remarks>
    public static byte[] OutputLine(ReadOnlySpan<byte> data)
    {
        const string prefix = "%output %0 ";
        List<byte> line = new(prefix.Length + (data.Length * 2) + 1);
        line.AddRange(Encoding.ASCII.GetBytes(prefix));
        foreach (byte value in data)
        {
            if (value < 0x20 || value == (byte)'\\')
            {
                line.Add((byte)'\\');
                line.Add((byte)('0' + ((value >> 6) & 7)));
                line.Add((byte)('0' + ((value >> 3) & 7)));
                line.Add((byte)('0' + (value & 7)));
            }
            else
            {
                line.Add(value);
            }
        }

        line.Add((byte)'\n');
        return [.. line];
    }

    /// <summary>Wrap a command's reply in the <c>%begin</c> / <c>%end</c> block tmux sends.</summary>
    public static byte[] Reply(long number, string body, bool failed)
    {
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        StringBuilder reply = new();
        reply.Append(CultureInfo.InvariantCulture, $"%begin {time} {number} 1\n");
        reply.Append(body);
        if (body.Length > 0 && body[^1] != '\n')
        {
            reply.Append('\n');
        }

        reply.Append(CultureInfo.InvariantCulture, $"{(failed ? "%error" : "%end")} {time} {number} 1\n");
        return Encoding.UTF8.GetBytes(reply.ToString());
    }
}

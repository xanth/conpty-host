using System.Text;

namespace ConPtyHost.Core.Tmux;

/// <summary>Expands tmux <c>#{variable}</c> formats.</summary>
public static class FormatExpander
{
    /// <summary>Replace each <c>#{name}</c> with its value, and <c>##</c> with <c>#</c>.</summary>
    /// <remarks>An unknown variable expands to nothing, as in tmux.</remarks>
    public static string Expand(string format, Func<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(variables);

        StringBuilder result = new(format.Length);
        for (int index = 0; index < format.Length; index++)
        {
            char current = format[index];
            if (current != '#' || index + 1 >= format.Length)
            {
                result.Append(current);
                continue;
            }

            char next = format[index + 1];
            if (next == '#')
            {
                result.Append('#');
                index++;
                continue;
            }

            int close = next == '{' ? format.IndexOf('}', index + 2) : -1;
            if (close < 0)
            {
                result.Append(current);
                continue;
            }

            result.Append(variables(format[(index + 2)..close]));
            index = close;
        }

        return result.ToString();
    }
}

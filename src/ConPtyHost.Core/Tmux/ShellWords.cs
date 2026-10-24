using System.Text;

namespace ConPtyHost.Core.Tmux;

/// <summary>Converts between command strings and argument lists.</summary>
public static class ShellWords
{
    /// <summary>Split a POSIX shell-quoted string into words.</summary>
    /// <remarks>
    /// Covers what Python's <c>shlex.join</c> produces and what control-mode
    /// command lines contain: single quotes, double quotes with backslash
    /// escapes, and backslash escapes outside quotes. No expansion is done.
    /// </remarks>
    /// <exception cref="TmuxException">An unterminated quote.</exception>
    public static IReadOnlyList<string> SplitPosix(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<string> words = [];
        StringBuilder word = new();
        bool inWord = false;

        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            switch (current)
            {
                case ' ' or '\t' or '\n':
                    if (inWord)
                    {
                        words.Add(word.ToString());
                        word.Clear();
                        inWord = false;
                    }

                    break;
                case '\'':
                    int close = text.IndexOf('\'', index + 1);
                    if (close < 0)
                    {
                        throw new TmuxException("unterminated single quote");
                    }

                    word.Append(text, index + 1, close - index - 1);
                    index = close;
                    inWord = true;
                    break;
                case '"':
                    index++;
                    while (index < text.Length && text[index] != '"')
                    {
                        if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] is '"' or '\\' or '$' or '`')
                        {
                            index++;
                        }

                        word.Append(text[index++]);
                    }

                    if (index >= text.Length)
                    {
                        throw new TmuxException("unterminated double quote");
                    }

                    inWord = true;
                    break;
                case '\\' when index + 1 < text.Length:
                    word.Append(text[++index]);
                    inWord = true;
                    break;
                default:
                    word.Append(current);
                    inWord = true;
                    break;
            }
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    /// <summary>Join words into a Windows command line that CommandLineToArgvW splits back.</summary>
    public static string JoinWindows(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        return string.Join(' ', words.Select(QuoteWindows));
    }

    private static string QuoteWindows(string word)
    {
        if (word.Length > 0 && word.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return word;
        }

        // Backslashes are literal except before a quote, so double the run
        // that precedes each quote (and the closing one) and escape the quote.
        StringBuilder quoted = new("\"");
        int backslashes = 0;
        foreach (char current in word)
        {
            if (current == '\\')
            {
                backslashes++;
                continue;
            }

            if (current == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                quoted.Append('\\', backslashes);
            }

            backslashes = 0;
            quoted.Append(current);
        }

        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }
}

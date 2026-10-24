namespace ConPtyHost.Core.Tmux;

/// <summary>One parsed command: its canonical name, flags and positional arguments.</summary>
public sealed class CommandArguments
{
    // Canonical name, then the flags that take no value and those that do.
    private static readonly Dictionary<string, (string Name, string Switches, string Valued)> Specs =
        new(StringComparer.Ordinal)
        {
            ["new-session"] = ("new-session", "dAEDPX", "sxycnFtef"),
            ["set-option"] = ("set-option", "gswpqouaFUR", "t"),
            ["set-window-option"] = ("set-window-option", "gqouaF", "t"),
            ["set-hook"] = ("set-hook", "gwpaRu", "t"),
            ["send-keys"] = ("send-keys", "lHRXMK", "tNc"),
            ["capture-pane"] = ("capture-pane", "pePJNCaqT", "tSEb"),
            ["display-message"] = ("display-message", "pavIlN", "tcdF"),
            ["list-panes"] = ("list-panes", "as", "tFf"),
            ["has-session"] = ("has-session", "", "t"),
            ["kill-session"] = ("kill-session", "aC", "t"),
            ["detach-client"] = ("detach-client", "aP", "stE"),
            ["kill-server"] = ("kill-server", "", ""),
            ["run-shell"] = ("run-shell", "bC", "td"),
            ["load-buffer"] = ("load-buffer", "w", "btc"),
            ["save-buffer"] = ("save-buffer", "a", "b"),
            ["refresh-client"] = ("refresh-client", "cDlLRSU", "CABft"),
            ["attach-session"] = ("attach-session", "dErx", "tcf"),
        };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["new"] = "new-session",
        ["set"] = "set-option",
        ["setw"] = "set-window-option",
        ["send"] = "send-keys",
        ["capturep"] = "capture-pane",
        ["display"] = "display-message",
        ["lsp"] = "list-panes",
        ["has"] = "has-session",
        ["detach"] = "detach-client",
        ["run"] = "run-shell",
        ["loadb"] = "load-buffer",
        ["saveb"] = "save-buffer",
        ["refresh"] = "refresh-client",
        ["attach"] = "attach-session",
    };

    private readonly HashSet<char> switches;
    private readonly Dictionary<char, string> values;

    private CommandArguments(string name, HashSet<char> switches, Dictionary<char, string> values, List<string> positional)
    {
        Name = name;
        this.switches = switches;
        this.values = values;
        Positional = positional;
    }

    public string Name { get; }

    public IReadOnlyList<string> Positional { get; }

    public bool Has(char flag) => switches.Contains(flag) || values.ContainsKey(flag);

    public string? Value(char flag) => values.TryGetValue(flag, out string? value) ? value : null;

    /// <summary>Parse one command, getopt style, as tmux does.</summary>
    /// <remarks>
    /// A flag that takes a value consumes the next argument even when it
    /// starts with a dash, which is how <c>capture-pane -S -2000</c> works.
    /// </remarks>
    /// <exception cref="TmuxException">An unknown command or flag.</exception>
    public static CommandArguments Parse(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0)
        {
            throw new TmuxException("no command");
        }

        string requested = tokens[0];
        string canonical = Aliases.GetValueOrDefault(requested, requested);
        if (!Specs.TryGetValue(canonical, out var spec))
        {
            throw new TmuxException($"unknown command: {requested}");
        }

        HashSet<char> switches = [];
        Dictionary<char, string> values = [];
        int index = 1;

        while (index < tokens.Count && tokens[index].Length > 1 && tokens[index][0] == '-')
        {
            string token = tokens[index++];
            if (token == "--")
            {
                break;
            }

            for (int position = 1; position < token.Length; position++)
            {
                char flag = token[position];
                if (spec.Valued.Contains(flag, StringComparison.Ordinal))
                {
                    values[flag] = position + 1 < token.Length
                        ? token[(position + 1)..]
                        : index < tokens.Count
                            ? tokens[index++]
                            : throw new TmuxException($"command {spec.Name}: -{flag} expects an argument");
                    break;
                }

                if (!spec.Switches.Contains(flag, StringComparison.Ordinal))
                {
                    throw new TmuxException($"command {spec.Name}: unknown flag -{flag}");
                }

                switches.Add(flag);
            }
        }

        return new CommandArguments(spec.Name, switches, values, [.. tokens.Skip(index)]);
    }
}

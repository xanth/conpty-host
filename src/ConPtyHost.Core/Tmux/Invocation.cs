namespace ConPtyHost.Core.Tmux;

/// <summary>Flags given before the first command.</summary>
public sealed record GlobalOptions(string? SocketPath, bool ControlMode, bool PrintVersion);

/// <summary>A tmux command line: global flags and the command sequence.</summary>
public sealed record Invocation(GlobalOptions Options, IReadOnlyList<IReadOnlyList<string>> Commands)
{
    /// <summary>Split <paramref name="args"/> the way tmux does.</summary>
    /// <remarks>
    /// Global flags come first; the rest is commands separated by a lone
    /// <c>;</c> argument. <c>-f</c> is accepted and ignored: callers pass it
    /// only to keep a user's config out, and there is no config here.
    /// </remarks>
    /// <exception cref="TmuxException">An unknown or incomplete global flag.</exception>
    public static Invocation Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? socketPath = null;
        bool controlMode = false;
        bool printVersion = false;
        int index = 0;

        while (index < args.Count && args[index].Length > 1 && args[index][0] == '-')
        {
            string token = args[index++];
            if (token == "--")
            {
                break;
            }

            for (int position = 1; position < token.Length; position++)
            {
                char flag = token[position];
                switch (flag)
                {
                    case 'C':
                        controlMode = true;
                        break;
                    case 'V':
                        printVersion = true;
                        break;
                    case '2' or 'D' or 'l' or 'N' or 'u' or 'v':
                        break;
                    case 'S' or 'f' or 'L' or 'c' or 'T':
                        string value = position + 1 < token.Length
                            ? token[(position + 1)..]
                            : index < args.Count
                                ? args[index++]
                                : throw new TmuxException($"option requires an argument -- {flag}");
                        if (flag == 'S')
                        {
                            socketPath = value;
                        }
                        else if (flag is 'L' or 'c' or 'T')
                        {
                            throw new TmuxException($"unsupported option -- {flag}");
                        }

                        position = token.Length;
                        break;
                    default:
                        throw new TmuxException($"unknown option -- {flag}");
                }
            }
        }

        List<IReadOnlyList<string>> commands = [];
        List<string> current = [];
        for (; index < args.Count; index++)
        {
            if (args[index] == ";")
            {
                if (current.Count > 0)
                {
                    commands.Add(current);
                    current = [];
                }

                continue;
            }

            current.Add(args[index]);
        }

        if (current.Count > 0)
        {
            commands.Add(current);
        }

        return new Invocation(new GlobalOptions(socketPath, controlMode, printVersion), commands);
    }
}

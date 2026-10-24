using System.Text;

namespace ConPtyHost.Core.Server;

/// <summary>What a command sequence printed, and whether it failed.</summary>
public sealed record CommandResult(string Output, string? Error)
{
    public int ExitCode => Error is null ? 0 : 1;

    internal static CommandResult From(StringBuilder output, string? error) => new(output.ToString(), error);
}

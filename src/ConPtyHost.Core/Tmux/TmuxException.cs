namespace ConPtyHost.Core.Tmux;

/// <summary>A command failed the way tmux reports failures: one message, exit 1.</summary>
public sealed class TmuxException : Exception
{
    public TmuxException()
    {
    }

    public TmuxException(string message)
        : base(message)
    {
    }

    public TmuxException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

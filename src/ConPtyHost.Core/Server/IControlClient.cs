namespace ConPtyHost.Core.Server;

/// <summary>A client attached in control mode (<c>tmux -C attach</c>).</summary>
public interface IControlClient
{
    /// <summary>Send protocol bytes: <c>%output</c> lines, replies, notifications.</summary>
    ValueTask SendAsync(byte[] data);

    /// <summary>End the client's attachment after anything already sent.</summary>
    void Detach();
}

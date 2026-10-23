using System.Threading.Channels;

namespace ConPtyHost.Core.Terminal;

/// <summary>
/// Where session notifications land on their way out of the process.
/// </summary>
/// <remarks>
/// Notifications are produced on whichever thread drained a pipe or reaped a
/// process, but a transport writes on its own loop. An unbounded channel is
/// the handoff: handlers never block on the transport, and the transport
/// reads at its own pace.
/// </remarks>
public sealed class SessionEventStream
{
    private readonly Channel<object> channel = Channel.CreateUnbounded<object>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<object> Reader => channel.Reader;

    internal ValueTask PublishAsync(object notification, CancellationToken cancellationToken) =>
        channel.Writer.WriteAsync(notification, cancellationToken);

    /// <summary>No further notifications will be produced.</summary>
    public void Complete() => channel.Writer.TryComplete();
}

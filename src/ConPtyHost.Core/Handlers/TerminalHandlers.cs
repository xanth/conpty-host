using ConPtyHost.Core.Contracts;
using ConPtyHost.Core.Terminal;
using Mediator;

namespace ConPtyHost.Core.Handlers;

public sealed class StartSessionHandler(ITerminalBackend backend, SessionRegistry registry)
    : IRequestHandler<StartSession, SessionStarted>
{
    public async ValueTask<SessionStarted> Handle(StartSession request, CancellationToken cancellationToken)
    {
        ITerminalSession session = await backend
            .StartAsync(request.Command, request.WorkingDirectory, request.Width, request.Height, cancellationToken)
            .ConfigureAwait(false);

        return new SessionStarted(registry.Add(session), session.ProcessId);
    }
}

public sealed class WriteInputHandler(SessionRegistry registry) : IRequestHandler<WriteInput, Unit>
{
    public async ValueTask<Unit> Handle(WriteInput request, CancellationToken cancellationToken)
    {
        ITerminalSession session = registry.Require(request.SessionId);
        await session.Input.WriteAsync(request.Data, cancellationToken).ConfigureAwait(false);
        await session.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed class ResizeSessionHandler(SessionRegistry registry) : IRequestHandler<ResizeSession, Unit>
{
    public async ValueTask<Unit> Handle(ResizeSession request, CancellationToken cancellationToken)
    {
        await registry.Require(request.SessionId)
            .ResizeAsync(request.Width, request.Height, cancellationToken)
            .ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed class CloseSessionHandler(SessionRegistry registry) : IRequestHandler<CloseSession, Unit>
{
    public async ValueTask<Unit> Handle(CloseSession request, CancellationToken cancellationToken)
    {
        _ = await registry.RemoveAsync(request.SessionId).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>
/// Forwards session output to the event stream a transport reads.
/// </summary>
/// <remarks>
/// Mediator's generator requires a handler for every declared message, which
/// is why these exist as real handlers rather than being left implicit.
/// </remarks>
public sealed class SessionOutputHandler(SessionEventStream events)
    : INotificationHandler<SessionOutput>
{
    public ValueTask Handle(SessionOutput notification, CancellationToken cancellationToken) =>
        events.PublishAsync(notification, cancellationToken);
}

public sealed class SessionExitedHandler(SessionEventStream events, SessionRegistry registry)
    : INotificationHandler<SessionExited>
{
    public async ValueTask Handle(SessionExited notification, CancellationToken cancellationToken)
    {
        // An exited session is unusable; drop it before telling anyone, so a
        // late request fails as "no such session" rather than on a dead pipe.
        _ = await registry.RemoveAsync(notification.SessionId).ConfigureAwait(false);
        await events.PublishAsync(notification, cancellationToken).ConfigureAwait(false);
    }
}

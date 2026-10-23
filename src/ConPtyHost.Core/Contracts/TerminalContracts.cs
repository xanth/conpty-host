using Mediator;
using PolyType;

namespace ConPtyHost.Core.Contracts;

/// <summary>
/// Wire contracts between a host process and this backend.
/// </summary>
/// <remarks>
/// Shapes are generated at compile time by PolyType (<c>[GenerateShape]</c>),
/// which is what lets Nerdbank.MessagePack serialise them without reflection
/// or IL emit, keeping the host AOT-publishable.
/// </remarks>
[GenerateShape]
public partial record StartSession(
    string Command,
    string? WorkingDirectory,
    short Width,
    short Height) : IRequest<SessionStarted>;

[GenerateShape]
public partial record SessionStarted(string SessionId, int ProcessId);

[GenerateShape]
public partial record WriteInput(string SessionId, byte[] Data) : IRequest<Unit>;

[GenerateShape]
public partial record ResizeSession(string SessionId, short Width, short Height) : IRequest<Unit>;

[GenerateShape]
public partial record CloseSession(string SessionId) : IRequest<Unit>;

/// <summary>Output produced by a session, streamed to the host as it arrives.</summary>
[GenerateShape]
public partial record SessionOutput(string SessionId, byte[] Data) : INotification;

/// <summary>The attached child exited; the session is no longer usable.</summary>
[GenerateShape]
public partial record SessionExited(string SessionId, int ExitCode) : INotification;

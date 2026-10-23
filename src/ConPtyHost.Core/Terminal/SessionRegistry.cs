using System.Collections.Concurrent;

namespace ConPtyHost.Core.Terminal;

/// <summary>Tracks live sessions by id so requests can address them.</summary>
public sealed class SessionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ITerminalSession> sessions = new(StringComparer.Ordinal);

    public string Add(ITerminalSession session)
    {
        string id = Guid.NewGuid().ToString("n");
        sessions[id] = session;
        return id;
    }

    public bool TryGet(string sessionId, out ITerminalSession session) =>
        sessions.TryGetValue(sessionId, out session!);

    public ITerminalSession Require(string sessionId) =>
        TryGet(sessionId, out ITerminalSession session)
            ? session
            : throw new KeyNotFoundException($"No live session '{sessionId}'.");

    public async ValueTask<bool> RemoveAsync(string sessionId)
    {
        if (!sessions.TryRemove(sessionId, out ITerminalSession? session))
        {
            return false;
        }

        await session.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (string id in sessions.Keys)
        {
            await RemoveAsync(id).ConfigureAwait(false);
        }
    }
}

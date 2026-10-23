using ConPtyHost.Core.Terminal;

namespace ConPtyHost.Tests;

/// <summary>
/// Registry behaviour, exercised with a fake session so these run on any OS.
/// </summary>
public class SessionRegistryTests
{
    [Test]
    public async Task Add_returns_distinct_ids()
    {
        await using SessionRegistry registry = new();
        string first = registry.Add(new FakeSession());
        string second = registry.Add(new FakeSession());

        await Assert.That(first).IsNotEqualTo(second);
    }

    [Test]
    public async Task Require_throws_for_an_unknown_session()
    {
        await using SessionRegistry registry = new();

        await Assert.That(() => registry.Require("missing"))
            .Throws<KeyNotFoundException>();
    }

    [Test]
    public async Task RemoveAsync_disposes_the_session_once()
    {
        await using SessionRegistry registry = new();
        FakeSession session = new();
        string id = registry.Add(session);

        await Assert.That(await registry.RemoveAsync(id)).IsTrue();
        await Assert.That(session.DisposeCount).IsEqualTo(1);
        // A second removal is a no-op rather than a double dispose.
        await Assert.That(await registry.RemoveAsync(id)).IsFalse();
        await Assert.That(session.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_disposes_every_live_session()
    {
        FakeSession first = new();
        FakeSession second = new();
        SessionRegistry registry = new();
        registry.Add(first);
        registry.Add(second);

        await registry.DisposeAsync();

        await Assert.That(first.DisposeCount).IsEqualTo(1);
        await Assert.That(second.DisposeCount).IsEqualTo(1);
    }

    private sealed class FakeSession : ITerminalSession
    {
        public int DisposeCount { get; private set; }

        public int ProcessId => 4321;

        public Stream Output { get; } = new MemoryStream();

        public Stream Input { get; } = new MemoryStream();

        public ValueTask ResizeAsync(short width, short height, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}

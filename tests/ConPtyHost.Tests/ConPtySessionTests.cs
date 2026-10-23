using System.Runtime.InteropServices;
using ConPtyHost.Core.Terminal;

namespace ConPtyHost.Tests;

/// <summary>
/// Real pseudoconsole sessions. Windows-only by construction: these are the
/// tests that must run on a Windows agent, since nothing here can be faked
/// without also faking the thing under test.
/// </summary>
public class ConPtySessionTests
{
    [Test]
    [Skip("Requires a Windows 10 1809+ agent; see OPEN-QUESTIONS.md on CI.")]
    public async Task A_session_echoes_what_it_is_told_to_print()
    {
        await Assert.That(OperatingSystem.IsWindows()).IsTrue();

        ConPtyTerminalBackend backend = new();
        await using ITerminalSession session = await backend.StartAsync(
            "pwsh -NoLogo -NoProfile -Command \"Write-Output conpty-ok\"",
            workingDirectory: null,
            width: 120,
            height: 30);

        await Assert.That(session.ProcessId).IsGreaterThan(0);

        using StreamReader reader = new(session.Output);
        char[] buffer = new char[4096];
        int read = await reader.ReadAsync(buffer, CancellationToken.None);

        await Assert.That(new string(buffer, 0, read)).Contains("conpty-ok");
    }

    [Test]
    [Skip("Requires a Windows 10 1809+ agent; see OPEN-QUESTIONS.md on CI.")]
    public async Task Resize_is_accepted_by_a_live_session()
    {
        ConPtyTerminalBackend backend = new();
        await using ITerminalSession session = await backend.StartAsync(
            "pwsh -NoLogo -NoProfile", workingDirectory: null, width: 80, height: 24);

        await session.ResizeAsync(132, 43);
    }

    [Test]
    public async Task Starting_a_session_rejects_a_blank_command()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Argument validation happens before any P/Invoke, but the type is
            // platform-gated, so only assert it where it can be constructed.
            return;
        }

        ConPtyTerminalBackend backend = new();
        await Assert.That(async () => await backend.StartAsync("  ", null, 80, 24))
            .Throws<ArgumentException>();
    }
}

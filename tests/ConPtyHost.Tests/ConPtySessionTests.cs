using System.Runtime.Versioning;
using System.Text;
using ConPtyHost.Core.Terminal;

namespace ConPtyHost.Tests;

/// <summary>
/// Real pseudoconsole sessions. Windows-only by construction: these are the
/// tests that must run on a Windows agent, since nothing here can be faked
/// without also faking the thing under test.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public class ConPtySessionTests
{
    [Test]
    public async Task A_session_echoes_what_it_is_told_to_print()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await Assert.That(OperatingSystem.IsWindows()).IsTrue();

        ConPtyTerminalBackend backend = new();
        await using ITerminalSession session = await backend.StartAsync(
            "pwsh -NoLogo -NoProfile -Command \"Write-Output conpty-ok\"",
            workingDirectory: null,
            width: 120,
            height: 30);

        await Assert.That(session.ProcessId).IsGreaterThan(0);

        // ConPTY emits terminal setup sequences before any program output, so
        // the marker will not be in the first chunk. Accumulate until it shows
        // up, the stream ends, or the clock runs out.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using StreamReader reader = new(session.Output);
        StringBuilder seen = new();
        char[] buffer = new char[4096];

        while (!seen.ToString().Contains("conpty-ok", StringComparison.Ordinal))
        {
            int read = await reader.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }

            seen.Append(buffer, 0, read);
        }

        await Assert.That(seen.ToString()).Contains("conpty-ok");
    }

    [Test]
    public async Task Resize_is_accepted_by_a_live_session()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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

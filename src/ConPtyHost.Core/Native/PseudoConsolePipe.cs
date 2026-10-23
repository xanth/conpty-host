using System.Runtime.InteropServices;

namespace ConPtyHost.Core.Native;

/// <summary>
/// An anonymous pipe pair handed to CreatePseudoConsole.
/// </summary>
/// <remarks>
/// ConPTY owns one end of each pipe for the lifetime of the pseudoconsole:
/// it reads the session's input from <see cref="ReadSide"/> and writes the
/// session's output to <see cref="WriteSide"/>. Closing these out of order
/// relative to <see cref="PseudoConsole"/> is what produces the classic
/// ConPTY hang, so disposal order matters — see PseudoConsole's remarks.
/// </remarks>
internal sealed class PseudoConsolePipe : IDisposable
{
    internal nint ReadSide { get; }

    internal nint WriteSide { get; }

    internal PseudoConsolePipe()
    {
        if (!PseudoConsoleApi.CreatePipe(out nint readSide, out nint writeSide, 0, 0))
        {
            throw new InvalidOperationException(
                "Could not create an anonymous pipe for the pseudoconsole.",
                Marshal.GetExceptionForHR(Marshal.GetLastPInvokeError()));
        }

        ReadSide = readSide;
        WriteSide = writeSide;
    }

    public void Dispose()
    {
        PseudoConsoleApi.CloseHandle(ReadSide);
        PseudoConsoleApi.CloseHandle(WriteSide);
    }
}

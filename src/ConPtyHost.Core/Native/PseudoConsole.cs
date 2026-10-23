using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ConPtyHost.Core.Native;

/// <summary>
/// A Windows pseudoconsole (HPCON) and the lifetime rules around it.
/// </summary>
/// <remarks>
/// Teardown order is load-bearing and is the most common source of hangs in a
/// ConPTY host: ClosePseudoConsole blocks until the attached client has
/// drained, so the output pipe must still be being read when it is called.
/// Close the pseudoconsole first, then the pipes.
/// </remarks>
internal sealed class PseudoConsole : IDisposable
{
    private readonly SafePseudoConsoleHandle handle;

    private PseudoConsole(SafePseudoConsoleHandle handle) => this.handle = handle;

    internal nint Handle => handle.DangerousGetHandle();

    internal static PseudoConsole Create(
        nint inputReadSide,
        nint outputWriteSide,
        short width,
        short height)
    {
        int hr = PseudoConsoleApi.CreatePseudoConsole(
            new Coord(width, height),
            inputReadSide,
            outputWriteSide,
            dwFlags: 0,
            out nint hPC);

        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return new PseudoConsole(new SafePseudoConsoleHandle(hPC));
    }

    /// <summary>Tell the attached client that its viewport changed.</summary>
    internal void Resize(short width, short height)
    {
        int hr = PseudoConsoleApi.ResizePseudoConsole(Handle, new Coord(width, height));
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public void Dispose() => handle.Dispose();

    private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafePseudoConsoleHandle(nint existingHandle)
            : base(ownsHandle: true)
        {
            SetHandle(existingHandle);
        }

        protected override bool ReleaseHandle()
        {
            PseudoConsoleApi.ClosePseudoConsole(handle);
            return true;
        }
    }
}

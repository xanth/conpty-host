// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Rhys Williams.
// Licensed under the MIT License.
//
// Derived from the GUIConsole.ConPTY sample in microsoft/terminal.
// https://github.com/microsoft/terminal/tree/main/samples/ConPTY/GUIConsole

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
    // conpty.dll is loaded from beside the app when the package shipped it.
    private static readonly bool BundledAvailable =
        NativeLibrary.TryLoad(
            "conpty.dll",
            typeof(PseudoConsole).Assembly,
            DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.ApplicationDirectory,
            out _);

    private readonly SafePseudoConsoleHandle handle;

    private PseudoConsole(SafePseudoConsoleHandle handle, bool bundled)
    {
        this.handle = handle;
        Bundled = bundled;
    }

    internal nint Handle => handle.DangerousGetHandle();

    /// <summary>Whether conpty.dll, rather than the inbox kernel32 API, owns this console.</summary>
    internal bool Bundled { get; }

    internal static PseudoConsole Create(
        nint inputReadSide,
        nint outputWriteSide,
        short width,
        short height)
    {
        Coord size = new(width, height);

        // A bundled conpty.dll can still fail, for instance when its
        // OpenConsole.exe is missing; the inbox API is always there.
        if (BundledAvailable
            && PseudoConsoleApi.ConptyCreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out nint bundled) == 0)
        {
            return new PseudoConsole(new SafePseudoConsoleHandle(bundled, isBundled: true), bundled: true);
        }

        int hr = PseudoConsoleApi.CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out nint inbox);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return new PseudoConsole(new SafePseudoConsoleHandle(inbox, isBundled: false), bundled: false);
    }

    /// <summary>Tell the attached client that its viewport changed.</summary>
    internal void Resize(short width, short height)
    {
        Coord size = new(width, height);
        int hr = Bundled
            ? PseudoConsoleApi.ConptyResizePseudoConsole(Handle, size)
            : PseudoConsoleApi.ResizePseudoConsole(Handle, size);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public void Dispose() => handle.Dispose();

    private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly bool isBundled;

        internal SafePseudoConsoleHandle(nint existingHandle, bool isBundled)
            : base(ownsHandle: true)
        {
            this.isBundled = isBundled;
            SetHandle(existingHandle);
        }

        protected override bool ReleaseHandle()
        {
            if (isBundled)
            {
                PseudoConsoleApi.ConptyClosePseudoConsole(handle);
            }
            else
            {
                PseudoConsoleApi.ClosePseudoConsole(handle);
            }

            return true;
        }
    }
}

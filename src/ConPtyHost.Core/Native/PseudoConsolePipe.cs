// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Rhys Williams.
// Licensed under the MIT License.
//
// Derived from the GUIConsole.ConPTY sample in microsoft/terminal.
// https://github.com/microsoft/terminal/tree/main/samples/ConPTY/GUIConsole

using System.Runtime.InteropServices;

namespace ConPtyHost.Core.Native;

/// <summary>
/// An anonymous pipe pair handed to CreatePseudoConsole.
/// </summary>
/// <remarks>
/// ConPTY duplicates the ends it is given, so the host must release its own
/// copies once the pseudoconsole and its client exist. Holding the output
/// write side open means our read side can never observe EOF: the read simply
/// blocks forever after the client stops producing.
/// </remarks>
internal sealed class PseudoConsolePipe : IDisposable
{
    private nint readSide;
    private nint writeSide;

    internal PseudoConsolePipe()
    {
        if (!PseudoConsoleApi.CreatePipe(out nint read, out nint write, 0, 0))
        {
            throw new InvalidOperationException(
                "Could not create an anonymous pipe for the pseudoconsole.",
                Marshal.GetExceptionForHR(Marshal.GetLastPInvokeError()));
        }

        readSide = read;
        writeSide = write;
    }

    internal nint ReadSide => readSide;

    internal nint WriteSide => writeSide;

    /// <summary>Release our copy of the read end; idempotent.</summary>
    internal void CloseReadSide() => Close(ref readSide);

    /// <summary>Release our copy of the write end; idempotent.</summary>
    internal void CloseWriteSide() => Close(ref writeSide);

    public void Dispose()
    {
        CloseReadSide();
        CloseWriteSide();
    }

    private static void Close(ref nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        PseudoConsoleApi.CloseHandle(handle);
        handle = 0;
    }
}

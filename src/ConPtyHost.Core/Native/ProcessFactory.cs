// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Rhys Williams.
// Licensed under the MIT License.
//
// Derived from the GUIConsole.ConPTY sample in microsoft/terminal.
// https://github.com/microsoft/terminal/tree/main/samples/ConPTY/GUIConsole

using System.Runtime.InteropServices;

namespace ConPtyHost.Core.Native;

/// <summary>A started child process attached to a pseudoconsole.</summary>
internal sealed class PseudoConsoleProcess(ProcessInformation info) : IDisposable
{
    internal int ProcessId => info.dwProcessId;

    internal nint ProcessHandle => info.hProcess;

    public void Dispose()
    {
        PseudoConsoleApi.CloseHandle(info.hThread);
        PseudoConsoleApi.CloseHandle(info.hProcess);
    }
}

/// <summary>
/// Starts a process attached to an HPCON.
/// </summary>
/// <remarks>
/// The attribute-list dance is prescribed: call
/// InitializeProcThreadAttributeList once with a null list to learn the
/// required size, allocate, initialise for real, then attach the HPCON with
/// PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE. CreateProcess must be told
/// EXTENDED_STARTUPINFO_PRESENT or the attribute list is ignored silently and
/// the child inherits the host's console instead.
/// </remarks>
internal static class ProcessFactory
{
    internal static PseudoConsoleProcess Start(string command, string? workingDirectory, nint hPC)
    {
        StartupInfoEx startupInfo = ConfigureProcessThread(hPC);
        try
        {
            // bInheritHandles must be false: the pseudoconsole, not handle
            // inheritance, is how the child receives its console.
            bool started = PseudoConsoleApi.CreateProcess(
                lpApplicationName: null,
                lpCommandLine: command,
                lpProcessAttributes: 0,
                lpThreadAttributes: 0,
                bInheritHandles: false,
                dwCreationFlags: PseudoConsoleApi.ExtendedStartupInfoPresent,
                lpEnvironment: 0,
                lpCurrentDirectory: workingDirectory,
                lpStartupInfo: ref startupInfo,
                lpProcessInformation: out ProcessInformation info);

            if (!started)
            {
                throw new InvalidOperationException(
                    $"Could not start '{command}' attached to the pseudoconsole.",
                    Marshal.GetExceptionForHR(Marshal.GetLastPInvokeError()));
            }

            return new PseudoConsoleProcess(info);
        }
        finally
        {
            if (startupInfo.lpAttributeList != 0)
            {
                PseudoConsoleApi.DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            }
        }
    }

    private static StartupInfoEx ConfigureProcessThread(nint hPC)
    {
        StartupInfoEx startupInfo = default;
        startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();

        // Null std handles, used explicitly: otherwise a host with redirected
        // stdio passes those handles to the child, which then writes around
        // the pseudoconsole instead of into it.
        startupInfo.StartupInfo.dwFlags = PseudoConsoleApi.StartfUseStdHandles;

        // First call is expected to fail; it reports the buffer size.
        nint size = 0;
        PseudoConsoleApi.InitializeProcThreadAttributeList(0, 1, 0, ref size);

        startupInfo.lpAttributeList = Marshal.AllocHGlobal(size);

        if (!PseudoConsoleApi.InitializeProcThreadAttributeList(
                startupInfo.lpAttributeList, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            startupInfo.lpAttributeList = 0;
            throw new InvalidOperationException(
                "Could not initialise the proc-thread attribute list.",
                Marshal.GetExceptionForHR(Marshal.GetLastPInvokeError()));
        }

        if (!PseudoConsoleApi.UpdateProcThreadAttribute(
                startupInfo.lpAttributeList,
                dwFlags: 0,
                attribute: PseudoConsoleApi.ProcThreadAttributePseudoConsole,
                lpValue: hPC,
                cbSize: nint.Size,
                lpPreviousValue: 0,
                lpReturnSize: 0))
        {
            PseudoConsoleApi.DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            startupInfo.lpAttributeList = 0;
            throw new InvalidOperationException(
                "Could not attach the pseudoconsole to the attribute list.",
                Marshal.GetExceptionForHR(Marshal.GetLastPInvokeError()));
        }

        return startupInfo;
    }
}

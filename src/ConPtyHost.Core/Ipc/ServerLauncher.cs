using System.Diagnostics;
using System.Runtime.InteropServices;
using ConPtyHost.Core.Native;
using ConPtyHost.Core.Tmux;

namespace ConPtyHost.Core.Ipc;

/// <summary>Starts this executable as a detached server for a socket.</summary>
public static class ServerLauncher
{
    /// <summary>The argument that makes the executable run as a server.</summary>
    public const string ServerArgument = "__server";

    /// <summary>Start <c>executable __server -S socketPath</c>, detached from the caller.</summary>
    /// <remarks>
    /// The server must hold none of the caller's handles. Callers wait for the
    /// client's stdout and stderr to close; a server that inherited them would
    /// keep them open for its whole life and hang the caller.
    /// </remarks>
    public static Task StartAsync(string executable, string socketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentException.ThrowIfNullOrEmpty(socketPath);

        if (OperatingSystem.IsWindows())
        {
            StartWindows(ShellWords.JoinWindows([executable, ServerArgument, "-S", socketPath]));
            return Task.CompletedTask;
        }

        ProcessStartInfo info = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(ServerArgument);
        info.ArgumentList.Add("-S");
        info.ArgumentList.Add(socketPath);

        // Redirecting gives the server fresh pipes instead of the caller's
        // descriptors; closing our ends leaves it holding nothing of ours.
        using Process server = Process.Start(info) ?? throw new InvalidOperationException("Could not start the tmux server.");
        server.StandardInput.Close();
        server.StandardOutput.Close();
        server.StandardError.Close();
        return Task.CompletedTask;
    }

    private static void StartWindows(string commandLine)
    {
        const uint detachedProcess = 0x00000008;
        const uint createNewProcessGroup = 0x00000200;
        const uint createBreakawayFromJob = 0x01000000;

        StartupInfo startupInfo = default;
        startupInfo.cb = Marshal.SizeOf<StartupInfo>();

        // Leaving the caller's job keeps the server alive if the caller's job
        // is torn down; a job that forbids breakaway refuses, so retry inside it.
        if (!PseudoConsoleApi.CreatePlainProcess(null, commandLine, 0, 0, false, detachedProcess | createNewProcessGroup | createBreakawayFromJob, 0, null, ref startupInfo, out ProcessInformation info)
            && !PseudoConsoleApi.CreatePlainProcess(null, commandLine, 0, 0, false, detachedProcess | createNewProcessGroup, 0, null, ref startupInfo, out info))
        {
            throw new InvalidOperationException(
                "Could not start the tmux server.",
                Marshal.GetExceptionForHR(Marshal.GetLastPInvokeError()));
        }

        PseudoConsoleApi.CloseHandle(info.hThread);
        PseudoConsoleApi.CloseHandle(info.hProcess);
    }
}

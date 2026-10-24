// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Rhys Williams.
// Licensed under the MIT License.
//
// Derived from the GUIConsole.ConPTY sample in microsoft/terminal.
// https://github.com/microsoft/terminal/tree/main/samples/ConPTY/GUIConsole

// Kept 1:1 with the sample in shape and call order. Four deviations:
//   - DllImport is replaced by source-generated LibraryImport, so the layer
//     stays AOT- and trim-safe.
//   - STARTUPINFO's unused string fields are declared as nint rather than
//     string, which keeps STARTUPINFOEX blittable and lets CreateProcess go
//     through LibraryImport. The sample never sets them.
//   - ProcessFactory sets STARTF_USESTDHANDLES with null handles. The sample
//     runs from a GUI app with no std handles; a host whose own stdio is
//     redirected would otherwise hand those handles to the child, and the
//     child's output would bypass the pseudoconsole entirely.
//   - The pseudoconsole calls go to Microsoft.Windows.Console.ConPTY's
//     conpty.dll when it ships alongside, which runs a current OpenConsole.exe
//     rather than the inbox conhost.exe; the inbox kernel32 API is the fallback.

using System.Runtime.InteropServices;

namespace ConPtyHost.Core.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct Coord(short x, short y)
{
    public short X = x;
    public short Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfo
{
    public int cb;
    public nint lpReserved;
    public nint lpDesktop;
    public nint lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public ushort wShowWindow;
    public ushort cbReserved2;
    public nint lpReserved2;
    public nint hStdInput;
    public nint hStdOutput;
    public nint hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    public StartupInfo StartupInfo;
    public nint lpAttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public nint hProcess;
    public nint hThread;
    public int dwProcessId;
    public int dwThreadId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SecurityAttributes
{
    public int nLength;
    public nint lpSecurityDescriptor;
    [MarshalAs(UnmanagedType.Bool)]
    public bool bInheritHandle;
}

internal static partial class PseudoConsoleApi
{
    private const string Kernel32 = "kernel32.dll";
    private const string Conpty = "conpty.dll";

    /// <summary>Attribute identifying an HPCON in a proc-thread attribute list.</summary>
    internal const nint ProcThreadAttributePseudoConsole = 0x00020016;

    /// <summary>Tells CreateProcess that STARTUPINFOEX, not STARTUPINFO, was passed.</summary>
    internal const uint ExtendedStartupInfoPresent = 0x00080000;

    /// <summary>STARTUPINFO flag: use the hStd* fields rather than inheriting the parent's.</summary>
    internal const int StartfUseStdHandles = 0x00000100;

    /// <summary>Seeds the pseudoconsole with the parent console's cursor position.</summary>
    internal const uint PseudoConsoleInheritCursor = 1;

    [LibraryImport(Kernel32, EntryPoint = "CreatePseudoConsole")]
    internal static partial int CreatePseudoConsole(
        Coord size,
        nint hInput,
        nint hOutput,
        uint dwFlags,
        out nint phPC);

    [LibraryImport(Kernel32, EntryPoint = "ResizePseudoConsole")]
    internal static partial int ResizePseudoConsole(nint hPC, Coord size);

    [LibraryImport(Kernel32, EntryPoint = "ClosePseudoConsole")]
    internal static partial void ClosePseudoConsole(nint hPC);

    [LibraryImport(Kernel32, EntryPoint = "CreatePipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreatePipe(
        out nint hReadPipe,
        out nint hWritePipe,
        nint lpPipeAttributes,
        int nSize);

    [LibraryImport(Kernel32, EntryPoint = "CreateProcessW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcess(
        string? lpApplicationName,
        string? lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    /// <summary>CreateProcess with a plain STARTUPINFO, for processes not attached to a pseudoconsole.</summary>
    [LibraryImport(Kernel32, EntryPoint = "CreateProcessW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreatePlainProcess(
        string? lpApplicationName,
        string? lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [LibraryImport(Kernel32, EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(
        nint lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref nint lpSize);

    [LibraryImport(Kernel32, EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(
        nint lpAttributeList,
        uint dwFlags,
        nint attribute,
        nint lpValue,
        nint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);

    [LibraryImport(Kernel32, EntryPoint = "DeleteProcThreadAttributeList")]
    internal static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

    internal const uint Infinite = 0xFFFFFFFF;

    [LibraryImport(Conpty, EntryPoint = "ConptyCreatePseudoConsole")]
    internal static partial int ConptyCreatePseudoConsole(
        Coord size,
        nint hInput,
        nint hOutput,
        uint dwFlags,
        out nint phPC);

    [LibraryImport(Conpty, EntryPoint = "ConptyResizePseudoConsole")]
    internal static partial int ConptyResizePseudoConsole(nint hPC, Coord size);

    [LibraryImport(Conpty, EntryPoint = "ConptyClosePseudoConsole")]
    internal static partial void ConptyClosePseudoConsole(nint hPC);

    [LibraryImport(Kernel32, EntryPoint = "WaitForSingleObject", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport(Kernel32, EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    [LibraryImport(Kernel32, EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    [LibraryImport(Kernel32, EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);
}

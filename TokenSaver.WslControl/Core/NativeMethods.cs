using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TokenSaver.WslControl.Core;

/// <summary>Win32 entry points used to run a process in the active user session.</summary>
internal static class NativeMethods
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(
        IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SECURITY_ATTRIBUTES sa, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFileW(
        string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disposition, uint flags, IntPtr template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessAsUserW(
        IntPtr token, string? application, [In, Out] char[] commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}


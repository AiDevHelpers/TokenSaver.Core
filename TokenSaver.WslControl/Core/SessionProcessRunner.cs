namespace TokenSaver.WslControl.Core;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

/// <summary>Result of one process run in the active user's session.</summary>
public sealed record SessionProcessResult(int ExitCode, byte[] Stdout, byte[] Stderr, bool TimedOut);

/// <summary>
///     Runs a process as the user logged on to the console session. Needed when TokenSaver runs as
///     LocalSystem: WSL distributions are registered per user, so a SYSTEM process can't see or
///     reach the user's distros. The launcher uses the user's primary token (WTSQueryUserToken)
///     and captures stdout and stderr through anonymous pipes.
/// </summary>
public interface ISessionProcessRunner
{
    /// <summary>Runs <paramref name="commandLine" /> as the console user and waits up to <paramref name="timeout" />.</summary>
    Task<SessionProcessResult> RunAsync(string commandLine, TimeSpan timeout);
}

public sealed class ConsoleUserProcessRunner : ISessionProcessRunner
{
    private const uint InvalidSessionId = 0xFFFFFFFF;
    private const uint TokenAllAccess = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionIdClass = 12;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;

    public Task<SessionProcessResult> RunAsync(string commandLine, TimeSpan timeout) =>
        Task.Run(() => RunCore(commandLine, timeout));

    private static SessionProcessResult RunCore(string commandLine, TimeSpan timeout)
    {
        uint sessionId = FindLoggedOnUserSession();
        if (sessionId == InvalidSessionId)
        {
            throw new InvalidOperationException("No active user session with a logged-on user was found.");
        }

        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero;
        IntPtr errRead = IntPtr.Zero, errWrite = IntPtr.Zero;
        IntPtr nul = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        bool processCreated = false;

        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed.");
            }

            if (!DuplicateTokenEx(userToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            }

            uint sid = sessionId;
            _ = SetTokenInformation(primaryToken, TokenSessionIdClass, ref sid, sizeof(uint));

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                environment = IntPtr.Zero;
            }

            SECURITY_ATTRIBUTES sa = new() { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
            if (!CreatePipe(out outRead, out outWrite, ref sa, 0) || !CreatePipe(out errRead, out errWrite, ref sa, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
            }

            _ = SetHandleInformation(outRead, HandleFlagInherit, 0);
            _ = SetHandleInformation(errRead, HandleFlagInherit, 0);
            nul = CreateFileW("NUL", GenericRead | GenericWrite, FileShareReadWrite, ref sa, OpenExisting, 0, IntPtr.Zero);

            STARTUPINFO si = new()
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default",
                dwFlags = StartfUseStdHandles,
                hStdInput = nul,
                hStdOutput = outWrite,
                hStdError = errWrite,
            };

            StringBuilder cmd = new(commandLine);
            if (!CreateProcessAsUserW(primaryToken, null, cmd, IntPtr.Zero, IntPtr.Zero, true,
                    CreateUnicodeEnvironment | CreateNoWindow, environment, null, ref si, out pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
            }

            processCreated = true;

            // The child holds the write ends now; close ours so the read ends see EOF on exit.
            CloseHandle(outWrite);
            outWrite = IntPtr.Zero;
            CloseHandle(errWrite);
            errWrite = IntPtr.Zero;

            using FileStream outStream = new(new SafeFileHandle(outRead, ownsHandle: false), FileAccess.Read);
            using FileStream errStream = new(new SafeFileHandle(errRead, ownsHandle: false), FileAccess.Read);
            using MemoryStream outBuf = new();
            using MemoryStream errBuf = new();
            Task outCopy = outStream.CopyToAsync(outBuf);
            Task errCopy = errStream.CopyToAsync(errBuf);

            uint wait = WaitForSingleObject(pi.hProcess, (uint)Math.Min(timeout.TotalMilliseconds, uint.MaxValue - 1));
            bool timedOut = wait == WaitTimeout;
            if (timedOut)
            {
                _ = TerminateProcess(pi.hProcess, 1);
                _ = WaitForSingleObject(pi.hProcess, 5000);
            }

            int exitCode = -1;
            if (!timedOut && wait == WaitObject0 && GetExitCodeProcess(pi.hProcess, out uint code))
            {
                exitCode = unchecked((int)code);
            }

            Task.WaitAll([outCopy, errCopy], 5000);
            return new SessionProcessResult(exitCode, outBuf.ToArray(), errBuf.ToArray(), timedOut);
        }
        finally
        {
            if (processCreated)
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }

            CloseIfValid(outWrite);
            CloseIfValid(errWrite);
            CloseIfValid(outRead);
            CloseIfValid(errRead);
            CloseIfValid(nul);
            if (environment != IntPtr.Zero)
            {
                _ = DestroyEnvironmentBlock(environment);
            }

            CloseIfValid(primaryToken);
            CloseIfValid(userToken);
        }
    }

    /// <summary>
    ///     Picks the first active session that has a logged-on user. The console session may have no
    ///     user (for example when you connect over RDP), so the console session isn't used.
    /// </summary>
    private static uint FindLoggedOnUserSession()
    {
        const int WtsActive = 0;
        const int WtsUserName = 5;
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr infos, out int count))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSEnumerateSessions failed.");
        }

        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                WTS_SESSION_INFO info = Marshal.PtrToStructure<WTS_SESSION_INFO>(infos + i * size);
                if (info.State != WtsActive)
                {
                    continue;
                }

                if (WTSQuerySessionInformationW(IntPtr.Zero, info.SessionId, WtsUserName, out IntPtr buf, out _))
                {
                    try
                    {
                        string? user = Marshal.PtrToStringUni(buf);
                        if (!string.IsNullOrEmpty(user))
                        {
                            return info.SessionId;
                        }
                    }
                    finally
                    {
                        WTSFreeMemory(buf);
                    }
                }
            }
        }
        finally
        {
            WTSFreeMemory(infos);
        }

        return InvalidSessionId;
    }

    private static void CloseIfValid(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = CloseHandle(handle);
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int length);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SECURITY_ATTRIBUTES sa, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disposition, uint flags, IntPtr template);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token, string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
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
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}

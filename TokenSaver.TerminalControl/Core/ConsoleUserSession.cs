namespace TokenSaver.TerminalControl.Core;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using TokenSaver.TerminalControl.Contracts;

/// <summary>
/// Starts processes in the logged-on console user's session. Needed when TokenSaver runs as LocalSystem:
/// a LocalSystem process lives in session 0, where it cannot show a window. Uses the same Win32 calls as
/// WslControl's ConsoleUserProcessRunner, which requires SeTcbPrivilege (held by LocalSystem).
/// </summary>
public sealed class ConsoleUserTerminalLauncher : ITerminalProcessLauncher
{
    public Process? Start(ProcessStartInfo startInfo) =>
        ConsoleUserSession.StartInUserSession(startInfo.FileName, startInfo.Arguments);
}

public static class ConsoleUserSession
{
    private const uint InvalidSessionId = 0xFFFFFFFF;
    private const uint TokenAllAccess = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionIdClass = 12;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int WtsActive = 0;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    /// <summary>True when this process runs as LocalSystem (session 0, no interactive desktop).</summary>
    public static bool RunningAsLocalSystem() =>
        WindowsIdentity.GetCurrent().IsSystem;

    /// <summary>SID of the user logged on to the first active session, or null when none is logged on.</summary>
    public static SecurityIdentifier? GetConsoleUserSid()
    {
        uint sessionId = FindLoggedOnUserSession();
        if (sessionId == InvalidSessionId)
        {
            return null;
        }

        var user = QuerySessionString(sessionId, WtsUserName);
        var domain = QuerySessionString(sessionId, WtsDomainName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        var account = string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
        return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
    }

    /// <summary>Starts <paramref name="fileName"/> with <paramref name="arguments"/> on the logged-on user's desktop.</summary>
    public static Process? StartInUserSession(string fileName, string arguments)
    {
        uint sessionId = FindLoggedOnUserSession();
        if (sessionId == InvalidSessionId)
        {
            throw new InvalidOperationException("No active user session with a logged-on user was found.");
        }

        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
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

            var startup = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default",
            };

            var commandLine = $"\"{fileName}\" {arguments}" + "\0";
            if (!CreateProcessAsUserW(primaryToken, null, commandLine.ToCharArray(), IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment, environment, null, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
            }

            _ = CloseHandle(info.hThread);
            _ = CloseHandle(info.hProcess);
            try
            {
                return Process.GetProcessById((int)info.dwProcessId);
            }
            catch (ArgumentException)
            {
                // The process already exited (wt.exe hands off to its window process). Liveness is
                // best-effort, so a null handle is acceptable here.
                return null;
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                _ = DestroyEnvironmentBlock(environment);
            }

            if (primaryToken != IntPtr.Zero)
            {
                _ = CloseHandle(primaryToken);
            }

            if (userToken != IntPtr.Zero)
            {
                _ = CloseHandle(userToken);
            }
        }
    }

    private static string? QuerySessionString(uint sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out IntPtr buf, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buf);
        }
        finally
        {
            WTSFreeMemory(buf);
        }
    }

    /// <summary>Picks the first active session with a logged-on user (the console may have none, e.g. over RDP).</summary>
    private static uint FindLoggedOnUserSession()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr infos, out int count))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSEnumerateSessions failed.");
        }

        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(infos + i * size);
                if (info.State == WtsActive && !string.IsNullOrEmpty(QuerySessionString(info.SessionId, WtsUserName)))
                {
                    return info.SessionId;
                }
            }
        }
        finally
        {
            WTSFreeMemory(infos);
        }

        return InvalidSessionId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, uint reserved, uint version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, ref uint tokenInformation, int tokenInformationLength);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? applicationName, char[] commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

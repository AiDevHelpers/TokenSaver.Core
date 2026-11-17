namespace TokenSaver.TerminalControl.Core;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using TokenSaver.TerminalControl.Contracts;
using TokenSaver.WindowsSession;

/// <summary>
/// Starts a terminal in the logged-on user's session. Needed when TokenSaver runs as LocalSystem: a LocalSystem
/// process lives in session 0, where it cannot show a window. Token and session calls come from TokenSaver.WindowsSession.
/// </summary>
public sealed class ConsoleUserTerminalLauncher : ITerminalProcessLauncher
{
    public Process? Start(ProcessStartInfo startInfo) =>
        ConsoleUserSession.StartInUserSession(startInfo.FileName, startInfo.Arguments);
}

public static class ConsoleUserSession
{
    private const uint CreateUnicodeEnvironment = 0x00000400;

    /// <summary>True when this process runs as LocalSystem.</summary>
    public static bool RunningAsLocalSystem() => UserSession.IsRunningAsLocalSystem();

    /// <summary>SID of the user logged on to the first active session, or null when none is logged on.</summary>
    public static SecurityIdentifier? GetConsoleUserSid()
    {
        uint sessionId = UserSession.FindFirstLoggedOnSession();
        return sessionId == UserSession.NoSession ? null : UserSession.GetUserSid(sessionId);
    }

    /// <summary>Starts <paramref name="fileName"/> with <paramref name="arguments"/> on the logged-on user's desktop.</summary>
    public static Process? StartInUserSession(string fileName, string arguments)
    {
        uint sessionId = UserSession.FindFirstLoggedOnSession();
        if (sessionId == UserSession.NoSession)
        {
            throw new InvalidOperationException("No active user session with a logged-on user was found.");
        }

        using var token = UserSession.OpenPrimaryToken(sessionId, bindToSession: true);
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!UserSession.TryCreateEnvironmentBlock(token, out environment))
            {
                environment = IntPtr.Zero;
            }

            var startup = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default",
            };

            var commandLine = $"\"{fileName}\" {arguments}" + "\0";
            if (!CreateProcessAsUserW(token.Handle, null, commandLine.ToCharArray(), IntPtr.Zero, IntPtr.Zero, false,
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
            UserSession.DestroyEnvironmentBlock(environment);
        }
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

#pragma warning disable SYSLIB1054, CA5392 // Plain P/Invoke declarations with explicit marshalling.
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? applicationName, char[] commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
#pragma warning restore SYSLIB1054, CA5392
}

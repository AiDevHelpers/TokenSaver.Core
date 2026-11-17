using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TokenSaver.LogViewer;

/// <summary>
/// Starts a process on the desktop of the user signed in at the console. A Windows service runs in session 0, whose
/// windows nobody sees, so the viewer is started with that user's token instead. Needs the privilege services running
/// as LocalSystem have (SeTcbPrivilege); it throws <see cref="Win32Exception"/> without it or without a signed-in user.
/// </summary>
#pragma warning disable CA1060, CA1838 // A small private P/Invoke surface for one purpose; StringBuilder buffers are the documented pattern for these calls.
[SupportedOSPlatform("windows")]
internal static class UserSessionProcess
{
    private const uint TokenAll = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint CreateNewConsole = 0x10;

    /// <summary>True when this process is in session 0, where windows cannot reach a user.</summary>
    public static bool IsServiceSession => System.Diagnostics.Process.GetCurrentProcess().SessionId == 0;

    /// <summary>Resolves a path that holds environment variables (for example %LOCALAPPDATA%) for the console user.</summary>
    public static string ExpandForConsoleUser(string template)
    {
        IntPtr token = QueryConsoleUserToken();
        try
        {
            var buffer = new StringBuilder(1024);
            if (!ExpandEnvironmentStringsForUser(token, template, buffer, (uint)buffer.Capacity))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return buffer.ToString();
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    /// <summary>Starts <paramref name="commandLine"/> (program path first) on the console user's desktop.</summary>
    public static int Start(string commandLine)
    {
        IntPtr userToken = QueryConsoleUserToken();
        IntPtr primary = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, TokenAll, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!CreateEnvironmentBlock(out environment, primary, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = "winsta0\\default" };
            var builder = new StringBuilder(commandLine);
            if (!CreateProcessAsUser(primary, null, builder, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment | CreateNewConsole, environment, null, ref startup, out ProcessInformation info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _ = CloseHandle(info.hThread);
            _ = CloseHandle(info.hProcess);
            return info.dwProcessId;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                _ = DestroyEnvironmentBlock(environment);
            }

            if (primary != IntPtr.Zero)
            {
                _ = CloseHandle(primary);
            }

            _ = CloseHandle(userToken);
        }
    }

    private static IntPtr QueryConsoleUserToken()
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF)
        {
            throw new Win32Exception("No user is signed in at the console.");
        }

        if (!WTSQueryUserToken(session, out IntPtr token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return token;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

#pragma warning disable SYSLIB1054, CA5392 // Plain P/Invoke declarations; the marshalling is simple and explicit.
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonation, int tokenType, out IntPtr duplicate);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExpandEnvironmentStringsForUser(IntPtr token, string source, StringBuilder destination, uint size);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? application,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startup,
        out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
#pragma warning restore SYSLIB1054, CA5392
}

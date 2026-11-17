namespace TokenSaver.WslControl.Core;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

using static TokenSaver.WslControl.Core.NativeMethods;

using Microsoft.Win32.SafeHandles;
using TokenSaver.WindowsSession;

/// <summary>Result of one process run in the active user's session.</summary>
public sealed record SessionProcessResult(int ExitCode, ReadOnlyMemory<byte> Stdout, ReadOnlyMemory<byte> Stderr, bool TimedOut);

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
        uint sessionId = UserSession.FindFirstLoggedOnSession();
        if (sessionId == UserSession.NoSession)
        {
            throw new InvalidOperationException("No active user session with a logged-on user was found.");
        }

        IntPtr environment = IntPtr.Zero;
        IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero;
        IntPtr errRead = IntPtr.Zero, errWrite = IntPtr.Zero;
        IntPtr nul = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        bool processCreated = false;

        using var token = UserSession.OpenPrimaryToken(sessionId, bindToSession: true);
        try
        {
            if (UserSession.TryCreateEnvironmentBlock(token, out environment) is false)
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

            char[] cmd = (commandLine + "\0").ToCharArray();
            if (!CreateProcessAsUserW(token.Handle, null, cmd, IntPtr.Zero, IntPtr.Zero, true,
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

            using SafeFileHandle outHandle = new(outRead, ownsHandle: false);
            using SafeFileHandle errHandle = new(errRead, ownsHandle: false);
            using FileStream outStream = new(outHandle, FileAccess.Read);
            using FileStream errStream = new(errHandle, FileAccess.Read);
            using MemoryStream outBuf = new();
            using MemoryStream errBuf = new();
#pragma warning disable CA2025 // Both copy tasks are awaited (Task.WaitAll) before this scope disposes the streams.
            Task outCopy = outStream.CopyToAsync(outBuf);
            Task errCopy = errStream.CopyToAsync(errBuf);
#pragma warning restore CA2025

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
            UserSession.DestroyEnvironmentBlock(environment);
        }
    }

    private static void CloseIfValid(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = CloseHandle(handle);
        }
    }
}

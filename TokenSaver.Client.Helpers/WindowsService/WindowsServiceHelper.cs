using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace TokenSaver.Client.Helpers.WindowsService;

/// <summary>
/// Utilities for querying and managing Windows services.
/// </summary>
public static class WindowsServiceHelper
{
    /// <summary>
    /// Checks if a Windows service with the given name is installed (registered with SCM).
    /// </summary>
    public static async Task<bool> IsServiceInstalledAsync(string serviceName)
    {
        try
        {
            var result = await RunScExeAsync("query", serviceName);
            return result.Output.Contains("STATE", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a Windows service using sc.exe.
    /// </summary>
    public static async Task<int> StartServiceAsync(string serviceName)
    {
        var result = await RunScExeAsync("start", serviceName);
        return result.ExitCode;
    }

    /// <summary>
    /// Stops a Windows service using sc.exe.
    /// </summary>
    public static async Task<int> StopServiceAsync(string serviceName)
    {
        var result = await RunScExeAsync("stop", serviceName);
        return result.ExitCode;
    }

    /// <summary>
    /// Deletes a Windows service using sc.exe.
    /// </summary>
    public static async Task<int> DeleteServiceAsync(string serviceName)
    {
        var result = await RunScExeAsync("delete", serviceName);
        return result.ExitCode;
    }

    /// <summary>
    /// Runs sc.exe with the given arguments and returns the result.
    /// </summary>
    private static Task<(int ExitCode, string Output)> RunScExeAsync(params string[] args)
    {
        return RunProcessAsync("sc.exe", args);
    }

    /// <summary>
    /// Runs a process and captures its output and exit code.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, params string[] args)
    {
        return await Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return (1, string.Empty);

            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            return (proc.ExitCode, output + error);
        });
    }

    /// <summary>
    /// Installs a service via an MSI file using msiexec with UAC elevation.
    /// </summary>
    public static async Task<int> InstallViaWindowsInstallerAsync(string msiPath)
    {
        var quotedPath = $"\"{msiPath}\"";
        var psCommand =
            $"$proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i','{quotedPath}','/qn','/norestart') " +
            $"-Verb RunAs -Wait -PassThru; exit $proc.ExitCode";

        return await RunPowerShellAsync(psCommand);
    }

    /// <summary>
    /// Uninstalls a service via an MSI file using msiexec with UAC elevation.
    /// </summary>
    public static async Task<int> UninstallViaWindowsInstallerAsync(string msiPath)
    {
        var quotedPath = $"\"{msiPath}\"";
        var psCommand =
            $"$proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/x','{quotedPath}','/qn','/norestart') " +
            $"-Verb RunAs -Wait -PassThru; exit $proc.ExitCode";

        return await RunPowerShellAsync(psCommand);
    }

    /// <summary>
    /// Runs a PowerShell command and returns the exit code.
    /// </summary>
    private static async Task<int> RunPowerShellAsync(string command)
    {
        return await Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return 1;

            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            return proc.ExitCode;
        });
    }
}

using TokenSaver.Common.Core.Contracts;

using Microsoft.Extensions.Logging;

using System.Diagnostics;

namespace TokenSaver.Library.Services;

/// <summary>
///     Implements IDE launching for Visual Studio Code, Visual Studio, and future IDE support.
///     Handles process management, port detection, readiness probing, and output capture.
/// </summary>
public sealed class IDELauncherService : IIDELauncher
{
    private readonly ILogger<IDELauncherService> _logger;
    private readonly IDebugOutputCapture _outputCapture;
    private readonly Dictionary<string, IDEInstance> _instances = [];
    private readonly object _lock = new();

    public IDELauncherService(
        ILogger<IDELauncherService> logger,
        IDebugOutputCapture outputCapture)
    {
        _logger = logger;
        _outputCapture = outputCapture;
    }

    public async Task<IDELaunchResult> LaunchAsync(
        string ideType,
        string workspaceRoot,
        bool waitForReady = true,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(workspaceRoot))
        {
            throw new ArgumentException($"Workspace root does not exist: {workspaceRoot}", nameof(workspaceRoot));
        }

        lock (_lock)
        {
            if (_instances.TryGetValue(ideType, out var existing) && existing.Process != null && !existing.Process.HasExited)
            {
                _logger.LogInformation($"IDE {ideType} already running (PID {existing.Process.Id})");
                return new IDELaunchResult(
                    existing.Process.Id,
                    existing.Port,
                    true,
                    $"{ideType} is already running",
                    new Dictionary<string, object> { { "alreadyRunning", true } });
            }
        }

        ProcessStartInfo psi = ideType.ToLowerInvariant() switch
        {
            "vscode" => CreateVsCodeStartInfo(workspaceRoot),
            "visualstudio2026" => CreateVisualStudioStartInfo(workspaceRoot),
            _ => throw new ArgumentException($"Unknown IDE type: {ideType}", nameof(ideType))
        };

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {ideType}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to launch {ideType}");
            throw;
        }

        lock (_lock)
        {
            _instances[ideType] = new IDEInstance { Process = process, WorkspaceRoot = workspaceRoot };
        }

        // Start capturing output from the IDE (if redirected)
        _outputCapture.StartCapturingIDE(ideType, process);

        _logger.LogInformation($"Started {ideType} (PID {process.Id}) at {workspaceRoot}");

        if (waitForReady)
        {
            await Task.Delay(500, ct); // Brief wait for IDE to initialize
        }

        return new IDELaunchResult(
            process.Id,
            null, // Port detection would go here for future HTTP-based IDEs
            true,
            $"{ideType} launched successfully",
            new Dictionary<string, object>
            {
                { "workspaceRoot", workspaceRoot },
                { "launchedAt", DateTimeOffset.UtcNow.ToString("O") }
            });
    }

    public async Task<IDEStatusResult> QueryStatusAsync(string ideType, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_instances.TryGetValue(ideType, out var instance))
            {
                if (instance.Process == null || instance.Process.HasExited)
                {
                    _instances.Remove(ideType);
                    return new IDEStatusResult(ideType, false, null, null, false, null);
                }

                return new IDEStatusResult(ideType, true, instance.Process.Id, instance.Port, true, instance.WorkspaceRoot);
            }
        }

        return new IDEStatusResult(ideType, false, null, null, false, null);
    }

    public async Task StopAsync(string ideType, bool graceful = true, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(ideType, out var instance) || instance.Process == null || instance.Process.HasExited)
            {
                _logger.LogInformation($"{ideType} is not running");
                return;
            }

            try
            {
                if (graceful)
                {
                    instance.Process.CloseMainWindow();
                    if (!instance.Process.WaitForExit(5000))
                    {
                        instance.Process.Kill();
                    }
                }
                else
                {
                    instance.Process.Kill();
                }

                _instances.Remove(ideType);
                _logger.LogInformation($"Stopped {ideType}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error stopping {ideType}");
            }
        }
    }

    private static ProcessStartInfo CreateVsCodeStartInfo(string workspaceRoot)
    {
        return new ProcessStartInfo
        {
            FileName = "code",
            Arguments = $"\"{workspaceRoot}\"",
            UseShellExecute = true,
            CreateNoWindow = false
        };
    }

    private static ProcessStartInfo CreateVisualStudioStartInfo(string workspaceRoot)
    {
        // Look for devenv.exe in the standard VS 2026 location
        string devenvPath = @"C:\Program Files\Microsoft Visual Studio\2026\Professional\Common7\IDE\devenv.exe";
        if (!File.Exists(devenvPath))
        {
            devenvPath = @"C:\Program Files\Microsoft Visual Studio\2026\Enterprise\Common7\IDE\devenv.exe";
        }
        if (!File.Exists(devenvPath))
        {
            devenvPath = @"C:\Program Files\Microsoft Visual Studio\2026\Community\Common7\IDE\devenv.exe";
        }

        string solutionFile = Directory.GetFiles(workspaceRoot, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
            ?? workspaceRoot;

        return new ProcessStartInfo
        {
            FileName = devenvPath,
            Arguments = $"\"{solutionFile}\"",
            UseShellExecute = false,
            CreateNoWindow = false
        };
    }

    private sealed class IDEInstance
    {
        public Process? Process { get; set; }
        public string? WorkspaceRoot { get; set; }
        public int? Port { get; set; }
    }
}

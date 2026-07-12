using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TokenSaver.Client.Helpers.Logging;

namespace TokenSaver.Client.Helpers.Process;

/// <summary>
/// Manages the lifecycle of a child process (start, monitor, stop).
/// Used for managing TokenSaver.MCP and DraftModel.Loader processes.
/// </summary>
public sealed class ProcessLifecycleManager : IAsyncDisposable
{
    private System.Diagnostics.Process? _process;
    private TaskCompletionSource<int>? _exitTcs;
    private readonly string _fileName;
    private readonly string _arguments;
    private readonly TimeSpan _startTimeout;
    private readonly Action<string>? _onStdoutLine;
    private readonly Action<string>? _onStderrLine;
    private readonly Action<int>? _onExit;

    public ProcessLifecycleManager(
        string fileName,
        string arguments,
        TimeSpan startTimeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        Action<int>? onExit = null)
    {
        _fileName = fileName;
        _arguments = arguments;
        _startTimeout = startTimeout;
        _onStdoutLine = onStdoutLine;
        _onStderrLine = onStderrLine;
        _onExit = onExit;
    }

    /// <summary>
    /// Starts the process and waits for it to begin running.
    /// </summary>
    public async Task StartAsync()
    {
        if (_process != null)
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _fileName,
                Arguments = _arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            _process = System.Diagnostics.Process.Start(psi);
            if (_process == null)
                throw new InvalidOperationException("Failed to start process");

            // Set up output handling
            _exitTcs = new TaskCompletionSource<int>();
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    _onStdoutLine?.Invoke(e.Data);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    _onStderrLine?.Invoke(e.Data);
            };
            _process.Exited += (_, _) =>
            {
                _exitTcs.TrySetResult(_process.ExitCode);
                _onExit?.Invoke(_process.ExitCode);
            };

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            ExtensionLoggerFacade.LogInfo($"Process started: {_fileName} {_arguments}");

            // Wait a bit to ensure process doesn't exit immediately
            await Task.Delay(100);
            if (_process.HasExited)
                throw new InvalidOperationException($"Process exited immediately with code {_process.ExitCode}");
        }
        catch (Exception ex)
        {
            ExtensionLoggerFacade.LogException(ex);
            throw;
        }
    }

    /// <summary>
    /// Stops the process gracefully, waiting up to the timeout.
    /// </summary>
    public async Task StopAsync(TimeSpan? gracefulTimeout = null)
    {
        if (_process == null)
            return;

        gracefulTimeout ??= TimeSpan.FromSeconds(3);
        var proc = _process;
        _process = null;

        try
        {
            proc.Kill(false); // Graceful kill first
            using var cts = new CancellationTokenSource(gracefulTimeout.Value);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                proc.Kill(); // Force kill if still running
            }

            ExtensionLoggerFacade.LogInfo("Process stopped");
        }
        catch (Exception ex)
        {
            ExtensionLoggerFacade.LogException(ex);
        }
        finally
        {
            proc.Dispose();
        }
    }

    /// <summary>
    /// Waits for the process to exit.
    /// </summary>
    public async Task<int> WaitForExitAsync()
    {
        if (_process == null || _exitTcs == null)
            return -1;

        try
        {
            return await _exitTcs.Task;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Gets whether the process is currently running.
    /// </summary>
    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>
    /// Gets the process ID, or null if not running.
    /// </summary>
    public int? ProcessId => _process?.Id;

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_process != null)
            await StopAsync();
    }
}

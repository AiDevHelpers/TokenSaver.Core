namespace TokenSaver.TerminalControl.Core;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TokenSaver.TerminalControl.Contracts;
using TokenSaver.TerminalControl.Models;
using TokenSaver.TerminalControl.Output;

public sealed class StandardTerminalSession : ITerminalSession
{
    private readonly ILogger _logger;
    private readonly NamedPipeManager _pipeManager;
    private Process? _process;
    private TerminalSessionConfig? _config;
    private TerminalConfiguration _terminalConfig = null!;

    public string SessionId { get; } = $"standard_{Guid.NewGuid():N}".Substring(0, 20);
    public bool IsElevated => false;
    public int ProcessId => _process?.Id ?? -1;
    public TerminalSessionState State { get; private set; } = TerminalSessionState.Initializing;
    public TerminalConfiguration Configuration => _terminalConfig;

    public StandardTerminalSession(ILogger logger, NamedPipeManager? pipeManager = null)
    {
        _logger = logger;
        // Create a logger for the pipe manager if not provided
        if (pipeManager is null)
        {
            var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddDebug());
            var pipeLogger = loggerFactory.CreateLogger<NamedPipeManager>();
            _pipeManager = new NamedPipeManager(pipeLogger);
        }
        else
        {
            _pipeManager = pipeManager;
        }
    }

    public async Task InitializeAsync(TerminalSessionConfig config, CancellationToken ct = default)
    {
        _config = config;
        State = TerminalSessionState.Initializing;

        try
        {
            _logger.LogInformation("Initializing standard terminal session {SessionId}", SessionId);

            // Placeholder: will implement Windows Terminal launching
            // For now, create a dummy configuration
            var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddDebug());
            var discoveryLogger = loggerFactory.CreateLogger<Discovery.TerminalConfigurationDiscovery>();
            var discovery = new Discovery.TerminalConfigurationDiscovery(discoveryLogger);
            _terminalConfig = await discovery.DiscoverConfigurationAsync(ct);

            State = TerminalSessionState.Ready;
            _logger.LogInformation("Standard terminal session {SessionId} initialized", SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize standard terminal session {SessionId}", SessionId);
            State = TerminalSessionState.Terminated;
            throw;
        }
    }

    public Task<TerminalTabInfo> CreateTabAsync(
        string tabName,
        string? initialCommand = null,
        CancellationToken ct = default)
    {
        EnsureReady();

        _logger.LogDebug("Creating tab '{TabName}' in standard session {SessionId}", tabName, SessionId);

        var tabId = $"{SessionId}-{Guid.NewGuid():N}".Substring(0, 24);

        // Create output pipes for this tab
        var stdoutPipe = _pipeManager.CreateOutputPipe(SessionId, tabId, NamedPipeStreamType.Stdout);
        var stderrPipe = _pipeManager.CreateOutputPipe(SessionId, tabId, NamedPipeStreamType.Stderr);

        var tabInfo = new TerminalTabInfo(
            TabId: tabId,
            TabName: tabName,
            SessionId: SessionId,
            CreatedAt: DateTime.UtcNow,
            IsActive: true,
            CurrentCommand: initialCommand,
            StdoutPipe: stdoutPipe,
            StderrPipe: stderrPipe);

        _logger.LogDebug(
            "Tab {TabId} created with pipes: stdout={StdoutPipe}, stderr={StderrPipe}",
            tabId,
            stdoutPipe.PipePath,
            stderrPipe.PipePath);

        return Task.FromResult(tabInfo);
    }

    public async Task<TerminalExecutionResult> ExecuteInTabAsync(
        string tabId,
        string command,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        EnsureReady();

        _logger.LogDebug("Executing command in tab {TabId}: {Command}", tabId, command);

        // Placeholder implementation
        await Task.Delay(100, ct);

        return new TerminalExecutionResult(
            Success: true,
            ExitCode: 0,
            Stdout: $"Command executed in standard context: {command}",
            ExecutionTimeMs: 100);
    }

    public Task CloseTabAsync(string tabId, CancellationToken ct = default)
    {
        EnsureReady();
        _logger.LogDebug("Closing tab {TabId} in standard session {SessionId}", tabId, SessionId);

        // Close associated pipes
        var stdoutPipeName = $"TokenSaver-{SessionId}-{tabId}-Stdout";
        var stderrPipeName = $"TokenSaver-{SessionId}-{tabId}-Stderr";
        _pipeManager.ClosePipe(stdoutPipeName);
        _pipeManager.ClosePipe(stderrPipeName);

        return Task.CompletedTask;
    }

    public async Task TerminateAsync(bool graceful = true, CancellationToken ct = default)
    {
        if (State == TerminalSessionState.Terminated || State == TerminalSessionState.Closing)
        {
            return;
        }

        State = TerminalSessionState.Closing;
        _logger.LogInformation("Terminating standard session {SessionId} (Graceful: {Graceful})", SessionId, graceful);

        try
        {
            if (_process is not null && !_process.HasExited)
            {
                if (graceful)
                {
                    _process.CloseMainWindow();
                    if (!await WaitForExitAsync(_process, _config?.ActualGracefulCloseTimeout ?? TimeSpan.FromSeconds(5)))
                    {
                        _process.Kill();
                    }
                }
                else
                {
                    _process.Kill();
                }

                _process.Dispose();
            }

            State = TerminalSessionState.Terminated;
            _logger.LogInformation("Standard session {SessionId} terminated", SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error terminating standard session {SessionId}", SessionId);
            State = TerminalSessionState.Terminated;
        }
        finally
        {
            // Clean up all pipes for this session
            _pipeManager.CloseSessionPipes(SessionId);
        }
    }

    public async IAsyncEnumerable<TerminalTabInfo> ListTabsAsync(CancellationToken ct = default)
    {
        EnsureReady();
        // Placeholder: yield empty list for now
        await Task.CompletedTask;
        yield break;
    }

    public Task<TerminalTabInfo?> GetTabAsync(string tabId, CancellationToken ct = default)
    {
        EnsureReady();
        return Task.FromResult<TerminalTabInfo?>(null);
    }

    public Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        return Task.FromResult(State == TerminalSessionState.Ready && (_process?.HasExited == false));
    }

    private void EnsureReady()
    {
        if (State != TerminalSessionState.Ready)
        {
            throw new InvalidOperationException($"Session is not in Ready state. Current state: {State}");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            return await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds));
        }
        catch
        {
            return false;
        }
    }
}

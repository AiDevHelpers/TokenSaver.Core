namespace TokenSaver.TerminalControl.Core;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TokenSaver.TerminalControl.Contracts;
using TokenSaver.TerminalControl.Models;
using TokenSaver.TerminalControl.Output;

public sealed class StandardTerminalSession : ITerminalSession, IDisposable
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CommandConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly NamedPipeManager _pipeManager;
    private readonly bool _ownsPipeManager;
    private readonly ITerminalProcessLauncher _launcher;
    private readonly ConcurrentDictionary<string, TabRuntime> _tabs = new();
    private Process? _process;
    private TerminalSessionConfig? _config;
    private TerminalConfiguration _terminalConfig = null!;

    public string SessionId { get; } = $"standard_{Guid.NewGuid():N}".Substring(0, 20);
    private string WindowName => $"tokensaver-{SessionId}";
    private static string WrapperPath => Path.Combine(AppContext.BaseDirectory, "tokensaver-tab.ps1");
    public bool IsElevated => false;
    public int ProcessId => _process?.Id ?? -1;
    public TerminalSessionState State { get; private set; } = TerminalSessionState.Initializing;
    public TerminalConfiguration Configuration => _terminalConfig;

    public StandardTerminalSession(
        ILogger logger,
        NamedPipeManager? pipeManager = null,
        ITerminalProcessLauncher? launcher = null)
    {
        _logger = logger;
        _launcher = launcher ?? new ProcessTerminalLauncher();
        // Create a logger for the pipe manager if not provided
        if (pipeManager is null)
        {
            using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddDebug());
            var pipeLogger = loggerFactory.CreateLogger<NamedPipeManager>();
            _pipeManager = new NamedPipeManager(pipeLogger);
            _ownsPipeManager = true;
        }
        else
        {
            _pipeManager = pipeManager;
        }
    }

    public void Dispose()
    {
        foreach (var tab in _tabs.Keys.ToList())
        {
            if (_tabs.TryRemove(tab, out var runtime))
            {
                runtime.Dispose();
            }
        }

        if (_ownsPipeManager)
        {
            _pipeManager.Dispose();
        }
    }

    public async Task InitializeAsync(TerminalSessionConfig config, CancellationToken ct = default)
    {
        _config = config;
        State = TerminalSessionState.Initializing;

        try
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Initializing standard terminal session {SessionId}", SessionId);
            }

            using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddDebug());
            var discoveryLogger = loggerFactory.CreateLogger<Discovery.TerminalConfigurationDiscovery>();
            var discovery = new Discovery.TerminalConfigurationDiscovery(discoveryLogger);
            _terminalConfig = await discovery.DiscoverConfigurationAsync(ct);

            // Open this session's own Windows Terminal window. Throws if wt.exe is missing.
            _process = _launcher.Start(new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-w {WindowName}",
                UseShellExecute = false,
            });

            State = TerminalSessionState.Ready;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Standard terminal session {SessionId} initialized", SessionId);
            }
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

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Creating tab '{TabName}' in standard session {SessionId}", tabName, SessionId);
        }

        var tabId = $"{SessionId}-{Guid.NewGuid():N}".Substring(0, 24);

        // Create output pipes for this tab
        var stdoutPipe = _pipeManager.CreateOutputPipe(SessionId, tabId, NamedPipeStreamType.Stdout);
        var stderrPipe = _pipeManager.CreateOutputPipe(SessionId, tabId, NamedPipeStreamType.Stderr);
        var commandPipeName = $"TokenSaver-{SessionId}-{tabId}-Cmd";

        var tabInfo = new TerminalTabInfo(
            TabId: tabId,
            TabName: tabName,
            SessionId: SessionId,
            CreatedAt: DateTime.UtcNow,
            IsActive: true,
            CurrentCommand: initialCommand,
            StdoutPipe: stdoutPipe,
            StderrPipe: stderrPipe);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Tab {TabId} created with pipes: stdout={StdoutPipe}, stderr={StderrPipe}",
                tabId,
                stdoutPipe.PipePath,
                stderrPipe.PipePath);
        }

        // Open the tab in this session's window, running the output wrapper.
        try
        {
            _launcher.Start(new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-w {WindowName} new-tab --title \"{tabName}\" pwsh -NoProfile -File \"{WrapperPath}\" " +
                    $"-StdoutPipe \"{stdoutPipe.PipeName}\" -StderrPipe \"{stderrPipe.PipeName}\" -CommandPipe \"{commandPipeName}\"",
                UseShellExecute = false,
            });
        }
        catch
        {
            _pipeManager.ClosePipe(stdoutPipe.PipeName);
            _pipeManager.ClosePipe(stderrPipe.PipeName);
            throw;
        }

        var runtime = new TabRuntime(tabInfo, commandPipeName);
        _tabs[tabId] = runtime;
        StartPump(stdoutPipe.PipeName, runtime.StdoutLines);
        StartPump(stderrPipe.PipeName, runtime.StderrLines);

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

        if (!_tabs.TryGetValue(tabId, out var runtime))
        {
            throw new InvalidOperationException($"Tab '{tabId}' does not exist in session {SessionId}.");
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Executing command in tab {TabId}: {Command}", tabId, command);
        }

        var fullCommand = workingDirectory is null
            ? command
            : $"Set-Location -LiteralPath '{workingDirectory.Replace("'", "''")}'; {command}";
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var line = TabProtocol.FormatCommand(requestId, fullCommand);

        await EnsureCommandConnectionAsync(runtime, ct);
        runtime.CommandWriter!.WriteLine(line);
        runtime.Pending.Enqueue(requestId);

        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultCommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var collected = new List<string>();
        try
        {
            while (await runtime.StdoutLines.Reader.WaitToReadAsync(linked.Token))
            {
                while (runtime.StdoutLines.Reader.TryRead(out var outLine))
                {
                    if (TabProtocol.TryParseEnd(outLine, out var endId, out var exitCode))
                    {
                        // Markers arrive in order; only the head of the queue is ours to finish.
                        if (runtime.Pending.TryPeek(out var head) && head == endId)
                        {
                            runtime.Pending.TryDequeue(out _);
                        }

                        if (endId == requestId)
                        {
                            return new TerminalExecutionResult(
                                Success: exitCode == 0,
                                ExitCode: exitCode,
                                Stdout: string.Join("\n", collected),
                                Stderr: DrainStderr(runtime),
                                ExecutionTimeMs: stopwatch.ElapsedMilliseconds);
                        }
                    }
                    else if (runtime.Pending.TryPeek(out var current) && current == requestId)
                    {
                        collected.Add(outLine);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Leave the request queued so its late output is discarded by the next call.
            return new TerminalExecutionResult(
                Success: false,
                ExitCode: -1,
                Stdout: string.Join("\n", collected),
                Stderr: DrainStderr(runtime),
                ExecutionTimeMs: stopwatch.ElapsedMilliseconds,
                Errors: new[] { $"Command timed out after {stopwatch.Elapsed.TotalSeconds:F0}s." });
        }

        throw new InvalidOperationException($"Tab '{tabId}' closed before the command finished.");
    }

    public Task CloseTabAsync(string tabId, CancellationToken ct = default)
    {
        EnsureReady();
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Closing tab {TabId} in standard session {SessionId}", tabId, SessionId);
        }

        if (!_tabs.TryRemove(tabId, out var runtime))
        {
            return Task.CompletedTask;
        }

        // Asking the wrapper to exit closes the wt tab; the wrapper then drops its pipes.
        try
        {
            if (runtime.CommandWriter is not null)
            {
                runtime.CommandWriter.WriteLine(TabProtocol.FormatCommand("close", TabProtocol.CloseCommand));
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Tab {TabId} command pipe already closed", tabId);
        }

        runtime.Dispose();
        _pipeManager.ClosePipe(runtime.Info.StdoutPipe.PipeName);
        _pipeManager.ClosePipe(runtime.Info.StderrPipe.PipeName);

        return Task.CompletedTask;
    }

    public async Task TerminateAsync(bool graceful = true, CancellationToken ct = default)
    {
        if (State == TerminalSessionState.Terminated || State == TerminalSessionState.Closing)
        {
            return;
        }

        State = TerminalSessionState.Closing;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Terminating standard session {SessionId} (Graceful: {Graceful})", SessionId, graceful);
        }

        try
        {
            foreach (var tabId in _tabs.Keys.ToList())
            {
                await CloseTabAsync(tabId, ct);
            }

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
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Standard session {SessionId} terminated", SessionId);
            }
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

    public async IAsyncEnumerable<TerminalTabInfo> ListTabsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();
        await Task.CompletedTask;
        foreach (var runtime in _tabs.Values)
        {
            ct.ThrowIfCancellationRequested();
            yield return runtime.Info;
        }
    }

    public Task<TerminalTabInfo?> GetTabAsync(string tabId, CancellationToken ct = default)
    {
        EnsureReady();
        return Task.FromResult(_tabs.TryGetValue(tabId, out var runtime) ? runtime.Info : null);
    }

    public Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        return Task.FromResult(State == TerminalSessionState.Ready);
    }

    private void EnsureReady()
    {
        if (State != TerminalSessionState.Ready)
        {
            throw new InvalidOperationException($"Session is not in Ready state. Current state: {State}");
        }
    }

    private async Task EnsureCommandConnectionAsync(TabRuntime runtime, CancellationToken ct)
    {
        if (runtime.CommandWriter is not null)
        {
            return;
        }

        // The wrapper creates the command pipe server once it starts; retry until it is up.
        var deadline = DateTime.UtcNow + CommandConnectTimeout;
        NamedPipeClientStream client;
        while (true)
        {
            client = new NamedPipeClientStream(".", runtime.CommandPipeName, PipeDirection.Out);
            try
            {
                await client.ConnectAsync(500, ct);
                break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException && DateTime.UtcNow < deadline)
            {
                client.Dispose();
                await Task.Delay(200, ct);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        runtime.CommandClient = client;
        runtime.CommandWriter = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    private void StartPump(string pipeName, Channel<string> target)
    {
        var pipe = _pipeManager.GetPipe(pipeName);
        if (pipe is null)
        {
            target.Writer.TryComplete();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in pipe.ReadLinesAsync())
                {
                    target.Writer.TryWrite(line);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Output pump for {PipeName} stopped", pipeName);
            }
            finally
            {
                target.Writer.TryComplete();
            }
        });
    }

    private static string? DrainStderr(TabRuntime runtime)
    {
        var lines = new List<string>();
        while (runtime.StderrLines.Reader.TryRead(out var line))
        {
            lines.Add(line);
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
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

    private sealed class TabRuntime : IDisposable
    {
        public TabRuntime(TerminalTabInfo info, string commandPipeName)
        {
            Info = info;
            CommandPipeName = commandPipeName;
        }

        public TerminalTabInfo Info { get; }
        public string CommandPipeName { get; }
        public Channel<string> StdoutLines { get; } = Channel.CreateUnbounded<string>();
        public Channel<string> StderrLines { get; } = Channel.CreateUnbounded<string>();
        public Queue<string> Pending { get; } = new();
        public NamedPipeClientStream? CommandClient { get; set; }
        public StreamWriter? CommandWriter { get; set; }

        public void Dispose()
        {
            try
            {
                CommandWriter?.Dispose();
            }
            catch (IOException)
            {
                // Pipe already closed by the wrapper.
            }

            CommandClient?.Dispose();
            CommandWriter = null;
            CommandClient = null;
        }
    }
}

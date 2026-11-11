namespace TokenSaver.TerminalControl.Output;

using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using TokenSaver.TerminalControl.Models;

public sealed class NamedPipeManager : IDisposable
{
    private readonly ILogger<NamedPipeManager> _logger;
    private readonly Dictionary<string, NamedServerPipe> _pipes = new();
    private readonly Dictionary<string, NamedCommandPipe> _commandPipes = new();
    private readonly object _syncLock = new();
    private bool _disposed;

    public NamedPipeManager(ILogger<NamedPipeManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a new named pipe for output capture.
    /// </summary>
    public NamedPipeInfo CreateOutputPipe(
        string sessionId,
        string tabId,
        NamedPipeStreamType streamType)
    {
        ThrowIfDisposed();

        var pipeName = $"TokenSaver-{sessionId}-{tabId}-{streamType}";
        var pipeInfo = new NamedPipeInfo(
            PipeName: pipeName,
            PipePath: $@"\\.\pipe\{pipeName}",
            StreamType: streamType,
            CreatedAt: DateTime.UtcNow);

        lock (_syncLock)
        {
            try
            {
                var serverPipe = new NamedServerPipe(pipeName, _logger);
                _pipes[pipeName] = serverPipe;

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Created named pipe {PipeName} for session {SessionId} tab {TabId}",
                        pipeName,
                        sessionId,
                        tabId);
                }

                return pipeInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create named pipe {PipeName}", pipeName);
                throw;
            }
        }
    }

    /// <summary>
    /// Creates a pipe that TokenSaver writes commands into; the tab's wrapper connects as the client.
    /// Server-side writes are used so an elevated client can read from a pipe created at a lower integrity level.
    /// </summary>
    public string CreateCommandPipe(string sessionId, string tabId)
    {
        ThrowIfDisposed();

        var pipeName = $"TokenSaver-{sessionId}-{tabId}-Cmd";
        lock (_syncLock)
        {
            _commandPipes[pipeName] = new NamedCommandPipe(pipeName, _logger);
        }

        return pipeName;
    }

    /// <summary>
    /// Gets a command pipe by name.
    /// </summary>
    public NamedCommandPipe? GetCommandPipe(string pipeName)
    {
        lock (_syncLock)
        {
            _commandPipes.TryGetValue(pipeName, out var pipe);
            return pipe;
        }
    }

    /// <summary>
    /// Closes and removes a named pipe.
    /// </summary>
    public void ClosePipe(string pipeName)
    {
        lock (_syncLock)
        {
            if (_commandPipes.Remove(pipeName, out var commandPipe))
            {
                commandPipe.Dispose();
            }

            if (_pipes.Remove(pipeName, out var serverPipe))
            {
                using (serverPipe)
                {
                    try
                    {
                        serverPipe.Dispose();
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Closed named pipe {PipeName}", pipeName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error closing named pipe {PipeName}", pipeName);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Closes all pipes for a session.
    /// </summary>
    public void CloseSessionPipes(string sessionId)
    {
        lock (_syncLock)
        {
            var pipeNames = _pipes.Keys
                .Concat(_commandPipes.Keys)
                .Where(name => name.Contains($"TokenSaver-{sessionId}-", StringComparison.Ordinal))
                .Distinct()
                .ToList();

            foreach (var pipeName in pipeNames)
            {
                ClosePipe(pipeName);
            }
        }
    }

    /// <summary>
    /// Gets a pipe server by name.
    /// </summary>
    public NamedServerPipe? GetPipe(string pipeName)
    {
        lock (_syncLock)
        {
            _pipes.TryGetValue(pipeName, out var pipe);
            return pipe;
        }
    }

    /// <summary>
    /// Monitors output from a pipe in real-time via an async enumerable.
    /// </summary>
    public async IAsyncEnumerable<string> MonitorPipeOutputAsync(
        string pipeName,
        System.Collections.Generic.IAsyncEnumerable<System.Collections.Generic.IEnumerable<string>>? ct = null)
    {
        var pipe = GetPipe(pipeName);
        if (pipe is null)
        {
            _logger.LogWarning("Pipe {PipeName} not found", pipeName);
            yield break;
        }

        await foreach (var line in pipe.ReadLinesAsync())
        {
            yield return line;
        }
    }

    /// <summary>
    /// Reads all accumulated output from a pipe.
    /// </summary>
    public async Task<string> ReadPipeOutputAsync(string pipeName)
    {
        var pipe = GetPipe(pipeName);
        if (pipe is null)
        {
            _logger.LogWarning("Pipe {PipeName} not found", pipeName);
            return string.Empty;
        }

        return await pipe.ReadAllOutputAsync();
    }

    /// <summary>
    /// Gets the current size of accumulated data in a pipe.
    /// </summary>
    public long GetPipeSize(string pipeName)
    {
        var pipe = GetPipe(pipeName);
        return pipe?.GetSize() ?? 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_syncLock)
        {
            foreach (var pipe in _pipes.Values)
            {
                try
                {
                    pipe.Dispose();
                }
                catch
                {
                    // Swallow disposal errors
                }
            }

            _pipes.Clear();

            foreach (var commandPipe in _commandPipes.Values)
            {
                commandPipe.Dispose();
            }

            _commandPipes.Clear();
        }

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>
/// Server side of a one-client command pipe: TokenSaver writes lines, the tab's wrapper reads them.
/// </summary>
public sealed class NamedCommandPipe : IDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly NamedPipeServerStream _stream;
    private StreamWriter? _writer;

    public NamedCommandPipe(string pipeName, ILogger logger)
    {
        _pipeName = pipeName;
        _logger = logger;
        _stream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    /// <summary>
    /// Waits until the wrapper connects. Throws OperationCanceledException on timeout.
    /// </summary>
    public async Task WaitForClientAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        await _stream.WaitForConnectionAsync(linked.Token);
        _writer = new StreamWriter(_stream, new System.Text.UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    public async Task WriteLineAsync(string line)
    {
        var writer = _writer ?? throw new InvalidOperationException($"Pipe {_pipeName} has no connected client.");
        await writer.WriteLineAsync(line);
    }

    public void Dispose()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Command pipe {PipeName} already closed", _pipeName);
            }
        }

        _stream.Dispose();
    }
}

/// <summary>
/// Wrapper around a named pipe server for output capture.
/// </summary>
public sealed class NamedServerPipe : IDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly MemoryStream _buffer = new();
    private NamedPipeServerStream? _serverStream;
    private StreamReader? _reader;
    private readonly object _syncLock = new();

    public NamedServerPipe(string pipeName, ILogger logger)
    {
        _pipeName = pipeName;
        _logger = logger;
        _serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    /// <summary>
    /// Reads lines from the pipe as they arrive.
    /// </summary>
    public async IAsyncEnumerable<string> ReadLinesAsync()
    {
        if (_serverStream is null)
        {
            yield break;
        }

        // Wait for a client connection
        try
        {
            await _serverStream.WaitForConnectionAsync();
            _reader = new StreamReader(_serverStream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error waiting for connection on pipe {PipeName}", _pipeName);
            yield break;
        }

        string? line;
        while ((line = await ReadNextLineAsync()) is not null)
        {
            yield return line;
        }
    }

    private async Task<string?> ReadNextLineAsync()
    {
        try
        {
            var reader = _reader ?? throw new InvalidOperationException("Pipe reader is not initialized.");
            var line = await reader.ReadLineAsync();
            if (line is not null)
            {
                // Write to buffer
                var data = System.Text.Encoding.UTF8.GetBytes(line + Environment.NewLine);
                lock (_syncLock)
                {
                    _buffer.Write(data);
                }
            }

            return line;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading from pipe {PipeName}", _pipeName);
            return null;
        }
    }

    /// <summary>
    /// Reads all accumulated output from the buffer.
    /// </summary>
    public Task<string> ReadAllOutputAsync()
    {
        string result;
        lock (_syncLock)
        {
            _buffer.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(_buffer, leaveOpen: true);
            result = reader.ReadToEnd();
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Gets the current size of the buffer.
    /// </summary>
    public long GetSize()
    {
        lock (_syncLock)
        {
            return _buffer.Length;
        }
    }

    /// <summary>
    /// Clears the buffer.
    /// </summary>
    public void ClearBuffer()
    {
        lock (_syncLock)
        {
            _buffer.SetLength(0);
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _serverStream?.Dispose();
        _buffer?.Dispose();
    }
}

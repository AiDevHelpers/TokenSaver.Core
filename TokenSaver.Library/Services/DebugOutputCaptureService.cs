using TokenSaver.Common.Core.Contracts;

using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TokenSaver.Library.Services;

/// <summary>
///     Captures stdout/stderr from launched processes and IDE instances so calling AI
///     can read stack traces, build errors, and diagnostics.
/// </summary>
public sealed class DebugOutputCaptureService : IDebugOutputCapture
{
    private readonly ILogger<DebugOutputCaptureService> _logger;
    private readonly ConcurrentDictionary<int, ProcessOutputBuffer> _buffers = [];
    private readonly ConcurrentDictionary<string, ProcessOutputBuffer> _ideBuffers = []; // keyed by IDE type
    private const int MaxLinesPerBuffer = 5000;
    private const int MaxBufferSizeBytes = 10_000_000; // 10MB per buffer

    public DebugOutputCaptureService(ILogger<DebugOutputCaptureService> logger)
    {
        _logger = logger;
    }

    public async Task<DebugOutput> GetCapturedOutputAsync(
        int? processId = null,
        string? ideType = null,
        int linesLimit = 1000,
        CancellationToken ct = default)
    {
        ProcessOutputBuffer? buffer = null;

        if (ideType != null)
        {
            _ideBuffers.TryGetValue(ideType, out buffer);
        }
        else if (processId.HasValue)
        {
            _buffers.TryGetValue(processId.Value, out buffer);
        }

        if (buffer == null)
        {
            return new DebugOutput(
                processId,
                ideType,
                DateTimeOffset.UtcNow.DateTime,
                [],
                IsComplete: false);
        }

        lock (buffer)
        {
            var lines = buffer.Lines
                .TakeLast(linesLimit)
                .ToList();

            return new DebugOutput(
                processId,
                ideType,
                DateTimeOffset.UtcNow.DateTime,
                lines.AsReadOnly(),
                IsComplete: true);
        }
    }

    public async Task ClearOutputAsync(int processId, CancellationToken ct = default)
    {
        _buffers.TryRemove(processId, out _);
        _logger.LogInformation($"Cleared output buffer for process {processId}");
    }

    public async Task<IEnumerable<DebugOutputLine>> SearchOutputAsync(
        string pattern,
        int? processId = null,
        DebugSeverity? severity = null,
        CancellationToken ct = default)
    {
        var buffers = new List<ProcessOutputBuffer>();

        if (processId.HasValue)
        {
            if (_buffers.TryGetValue(processId.Value, out var buffer))
            {
                buffers.Add(buffer);
            }
        }
        else
        {
            buffers.AddRange(_buffers.Values);
            buffers.AddRange(_ideBuffers.Values);
        }

        var results = new List<DebugOutputLine>();
        var regex = CreateRegex(pattern);

        foreach (var buffer in buffers)
        {
            lock (buffer)
            {
                foreach (var line in buffer.Lines)
                {
                    if (severity.HasValue && line.Severity != severity.Value)
                        continue;

                    if (regex.IsMatch(line.Content))
                    {
                        results.Add(line);
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    ///     Start capturing output from a process.
    /// </summary>
    public void StartCapturing(Process process)
    {
        if (process.HasExited)
        {
            _logger.LogWarning($"Cannot capture output from already-exited process {process.Id}");
            return;
        }

        var buffer = new ProcessOutputBuffer();
        _buffers[process.Id] = buffer;

        // Capture stdout
        if (process.StandardOutput != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                    {
                        buffer.AddLine(line, "stdout", DebugSeverity.Info);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error reading stdout from process {process.Id}");
                }
            });
        }

        // Capture stderr
        if (process.StandardError != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync()) != null)
                    {
                        buffer.AddLine(line, "stderr", DetermineSeverity(line));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error reading stderr from process {process.Id}");
                }
            });
        }

        _logger.LogInformation($"Started capturing output from process {process.Id}");
    }

    /// <summary>
    ///     Start capturing output from a launched IDE.
    /// </summary>
    public void StartCapturingIDE(string ideType, Process process)
    {
        var buffer = new ProcessOutputBuffer();
        _ideBuffers[ideType] = buffer;
        StartCapturing(process);
        _logger.LogInformation($"Started capturing IDE output for {ideType} (PID {process.Id})");
    }

    private static Regex CreateRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        }
        catch
        {
            // If pattern is not valid regex, treat it as literal substring
            return new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        }
    }

    private static DebugSeverity DetermineSeverity(string line)
    {
        var lower = line.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("fatal") || lower.Contains("crash") => DebugSeverity.Fatal,
            _ when lower.Contains("error") || lower.Contains("failed") || lower.Contains("exception") => DebugSeverity.Error,
            _ when lower.Contains("warn") => DebugSeverity.Warning,
            _ => DebugSeverity.Info
        };
    }

    private sealed class ProcessOutputBuffer
    {
        private readonly List<DebugOutputLine> _lines = [];
        private long _totalBytes = 0;
        private int _lineCounter = 0;

        public IReadOnlyList<DebugOutputLine> Lines
        {
            get
            {
                lock (this)
                {
                    return _lines.AsReadOnly();
                }
            }
        }

        public void AddLine(string content, string stream, DebugSeverity severity)
        {
            lock (this)
            {
                var lineLength = content.Length + stream.Length + 32; // rough estimate with timestamp
                _totalBytes += lineLength;

                // Trim oldest lines if we exceed capacity
                while (_lines.Count >= MaxLinesPerBuffer || _totalBytes > MaxBufferSizeBytes)
                {
                    if (_lines.Count == 0) break;
                    _totalBytes -= _lines[0].Content.Length;
                    _lines.RemoveAt(0);
                }

                var line = new DebugOutputLine(
                    ++_lineCounter,
                    DateTime.UtcNow,
                    severity,
                    stream,
                    content);

                _lines.Add(line);
            }
        }
    }
}

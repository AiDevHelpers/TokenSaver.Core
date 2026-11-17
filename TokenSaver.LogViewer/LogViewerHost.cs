using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TokenSaver.LogHelper.Filtering;
using TokenSaver.LogHelper.Models;
using TokenSaver.LogHelper.Services;

namespace TokenSaver.LogViewer;

public enum WatchState
{
    Starting,
    Connected,

    /// <summary>The user closed the viewer tab. It is not reopened until a restart is asked for.</summary>
    Closed,

    /// <summary>No viewer connected after the allowed launch attempts.</summary>
    NoViewer,

    /// <summary>The channel could not be read.</summary>
    Faulted,
    Stopped,
}

public sealed record WatchInfo(string Id, string Channel, string? Filter, WatchState State, int? ViewerPid, string? Error);

public sealed record LogViewerOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Launches tried before a watch gives up with <see cref="WatchState.NoViewer"/>.</summary>
    public int MaxLaunchAttempts { get; init; } = 3;

    /// <summary>Entries kept per watch and replayed to a viewer that connects late or is restarted.</summary>
    public int ReplayEntries { get; init; } = 500;

    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a viewer gets to exit on its own after being asked to close, before it is killed.</summary>
    public TimeSpan CloseGrace { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Runs the watches shown to the user. Each watch follows one log channel (through <see cref="LogWatcher"/>, so rolling
/// logs are followed), serves its entries on a named pipe, and keeps one viewer tab connected to that pipe: it opens the
/// tab, remembers which process is in it, replays the recent entries to a viewer that connects late, closes a hung
/// viewer and opens a new one. A tab the user closed stays closed until a restart is asked for.
/// </summary>
public sealed class LogViewerHost : IAsyncDisposable
{
    private readonly LogWatcher _watcher;
    private readonly LogHelperOptions _logOptions;
    private readonly IViewerLauncher _launcher;
    private readonly LogViewerOptions _options;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public LogViewerHost(LogWatcher watcher, LogHelperOptions logOptions, IViewerLauncher launcher, LogViewerOptions? options = null, ILogger? logger = null)
    {
        _watcher = watcher;
        _logOptions = logOptions;
        _launcher = launcher;
        _options = options ?? new LogViewerOptions();
        _logger = logger;
    }

    /// <summary>
    /// Starts watching <paramref name="channel"/> in a viewer tab, or returns the watch that already follows it with the
    /// same filter. A watch whose tab was closed is reopened only when <paramref name="reopen"/> is true.
    /// Waits briefly for the first read so an unreadable channel is reported here.
    /// </summary>
    public async Task<WatchInfo> StartAsync(ChannelId channel, LogFilterSpec? filter, string? filterText, bool reopen, CancellationToken ct)
    {
        string key = $"{channel}|{filterText}";
        Watch watch;
        bool created = false;
        lock (_gate)
        {
            // An explicit start matches channel and filter. An automatic one (reopen false) joins any watch on the
            // channel, so a query that changes its filter every time does not open a tab each time.
            Watch? existing = _watches.Values.FirstOrDefault(w =>
                w.State != WatchState.Stopped && (reopen ? w.Key == key : w.Channel == channel));
            if (existing is null)
            {
                watch = new Watch(NewId(), key, channel, filter, filterText, this);
                _watches[watch.Id] = watch;
                created = true;
            }
            else
            {
                watch = existing;
            }
        }

        if (created)
        {
            watch.Begin();
        }
        else if (reopen && watch.State is WatchState.Closed or WatchState.NoViewer)
        {
            watch.RequestRestart();
        }

        try
        {
            await watch.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // An empty log yields nothing yet; the watch is running.
        }

        return watch.Info();
    }

    public IReadOnlyList<WatchInfo> List() => _watches.Values.Select(w => w.Info()).OrderBy(w => w.Channel, StringComparer.Ordinal).ToList();

    /// <summary>Stops a watch and closes its viewer.</summary>
    public async Task<bool> StopAsync(string id)
    {
        if (!_watches.TryRemove(id, out Watch? watch))
        {
            return false;
        }

        await watch.StopAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Closes the watch's viewer and opens a new one. Works for a hung viewer and for a tab the user closed.</summary>
    public bool Restart(string id)
    {
        if (!_watches.TryGetValue(id, out Watch? watch))
        {
            return false;
        }

        watch.RequestRestart();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (string id in _watches.Keys.ToList())
        {
            await StopAsync(id).ConfigureAwait(false);
        }
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

#pragma warning disable CA1001 // A watch lives until the host stops it; its sources are released when the process ends.
    private sealed class Watch
    {
        private readonly LogViewerHost _host;
        private readonly CancellationTokenSource _cts = new();
        private readonly LogFilterSpec? _filter;
        private readonly Queue<ViewerMessage> _ring = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _ringLock = new();
        private StreamWriter? _writer;
        private CancellationTokenSource? _sessionCts;
        private volatile bool _restartRequested;
        private Task _watchTask = Task.CompletedTask;
        private Task _viewerTask = Task.CompletedTask;

        public Watch(string id, string key, ChannelId channel, LogFilterSpec? filter, string? filterText, LogViewerHost host)
        {
            Id = id;
            Key = key;
            Channel = channel;
            _filter = filter;
            FilterText = filterText;
            _host = host;
        }

        public string Id { get; }

        public string Key { get; }

        public ChannelId Channel { get; }

        public string? FilterText { get; }

        public WatchState State { get; private set; } = WatchState.Starting;

        public int? ViewerPid { get; private set; }

        public string? Error { get; private set; }

        public Task FirstRead => _firstRead.Task;

        public WatchInfo Info() => new(Id, Channel.ToString(), FilterText, State, ViewerPid, Error);

        public void Begin()
        {
            _watchTask = Task.Run(() => FollowAsync(_cts.Token));
            _viewerTask = Task.Run(() => KeepViewerAsync(_cts.Token));
        }

        public void RequestRestart()
        {
            _restartRequested = true;
            _sessionCts?.Cancel();
            try
            {
                _ = _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // A wake-up is already pending.
            }
        }

        public async Task StopAsync()
        {
            State = WatchState.Stopped;
            await _cts.CancelAsync().ConfigureAwait(false);
            await CloseViewerAsync().ConfigureAwait(false);
            await Task.WhenAll(_watchTask, _viewerTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        // Follows the channel for as long as the watch lives, whether or not a viewer is connected.
        private async Task FollowAsync(CancellationToken ct)
        {
            try
            {
                await foreach (LogReadBatch batch in _host._watcher.WatchAsync(Channel, null, null, ct).ConfigureAwait(false))
                {
                    _firstRead.TrySetResult();
                    var messages = new List<ViewerMessage>();
                    if (batch.Reset || batch.Rotated)
                    {
                        messages.Add(new ViewerMessage { Type = ViewerMessage.Note, Text = batch.Reset ? "log was replaced; reading from its start" : "log rolled; following the new file" });
                    }

                    HashSet<LogEntry> matched = _filter is null
                        ? new HashSet<LogEntry>()
                        : LogFilter.Apply(batch.Entries, _filter, _host._logOptions).Kept.ToHashSet();
                    foreach (LogEntry entry in batch.Entries)
                    {
                        messages.Add(new ViewerMessage
                        {
                            Type = ViewerMessage.Entry,
                            Ts = entry.Timestamp,
                            Severity = entry.Severity.ToString(),
                            Category = entry.Category,
                            Text = entry.Message,
                            Matched = _filter is null || matched.Contains(entry),
                        });
                    }

                    await SendAsync(messages, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Stopped.
            }
#pragma warning disable CA1031 // A channel that cannot be read must end up as a state on the watch, not an unobserved fault.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Error = ex.Message;
                State = WatchState.Faulted;
                _firstRead.TrySetResult();
                _host._logger?.LogWarning(ex, "Log watch {Id} on {Channel} failed", Id, Channel);
                await SendAsync(new[] { new ViewerMessage { Type = ViewerMessage.Note, Text = $"watch failed: {ex.Message}" } }, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // Keeps a viewer connected: opens one, serves it, and decides what to do when it goes away.
        private async Task KeepViewerAsync(CancellationToken ct)
        {
            int attempts = 0;
            while (!ct.IsCancellationRequested)
            {
                _restartRequested = false;
                attempts++;
                State = State == WatchState.Faulted ? WatchState.Faulted : WatchState.Starting;
                SessionEnd end = await RunSessionAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (_restartRequested)
                {
                    attempts = 0;
                    continue;
                }

                switch (end)
                {
                    case SessionEnd.NeverConnected when attempts < _host._options.MaxLaunchAttempts:
                    case SessionEnd.Hung when attempts < _host._options.MaxLaunchAttempts:
                        continue;
                    case SessionEnd.UserClosed:
                        State = WatchState.Closed;
                        break;
                    default:
                        State = WatchState.NoViewer;
                        break;
                }

                // Idle until a restart is asked for; the watch keeps collecting entries meanwhile.
                try
                {
                    await _wake.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                attempts = 0;
            }
        }

        private async Task<SessionEnd> RunSessionAsync(CancellationToken ct)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _sessionCts = session;
            string pipeName = ViewerMessage.PipeNameFor(Id);
            await using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
            try
            {
                try
                {
                    _host._launcher.OpenTab(TitleOf(Channel), pipeName);
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Error = ex.Message;
                    _host._logger?.LogWarning(ex, "Could not open a viewer for {Channel}", Channel);
                    return SessionEnd.LaunchFailed;
                }

                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                connectTimeout.CancelAfter(_host._options.ConnectTimeout);
                try
                {
                    await server.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return SessionEnd.NeverConnected;
                }

                // The session owns the pipe, so the reader and writer leave it open: it is still written to (the close request) after they go out of scope.
                using var reader = new StreamReader(server, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
                string? ready;
                try
                {
                    ready = await reader.ReadLineAsync(connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return session.IsCancellationRequested ? SessionEnd.Restarted : SessionEnd.NeverConnected;
                }

                if (ViewerMessage.TryParse(ready, out ViewerMessage? hello) && hello is { Pid: { } pid })
                {
                    ViewerPid = pid;
                }

#pragma warning disable CA2000 // The writer owns the pipe, which the session disposes.
                var writer = new StreamWriter(server, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
#pragma warning restore CA2000
                await _writeGate.WaitAsync(session.Token).ConfigureAwait(false);
                try
                {
                    await WriteAsync(writer, new ViewerMessage
                    {
                        Type = ViewerMessage.Hello,
                        Channel = Channel.ToString(),
                        Filter = FilterText,
                        Text = TitleOf(Channel),
                    }, session.Token).ConfigureAwait(false);
                    ViewerMessage[] replay;
                    lock (_ringLock)
                    {
                        replay = _ring.ToArray();
                    }

                    foreach (ViewerMessage message in replay)
                    {
                        await WriteAsync(writer, message, session.Token).ConfigureAwait(false);
                    }

                    _writer = writer;
                }
                finally
                {
                    _ = _writeGate.Release();
                }

                if (State != WatchState.Faulted)
                {
                    State = WatchState.Connected;
                }

                // The viewer says nothing more; a closed pipe means it went away.
                try
                {
                    while (await reader.ReadLineAsync(session.Token).ConfigureAwait(false) is not null)
                    {
                    }
                }
                catch (OperationCanceledException)
                {
                    return SessionEnd.Restarted;
                }
                catch (IOException)
                {
                    // Broken pipe: same as closed.
                }

                _writer = null;
                if (ViewerPid is { } alive && _host._launcher.IsRunning(alive))
                {
                    // The pipe is gone but the process is not: it is stuck. Close it so a new one can take its place.
                    _host._launcher.Kill(alive);
                    return SessionEnd.Hung;
                }

                return SessionEnd.UserClosed;
            }
            finally
            {
                await CloseViewerAsync().ConfigureAwait(false);
                _writer = null;
                ViewerPid = null;
                _sessionCts = null;
            }
        }

        private async Task SendAsync(IReadOnlyList<ViewerMessage> messages, CancellationToken ct)
        {
            if (messages.Count == 0)
            {
                return;
            }

            // Remembering the entries and writing them happen under one gate, so a viewer that connects meanwhile gets each
            // entry once: either in its replay or live, never both.
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_ringLock)
                {
                    foreach (ViewerMessage message in messages)
                    {
                        _ring.Enqueue(message);
                    }

                    while (_ring.Count > _host._options.ReplayEntries)
                    {
                        _ = _ring.Dequeue();
                    }
                }

                StreamWriter? writer = _writer;
#pragma warning disable CA1508 // The session can clear the writer between the first check and the gate.
                if (writer is null)
#pragma warning restore CA1508
                {
                    return;
                }

                try
                {
                    foreach (ViewerMessage message in messages)
                    {
                        await WriteAsync(writer, message, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
                {
                    // The viewer is gone or not reading: the session loop sees the closed pipe or restarts it.
                    _writer = null;
                    _restartRequested = ex is TimeoutException;
                    if (_sessionCts is { } stuck)
                    {
                        await stuck.CancelAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _ = _writeGate.Release();
            }
        }

        private async Task WriteAsync(StreamWriter writer, ViewerMessage message, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_host._options.WriteTimeout);
            try
            {
                await writer.WriteLineAsync(message.ToLine().AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The viewer stopped reading.");
            }
        }

        // Asks the viewer to exit so its tab closes, and kills it when it does not.
        private async Task CloseViewerAsync()
        {
            if (ViewerPid is not { } pid)
            {
                return;
            }

            // Written under the write gate: a writer takes one write at a time.
            if (_writer is not null && await _writeGate.WaitAsync(_host._options.WriteTimeout).ConfigureAwait(false))
            {
                try
                {
                    StreamWriter? writer = _writer;
#pragma warning disable CA1508 // The session can clear the writer while this waited for the gate.
                    if (writer is not null)
#pragma warning restore CA1508
                    {
                        await WriteAsync(writer, new ViewerMessage { Type = ViewerMessage.Close }, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or InvalidOperationException)
                {
                    // Not reachable: fall through to the kill.
                }
                finally
                {
                    _ = _writeGate.Release();
                }
            }

            DateTime deadline = DateTime.UtcNow + _host._options.CloseGrace;
            while (_host._launcher.IsRunning(pid) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }

            if (_host._launcher.IsRunning(pid))
            {
                _host._launcher.Kill(pid);
            }
        }

        private static string TitleOf(ChannelId channel)
        {
            string name = channel.Name.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            return "log: " + (slash >= 0 ? name[(slash + 1)..] : name);
        }
    }

    private enum SessionEnd
    {
        UserClosed,
        Hung,
        NeverConnected,
        LaunchFailed,
        Restarted,
    }
}

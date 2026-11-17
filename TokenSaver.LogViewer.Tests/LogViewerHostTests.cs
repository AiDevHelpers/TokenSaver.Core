using Xunit;
using System.Collections.Concurrent;
using System.IO.Pipes;
using TokenSaver.LogHelper.Models;
using TokenSaver.LogHelper.Services;
using TokenSaver.LogHelper.Sources;

namespace TokenSaver.LogViewer.Tests;

public sealed class LogViewerHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tslv-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly FakeLauncher _launcher = new();
    private readonly LogHelperOptions _options;
    private readonly LogViewerHost _host;

    public LogViewerHostTests()
    {
        _ = Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "app.log");
        File.WriteAllText(_file, "2026-01-01 10:00:00 INFO boot ok\n2026-01-01 10:00:01 ERROR db down\n");
        _options = new LogHelperOptions { AllowedRoots = new[] { _dir } };
        var watcher = new LogWatcher(new[] { new FileLogSourceProvider(_options) }, _options, TimeSpan.FromMilliseconds(50));
        _host = new LogViewerHost(
            watcher,
            _options,
            _launcher,
            new LogViewerOptions { ConnectTimeout = TimeSpan.FromMilliseconds(600), MaxLaunchAttempts = 2, CloseGrace = TimeSpan.FromMilliseconds(300) });
    }

    private ChannelId Channel => new(null, "file", _file);

    public void Dispose()
    {
        _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _launcher.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Start_OpensATab_AndShowsExistingAndNewEntries()
    {
        WatchInfo info = await _host.StartAsync(Channel, null, null, reopen: true, CancellationToken.None);
        FakeViewer viewer = await _launcher.NextViewerAsync();

        await Eventually(() => viewer.Messages.Count(m => m.Type == ViewerMessage.Entry) >= 2);
        Assert.Contains(viewer.Messages, m => m.Type == ViewerMessage.Hello && m.Channel == Channel.ToString());
        Assert.Contains(viewer.Messages, m => Says(m, "db down") && m.Severity == "Error");

        File.AppendAllText(_file, "2026-01-01 10:00:02 WARN slow query\n");
        await Eventually(() => viewer.Messages.Any(m => Says(m, "slow query")));

        await Eventually(() => _host.List().Single(w => w.Id == info.Id).State == WatchState.Connected);
        Assert.Equal(viewer.Pid, _host.List().Single().ViewerPid);
    }

    [Fact]
    public async Task Filter_MarksEntriesTheFilterDidNotMatch()
    {
        _ = await _host.StartAsync(Channel, new LogFilterSpec { MinLevel = LogSeverity.Error }, "minLevel=Error", reopen: true, CancellationToken.None);
        FakeViewer viewer = await _launcher.NextViewerAsync();

        await Eventually(() => viewer.Messages.Count(m => m.Type == ViewerMessage.Entry) >= 2);
        Assert.Equal(false, viewer.Messages.Single(m => Says(m, "boot ok")).Matched);
        Assert.Equal(true, viewer.Messages.Single(m => Says(m, "db down")).Matched);
    }

    [Fact]
    public async Task SameChannel_IsOneWatch()
    {
        WatchInfo first = await _host.StartAsync(Channel, null, null, reopen: true, CancellationToken.None);
        WatchInfo again = await _host.StartAsync(Channel, null, null, reopen: false, CancellationToken.None);
        Assert.Equal(first.Id, again.Id);
        _ = await _launcher.NextViewerAsync();
        Assert.Equal(1, _launcher.Opened);
    }

    [Fact]
    public async Task Restart_ClosesTheViewer_OpensANewOne_AndReplaysEntries()
    {
        WatchInfo info = await _host.StartAsync(Channel, null, null, reopen: true, CancellationToken.None);
        FakeViewer first = await _launcher.NextViewerAsync();
        await Eventually(() => first.Messages.Count(m => m.Type == ViewerMessage.Entry) >= 2);

        Assert.True(_host.Restart(info.Id));
        FakeViewer second = await _launcher.NextViewerAsync();

        await Eventually(() => first.Messages.Any(m => m.Type == ViewerMessage.Close));
        await Eventually(() => second.Messages.Count(m => m.Type == ViewerMessage.Entry) >= 2);
        Assert.NotEqual(first.Pid, second.Pid);
    }

    [Fact]
    public async Task UserClosingTheTab_IsNotReopened_UntilRestart()
    {
        WatchInfo info = await _host.StartAsync(Channel, null, null, reopen: true, CancellationToken.None);
        FakeViewer viewer = await _launcher.NextViewerAsync();
        await Eventually(() => _host.List().Single().State == WatchState.Connected);

        viewer.Exit();
        await Eventually(() => _host.List().Single().State == WatchState.Closed);
        await Task.Delay(300);
        Assert.Equal(1, _launcher.Opened);

        // An automatic start (a query) does not reopen it either.
        _ = await _host.StartAsync(Channel, null, null, reopen: false, CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(1, _launcher.Opened);

        // An explicit start does.
        _ = await _host.StartAsync(Channel, null, null, reopen: true, CancellationToken.None);
        _ = await _launcher.NextViewerAsync();
        Assert.Equal(info.Id, _host.List().Single().Id);
    }

    [Fact]
    public async Task HungViewer_IsKilled_AndReplaced()
    {
        _ = await _host.StartAsync(Channel, null, null, reopen: true, CancellationToken.None);
        FakeViewer hung = await _launcher.NextViewerAsync();
        await Eventually(() => _host.List().Single().State == WatchState.Connected);

        hung.DropPipeKeepProcess();
        FakeViewer replacement = await _launcher.NextViewerAsync();

        Assert.True(hung.Killed);
        await Eventually(() => replacement.Messages.Any(m => m.Type == ViewerMessage.Entry));
    }

    [Fact]
    public async Task NoViewerConnecting_EndsAsNoViewer_AfterTheAllowedAttempts()
    {
        _launcher.ConnectViewers = false;
        _ = await _host.StartAsync(Channel, null, null, reopen: true, CancellationToken.None);

        await Eventually(() => _host.List().Single().State == WatchState.NoViewer, TimeSpan.FromSeconds(10));
        Assert.Equal(2, _launcher.Opened);
    }

    [Fact]
    public async Task Stop_ClosesTheViewer_AndRemovesTheWatch()
    {
        WatchInfo info = await _host.StartAsync(Channel, null, null, reopen: true, CancellationToken.None);
        FakeViewer viewer = await _launcher.NextViewerAsync();
        await Eventually(() => _host.List().Single().State == WatchState.Connected);

        Assert.True(await _host.StopAsync(info.Id));

        Assert.Empty(_host.List());
        await Eventually(() => viewer.Messages.Any(m => m.Type == ViewerMessage.Close));
        Assert.False(await _host.StopAsync(info.Id));
    }

    [Fact]
    public async Task UnreadableChannel_ReportsFaulted()
    {
        var outside = new ChannelId(null, "file", Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".log"));
        WatchInfo info = await _host.StartAsync(outside, null, null, reopen: true, CancellationToken.None);
        await Eventually(() => _host.List().Any(w => w.Id == info.Id && w.State == WatchState.Faulted));
        Assert.NotNull(_host.List().Single(w => w.Id == info.Id).Error);
    }

    // The parser keeps the level word in the message text, so match on what the line says.
    private static bool Says(ViewerMessage m, string text) => m.Text?.Contains(text, StringComparison.Ordinal) == true;

    private static async Task Eventually(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(8));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "condition was not met in time");
    }

    private sealed class FakeLauncher : IViewerLauncher, IDisposable
    {
        private readonly ConcurrentDictionary<int, FakeViewer> _viewers = new();
        private readonly System.Threading.Channels.Channel<FakeViewer> _opened = System.Threading.Channels.Channel.CreateUnbounded<FakeViewer>();
        private int _nextPid = 4000;
        private int _count;

        public bool ConnectViewers { get; set; } = true;

        public int Opened => Volatile.Read(ref _count);

        public void OpenTab(string title, string pipeName)
        {
            _ = Interlocked.Increment(ref _count);
            if (!ConnectViewers)
            {
                return;
            }

            // A real launcher returns at once and the viewer connects on its own, so the fake does the same.
            int pid = Interlocked.Increment(ref _nextPid);
            _ = Task.Run(() =>
            {
                var viewer = new FakeViewer(pid, pipeName);
                _viewers[viewer.Pid] = viewer;
                _ = _opened.Writer.TryWrite(viewer);
            });
        }

        public async Task<FakeViewer> NextViewerAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await _opened.Reader.ReadAsync(cts.Token);
        }

        public bool IsRunning(int pid) => _viewers.TryGetValue(pid, out FakeViewer? v) && v.Alive;

        public void Kill(int pid)
        {
            if (_viewers.TryGetValue(pid, out FakeViewer? v))
            {
                v.Kill();
            }
        }

        public void Dispose()
        {
            foreach (FakeViewer viewer in _viewers.Values)
            {
                viewer.Kill();
            }
        }
    }

    /// <summary>Stands in for the viewer process: connects to the pipe, says ready, records what it is sent.</summary>
#pragma warning disable CA1001 // Test fake: its pipe is closed by Kill or when the process is torn down.
    private sealed class FakeViewer
    {
        private readonly NamedPipeClientStream _client;
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;

        public FakeViewer(int pid, string pipeName)
        {
            Pid = pid;
            _client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _client.Connect(5000);
            _reader = new StreamReader(_client);
            _writer = new StreamWriter(_client) { AutoFlush = true };
            _writer.WriteLine(new ViewerMessage { Type = ViewerMessage.Ready, Pid = pid }.ToLine());
            _ = Task.Run(Read);
        }

        public int Pid { get; }

        public volatile bool Alive = true;

        public bool Killed { get; private set; }

        public ConcurrentQueue<ViewerMessage> Seen { get; } = new();

        public IReadOnlyCollection<ViewerMessage> Messages => Seen.ToArray();

        /// <summary>The user closed the tab: the process ends and the pipe closes.</summary>
        public void Exit()
        {
            Alive = false;
            _client.Dispose();
        }

        /// <summary>The pipe breaks but the process keeps running.</summary>
        public void DropPipeKeepProcess() => _client.Dispose();

        public void Kill()
        {
            Killed = true;
            Exit();
        }

        private async Task Read()
        {
            try
            {
                while (await _reader.ReadLineAsync() is { } line)
                {
                    if (ViewerMessage.TryParse(line, out ViewerMessage? message) && message is not null)
                    {
                        Seen.Enqueue(message);
                        if (message.Type == ViewerMessage.Close)
                        {
                            Exit();
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }
}

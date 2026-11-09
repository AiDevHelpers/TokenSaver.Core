namespace TokenSaver.TerminalControl.Tests;

using Microsoft.Extensions.Logging;
using Xunit;
using TokenSaver.TerminalControl.Core;
using TokenSaver.TerminalControl.Models;
using TokenSaver.TerminalControl.Output;

public sealed class ElevatedTerminalSessionTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.AddDebug());

    public void Dispose() => _loggerFactory.Dispose();

    [Fact]
    public async Task InitializeAsync_LaunchesWindowElevatedWithRunAs()
    {
        var launcher = new RecordingTerminalLauncher();
        using var pipes = new NamedPipeManager(_loggerFactory.CreateLogger<NamedPipeManager>());
        var session = new ElevatedTerminalSession(
            _loggerFactory.CreateLogger<ElevatedTerminalSession>(),
            pipes,
            launcher);

        await session.InitializeAsync(new TerminalSessionConfig(IsElevated: true));

        var launch = Assert.Single(launcher.Started);
        Assert.Equal("runas", launch.Verb);
        Assert.True(launch.UseShellExecute);
        Assert.True(session.IsElevated);
    }

    [Fact]
    public async Task CreateTabAsync_RunsTabWithoutRunAs()
    {
        // The probe showed a plain wt -w new-tab from a standard session lands elevated in the elevated window.
        var launcher = new RecordingTerminalLauncher();
        using var pipes = new NamedPipeManager(_loggerFactory.CreateLogger<NamedPipeManager>());
        var session = new ElevatedTerminalSession(
            _loggerFactory.CreateLogger<ElevatedTerminalSession>(),
            pipes,
            launcher);
        await session.InitializeAsync(new TerminalSessionConfig(IsElevated: true));

        await session.CreateTabAsync("elevated-tab");

        var tabLaunch = Assert.Single(launcher.Started.Skip(1));
        Assert.Equal("wt.exe", tabLaunch.FileName);
        Assert.Contains("new-tab", tabLaunch.Arguments, StringComparison.Ordinal);
        Assert.NotEqual("runas", tabLaunch.Verb);
    }
}

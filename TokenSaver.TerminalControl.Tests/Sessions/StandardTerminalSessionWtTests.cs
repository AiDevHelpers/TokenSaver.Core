namespace TokenSaver.TerminalControl.Tests;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;
using TokenSaver.TerminalControl.Contracts;
using TokenSaver.TerminalControl.Core;
using TokenSaver.TerminalControl.Models;
using TokenSaver.TerminalControl.Output;

public sealed class StandardTerminalSessionWtTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.AddDebug());

    public void Dispose() => _loggerFactory.Dispose();

    private StandardTerminalSession CreateSession(RecordingTerminalLauncher launcher, NamedPipeManager pipes) =>
        new(_loggerFactory.CreateLogger<StandardTerminalSession>(), pipes, launcher);

    [Fact]
    public async Task InitializeAsync_LaunchesWtWithWindowArgument()
    {
        var launcher = new RecordingTerminalLauncher();
        using var pipes = new NamedPipeManager(_loggerFactory.CreateLogger<NamedPipeManager>());
        var session = CreateSession(launcher, pipes);

        await session.InitializeAsync(new TerminalSessionConfig());

        var launch = Assert.Single(launcher.Started);
        Assert.EndsWith("wt.exe", launch.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-w", launch.Arguments);
        Assert.Equal(TerminalSessionState.Ready, session.State);
    }

    [Fact]
    public async Task CreateTabAsync_OpensNewTabRunningWrapperWithPipeNames()
    {
        var launcher = new RecordingTerminalLauncher();
        using var pipes = new NamedPipeManager(_loggerFactory.CreateLogger<NamedPipeManager>());
        var session = CreateSession(launcher, pipes);
        await session.InitializeAsync(new TerminalSessionConfig());

        var tab = await session.CreateTabAsync("build");

        var tabLaunch = Assert.Single(launcher.Started.Skip(1));
        Assert.Contains("new-tab", tabLaunch.Arguments);
        Assert.Contains("tokensaver-tab.ps1", tabLaunch.Arguments);
        Assert.Contains(tab.StdoutPipe.PipePath, tabLaunch.Arguments);
        Assert.Contains(tab.StderrPipe.PipePath, tabLaunch.Arguments);
    }

    [Fact]
    public async Task InitializeAsync_WhenWtMissing_TerminatesAndRethrows()
    {
        var launcher = new RecordingTerminalLauncher { ThrowOnStart = new FileNotFoundException("wt.exe") };
        using var pipes = new NamedPipeManager(_loggerFactory.CreateLogger<NamedPipeManager>());
        var session = CreateSession(launcher, pipes);

        await Assert.ThrowsAsync<FileNotFoundException>(() => session.InitializeAsync(new TerminalSessionConfig()));
        Assert.Equal(TerminalSessionState.Terminated, session.State);
    }
}

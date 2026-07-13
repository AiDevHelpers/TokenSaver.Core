namespace TokenSaver.TerminalControl.Tests;

using Microsoft.Extensions.Logging;
using Xunit;
using TokenSaver.TerminalControl.Core;
using TokenSaver.TerminalControl.Models;
using TokenSaver.TerminalControl.Output;

public sealed class TerminalSessionPipeIntegrationTests
{
    private readonly ILogger<NamedPipeManager> _loggerPipeManager;
    private readonly ILogger<ElevatedTerminalSession> _loggerElevated;
    private readonly ILogger<StandardTerminalSession> _loggerStandard;

    public TerminalSessionPipeIntegrationTests()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        _loggerPipeManager = loggerFactory.CreateLogger<NamedPipeManager>();
        _loggerElevated = loggerFactory.CreateLogger<ElevatedTerminalSession>();
        _loggerStandard = loggerFactory.CreateLogger<StandardTerminalSession>();
    }

    [Fact]
    public async Task ElevatedSession_CreateTabAsync_CreatesPipes()
    {
        // Arrange
        var pipeManager = new NamedPipeManager(_loggerPipeManager);
        var session = new ElevatedTerminalSession(_loggerElevated, pipeManager);
        await session.InitializeAsync(new TerminalSessionConfig());

        // Act
        var tab = await session.CreateTabAsync("test-tab");

        // Assert
        Assert.NotNull(tab);
        Assert.NotNull(tab.StdoutPipe);
        Assert.NotNull(tab.StderrPipe);
        Assert.True(tab.StdoutPipe.IsValid);
        Assert.True(tab.StderrPipe.IsValid);
        Assert.NotEqual(tab.StdoutPipe.PipeName, tab.StderrPipe.PipeName);
    }

    [Fact]
    public async Task StandardSession_CreateTabAsync_CreatesPipes()
    {
        // Arrange
        var pipeManager = new NamedPipeManager(_loggerPipeManager);
        var session = new StandardTerminalSession(_loggerStandard, pipeManager);
        await session.InitializeAsync(new TerminalSessionConfig());

        // Act
        var tab = await session.CreateTabAsync("test-tab");

        // Assert
        Assert.NotNull(tab);
        Assert.NotNull(tab.StdoutPipe);
        Assert.NotNull(tab.StderrPipe);
        Assert.True(tab.StdoutPipe.IsValid);
        Assert.True(tab.StderrPipe.IsValid);
    }

    [Fact]
    public async Task CreateTabAsync_PipeNamesContainSessionAndTabId()
    {
        // Arrange
        var pipeManager = new NamedPipeManager(_loggerPipeManager);
        var session = new ElevatedTerminalSession(_loggerElevated, pipeManager);
        await session.InitializeAsync(new TerminalSessionConfig());

        // Act
        var tab = await session.CreateTabAsync("my-task");

        // Assert
        Assert.Contains(session.SessionId, tab.StdoutPipe.PipeName);
        Assert.Contains(tab.TabId, tab.StdoutPipe.PipeName);
        Assert.Contains("Stdout", tab.StdoutPipe.PipeName);
        Assert.Contains("Stderr", tab.StderrPipe.PipeName);
    }

    [Fact]
    public async Task CreateTabAsync_PipePathsAreValid()
    {
        // Arrange
        var pipeManager = new NamedPipeManager(_loggerPipeManager);
        var session = new StandardTerminalSession(_loggerStandard, pipeManager);
        await session.InitializeAsync(new TerminalSessionConfig());

        // Act
        var tab = await session.CreateTabAsync("test");

        // Assert
        Assert.StartsWith(@"\\.\pipe\", tab.StdoutPipe.PipePath);
        Assert.StartsWith(@"\\.\pipe\", tab.StderrPipe.PipePath);
        Assert.EndsWith(tab.StdoutPipe.PipeName, tab.StdoutPipe.PipePath);
        Assert.EndsWith(tab.StderrPipe.PipeName, tab.StderrPipe.PipePath);
    }

    [Fact]
    public async Task GetActivePipes_ReturnsBothPipes()
    {
        // Arrange
        var pipeManager = new NamedPipeManager(_loggerPipeManager);
        var session = new ElevatedTerminalSession(_loggerElevated, pipeManager);
        await session.InitializeAsync(new TerminalSessionConfig());

        // Act
        var tab = await session.CreateTabAsync("test");
        var activePipes = tab.GetActivePipes();

        // Assert
        Assert.Equal(2, activePipes.Count);
        Assert.Contains(tab.StdoutPipe, activePipes);
        Assert.Contains(tab.StderrPipe, activePipes);
    }

    [Fact]
    public async Task MultipleTabsInSession_HaveSeparatePipes()
    {
        // Arrange
        var pipeManager = new NamedPipeManager(_loggerPipeManager);
        var session = new StandardTerminalSession(_loggerStandard, pipeManager);
        await session.InitializeAsync(new TerminalSessionConfig());

        // Act
        var tab1 = await session.CreateTabAsync("tab-1");
        var tab2 = await session.CreateTabAsync("tab-2");

        // Assert
        Assert.NotEqual(tab1.StdoutPipe.PipeName, tab2.StdoutPipe.PipeName);
        Assert.NotEqual(tab1.StderrPipe.PipeName, tab2.StderrPipe.PipeName);
        Assert.NotEqual(tab1.TabId, tab2.TabId);
    }

    [Fact]
    public async Task CloseTabAsync_ClosesPipes()
    {
        // Arrange
        var pipeManager = new NamedPipeManager(_loggerPipeManager);
        var session = new ElevatedTerminalSession(_loggerElevated, pipeManager);
        await session.InitializeAsync(new TerminalSessionConfig());
        var tab = await session.CreateTabAsync("test");

        // Verify pipes exist
        Assert.NotNull(pipeManager.GetPipe(tab.StdoutPipe.PipeName));
        Assert.NotNull(pipeManager.GetPipe(tab.StderrPipe.PipeName));

        // Act
        await session.CloseTabAsync(tab.TabId);

        // Assert
        Assert.Null(pipeManager.GetPipe(tab.StdoutPipe.PipeName));
        Assert.Null(pipeManager.GetPipe(tab.StderrPipe.PipeName));
    }

    [Fact]
    public async Task TerminateAsync_ClosesAllSessionPipes()
    {
        // Arrange
        var pipeManager = new NamedPipeManager(_loggerPipeManager);
        var session = new StandardTerminalSession(_loggerStandard, pipeManager);
        await session.InitializeAsync(new TerminalSessionConfig());

        var tab1 = await session.CreateTabAsync("tab-1");
        var tab2 = await session.CreateTabAsync("tab-2");
        var tab3 = await session.CreateTabAsync("tab-3");

        var pipes = new[] {
            tab1.StdoutPipe.PipeName, tab1.StderrPipe.PipeName,
            tab2.StdoutPipe.PipeName, tab2.StderrPipe.PipeName,
            tab3.StdoutPipe.PipeName, tab3.StderrPipe.PipeName
        };

        // Verify all pipes exist
        foreach (var pipeName in pipes)
        {
            Assert.NotNull(pipeManager.GetPipe(pipeName));
        }

        // Act
        await session.TerminateAsync();

        // Assert
        foreach (var pipeName in pipes)
        {
            Assert.Null(pipeManager.GetPipe(pipeName));
        }
    }

    [Fact]
    public async Task TabInfo_StoresPipeMetadata()
    {
        // Arrange
        var pipeManager = new NamedPipeManager(_loggerPipeManager);
        var session = new ElevatedTerminalSession(_loggerElevated, pipeManager);
        await session.InitializeAsync(new TerminalSessionConfig());

        // Act
        var tab = await session.CreateTabAsync("metadata-test");

        // Assert
        Assert.Equal(NamedPipeStreamType.Stdout, tab.StdoutPipe.StreamType);
        Assert.Equal(NamedPipeStreamType.Stderr, tab.StderrPipe.StreamType);
        Assert.True(tab.StdoutPipe.CreatedAt <= DateTime.UtcNow);
        Assert.True(tab.StderrPipe.CreatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task CreateTabAsync_PipeRefIsConsistent()
    {
        // Arrange
        var pipeManager = new NamedPipeManager(_loggerPipeManager);
        var session = new StandardTerminalSession(_loggerStandard, pipeManager);
        await session.InitializeAsync(new TerminalSessionConfig());

        // Act
        var tab = await session.CreateTabAsync("consistent-test");

        // The pipe name in the tab should match what the pipe manager has
        var stdout = pipeManager.GetPipe(tab.StdoutPipe.PipeName);
        var stderr = pipeManager.GetPipe(tab.StderrPipe.PipeName);

        // Assert
        Assert.NotNull(stdout);
        Assert.NotNull(stderr);
        Assert.Equal(tab.StdoutPipe.PipePath, @$"\\.\pipe\{tab.StdoutPipe.PipeName}");
        Assert.Equal(tab.StderrPipe.PipePath, @$"\\.\pipe\{tab.StderrPipe.PipeName}");
    }
}

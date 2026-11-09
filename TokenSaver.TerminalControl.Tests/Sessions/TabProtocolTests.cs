namespace TokenSaver.TerminalControl.Tests;

using Xunit;
using TokenSaver.TerminalControl.Core;

public sealed class TabProtocolTests
{
    [Fact]
    public void FormatCommand_PrefixesRequestId()
    {
        Assert.Equal("req1 dir C:\\", TabProtocol.FormatCommand("req1", "dir C:\\"));
    }

    [Fact]
    public void FormatCommand_RejectsNewlines()
    {
        Assert.Throws<ArgumentException>(() => TabProtocol.FormatCommand("req1", "a\nb"));
        Assert.Throws<ArgumentException>(() => TabProtocol.FormatCommand("req1", "a\rb"));
    }

    [Fact]
    public void TryParseEnd_ParsesIdAndExitCode()
    {
        var parsed = TabProtocol.TryParseEnd("__TOKENSAVER_END__ req1 3", out var id, out var exitCode);

        Assert.True(parsed);
        Assert.Equal("req1", id);
        Assert.Equal(3, exitCode);
    }

    [Fact]
    public void TryParseError_ParsesIdAndTextWithSpaces()
    {
        var parsed = TabProtocol.TryParseError("__TOKENSAVER_ERR__ req1 boom at line 3", out var id, out var text);

        Assert.True(parsed);
        Assert.Equal("req1", id);
        Assert.Equal("boom at line 3", text);
    }

    [Fact]
    public void TryParseError_IgnoresStdoutAndEndMarkers()
    {
        Assert.False(TabProtocol.TryParseError("plain output", out _, out _));
        Assert.False(TabProtocol.TryParseEnd("__TOKENSAVER_ERR__ req1 boom", out _, out _));
    }

    [Fact]
    public void TryParseEnd_IgnoresOrdinaryOutput()
    {
        Assert.False(TabProtocol.TryParseEnd("hello world", out _, out _));
        Assert.False(TabProtocol.TryParseEnd("__TOKENSAVER_END__ only-id", out _, out _));
    }
}

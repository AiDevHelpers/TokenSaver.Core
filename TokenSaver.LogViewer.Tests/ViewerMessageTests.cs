using Xunit;
namespace TokenSaver.LogViewer.Tests;

public sealed class ViewerMessageTests
{
    [Fact]
    public void RoundTrips_AndOmitsEmptyFields()
    {
        var message = new ViewerMessage { Type = ViewerMessage.Entry, Text = "hello", Severity = "Error", Matched = false };
        string line = message.ToLine();

        Assert.DoesNotContain("category", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
        Assert.True(ViewerMessage.TryParse(line, out ViewerMessage? back));
        Assert.Equal(message, back);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void TryParse_RejectsGarbage(string text) => Assert.False(ViewerMessage.TryParse(text, out _));

    [Fact]
    public void PipeName_IsStablePerWatch() =>
        Assert.Equal("tokensaver-logview-ab12", ViewerMessage.PipeNameFor("ab12"));
}

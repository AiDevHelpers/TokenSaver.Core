using AiDevTools.TokenSaver.Common.Core.Processes;

using Xunit;

namespace AiDevTools.TokenSaver.Utilities.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("test --filter Foo", new[] { "test", "--filter", "Foo" })]
    [InlineData("  a   b  ", new[] { "a", "b" })]
    [InlineData("--format \"{{.A}} {{.B}}\" name", new[] { "--format", "{{.A}} {{.B}}", "name" })]
    [InlineData("x \"say \\\"hi\\\"\"", new[] { "x", "say \"hi\"" })]
    [InlineData("a \"\" b", new[] { "a", "", "b" })]
    [InlineData("a;b && c | d", new[] { "a;b", "&&", "c", "|", "d" })]
    [InlineData("%PATH% $env:X *.cs", new[] { "%PATH%", "$env:X", "*.cs" })]
    [InlineData("", new string[0])]
    public void Split_GroupsByQuotesAndNeverInterpretsAnythingElse(string line, string[] expected)
    {
        Assert.Equal(expected, CommandLine.Split(line));
    }

    [Fact]
    public void Split_Null_IsEmpty()
    {
        Assert.Empty(CommandLine.Split(null));
    }
}

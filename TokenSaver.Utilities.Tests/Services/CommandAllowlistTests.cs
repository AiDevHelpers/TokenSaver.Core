using TokenSaver.Utilities.Services;

using Xunit;

namespace TokenSaver.Utilities.Tests;

public sealed class CommandAllowlistTests
{
    [Theory]
    [InlineData("dotnet test --filter Foo", "dotnet", "test --filter Foo")]
    [InlineData("npm run lint", "npm", "run lint")]
    [InlineData("DOTNET build", "DOTNET", "build")]
    [InlineData("dotnet.exe test", "dotnet.exe", "test")]
    [InlineData("pytest", "pytest", "")]
    public void AllowedCommands_SplitIntoExecutableAndArguments(string line, string command, string args)
    {
        CommandCheck check = CommandAllowlist.Check(line, null);

        Assert.True(check.Ok, check.Error);
        Assert.Equal(command, check.Command);
        Assert.Equal(args, check.Arguments);
    }

    [Theory]
    [InlineData("cmd /c del /q *")]
    [InlineData("powershell -c Remove-Item x")]
    [InlineData("pwsh -File evil.ps1")]
    [InlineData("sh -c rm")]
    [InlineData("bash -c rm")]
    [InlineData("curl http://x")]
    [InlineData(@"C:\Windows\System32\cmd.exe /c dir")]
    [InlineData("./dotnet test")]
    [InlineData(@"..\dotnet test")]
    [InlineData("dotnet.bat test")]
    [InlineData("dotnetx test")]
    public void NonAllowlistedOrPathedCommands_AreRefused(string line)
    {
        CommandCheck check = CommandAllowlist.Check(line, null);

        Assert.False(check.Ok);
        Assert.NotNull(check.Error);
    }

    [Fact]
    public void EmptyCommand_IsRefused()
    {
        Assert.False(CommandAllowlist.Check("", null).Ok);
        Assert.False(CommandAllowlist.Check("   ", null).Ok);
        Assert.False(CommandAllowlist.Check(null, null).Ok);
    }

    [Fact]
    public void ConfiguredList_ReplacesTheDefault()
    {
        Assert.True(CommandAllowlist.Check("make test", "make, ninja").Ok);
        Assert.False(CommandAllowlist.Check("dotnet test", "make, ninja").Ok);
    }
}

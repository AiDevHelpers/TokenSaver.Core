namespace AiDevTools.TokenSaver.WslControl.Tests;

using AiDevTools.TokenSaver.WslControl.Core;

using Xunit;

public sealed class SessionWslTests
{
    [Fact]
    public void ParseDistroList_ReadsDefaultAndVersionFromUtf16Output()
    {
        // wsl -l -v prints UTF-16 text with a header and an optional leading '*' on the default.
        const string text = "  NAME            STATE           VERSION\r\n" +
                            "* kali-linux    Stopped         2\r\n" +
                            "  Ubuntu        Running         1\r\n";

        var distros = SessionWslApi.ParseDistroList(text);

        Assert.Equal(2, distros.Count);
        Assert.Equal("kali-linux", distros[0].DistroName);
        Assert.True(distros[0].IsDefault);
        Assert.Equal(2, distros[0].WslVersion);
        Assert.Equal("Ubuntu", distros[1].DistroName);
        Assert.False(distros[1].IsDefault);
        Assert.Equal(1, distros[1].WslVersion);
    }

    [Fact]
    public void ParseDistroList_IgnoresEmptyAndHeaderOnlyOutput()
    {
        Assert.Empty(SessionWslApi.ParseDistroList(string.Empty));
        Assert.Empty(SessionWslApi.ParseDistroList("  NAME    STATE    VERSION\r\n"));
    }

    [Fact]
    public void Quote_LeavesSimpleArgsAndWrapsSpacedOnes()
    {
        Assert.Equal("kali-linux", WslCommandLine.Quote("kali-linux"));
        Assert.Equal("\"a b\"", WslCommandLine.Quote("a b"));
        Assert.Equal("\"say \\\"hi\\\"\"", WslCommandLine.Quote("say \"hi\""));
    }

    [Fact]
    public void ForExec_PutsDistroFileNameAndArgsAfterDashDash()
    {
        string line = WslCommandLine.ForExec("kali-linux", "/bin/bash", ["-c", "uname -sr"]);

        Assert.EndsWith(" -d kali-linux -- /bin/bash -c \"uname -sr\"", line, StringComparison.Ordinal);
        Assert.Contains("wsl.exe", line, StringComparison.OrdinalIgnoreCase);
    }
}

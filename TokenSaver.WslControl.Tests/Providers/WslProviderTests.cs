namespace AiDevTools.TokenSaver.WslControl.Tests;

using Microsoft.Extensions.Logging.Abstractions;

using AiDevTools.TokenSaver.WslControl.Core;
using AiDevTools.TokenSaver.WslControl.Models;

using Xunit;

public sealed class WslProviderTests
{
    private static WslProvider Build(FakeWslApi api, FakeExecutor executor) =>
        new(api, executor, NullLogger<WslProvider>.Instance);

    private static FakeWslApi WithDefault(string name = "Ubuntu") =>
        new() { Distros = [FakeWslApi.Distro(name, isDefault: true)] };

    [Fact]
    public async Task CheckAvailability_NotSupported_ReportsMissing()
    {
        WslProvider provider = Build(new FakeWslApi { Supported = false, MissingReason = "no vmp" }, new FakeExecutor());

        WslAvailability result = await provider.CheckAvailabilityAsync();

        Assert.False(result.IsInstalled);
        Assert.Equal("no vmp", result.Reason);
        Assert.False(result.IsUsable);
    }

    [Fact]
    public async Task CheckAvailability_Supported_CountsDistrosAndDefault()
    {
        FakeWslApi api = new()
        {
            Distros = [FakeWslApi.Distro("Ubuntu", isDefault: true), FakeWslApi.Distro("Debian")]
        };

        WslAvailability result = await Build(api, new FakeExecutor()).CheckAvailabilityAsync();

        Assert.True(result.IsUsable);
        Assert.Equal(2, result.DistroCount);
        Assert.Equal("Ubuntu", result.DefaultDistro);
    }

    [Fact]
    public async Task AptInstall_RejectsInvalidPackageName_AndDoesNotExecute()
    {
        FakeExecutor executor = new();
        WslProvider provider = Build(WithDefault(), executor);

        WslCommandResult result = await provider.AptInstallAsync(["ripgrep", "bad;rm -rf"]);

        Assert.False(result.Succeeded);
        Assert.Contains("bad;rm -rf", result.ExecutionError, StringComparison.Ordinal);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task AptInstall_EmptyList_IsRejected()
    {
        FakeExecutor executor = new();
        WslCommandResult result = await Build(WithDefault(), executor).AptInstallAsync([]);

        Assert.False(result.Succeeded);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task AptInstall_BuildsQuotedNonInteractiveLine()
    {
        FakeExecutor executor = new();
        WslProvider provider = Build(WithDefault(), executor);

        _ = await provider.AptInstallAsync(["ripgrep", "fd-find"]);

        Assert.Equal("/bin/bash", executor.Calls[^1].FileName);
        Assert.Equal("-c", executor.Calls[^1].Args[0]);
        string line = executor.LastCommandLine!;
        Assert.Contains("apt-get install -y 'ripgrep' 'fd-find'", line, StringComparison.Ordinal);
        Assert.Contains("DEBIAN_FRONTEND=noninteractive", line, StringComparison.Ordinal);
        Assert.Contains("sudo -n", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_WindowsPath_TranslatesViaWslpath()
    {
        FakeExecutor executor = new();
        WslProvider provider = Build(WithDefault(), executor);

        _ = await provider.BuildAsync(@"F:\mcp\TokenSaver", "dotnet build");

        string line = executor.LastCommandLine!;
        Assert.Contains("wslpath -a 'F:\\mcp\\TokenSaver'", line, StringComparison.Ordinal);
        Assert.Contains("&& dotnet build", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_LinuxPath_UsedDirectly()
    {
        FakeExecutor executor = new();
        WslProvider provider = Build(WithDefault(), executor);

        _ = await provider.BuildAsync("/home/rich/proj", "make");

        string line = executor.LastCommandLine!;
        Assert.DoesNotContain("wslpath", line, StringComparison.Ordinal);
        Assert.Contains("cd '/home/rich/proj' && make", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunBash_NoDistroAndNoDefault_ReturnsExecutionError()
    {
        FakeExecutor executor = new();
        WslProvider provider = Build(new FakeWslApi { Distros = [] }, executor);

        WslCommandResult result = await provider.RunBashAsync("ls");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.ExecutionError);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task CheckHealth_ParsesKeyValueProbeOutput()
    {
        FakeExecutor executor = new(_ => new WslCommandResult(
            "Ubuntu", "probe", 0,
            Stdout: "KERNEL=Linux 5.15.0\nUSER=rich\nOS=Ubuntu 22.04.4 LTS\nAPT=1\nSUDO=1"));
        WslProvider provider = Build(WithDefault(), executor);

        WslHealthReport report = await provider.CheckHealthAsync();

        Assert.True(report.IsHealthy);
        Assert.Equal("Ubuntu 22.04.4 LTS", report.OsDescription);
        Assert.Equal("Linux 5.15.0", report.Kernel);
        Assert.Equal("rich", report.DefaultUser);
        Assert.True(report.HasApt);
        Assert.True(report.HasSudo);
    }

    [Fact]
    public async Task CheckHealth_ProbeFailure_IsUnhealthy()
    {
        FakeExecutor executor = new(_ => new WslCommandResult("Ubuntu", "probe", 1, Stderr: "boom"));
        WslProvider provider = Build(WithDefault(), executor);

        WslHealthReport report = await provider.CheckHealthAsync();

        Assert.False(report.IsHealthy);
        Assert.Equal("boom", report.Detail);
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it'\\''s'")]
    public void BashQuote_SingleQuotesAndEscapes(string input, string expected) =>
        Assert.Equal(expected, WslProvider.BashQuote(input));
}

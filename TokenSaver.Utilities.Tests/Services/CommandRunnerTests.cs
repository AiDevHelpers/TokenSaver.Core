using System.Diagnostics;

using AiDevTools.TokenSaver.Common.Core.Processes;

using Xunit;

namespace AiDevTools.TokenSaver.Utilities.Tests;

/// <summary>The shared runner against real child processes (PowerShell 7, present on the dev and CI machines).</summary>
public sealed class CommandRunnerTests
{
    private static CommandRequest Pwsh(string script, TimeSpan? timeout = null, int? max = null)
    {
        return new CommandRequest
        {
            FileName = "pwsh",
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
            MaxOutputChars = max ?? 4_000_000,
        };
    }

    [Fact]
    public async Task CapturesStdoutStderrAndExitCode()
    {
        CommandResult r = await CommandRunner.RunAsync(Pwsh("[Console]::Out.Write('out'); [Console]::Error.Write('err'); exit 3"));

        Assert.True(r.Launched);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("out", r.StdOut);
        Assert.Equal("err", r.StdErr);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task ABigWriteToStderrWhileStdoutIsOpen_DoesNotDeadlock()
    {
        // 1 MB to stderr first, then stdout: the old read-stdout-then-stderr loop blocked forever here.
        CommandResult r = await CommandRunner.RunAsync(
            Pwsh("[Console]::Error.Write('e' * 1048576); [Console]::Out.Write('done')", TimeSpan.FromSeconds(30)));

        Assert.False(r.TimedOut);
        Assert.Equal("done", r.StdOut);
        Assert.Equal(1048576, r.StdErr.Length);
    }

    [Fact]
    public async Task ArgumentsAreLiteral_NoShellParsing()
    {
        string script = Path.Combine(Path.GetTempPath(), "cr-args-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(script, "foreach ($a in $args) { [Console]::Out.WriteLine($a) }");
        try
        {
            CommandResult r = await CommandRunner.RunAsync(new CommandRequest
            {
                FileName = "pwsh",
                Arguments = ["-NoProfile", "-File", script, "a b", "c;d & e \"q\"", "%PATH%"],
            });

            string[] lines = r.StdOut.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(["a b", "c;d & e \"q\"", "%PATH%"], lines);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task ATimeout_KillsTheTreeAndReportsIt()
    {
        Stopwatch watch = Stopwatch.StartNew();

        CommandResult r = await CommandRunner.RunAsync(Pwsh("Start-Sleep -Seconds 60", TimeSpan.FromMilliseconds(800)));

        Assert.True(r.TimedOut);
        Assert.False(r.Success);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Cancellation_KillsTheChildAndThrows()
    {
        using CancellationTokenSource cts = new();
        Task<CommandResult> run = CommandRunner.RunAsync(Pwsh("Start-Sleep -Seconds 60"), cts.Token);
        await Task.Delay(800);

        await cts.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task OutputBeyondTheCap_IsTruncatedAndTheChildStillFinishes()
    {
        CommandResult r = await CommandRunner.RunAsync(Pwsh("[Console]::Out.Write('x' * 500000)", max: 1000));

        Assert.True(r.StdOutTruncated);
        Assert.Equal(1000, r.StdOut.Length);
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task AMissingExecutable_IsNotLaunchedAndDoesNotThrow()
    {
        CommandResult r = await CommandRunner.RunAsync(new CommandRequest { FileName = "tokensaver-no-such-tool-xyz" });

        Assert.False(r.Launched);
        Assert.NotNull(r.LaunchError);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task WorkingDirectoryAndEnvironmentAreApplied()
    {
        string dir = Path.GetTempPath().TrimEnd('\\', '/');
        CommandResult r = await CommandRunner.RunAsync(new CommandRequest
        {
            FileName = "pwsh",
            Arguments = ["-NoProfile", "-Command", "(Get-Location).Path; $env:TS_PROBE"],
            WorkingDirectory = dir,
            Environment = new Dictionary<string, string?> { ["TS_PROBE"] = "hello" },
        });

        Assert.Contains("hello", r.StdOut, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(dir), r.StdOut, StringComparison.OrdinalIgnoreCase);
    }
}

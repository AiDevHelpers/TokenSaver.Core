namespace TokenSaver.TerminalControl.Tests;

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xunit;
using TokenSaver.TerminalControl.Contracts;
using TokenSaver.TerminalControl.Core;
using TokenSaver.TerminalControl.Models;
using TokenSaver.TerminalControl.Output;

/// <summary>
/// Manual smoke test: opens a real Windows Terminal window. Runs only when TOKENSAVER_WT_SMOKE=1.
/// </summary>
public sealed class WindowsTerminalSmokeTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.AddDebug());

    public void Dispose() => _loggerFactory.Dispose();

    [Fact]
    public async Task RealWindowsTerminal_RunsCommandInTab()
    {
        if (Environment.GetEnvironmentVariable("TOKENSAVER_WT_SMOKE") != "1")
        {
            return;
        }

        var wrapper = FindWrapperScript();
        using var pipes = new NamedPipeManager(_loggerFactory.CreateLogger<NamedPipeManager>());
        var session = new StandardTerminalSession(
            _loggerFactory.CreateLogger<StandardTerminalSession>(),
            pipes,
            new WrapperPathRewritingLauncher(wrapper));

        await session.InitializeAsync(new TerminalSessionConfig());
        var tab = await session.CreateTabAsync("smoke");

        var result = await session.ExecuteInTabAsync(
            tab.TabId,
            "Write-Output 'smoke-ok'; $PSVersionTable.PSVersion.ToString()",
            timeout: TimeSpan.FromSeconds(60));

        Assert.True(result.Success, $"Command failed: {string.Join("; ", result.Errors ?? Array.Empty<string>())}");
        Assert.Contains("smoke-ok", result.Stdout ?? string.Empty, StringComparison.Ordinal);

        await session.CloseTabAsync(tab.TabId);
        await session.TerminateAsync(graceful: true);
    }

    private static string FindWrapperScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Core", "TokenSaver.TerminalControl", "Scripts", "tokensaver-tab.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("tokensaver-tab.ps1 not found above the test output folder.");
    }

    /// <summary>Starts the real wt.exe, pointing the wrapper path at the source script.</summary>
    private sealed class WrapperPathRewritingLauncher : ITerminalProcessLauncher
    {
        private static readonly Regex WrapperArg = new("-File \"([^\"]+)\"", RegexOptions.Compiled);
        private readonly string _wrapperPath;

        public WrapperPathRewritingLauncher(string wrapperPath) => _wrapperPath = wrapperPath;

        public Process? Start(ProcessStartInfo startInfo)
        {
            var args = WrapperArg.Replace(startInfo.Arguments, $"-File \"{_wrapperPath}\"", 1);
            return Process.Start(new ProcessStartInfo
            {
                FileName = startInfo.FileName,
                Arguments = args,
                UseShellExecute = false,
            });
        }
    }
}

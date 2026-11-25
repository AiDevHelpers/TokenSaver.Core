using System.Diagnostics;

using AiDevTools.TokenSaver.Common.Contracts;

using Xunit;

namespace AiDevTools.TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>
///     The git baseline (RepoStaleness.LastHeadSha) must advance only after indexing succeeds.
///     Otherwise a cancelled or failed run loses the changed files for good.
/// </summary>
public sealed class GitBaselineTests : IDisposable
{
    private readonly CountingSummarizer _summarizer = new();
    private readonly IndexTestHarness _h;

    public GitBaselineTests()
    {
        _h = new IndexTestHarness(_summarizer);
        Git("init");
        Git("config user.email test@example.com");
        Git("config user.name test");
    }

    public void Dispose() => _h.Dispose();

    private void Git(string args)
    {
        ProcessStartInfo psi = new("git", $"-C \"{_h.Root}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("git not found on PATH");
        _ = p.StandardOutput.ReadToEnd();
        _ = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public async Task CancelledIndex_DoesNotAdvanceBaseline_NextRunIndexesChangedFile()
    {
        await _h.WriteAsync(".gitignore", ".tokensaver/\n");
        await _h.WriteAsync("src/Alpha.cs", "public sealed class Alpha\n{\n}\n");
        Git("add -A");
        Git("commit -m init");
        _ = await _h.NewIndexService().IndexWorkspaceAsync(_h.Root);

        await _h.WriteAsync("src/Beta.cs", "public sealed class Beta\n{\n}\n");
        Git("add -A");
        Git("commit -m beta");

        // Cancel while Beta is being summarized: its DB write then fails, so Beta is not indexed.
        using CancellationTokenSource cts = new();
        _summarizer.OnCall = () => cts.Cancel();
        try
        {
            _ = await _h.NewIndexService().IndexWorkspaceAsync(_h.Root, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected on some paths; the assertion below is what matters
        }

        _summarizer.OnCall = null;
        _ = await _h.NewIndexService().IndexWorkspaceAsync(_h.Root);

        Assert.Equal(2, await _h.Search.CountAllSymbolsAsync(_h.Root, CancellationToken.None));
    }
}

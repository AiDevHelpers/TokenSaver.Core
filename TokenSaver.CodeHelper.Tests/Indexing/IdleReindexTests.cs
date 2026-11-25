using System.Diagnostics;

using AiDevTools.TokenSaver.CodeHelper.Models.Index;

using Xunit;

namespace AiDevTools.TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>An index pass over an unchanged git repo does no file reads, no model calls, no reference work.</summary>
public sealed class IdleReindexTests : IDisposable
{
    private readonly CountingSummarizer _summarizer = new();
    private readonly IndexTestHarness _h;

    public IdleReindexTests()
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
    public async Task SecondPassOnUnchangedRepo_DoesNoWork()
    {
        await _h.WriteAsync(".gitignore", ".tokensaver/\n");
        for (int i = 0; i < 5; i++)
        {
            await _h.WriteAsync($"src/S{i}.cs", $"public sealed class S{i}\n{{\n}}\n");
        }

        Git("add -A");
        Git("commit -m init");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root);
        int callsAfterFirst = _summarizer.Calls;

        IndexStats second = await _h.Index.IndexWorkspaceAsync(_h.Root);

        Assert.Equal(0, second.FilesIndexed);
        Assert.Equal(0, second.ReferenceFilesRead);
        Assert.Equal(callsAfterFirst, _summarizer.Calls);
    }
}

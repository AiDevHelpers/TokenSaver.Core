using System.Diagnostics;

using AiDevTools.TokenSaver.Database.Context;
using AiDevTools.TokenSaver.Database.Context.Entities;

using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiDevTools.TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>A committed git rename must drop the old path from the index on the next pass.</summary>
public sealed class GitRenameTests : IDisposable
{
    private readonly IndexTestHarness _h = new();

    public GitRenameTests()
    {
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
    public async Task CommittedRename_RemovesOldPathFromIndexImmediately()
    {
        await _h.WriteAsync(".gitignore", ".tokensaver/\n");
        await _h.WriteAsync("src/Alpha.cs", "public sealed class Alpha\n{\n}\n");
        Git("add -A");
        Git("commit -m init");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root);

        Git("mv src/Alpha.cs src/Beta.cs");
        Git("commit -m rename");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root);

        WorkspaceIndexContext ctx = await _h.Accessor.GetDbAsync(_h.Root, CancellationToken.None);
        Assert.False(await ctx.Files.AnyAsync(f => f.RelativePath == "src/Alpha.cs"));
        Assert.True(await ctx.Files.AnyAsync(f => f.RelativePath == "src/Beta.cs"));
    }
}

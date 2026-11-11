using System.Diagnostics;

using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;

using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>
///     Reference edges are rebuilt only for changed files and the sources that depend on them,
///     not for every C# file on each index pass.
/// </summary>
public sealed class IncrementalReferencesTests : IDisposable
{
    private readonly IndexTestHarness _h = new();

    public IncrementalReferencesTests()
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
    public async Task ChangedFile_RereadsOnlyItselfNotUnchangedSiblings()
    {
        await _h.WriteAsync(".gitignore", ".tokensaver/\n");
        for (int i = 0; i < 10; i++)
        {
            await _h.WriteAsync($"src/U{i}.cs", $"public sealed class U{i}\n{{\n}}\n");
        }

        Git("add -A");
        Git("commit -m init");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root);

        await _h.WriteAsync("src/U0.cs", "public sealed class U0\n{\n    public int X;\n}\n");
        Git("add -A");
        Git("commit -m edit-u0");
        IndexStats stats = await _h.Index.IndexWorkspaceAsync(_h.Root);

        Assert.Equal(1, stats.ReferenceFilesRead);
    }

    [Fact]
    public async Task RenamedTarget_RemovesEdgesThatPointAtTheOldSymbol()
    {
        await _h.WriteAsync(".gitignore", ".tokensaver/\n");
        await _h.WriteAsync("src/Alpha.cs", "public sealed class Alpha\n{\n}\n");
        await _h.WriteAsync("src/Beta.cs", "public sealed class Beta\n{\n    public Alpha Field;\n}\n");
        Git("add -A");
        Git("commit -m init");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root);

        WorkspaceIndexContext ctx = await _h.Accessor.GetDbAsync(_h.Root, CancellationToken.None);
        string oldAlphaId = await ctx.Symbols.Where(s => s.Name == "Alpha").Select(s => s.Id).FirstAsync();
        Assert.True(await ctx.SymbolReferences.AnyAsync(e => e.TargetSymbolId == oldAlphaId));

        await _h.WriteAsync("src/Alpha.cs", "public sealed class Renamed\n{\n}\n");
        Git("add -A");
        Git("commit -m rename");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root);

        ctx = await _h.Accessor.GetDbAsync(_h.Root, CancellationToken.None);
        Assert.False(await ctx.SymbolReferences.AnyAsync(e => e.TargetSymbolId == oldAlphaId));
    }
}

using AiDevTools.TokenSaver.Database.Context;
using AiDevTools.TokenSaver.Database.Context.Entities;

using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiDevTools.TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>Targeted indexing: only the listed paths are touched, everything else is left alone.</summary>
public sealed class IndexPathsTests : IDisposable
{
    private readonly IndexTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task IndexPaths_IndexesOnlyListedFiles()
    {
        await _h.WriteAsync("src/A.cs", "public sealed class A1\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root);

        // A changes (listed) and B is new but not listed: only A may be re-read.
        await _h.WriteAsync("src/A.cs", "public sealed class A2\n{\n}\n");
        await _h.WriteAsync("src/B.cs", "public sealed class BNew\n{\n}\n");

        _ = await _h.Index.IndexPathsAsync(_h.Root, ["src/A.cs"]);

        WorkspaceIndexContext ctx = await _h.Accessor.GetDbAsync(_h.Root, CancellationToken.None);
        Assert.True(await ctx.Symbols.AnyAsync(s => s.Name == "A2"));
        Assert.False(await ctx.Symbols.AnyAsync(s => s.Name == "BNew"));
    }

    [Fact]
    public async Task IndexPaths_RemovesListedFileThatNoLongerExists()
    {
        await _h.WriteAsync("src/Gone.cs", "public sealed class Gone\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root);

        _h.Delete("src/Gone.cs");
        _ = await _h.Index.IndexPathsAsync(_h.Root, ["src/Gone.cs"]);

        WorkspaceIndexContext ctx = await _h.Accessor.GetDbAsync(_h.Root, CancellationToken.None);
        Assert.False(await ctx.Files.AnyAsync(f => f.RelativePath == "src/Gone.cs"));
    }
}

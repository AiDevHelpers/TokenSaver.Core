using Xunit;

namespace AiDevTools.TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>One shared always-skip list: build output and virtualenvs are never indexed.</summary>
public sealed class IgnoreSetTests : IDisposable
{
    private readonly IndexTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task BuildOutputDirectory_IsNotIndexed()
    {
        await _h.WriteAsync("src/A.cs", "public sealed class A\n{\n}\n");
        await _h.WriteAsync("build/Gen.cs", "public sealed class Gen\n{\n}\n");

        _ = await _h.NewIndexService().IndexWorkspaceAsync(_h.Root);

        Assert.Equal(1, await _h.Search.CountAllSymbolsAsync(_h.Root, CancellationToken.None));
    }
}

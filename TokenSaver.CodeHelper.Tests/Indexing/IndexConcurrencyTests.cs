using AiDevTools.TokenSaver.Common.Contracts;

using Xunit;

namespace AiDevTools.TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>Concurrent index passes over one root must not each summarize the same files.</summary>
public sealed class IndexConcurrencyTests : IDisposable
{
    private readonly CountingSummarizer _summarizer = new();
    private readonly IndexTestHarness _h;

    public IndexConcurrencyTests()
    {
        _h = new IndexTestHarness(_summarizer);
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task ConcurrentWorkspaceIndexCalls_SummarizeEachFileOnce()
    {
        for (int i = 0; i < 3; i++)
        {
            await _h.WriteAsync($"src/F{i}.cs", $"public sealed class F{i}\n{{\n}}\n");
        }

        Task first = _h.NewIndexService().IndexWorkspaceAsync(_h.Root);
        Task second = _h.NewIndexService().IndexWorkspaceAsync(_h.Root);
        await Task.WhenAll(first, second);

        Assert.Equal(3, _summarizer.Calls);
    }
}

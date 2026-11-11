using TokenSaver.Common.Contracts;

using Xunit;

namespace TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>Counts summary calls so tests can prove the hash gate skips the model.</summary>
internal sealed class CountingSummarizer : ICodeSummarizer
{
    public int Calls;

    public Task<string?> SummarizeAsync(string fileContent, string fileName, string fileHash,
        bool preferBackupEndpoint = false, CancellationToken ct = default)
    {
        _ = Interlocked.Increment(ref Calls);
        return Task.FromResult<string?>("summary");
    }
}

/// <summary>Raw-byte content hash gate: mtime changes with identical bytes must not re-summarize.</summary>
public sealed class ContentHashGateTests : IDisposable
{
    private readonly CountingSummarizer _summarizer = new();
    private readonly IndexTestHarness _h;

    public ContentHashGateTests()
    {
        _h = new IndexTestHarness(_summarizer);
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task TouchOnly_SameBytes_DoesNotCallSummarizer()
    {
        await _h.WriteAsync("src/Alpha.cs", "public sealed class Alpha\n{\n}\n");
        _ = await _h.NewIndexService().IndexWorkspaceAsync(_h.Root);
        int afterFirst = _summarizer.Calls;
        Assert.True(afterFirst > 0, "first index should summarize the file");

        // Same bytes, newer mtime (what a checkout or touch produces).
        string abs = Path.Combine(_h.Root, "src", "Alpha.cs");
        File.SetLastWriteTimeUtc(abs, DateTime.UtcNow.AddMinutes(5));

        _ = await _h.NewIndexService().IndexWorkspaceAsync(_h.Root);

        Assert.Equal(afterFirst, _summarizer.Calls);
    }

    [Fact]
    public async Task ContentChange_CallsSummarizerAgain()
    {
        await _h.WriteAsync("src/Alpha.cs", "public sealed class Alpha\n{\n}\n");
        _ = await _h.NewIndexService().IndexWorkspaceAsync(_h.Root);
        int afterFirst = _summarizer.Calls;

        await _h.WriteAsync("src/Alpha.cs", "public sealed class Beta\n{\n}\n");
        _ = await _h.NewIndexService().IndexWorkspaceAsync(_h.Root);

        Assert.True(_summarizer.Calls > afterFirst);
    }
}

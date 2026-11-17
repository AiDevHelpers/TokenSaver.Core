using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.CodeHelper.Models.Patch;
using TokenSaver.CodeHelper.Patching;

using Xunit;

namespace TokenSaver.CodeHelper.Tests;

public sealed class PatchEngineTests : IDisposable
{
    private sealed class NoopReindexer : IFileReindexer
    {
        public Task<IndexStats> IndexFileAsync(string root, string relPath, CancellationToken ct = default)
        {
            return Task.FromResult(new IndexStats());
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchengine-" + Guid.NewGuid().ToString("N"));
    private readonly PatchEngine _engine;

    public PatchEngineTests()
    {
        _ = Directory.CreateDirectory(_root);
        _engine = new PatchEngine(Options.Create(new CodeHelperConfig()), new NoopReindexer(), NullLogger<PatchEngine>.Instance);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private static ReplaceOperation Replace(string file, string oldText, string newText)
    {
        return new ReplaceOperation { FilePath = file, OldText = oldText, NewText = newText };
    }

    [Fact]
    public async Task ApplyBatch_TwoEditsToOneFile_KeepTwoDistinctBackupsAndTheOriginal()
    {
        string path = Path.Combine(_root, "a.txt");
        byte[] original = new UTF8Encoding(false).GetBytes("one two three");
        await File.WriteAllBytesAsync(path, original);

        IReadOnlyList<PatchResult> results = await _engine.ApplyBatchAsync(
            [Replace("a.txt", "one", "1"), Replace("a.txt", "two", "2")], _root);

        Assert.All(results, r => Assert.True(r.Success, r.Error));
        string?[] backups = results.Select(r => r.BackupPath).ToArray();
        Assert.NotEqual(backups[0], backups[1]);
        Assert.Equal(original, await File.ReadAllBytesAsync(backups[0]!));
        Assert.Equal("1 2 three", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public async Task Apply_PreservesEncodingAndBom_AndBackupIsByteExact(string kind)
    {
        Encoding enc = kind switch
        {
            "utf8bom" => new UTF8Encoding(true),
            "utf16le" => new UnicodeEncoding(false, true),
            "utf16be" => new UnicodeEncoding(true, true),
            _ => new UTF8Encoding(false)
        };
        string path = Path.Combine(_root, "f.txt");
        byte[] original = [.. enc.GetPreamble(), .. enc.GetBytes("alpha\r\nbeta\r\n")];
        await File.WriteAllBytesAsync(path, original);

        PatchResult result = await _engine.ApplyAsync(Replace("f.txt", "alpha", "ALPHA"), _root);

        Assert.True(result.Success, result.Error);
        byte[] expected = [.. enc.GetPreamble(), .. enc.GetBytes("ALPHA\r\nbeta\r\n")];
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(result.BackupPath!));
    }

    [Fact]
    public async Task Apply_LeavesNoTempFilesBehind()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "t.txt"), "x");

        _ = await _engine.ApplyAsync(Replace("t.txt", "x", "y"), _root);

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}

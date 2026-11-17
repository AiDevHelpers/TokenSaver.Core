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

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    public async Task Apply_RefusesPathsThatEscapeTheWorkspace(string relative)
    {
        string outside = Path.GetFullPath(Path.Combine(_root, "..", "outside.txt"));
        await File.WriteAllTextAsync(outside, "keep");
        try
        {
            PatchResult result = await _engine.ApplyAsync(Replace(relative, "keep", "changed"), _root);

            Assert.False(result.Success);
            Assert.Contains("outside the workspace root", result.Error, StringComparison.Ordinal);
            Assert.Equal("keep", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Apply_RefusesAnAbsolutePathOutsideTheWorkspace()
    {
        string other = Path.Combine(Path.GetTempPath(), "other-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(other, "keep");
        try
        {
            PatchResult result = await _engine.ApplyAsync(Replace(other, "keep", "changed"), _root);

            Assert.False(result.Success);
            Assert.Equal("keep", await File.ReadAllTextAsync(other));
        }
        finally
        {
            File.Delete(other);
        }
    }

    [Fact]
    public async Task Apply_WithNoWorkspaceRoot_FailsInsteadOfUsingTheWorkingDirectory()
    {
        PatchResult result = await _engine.ApplyAsync(Replace("a.txt", "x", "y"), "");

        Assert.False(result.Success);
        Assert.Contains("No workspace root", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_RestoresFromARealBackup()
    {
        string path = Path.Combine(_root, "r.txt");
        await File.WriteAllTextAsync(path, "before");
        PatchResult applied = await _engine.ApplyAsync(Replace("r.txt", "before", "after"), _root);
        Assert.True(applied.Success, applied.Error);

        bool ok = await _engine.RollbackAsync(applied.BackupPath!, "r.txt", _root);

        Assert.True(ok);
        Assert.Equal("before", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Rollback_RefusesASourceThatIsNotABackup()
    {
        string path = Path.Combine(_root, "target.txt");
        await File.WriteAllTextAsync(path, "intact");
        string stranger = Path.Combine(Path.GetTempPath(), "stranger-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(stranger, "payload");
        try
        {
            bool ok = await _engine.RollbackAsync(stranger, "target.txt", _root);

            Assert.False(ok);
            Assert.Equal("intact", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(stranger);
        }
    }

    [Fact]
    public async Task Rollback_RefusesATargetOutsideTheWorkspace()
    {
        string path = Path.Combine(_root, "src.txt");
        await File.WriteAllTextAsync(path, "x");
        PatchResult applied = await _engine.ApplyAsync(Replace("src.txt", "x", "y"), _root);
        string outside = Path.Combine(Path.GetTempPath(), "rb-out-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outside, "intact");
        try
        {
            bool ok = await _engine.RollbackAsync(applied.BackupPath!, outside, _root);

            Assert.False(ok);
            Assert.Equal("intact", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}

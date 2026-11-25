using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.CodeHelper.Models.Index;
using AiDevTools.TokenSaver.CodeHelper.Models.Patch;
using AiDevTools.TokenSaver.CodeHelper.Patching;

using Xunit;

namespace AiDevTools.TokenSaver.CodeHelper.Tests;

/// <summary>Each patch operation on its own: the change it makes, and that a failed one leaves the file untouched.</summary>
public sealed class PatchOperationTests : IDisposable
{
    private sealed class NoopReindexer : IFileReindexer
    {
        public Task<IndexStats> IndexFileAsync(string root, string relPath, CancellationToken ct = default) =>
            Task.FromResult(new IndexStats());
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "patchops-" + Guid.NewGuid().ToString("N"));
    private readonly PatchEngine _engine;

    public PatchOperationTests()
    {
        _ = Directory.CreateDirectory(_root);
        _engine = new PatchEngine(Options.Create(new CodeHelperConfig()), new NoopReindexer(), NullLogger<PatchEngine>.Instance);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private string Write(string name, string content)
    {
        File.WriteAllText(Path.Combine(_root, name), content, new UTF8Encoding(false));
        return name;
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(_root, name));

    [Fact]
    public async Task Insert_AfterAnchor_PlacesTheTextRightAfterIt()
    {
        string file = Write("i.txt", "alpha\nbeta\ngamma\n");

        PatchResult r = await _engine.ApplyAsync(new InsertOperation { FilePath = file, Anchor = "beta", Content = "BETA2", Position = InsertPosition.After }, _root);

        Assert.True(r.Success, r.Error);
        Assert.Equal("alpha\nbetaBETA2\ngamma\n", Read(file));
    }

    [Fact]
    public async Task Insert_BeforeAnchor_PlacesTheTextBeforeIt()
    {
        string file = Write("b.txt", "one two three");

        PatchResult r = await _engine.ApplyAsync(new InsertOperation { FilePath = file, Anchor = "two", Content = "[x]", Position = InsertPosition.Before }, _root);

        Assert.True(r.Success, r.Error);
        Assert.Equal("one [x]two three", Read(file));
    }

    [Fact]
    public async Task Insert_MissingAnchor_FailsAndLeavesTheFileAlone()
    {
        string file = Write("m.txt", "unchanged");

        PatchResult r = await _engine.ApplyAsync(new InsertOperation { FilePath = file, Anchor = "nope", Content = "x" }, _root);

        Assert.False(r.Success);
        Assert.Equal("unchanged", Read(file));
    }

    [Fact]
    public async Task Delete_Literal_RemovesOnlyTheMatchedText()
    {
        string file = Write("d.txt", "keep DROP keep");

        PatchResult r = await _engine.ApplyAsync(new DeleteOperation { FilePath = file, Pattern = "DROP " }, _root);

        Assert.True(r.Success, r.Error);
        Assert.Equal("keep keep", Read(file));
    }

    [Fact]
    public async Task Delete_Regex_RemovesEveryMatch()
    {
        string file = Write("r.txt", "a1 b22 c333");

        PatchResult r = await _engine.ApplyAsync(new DeleteOperation { FilePath = file, Pattern = @"\d+", IsRegex = true }, _root);

        Assert.True(r.Success, r.Error);
        Assert.Equal("a b c", Read(file));
    }

    [Fact]
    public async Task Delete_NoMatch_FailsAndLeavesTheFileAlone()
    {
        string file = Write("nd.txt", "untouched");

        PatchResult r = await _engine.ApplyAsync(new DeleteOperation { FilePath = file, Pattern = "zzz" }, _root);

        Assert.False(r.Success);
        Assert.Equal("untouched", Read(file));
    }

    [Fact]
    public async Task Rename_ChangesWholeIdentifiersOnly()
    {
        string file = Write("n.cs", "int count = 1; int counter = count + 2;");

        PatchResult r = await _engine.ApplyAsync(new RenameOperation { FilePath = file, OldName = "count", NewName = "total" }, _root);

        Assert.True(r.Success, r.Error);
        Assert.Equal("int total = 1; int counter = total + 2;", Read(file));
    }

    [Fact]
    public async Task Rename_NotFound_FailsAndLeavesTheFileAlone()
    {
        string file = Write("nn.cs", "int a;");

        PatchResult r = await _engine.ApplyAsync(new RenameOperation { FilePath = file, OldName = "missing", NewName = "x" }, _root);

        Assert.False(r.Success);
        Assert.Equal("int a;", Read(file));
    }

    [Fact]
    public async Task Replace_Occurrence_ChangesOnlyThatMatch()
    {
        string file = Write("o.txt", "x x x");

        PatchResult r = await _engine.ApplyAsync(new ReplaceOperation { FilePath = file, OldText = "x", NewText = "Y", Occurrence = 2 }, _root);

        Assert.True(r.Success, r.Error);
        Assert.Equal("x Y x", Read(file));
    }

    [Fact]
    public async Task Replace_NotFound_FailsAndLeavesTheFileAlone()
    {
        string file = Write("rn.txt", "abc");

        PatchResult r = await _engine.ApplyAsync(new ReplaceOperation { FilePath = file, OldText = "zzz", NewText = "y" }, _root);

        Assert.False(r.Success);
        Assert.Equal("abc", Read(file));
    }

    [Fact]
    public async Task DryRun_ReportsTheChangeButWritesNothingAndMakesNoBackup()
    {
        string file = Write("dry.txt", "before");

        PatchResult r = await _engine.ApplyAsync(new ReplaceOperation { FilePath = file, OldText = "before", NewText = "after", DryRun = true }, _root);

        Assert.True(r.Success, r.Error);
        Assert.True(r.WasDryRun);
        Assert.Null(r.BackupPath);
        Assert.Equal("before", Read(file));
        Assert.Empty(Directory.GetFiles(_root, "*.bak", SearchOption.AllDirectories));
    }
}

using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.Config;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.Library.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.Utilities.Services;
using Xunit;

namespace TokenSaver.CodeHelper.Tests;

public sealed class WorkspaceIndexServiceTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceIndexDbAccessor _accessor;
    private readonly WorkspaceIndexService _service;
    private readonly HttpClient _http;

    public WorkspaceIndexServiceTests()
    {
        _root = Directory.CreateTempSubdirectory("tswi_").FullName;
        _accessor = new WorkspaceIndexDbAccessor(NullLogger<WorkspaceIndexDbAccessor>.Instance);

        GitStalenessService gitStaleness = new(
            _accessor,
            new GitStalenessChecker(NullLogger<GitStalenessChecker>.Instance),
            NullLogger<GitStalenessService>.Instance);

        WorkspaceConfigManager configManager = new(
            Options.Create(new TokenSaverConfig { WorkspaceRoot = _root }),
            NullLogger<WorkspaceConfigManager>.Instance);

        FolderTreeManager folderTree = new(_accessor, configManager, NullLogger<FolderTreeManager>.Instance);

        AiSummarizationService aiSummarization = new(NullLogger<AiSummarizationService>.Instance);

        // Unreachable base address — DraftModelCapacityClient falls back to a conservative
        // default (1 concurrent summary request) when /api/capacity can't be reached.
        _http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        DraftModelCapacityClient capacityClient = new(_http, NullLogger<DraftModelCapacityClient>.Instance);

        _service = new WorkspaceIndexService(
            Options.Create(new CodeHelperConfig()),
            [],
            _accessor,
            gitStaleness,
            folderTree,
            aiSummarization,
            capacityClient,
            NullLogger<WorkspaceIndexService>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
        _accessor.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private void WriteWorkspaceFiles()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "public class A {}");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "public class B {}");
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "c.txt"), "hello world");
    }

    [Fact]
    public async Task IndexFileAsync_IndexesSingleFile()
    {
        WriteWorkspaceFiles();

        IndexStats stats = await _service.IndexFileAsync(_root, "a.cs", CancellationToken.None);

        Assert.Equal(1, stats.FilesIndexed);

        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(_root, CancellationToken.None);
        FileEntity? row = await ctx.Files.AsNoTracking().FirstOrDefaultAsync(f => f.RelativePath == "a.cs");
        Assert.NotNull(row);
    }

    [Fact]
    public async Task IndexWorkspaceAsync_BootstrapsFolderAndFileRows()
    {
        WriteWorkspaceFiles();

        IndexStats stats = await _service.IndexWorkspaceAsync(_root, CancellationToken.None);

        Assert.True(stats.FilesIndexed >= 3, $"expected at least 3 files indexed, got {stats.FilesIndexed}");

        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(_root, CancellationToken.None);
        List<FileEntity> files = await ctx.Files.AsNoTracking().ToListAsync();
        List<FolderEntity> folders = await ctx.Folders.AsNoTracking().ToListAsync();

        Assert.Contains(files, f => f.RelativePath == "a.cs");
        Assert.Contains(files, f => f.RelativePath == "b.cs");
        Assert.Contains(files, f => f.RelativePath == "sub/c.txt");
        Assert.Contains(folders, f => f.RelativePath == ".");
        Assert.Contains(folders, f => f.RelativePath == "sub");
    }

    [Fact]
    public async Task IndexWorkspaceAsync_SecondCallSkipsUnchangedFiles()
    {
        WriteWorkspaceFiles();
        _ = await _service.IndexWorkspaceAsync(_root, CancellationToken.None);

        IndexStats second = await _service.IndexWorkspaceAsync(_root, CancellationToken.None);

        Assert.True(second.FilesSkipped >= 3, $"expected at least 3 skipped, got {second.FilesSkipped}");
    }

    [Fact]
    public async Task ReconcileIgnoreChangeAsync_RemovesNewlyIgnoredFile()
    {
        WriteWorkspaceFiles();
        _ = await _service.IndexWorkspaceAsync(_root, CancellationToken.None);

        WorkspaceIndexContext ctxBefore = await _accessor.GetDbAsync(_root, CancellationToken.None);
        Assert.Contains(
            await ctxBefore.Files.AsNoTracking().ToListAsync(),
            f => f.RelativePath == "a.cs");

        File.WriteAllText(Path.Combine(_root, ".gitignore"), "a.cs\n");

        IndexStats reconcileStats = await _service.ReconcileIgnoreChangeAsync(_root, CancellationToken.None);

        Assert.True(reconcileStats.FilesPruned >= 1, $"expected at least 1 pruned file, got {reconcileStats.FilesPruned}");

        WorkspaceIndexContext ctxAfter = await _accessor.GetDbAsync(_root, CancellationToken.None);
        List<FileEntity> filesAfter = await ctxAfter.Files.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(filesAfter, f => f.RelativePath == "a.cs");
        Assert.Contains(filesAfter, f => f.RelativePath == "b.cs");
    }
}

using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.Config;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>
///     End-to-end repro: scan a nested C# file with the production parser set, then find its class.
/// </summary>
public sealed class CodeIndexFindReproTests : IDisposable
{
    private const string Source = """
        using Microsoft.Extensions.Hosting;

        namespace VideoForensics.Hosting.Services;

        /// <summary>Pulls events in the background.</summary>
        public sealed class EventPullService : BackgroundService
        {
            public EventPullService() { }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                await Task.CompletedTask;
            }
        }
        """;

    private readonly string _root;
    private readonly WorkspaceIndexDbAccessor _accessor;
    private readonly WorkspaceIndexService _index;
    private readonly SymbolSearchService _search;
    private readonly DependencyTrackingService _deps;
    private readonly HttpClient _http;

    public CodeIndexFindReproTests()
    {
        _root = Directory.CreateTempSubdirectory("tsfind_").FullName;
        _accessor = new WorkspaceIndexDbAccessor(NullLogger<WorkspaceIndexDbAccessor>.Instance);

        WorkspaceConfigManager configManager = new(
            Options.Create(new TokenSaverConfig { WorkspaceRoot = _root }),
            NullLogger<WorkspaceConfigManager>.Instance);

        GitStalenessService gitStaleness = new(
            _accessor,
            new GitStalenessChecker(NullLogger<GitStalenessChecker>.Instance),
            NullLogger<GitStalenessService>.Instance);

        FolderTreeManager folderTree = new(_accessor, configManager, NullLogger<FolderTreeManager>.Instance);
        AiSummarizationService aiSummarization = new(NullLogger<AiSummarizationService>.Instance);

        _http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        SimpleModelServerCapacityClient capacity = new(_http, NullLogger<SimpleModelServerCapacityClient>.Instance);

        ISymbolParser[] parsers =
        [
            new CSharpSymbolParser(),
            new TypeScriptSymbolParser(),
            new GenericSymbolParser(),
        ];

        _index = new WorkspaceIndexService(
            Options.Create(new CodeHelperConfig()),
            parsers,
            _accessor,
            gitStaleness,
            folderTree,
            aiSummarization,
            capacity,
            NullLogger<WorkspaceIndexService>.Instance);

        _search = new SymbolSearchService(_accessor, configManager);
        _deps = new DependencyTrackingService(_accessor, configManager);
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

    [Fact]
    public async Task Scan_ThenFind_NestedCSharpClass_ReturnsClass()
    {
        string dir = Path.Combine(_root, "src", "Services");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "EventPullService.cs"), Source);

        IndexStats stats = await _index.IndexWorkspaceAsync(_root, CancellationToken.None);
        Assert.True(stats.FilesIndexed >= 1, $"expected the .cs file to be indexed, got {stats.FilesIndexed}");

        IReadOnlyList<SymbolEntry> byName = await _search.FindSymbolAsync("EventPullService", null, null, _root, CancellationToken.None);
        Assert.Contains(byName, s => s.Kind == SymbolKind.Class);

        IReadOnlyList<SymbolEntry> byKind = await _search.FindSymbolAsync(
            "EventPullService", SymbolKind.Class, "src/Services/EventPullService.cs", _root, CancellationToken.None);
        Assert.Single(byKind);
    }

    [Fact]
    public async Task ScanSubRoot_ThenFindByName_ReturnsClass()
    {
        string srcRoot = Path.Combine(_root, "src");
        string dir = Path.Combine(srcRoot, "Services");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "EventPullService.cs"), Source);

        _ = await _index.IndexWorkspaceAsync(srcRoot, CancellationToken.None);

        IReadOnlyList<SymbolEntry> byName = await _search.FindSymbolAsync("EventPullService", null, null, srcRoot, CancellationToken.None);
        Assert.Contains(byName, s => s.Kind == SymbolKind.Class);
    }

    [Fact]
    public async Task ScanWorkspaceRoot_ThenDependenciesFromSubRoot_ReturnsEdge()
    {
        string srcRoot = Path.Combine(_root, "src");
        string servicesDir = Path.Combine(srcRoot, "Services");
        string otherDir = Path.Combine(srcRoot, "Other");
        _ = Directory.CreateDirectory(servicesDir);
        _ = Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(servicesDir, "EventPullService.cs"), Source);
        File.WriteAllText(Path.Combine(otherDir, "Helper.cs"),
            "namespace VideoForensics.Other;\n\npublic sealed class Helper\n{\n}\n");

        _ = await _index.IndexWorkspaceAsync(_root, CancellationToken.None);

        SymbolEntry source = (await _search.FindSymbolAsync("EventPullService", SymbolKind.Class, null, _root, CancellationToken.None)).Single();
        SymbolEntry target = (await _search.FindSymbolAsync("Helper", SymbolKind.Class, null, _root, CancellationToken.None)).Single();

        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(_root, CancellationToken.None);
        await _accessor.WithDbLockAsync(async () =>
        {
            _ = ctx.SymbolReferences.Add(new SymbolReferenceEntity
            {
                SourceSymbolId = source.Id,
                TargetSymbolId = target.Id,
                Kind = 0,
                Line = 1,
            });
            _ = await ctx.SaveChangesAsync();
        }, CancellationToken.None);

        IReadOnlyList<string> uses = await _deps.GetFileDependenciesAsync(
            "src/Services/EventPullService.cs", srcRoot, CancellationToken.None);
        Assert.Equal(["src/Other/Helper.cs"], uses);
    }

    [Fact]
    public async Task Scan_BuildsUsingAndTypeUsageEdges_ForDependencyQueries()
    {
        string srcRoot = Path.Combine(_root, "src");
        string servicesDir = Path.Combine(srcRoot, "Services");
        string otherDir = Path.Combine(srcRoot, "Other");
        _ = Directory.CreateDirectory(servicesDir);
        _ = Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(servicesDir, "EventPullService.cs"),
            "using VideoForensics.Other;\n\nnamespace VideoForensics.Services;\n\n" +
            "public sealed class EventPullService\n{\n    private readonly Helper _helper = new();\n}\n");
        File.WriteAllText(Path.Combine(otherDir, "Helper.cs"),
            "namespace VideoForensics.Other;\n\npublic sealed class Helper\n{\n}\n");

        _ = await _index.IndexWorkspaceAsync(_root, CancellationToken.None);

        IReadOnlyList<string> uses = await _deps.GetFileDependenciesAsync(
            "src/Services/EventPullService.cs", srcRoot, CancellationToken.None);
        Assert.Equal(["src/Other/Helper.cs"], uses);

        IReadOnlyList<string> usedBy = await _deps.GetFileDependentsAsync(
            "src/Other/Helper.cs", srcRoot, CancellationToken.None);
        Assert.Equal(["src/Services/EventPullService.cs"], usedBy);
    }

    [Fact]
    public async Task ScanWorkspaceRoot_ThenFindWithSubRoot_ReturnsClass()
    {
        string srcRoot = Path.Combine(_root, "src");
        string dir = Path.Combine(srcRoot, "Services");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "EventPullService.cs"), Source);

        _ = await _index.IndexWorkspaceAsync(_root, CancellationToken.None);

        IReadOnlyList<SymbolEntry> byName = await _search.FindSymbolAsync("EventPullService", null, null, srcRoot, CancellationToken.None);
        Assert.Contains(byName, s => s.Kind == SymbolKind.Class);
    }

    [Fact]
    public async Task ScanSubRoot_ThenFindWithWorkspaceRelativePath_ReturnsClass()
    {
        // Mirrors the report: scan root is <ws>\src, but filePath is written relative to the workspace.
        string srcRoot = Path.Combine(_root, "src");
        string dir = Path.Combine(srcRoot, "Services");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "EventPullService.cs"), Source);

        _ = await _index.IndexWorkspaceAsync(srcRoot, CancellationToken.None);

        IReadOnlyList<SymbolEntry> byKind = await _search.FindSymbolAsync(
            "EventPullService", SymbolKind.Class, "src/Services/EventPullService.cs", srcRoot, CancellationToken.None);
        Assert.Single(byKind);
    }
}

using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.Common.Contracts;
using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.Config;
using AiDevTools.TokenSaver.Database.Context;
using AiDevTools.TokenSaver.OllamaIntegration.Services;
using AiDevTools.TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiDevTools.TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>
///     Temp-directory workspace wired to the production indexing services and a real SQLite index.
/// </summary>
internal sealed class IndexTestHarness : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<WorkspaceIndexService> _newIndex;

    public IndexTestHarness(ICodeSummarizer? summarizer = null)
    {
        Root = Directory.CreateTempSubdirectory("tsidx_").FullName;
        Accessor = new WorkspaceIndexDbAccessor(NullLogger<WorkspaceIndexDbAccessor>.Instance);

        WorkspaceConfigManager configManager = new(
            Options.Create(new TokenSaverConfig { WorkspaceRoot = Root }),
            NullLogger<WorkspaceConfigManager>.Instance);

        GitStalenessService gitStaleness = new(
            Accessor,
            new GitStalenessChecker(NullLogger<GitStalenessChecker>.Instance),
            NullLogger<GitStalenessService>.Instance);

        FolderTreeManager folderTree = new(Accessor, configManager, NullLogger<FolderTreeManager>.Instance);
        AiSummarizationService aiSummarization = new(NullLogger<AiSummarizationService>.Instance, summarizer);

        _http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        SimpleModelServerCapacityClient capacity = new(_http, NullLogger<SimpleModelServerCapacityClient>.Instance);

        ISymbolParser[] parsers =
        [
            new CSharpSymbolParser(),
            new TypeScriptSymbolParser(),
            new GenericSymbolParser(),
        ];

        _newIndex = () => new WorkspaceIndexService(
            Options.Create(new CodeHelperConfig()),
            parsers,
            Accessor,
            gitStaleness,
            folderTree,
            aiSummarization,
            capacity,
            NullLogger<WorkspaceIndexService>.Instance);

        Index = _newIndex();
        Search = new SymbolSearchService(Accessor, configManager);
        Deps = new DependencyTrackingService(Accessor, configManager);
    }

    public string Root { get; }
    public WorkspaceIndexDbAccessor Accessor { get; }
    public WorkspaceIndexService Index { get; }

    /// <summary>A fresh service instance, so its prune rate limit starts clear.</summary>
    public WorkspaceIndexService NewIndexService() => _newIndex();

    public SymbolSearchService Search { get; }
    public DependencyTrackingService Deps { get; }

    public async Task WriteAsync(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
    }

    public void Delete(string relativePath)
    {
        File.Delete(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public void Dispose()
    {
        _http.Dispose();
        Accessor.Dispose();
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}

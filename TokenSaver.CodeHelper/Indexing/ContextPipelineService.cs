using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.Library.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.Library.Models.Context;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using System.Collections.Concurrent;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Context-building pipeline (replaces LocalContextService): concise-context assembly,
///     hygiene reporting, and on-demand file content retrieval. Depends on the
///     <see cref="WorkspaceIndexService" /> orchestrator for indexing side effects
///     (IndexWorkspaceAsync / EnsureFileIndexedAsync) triggered as part of building context.
///     This is a one-way dependency — the orchestrator's own kept methods (IndexWorkspaceAsync,
///     IndexFileAsync, EnsureFileIndexedAsync, IndexOneFileAsync) never call back into this
///     service, so there's no DI constructor cycle.
/// </summary>
public sealed class ContextPipelineService
{
    private readonly WorkspaceIndexDbAccessor _accessor;
    private readonly ContextCleanerService _cleaner;
    private readonly TokenSaverConfig _config;
    private readonly ConcurrentDictionary<string, ConversationIndex> _conversationIndexes = new();
    private readonly GitStalenessService _gitStaleness;
    private readonly WorkspaceIndexService _orchestrator;
    private readonly WorkspaceConfigManager _workspaceConfig;

    public ContextPipelineService(
        WorkspaceIndexDbAccessor accessor,
        ContextCleanerService cleaner,
        GitStalenessService gitStaleness,
        WorkspaceConfigManager workspaceConfig,
        WorkspaceIndexService orchestrator,
        IOptions<TokenSaverConfig> config)
    {
        _accessor = accessor;
        _cleaner = cleaner;
        _gitStaleness = gitStaleness;
        _workspaceConfig = workspaceConfig;
        _orchestrator = orchestrator;
        _config = config.Value;
    }

    public async Task<BuildContextResult> BuildConciseContextAsync(
        string prompt,
        string[]? history = null,
        string? sessionId = null,
        string[]? requestedFiles = null,
        string? workspaceRoot = null,
        int? maxTokens = null,
        bool avoidChainOfThought = false,
        int perFileTokenLimit = 0,
        CancellationToken ct = default)
    {
        string root = workspaceRoot ?? _workspaceConfig.GetWorkspaceRoot();

        PipelineResult pipelineResult = await _cleaner.CleanContextAsync(
            prompt, history, sessionId, maxTokens ?? _config.ContextSizeLimit,
            avoidChainOfThought: avoidChainOfThought, ct: ct);

        string[] normalizedRequested = NormalizeRequestedFiles(requestedFiles);
        if (sessionId != null)
        {
            ConversationIndex previous = _conversationIndexes.GetOrAdd(sessionId, _ => new ConversationIndex());
            previous.RequestedFiles = normalizedRequested;
            previous.NeededItems = previous.NeededItems.Union(normalizedRequested).ToArray();
            previous.Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // Keep the whole workspace index fresh — cheap when nothing changed (see IndexWorkspaceAsync).
        _ = await _orchestrator.IndexWorkspaceAsync(root, ct);

        List<string> generatedFiles = [];
        foreach (string? absFile in ExpandRequestedPaths(root, normalizedRequested).Where(File.Exists))
        {
            string rel = Path.GetRelativePath(root, absFile).Replace('\\', '/');
            await _orchestrator.EnsureFileIndexedAsync(root, rel, ct);
            generatedFiles.Add(rel);
        }

        int limit = maxTokens ?? _config.ContextSizeLimit;
        bool alert = pipelineResult.TokenCount > limit;

        ContextHygieneReport? hygieneReport = null;
        if (alert)
        {
            hygieneReport = await BuildContextHygieneReportAsync(sessionId, root, ct);
        }

        string[] staleFiles = await _gitStaleness.GetStaleFilesAsync(root, ct);

        return new BuildContextResult
        {
            ContextText = pipelineResult.ContextText,
            TokenCount = pipelineResult.TokenCount,
            Alert = alert,
            AlertMessage = pipelineResult.AlertMessage,
            GeneratedFiles = generatedFiles.ToArray(),
            HygieneReport = hygieneReport,
            ConversationIndex = sessionId != null ? _conversationIndexes.GetValueOrDefault(sessionId) : null,
            PipelineSummary = pipelineResult.Summary,
            StaleFiles = staleFiles
        };
    }

    public async Task<ContextHygieneReport> BuildContextHygieneReportAsync(
        string? sessionId = null, string? workspaceRoot = null, CancellationToken ct = default)
    {
        string root = workspaceRoot ?? _workspaceConfig.GetWorkspaceRoot();
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);

        return await _accessor.WithDbLockAsync(async () =>
        {
            int fileCount = await ctx.Files.CountAsync(ct);
            int symbolCount = await ctx.Symbols.CountAsync(ct);

            long staleThreshold = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (30L * 24 * 60 * 60 * 1000);
            List<FileEntity> staleFiles = await ctx.Files.AsNoTracking()
                .Where(f => f.UpdatedAt < staleThreshold)
                .OrderBy(f => f.UpdatedAt)
                .Take(20)
                .ToListAsync(ct);

            ConversationIndex? index = sessionId != null ? _conversationIndexes.GetValueOrDefault(sessionId) : null;
            return new ContextHygieneReport
            {
                FileCount = fileCount,
                SectionCount = symbolCount,
                StaleSections = staleFiles.Select(f => new StaleSection
                {
                    Id = f.Id,
                    RelativePath = f.RelativePath,
                    SectionName = f.FileType
                }).ToArray(),
                ConversationIndex = index
            };
        }, ct);
    }

    public async Task<string?> GetFileContentAsync(string relativePath, string? workspaceRoot = null,
        CancellationToken ct = default)
    {
        string root = workspaceRoot ?? _workspaceConfig.GetWorkspaceRoot();
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);
        string relPath = relativePath.Replace('\\', '/');

        FileEntity? row = await _accessor.WithDbLockAsync(
            () => ctx.Files.AsNoTracking().FirstOrDefaultAsync(f => f.RelativePath == relPath, ct), ct);

        if (row == null)
        {
            return null;
        }

        string abs = Path.Combine(root, relPath);
        if (!File.Exists(abs))
        {
            return $"(file no longer exists on disk: {relPath})";
        }

        string content = await File.ReadAllTextAsync(abs, ct);
        int tokens = PipelineContext.EstimateTokens(content);
        string compressed = tokens > 500 ? await _cleaner.CleanContextTextAsync(content, ct: ct) : content;

        return
            $"<!-- tokensaver:summary-of:{relPath} -->\n<!-- tokensaver:description:{row.Description} -->\n{compressed}";
    }

    private static string[] NormalizeRequestedFiles(string[]? requestedFiles)
    {
        return requestedFiles == null || requestedFiles.Length == 0
            ? []
            : requestedFiles
            .Select(f => f.Trim().TrimEnd('/', '\\'))
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct()
            .ToArray();
    }

    private static IEnumerable<string> ExpandRequestedPaths(string root, string[] requested)
    {
        foreach (string rel in requested)
        {
            string abs = Path.GetFullPath(Path.Combine(root, rel));
            if (File.Exists(abs))
            {
                yield return abs;
            }
            else if (Directory.Exists(abs))
            {
                foreach (string f in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
                {
                    yield return f;
                }
            }
        }
    }
}

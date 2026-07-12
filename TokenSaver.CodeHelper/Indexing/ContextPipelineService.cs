using TokenSaver.CodeHelper.Configuration;
using TokenSaver.ContextHelper.Calibration;
using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Models.Calibration;
using TokenSaver.ContextHelper.Pipeline;
using TokenSaver.Config;
using TokenSaver.Library.Configuration;
using TokenSaver.Library.Contracts;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.Library.Models.Context;
using TokenSaver.Library.Services;

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
    /// <summary>Below this, GetFileContentAsync returns a requested file's content raw — not worth the pipeline's cost for a trivially small file.</summary>
    private const int MinTokensForFileCleaning = 50;

    private readonly WorkspaceIndexDbAccessor _accessor;
    private readonly ContextCleanerService _cleaner;
    private readonly TokenSaverConfig _config;
    private readonly ConcurrentDictionary<string, ConversationIndex> _conversationIndexes = new();
    private readonly GitStalenessService _gitStaleness;
    private readonly WorkspaceIndexService _orchestrator;
    private readonly ISavingsTracker _savings;
    private readonly SemanticIndexBackgroundTrigger _semanticIndexTrigger;
    private readonly RetrievalCalibrationService _retrievalCalibration;
    private readonly WorkspaceConfigManager _workspaceConfig;
    private readonly BuildArtifactDetector _artifactDetector;
    private readonly BehaviorConfigService _behaviorConfig;

    public ContextPipelineService(
        WorkspaceIndexDbAccessor accessor,
        ContextCleanerService cleaner,
        GitStalenessService gitStaleness,
        WorkspaceConfigManager workspaceConfig,
        WorkspaceIndexService orchestrator,
        SemanticIndexBackgroundTrigger semanticIndexTrigger,
        ISavingsTracker savings,
        RetrievalCalibrationService retrievalCalibration,
        BuildArtifactDetector artifactDetector,
        BehaviorConfigService behaviorConfig,
        IOptions<TokenSaverConfig> config)
    {
        _accessor = accessor;
        _cleaner = cleaner;
        _gitStaleness = gitStaleness;
        _workspaceConfig = workspaceConfig;
        _orchestrator = orchestrator;
        _semanticIndexTrigger = semanticIndexTrigger;
        _savings = savings;
        _retrievalCalibration = retrievalCalibration;
        _artifactDetector = artifactDetector;
        _behaviorConfig = behaviorConfig;
        _config = config.Value;
    }

    /// <summary>
    /// Filters build/cache artifacts from the context text if enabled in config.
    /// Extracts file references from tokensaver comments, identifies artifacts, and removes them.
    /// Returns the filtered result or the original if artifact detection is disabled.
    /// </summary>
    private (string FilteredContext, int RemovedCount) FilterBuildArtifactsFromContext(
        PipelineResult pipelineResult, string workspaceRoot)
    {
        TokenSaverBehaviorConfig merged = _behaviorConfig.GetConfig(workspaceRoot);
        if (merged?.Context?.DetectBuildArtifacts != true)
        {
            return (pipelineResult.ContextText, 0);
        }

        var lines = pipelineResult.ContextText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
        var artifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Extract file paths from tokensaver comments: <!-- tokensaver:summary-of:path/to/file -->
        foreach (var line in lines)
        {
            if (line.Contains("tokensaver:summary-of:", StringComparison.OrdinalIgnoreCase))
            {
                // Parse: <!-- tokensaver:summary-of:path/to/file -->
                int start = line.IndexOf("tokensaver:summary-of:", StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    start += "tokensaver:summary-of:".Length;
                    int end = line.IndexOf(" -->", start, StringComparison.OrdinalIgnoreCase);
                    if (end > start)
                    {
                        string filePath = line.Substring(start, end - start).Trim();
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            artifacts.Add(filePath);
                        }
                    }
                }
            }
        }

        // Detect which are build artifacts
        var detectedArtifacts = _artifactDetector.DetectArtifacts(artifacts);
        if (detectedArtifacts.Count == 0)
        {
            return (pipelineResult.ContextText, 0);
        }

        // Remove file blocks that are artifacts. A "block" is from one <!-- tokensaver:summary-of -->
        // comment to the next, or to the end of the file.
        var artifactSet = new HashSet<string>(detectedArtifacts, StringComparer.OrdinalIgnoreCase);
        var filteredLines = new List<string>();
        string? currentArtifactPath = null;

        foreach (var line in lines)
        {
            // Check if this is a tokensaver comment line
            if (line.Contains("tokensaver:summary-of:", StringComparison.OrdinalIgnoreCase))
            {
                // Extract the file path
                int start = line.IndexOf("tokensaver:summary-of:", StringComparison.OrdinalIgnoreCase);
                start += "tokensaver:summary-of:".Length;
                int end = line.IndexOf(" -->", start, StringComparison.OrdinalIgnoreCase);
                if (end > start)
                {
                    string filePath = line.Substring(start, end - start).Trim();
                    currentArtifactPath = artifactSet.Contains(filePath) ? filePath : null;
                }
            }

            // Only add the line if we're not inside an artifact block
            if (currentArtifactPath == null)
            {
                filteredLines.Add(line);
            }
        }

        string filtered = string.Join(Environment.NewLine, filteredLines);
        int removedCount = detectedArtifacts.Count;

        return (filtered, removedCount);
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
        string[] normalizedRequested = NormalizeRequestedFiles(requestedFiles);

        // Must run before CleanContextAsync: that call's SemanticRelevanceFilterStage overwrites
        // this session's "last pass" state with THIS turn's results, so the comparison against
        // the PREVIOUS turn's relevance bar has to happen first.
        RetrievalCalibrationCheckResult? calibrationCheck = await _retrievalCalibration.CheckLateRequestAsync(
            sessionId, root, prompt, normalizedRequested, ct);

        PipelineResult pipelineResult = await _cleaner.CleanContextAsync(
            prompt, history, sessionId, maxTokens ?? _config.ContextSizeLimit,
            avoidChainOfThought: avoidChainOfThought, workspaceRoot: root, ct: ct);

        // Apply build artifact detection/filtering after main context assembly
        var (filteredContextText, removedArtifactCount) = FilterBuildArtifactsFromContext(pipelineResult, root);
        if (removedArtifactCount > 0)
        {
            pipelineResult.ContextText = filteredContextText;
            pipelineResult.TokenCount = PipelineContext.EstimateTokens(filteredContextText);
            pipelineResult.AlertMessage = $"Excluded {removedArtifactCount} build/cache artifact(s) from context.";
        }

        if (sessionId != null)
        {
            ConversationIndex previous = _conversationIndexes.GetOrAdd(sessionId, _ => new ConversationIndex());
            previous.RequestedFiles = normalizedRequested;
            previous.NeededItems = previous.NeededItems.Union(normalizedRequested).ToArray();
            previous.Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // These three don't depend on each other's results, so run them concurrently instead of
        // three sequential round-trips.
        int conversationTurns = history?.Length ?? 0;
        int conversationTokens = history?.Sum(PipelineContext.EstimateTokens) ?? 0;
        Task savingsTask = _savings.RecordAsync(
            root, "buildConciseContext", RoleKey(pipelineResult.Summary.TaskType), avoidChainOfThought,
            pipelineResult.Summary.TotalInputTokens, pipelineResult.TokenCount,
            (int)pipelineResult.Summary.TotalElapsed.TotalMilliseconds,
            conversationTurns, conversationTokens, ct);

        // Keep the whole workspace index fresh — cheap when nothing changed (see IndexWorkspaceAsync).
        Task indexWorkspaceTask = _orchestrator.IndexWorkspaceAsync(root, ct);

        // First-run only: kick off the chunk/embedding index (Chunks table) in the background if
        // it's never been built for this workspace — SemanticGrep is otherwise silently empty
        // forever unless IndexRepo is called manually. Cheap no-op on every later call once the
        // table has rows. See SemanticIndexBackgroundTrigger's doc comment for why this must not
        // be awaited or share `ct`.
        Task<SemanticIndexTriggerStatus> semanticIndexTask = _semanticIndexTrigger.EnsureStartedAsync(root, ct);

        await Task.WhenAll(savingsTask, indexWorkspaceTask, semanticIndexTask);
        SemanticIndexTriggerStatus semanticIndexStatus = semanticIndexTask.Result;

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
            StaleFiles = staleFiles,
            SemanticIndexStatus = semanticIndexStatus,
            CalibrationCheck = calibrationCheck
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

        // Check if file is a build artifact and if blocking is enabled
        TokenSaverBehaviorConfig config = _behaviorConfig.GetConfig(root);
        if (config?.Context?.BlockArtifactFileReads == true && _artifactDetector.DetectArtifacts([relPath]).Count > 0)
        {
            return $"(artifact file — blocked by detectBuildArtifacts: {relPath})\n\n" +
                   "This path appears to be a build/cache artifact (bin/, obj/, node_modules/, etc.) " +
                   "and has been blocked to prevent wasting tokens. If this is legitimate source code, " +
                   "update .tokensaver/.ignore-bypass to force-include it, or set blockArtifactFileReads=false in config.json to allow fallback retry.";
        }

        string abs = Path.Combine(root, relPath);
        if (!File.Exists(abs))
        {
            return $"(file no longer exists on disk: {relPath})";
        }

        string content = await File.ReadAllTextAsync(abs, ct);
        int tokens = PipelineContext.EstimateTokens(content);
        // Lowered from 500: even small requested files benefit from dedup/log-strip, and tagging
        // this as ContentKind.File (see RequestedFileSource) means it's cheap CPU-only cleaning
        // unless SemanticRelevanceFilterStage's embedding call is reachable.
        string compressed = tokens > MinTokensForFileCleaning
            ? await _cleaner.CleanContextTextAsync(content, workspaceRoot: root, requestedFileSource: relPath, ct: ct)
            : content;

        return
            $"<!-- tokensaver:summary-of:{relPath} -->\n<!-- tokensaver:description:{row.Description} -->\n{compressed}";
    }

    private static string RoleKey(TaskType taskType)
    {
        return taskType switch
        {
            TaskType.Code => "code",
            TaskType.Debug => "debug",
            TaskType.Planning => "planning",
            TaskType.Review => "review",
            TaskType.Explain => "explain",
            TaskType.Refactor => "refactor",
            TaskType.Test => "test",
            _ => "general"
        };
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

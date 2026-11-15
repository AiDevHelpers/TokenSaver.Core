using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.CodeHelper.Contracts;
using TokenSaver.Config;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.Utilities.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Unified replacement for the old LocalContextService (context.db: files/sections/summaries/
///     folder_indices JSON) and CodeIndexService (code-index.db: file_manifest/symbols/references_).
///     Every file not excluded by .gitignore/ExcludePatterns/.ignore-additional gets a File row
///     (type + description); files matching an <see cref="ISymbolParser" /> additionally get Symbol
///     rows. Staleness is git-aware (see <see cref="GitStalenessChecker" />) with a per-file
///     last-write-time fallback for non-git folders. See plans/TokenSaver.UnifiedIndex.plan.md.
///     Slimmed orchestrator: owns only the indexing pipeline itself (bootstrap/incremental index,
///     pruning, single-file re-index) and delegates everything else — DB access, staleness
///     resolution, folder tree maintenance, symbol queries, AI summarization, context building,
///     dependency queries, and workspace config/preferences — to the extracted services below.
/// </summary>
public sealed partial class WorkspaceIndexService : IWorkspaceIndexService, IFileReindexer
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped {File}: {Msg}")]
    private static partial void LogFileSkipped(ILogger logger, string file, string msg);

    [LoggerMessage(Level = LogLevel.Information, Message = "Index: {Indexed} files, {Symbols} symbols ({Skipped} unchanged) in {Ms}ms")]
    private static partial void LogIndexSummary(ILogger logger, int indexed, int symbols, int skipped, long ms);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Files} files, {Symbols} symbols ({Removed} removed from disk, {Ignored} newly gitignored/excluded) in {Ms}ms")]
    private static partial void LogPruneSummary(ILogger logger, int files, int symbols, int removed, int ignored, long ms);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Symbol parse failed for {File}")]
    private static partial void LogSymbolParseFailed(ILogger logger, Exception ex, string file);

    private static readonly int CpuDegree = Math.Max(1, Environment.ProcessorCount - 2);

    private readonly WorkspaceIndexDbAccessor _accessor;
    private readonly AiSummarizationService _aiSummarization;
    private readonly SimpleModelServerCapacityClient _capacityClient;
    private readonly CodeHelperConfig _codeConfig;
    private readonly FolderTreeManager _folderTree;
    private readonly GitStalenessService _gitStaleness;
    private readonly ILogger<WorkspaceIndexService> _logger;
    private readonly IReadOnlyList<ISymbolParser> _parsers;
    private long _lastPruneAtMs;
    private long _lastFullReferenceAtMs;

    // Pruning does a full Files-table scan (disk + gitignore check per row). IndexWorkspaceAsync
    // can be called on every interactive request (see BuildConciseContextAsync), so the sweep is
    // rate-limited rather than run on every call — deleted/newly-ignored files are edge cases that
    // don't need sub-minute detection.
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(10);

    private const int ReferenceInsertChunk = 5000;

    // Per-root gates shared by every WorkspaceIndexService instance in the process.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootGates =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-root set of paths whose last index attempt threw (see GetFailedPaths).
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> FailedPathsByRoot =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceIndexService(
        IOptions<CodeHelperConfig> codeConfig,
        IEnumerable<ISymbolParser> parsers,
        WorkspaceIndexDbAccessor accessor,
        GitStalenessService gitStaleness,
        FolderTreeManager folderTree,
        AiSummarizationService aiSummarization,
        SimpleModelServerCapacityClient capacityClient,
        ILogger<WorkspaceIndexService> logger)
    {
        _codeConfig = codeConfig.Value;
        _parsers = parsers.ToList();
        _accessor = accessor;
        _gitStaleness = gitStaleness;
        _folderTree = folderTree;
        _aiSummarization = aiSummarization;
        _capacityClient = capacityClient;
        _logger = logger;
    }

    /// <summary>
    ///     Indexes exactly one file — public entry point for callers that just applied a
    ///     patch/edit to a single file and want it re-indexed immediately (replaces
    ///     CodeIndexService.IndexFileAsync for consumers like PatchEngine).
    /// </summary>
    public async Task<IndexStats> IndexFileAsync(string root, string relPath, CancellationToken ct = default)
    {
        return await IndexPathsAsync(root, [relPath], ct);
    }

    /// <summary>
    ///     Indexes exactly the listed workspace-relative paths; a listed path that no longer exists
    ///     is removed from the index. The single entry point for targeted work: the change queue
    ///     (<see cref="IndexChangeBuffer" />) and single-file edits (<see cref="IndexFileAsync" />) both
    ///     land here. Shares the per-root gate with <see cref="IndexWorkspaceAsync" />.
    /// </summary>
    public async Task<IndexStats> IndexPathsAsync(string root, IEnumerable<string> relPaths, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        List<string> paths = relPaths
            .Select(p => p.Replace('\\', '/').TrimStart('/'))
            .Where(p => p.Length > 0 && !AlwaysSkipDirectories.IsUnderSkippedDirectory(p, isDirectory: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            return new IndexStats { Elapsed = sw.Elapsed };
        }

        SemaphoreSlim gate = RootGates.GetOrAdd(Path.GetFullPath(root), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);
            (int indexed, int skipped, int symbolCount, _) = await IndexFilesAsync(ctx, root, paths, null, ct);
            await _folderTree.RefreshTouchedFolderDescriptionsAsync(ctx, paths, ct);
            int refs = await RebuildReferencesAsync(ctx, root, paths, false, ct);

            return new IndexStats
            {
                FilesIndexed = indexed,
                SymbolsIndexed = symbolCount,
                FilesSkipped = skipped,
                ReferenceFilesRead = refs,
                Elapsed = sw.Elapsed
            };
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <summary>
    ///     Paths whose last index attempt failed (exception while reading, summarizing or writing).
    ///     Replaces the old full-scan "stale files" report: the failed set is what a context build
    ///     needs to surface, and it is kept by the index pass itself.
    /// </summary>
    public string[] GetFailedPaths(string root) =>
        FailedPathsByRoot.TryGetValue(Path.GetFullPath(root), out ConcurrentDictionary<string, byte>? set)
            ? set.Keys.ToArray()
            : [];

    private static void RecordFailure(string root, string relPath) =>
        FailedPathsByRoot.GetOrAdd(Path.GetFullPath(root), _ => new(StringComparer.OrdinalIgnoreCase))[relPath] = 0;

    private static void ClearFailure(string root, string relPath)
    {
        if (FailedPathsByRoot.TryGetValue(Path.GetFullPath(root), out ConcurrentDictionary<string, byte>? set))
        {
            _ = set.TryRemove(relPath, out _);
        }
    }

    // ── Public: workspace indexing ────────────────────────────────────────────

    /// <summary>
    ///     Runs a git-staleness check (§2 of the design doc) and (re)indexes whatever changed —
    ///     on a workspace with no git anywhere, or the very first call, this indexes everything.
    ///     Cheap to call on every BuildConciseContext request: when nothing changed, this is just
    ///     one or two `git` subprocess calls per repo root.
    /// </summary>
    public async Task<IndexStats> IndexWorkspaceAsync(string workspaceRoot, CancellationToken ct = default)
    {
        // One pass per root at a time: concurrent callers (interactive request, background
        // trigger, codegen push) queue behind it instead of each summarizing the same files.
        SemaphoreSlim gate = RootGates.GetOrAdd(Path.GetFullPath(workspaceRoot), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await IndexWorkspaceCoreAsync(workspaceRoot, ct);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private async Task<IndexStats> IndexWorkspaceCoreAsync(string root, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);

        GitStalenessService.StalenessPlan plan = await _gitStaleness.PlanTargetFilesAsync(ctx, root, ct);
        List<string>? targets = plan.Targets;
        bool isFullBootstrap = targets == null;

        Dictionary<string, int>? prebuiltFolderIds = null;
        List<string> files;
        if (isFullBootstrap)
        {
            prebuiltFolderIds = await _folderTree.EnsureFoldersAsync(ctx, root, ct);
            files = GitStalenessService.EnumerateAllFiles(root, root).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .ToList();
        }
        else
        {
            files = targets!;
        }

        (int indexed, int skipped, int symbolCount, int failed) =
            await IndexFilesAsync(ctx, root, files, prebuiltFolderIds, ct);

        // Full reference rebuild on bootstrap and at most once per PruneInterval. Otherwise only the
        // changed files and the sources that point at them are re-read (see RebuildReferencesAsync).
        long nowMsForRefs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool fullRefs = isFullBootstrap
            || nowMsForRefs - Interlocked.Read(ref _lastFullReferenceAtMs) >= PruneInterval.TotalMilliseconds;
        int referenceFilesRead = 0;
        if (files.Count > 0)
        {
            await _folderTree.RefreshTouchedFolderDescriptionsAsync(ctx, files, ct);
            if (fullRefs)
            {
                _ = Interlocked.Exchange(ref _lastFullReferenceAtMs, nowMsForRefs);
            }

            referenceFilesRead = await RebuildReferencesAsync(ctx, root, files, fullRefs, ct);
        }

        // Advance the git baseline only when every planned file indexed. A failed or cancelled
        // file leaves the old baseline, so the next run diffs from it and retries the change.
        if (failed == 0)
        {
            await _gitStaleness.CommitBaselinesAsync(ctx, plan, ct);
        }

        // Low-priority maintenance pass: sweep the whole Files table for rows that no longer
        // belong (deleted from disk, or newly covered by .gitignore/.ignore-additional). Rate
        // limited (see PruneInterval) since IndexWorkspaceAsync can be called on every interactive
        // request. See plans/TokenSaver.IndexPruning.plan.md.
        (int prunedFiles, int prunedSymbols) = (0, 0);
        long nowMsForPrune = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMsForPrune - Interlocked.Read(ref _lastPruneAtMs) >= PruneInterval.TotalMilliseconds)
        {
            _ = Interlocked.Exchange(ref _lastPruneAtMs, nowMsForPrune);
            (prunedFiles, prunedSymbols) = await PruneRemovedAndIgnoredFiles(ctx, root, ct);
        }

        LogIndexSummary(_logger, indexed, symbolCount, skipped, sw.ElapsedMilliseconds);

        return new IndexStats
        {
            FilesIndexed = indexed,
            SymbolsIndexed = symbolCount,
            FilesSkipped = skipped,
            Elapsed = sw.Elapsed,
            FilesPruned = prunedFiles,
            SymbolsPruned = prunedSymbols,
            ReferenceFilesRead = referenceFilesRead
        };
    }

    /// <summary>
    ///     Forces an immediate full reconciliation after a change to <c>.gitignore</c> or one of the
    ///     <c>.tokensaver</c> ignore overrides (<c>.ignore-additional</c>/<c>.ignore-bypass</c>): a
    ///     full-tree walk (git diff can't tell us about files whose ignored status just flipped) to
    ///     pick up newly-included files, plus an un-rate-limited prune pass to drop newly-excluded
    ///     ones. Called by <c>GitignoreWatcherService</c>; not on the interactive hot path.
    /// </summary>
    public async Task<IndexStats> ReconcileIgnoreChangeAsync(string root, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);

        Dictionary<string, int> prebuiltFolderIds = await _folderTree.EnsureFoldersAsync(ctx, root, ct);
        List<string> files = GitStalenessService.EnumerateAllFiles(root, root)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        (int indexed, int skipped, int symbolCount, _) =
            await IndexFilesAsync(ctx, root, files, prebuiltFolderIds, ct);

        if (files.Count > 0)
        {
            await _folderTree.RefreshTouchedFolderDescriptionsAsync(ctx, files, ct);
        }

        _ = Interlocked.Exchange(ref _lastPruneAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        (int prunedFiles, int prunedSymbols) = await PruneRemovedAndIgnoredFiles(ctx, root, ct);

        LogIndexSummary(_logger, indexed, symbolCount, skipped, sw.ElapsedMilliseconds);

        return new IndexStats
        {
            FilesIndexed = indexed,
            SymbolsIndexed = symbolCount,
            FilesSkipped = skipped,
            Elapsed = sw.Elapsed,
            FilesPruned = prunedFiles,
            SymbolsPruned = prunedSymbols
        };
    }

    /// <summary>
    ///     Rebuilds every SymbolReferences edge from scratch (C# only; see <see cref="CSharpReferenceGraph" />).
    ///     Full rebuild keeps edges correct across deletes and renames; cost is one read of each
    ///     indexed .cs file per call that changed something.
    /// </summary>
    /// <summary>
    ///     Rebuilds SymbolReferences edges. A full rebuild re-reads every C# type file. An incremental
    ///     one re-reads only <paramref name="changedFiles" /> plus the dependents: sources whose edges
    ///     point at a symbol that no longer exists. Returns the number of files re-read.
    ///     Known gap: a brand-new reference from an unchanged file to a new type is picked up on the
    ///     next full rebuild (bootstrap, or once per PruneInterval), not on the incremental pass.
    /// </summary>
    private async Task<int> RebuildReferencesAsync(
        WorkspaceIndexContext ctx, string root, IReadOnlyCollection<string> changedFiles, bool fullRebuild,
        CancellationToken ct)
    {
        var typeRows = await _accessor.WithDbLockAsync(() => ctx.Symbols
            .Include(s => s.File)
            .Where(s => s.ContainingType == null && s.File != null && s.File.RelativePath.EndsWith(".cs")
                && (s.Kind == (int)SymbolKind.Class || s.Kind == (int)SymbolKind.Interface
                    || s.Kind == (int)SymbolKind.Struct || s.Kind == (int)SymbolKind.Enum
                    || s.Kind == (int)SymbolKind.Delegate))
            .Select(s => new { s.Id, Path = s.File!.RelativePath, s.Name })
            .ToListAsync(ct), ct);

        List<CSharpReferenceGraph.TypeNode> types = typeRows
            .Select(r => new CSharpReferenceGraph.TypeNode(r.Id, r.Path, r.Name))
            .ToList();

        HashSet<string> currentIds = types.Select(t => t.SymbolId).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> idToPath = new(StringComparer.Ordinal);
        foreach (CSharpReferenceGraph.TypeNode t in types)
        {
            idToPath[t.SymbolId] = t.FilePath;
        }

        HashSet<string> csPaths = types.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Targets no longer present: renamed, removed, or re-created with a new id by reparsing.
        List<string> staleTargets = [];
        HashSet<string> rebuildPaths = new(StringComparer.OrdinalIgnoreCase);
        if (fullRebuild)
        {
            rebuildPaths = csPaths;
        }
        else
        {
            List<string> allTargets = await _accessor.WithDbLockAsync(
                () => ctx.SymbolReferences.Select(e => e.TargetSymbolId).Distinct().ToListAsync(ct), ct);
            staleTargets = allTargets.Where(t => !currentIds.Contains(t)).ToList();

            foreach (string f in changedFiles)
            {
                if (csPaths.Contains(f))
                {
                    _ = rebuildPaths.Add(f);
                }
            }

            if (staleTargets.Count > 0)
            {
                List<string> dependentSources = await _accessor.WithDbLockAsync(
                    () => ctx.SymbolReferences.Where(e => staleTargets.Contains(e.TargetSymbolId))
                        .Select(e => e.SourceSymbolId).Distinct().ToListAsync(ct), ct);
                foreach (string src in dependentSources)
                {
                    if (idToPath.TryGetValue(src, out string? depPath))
                    {
                        _ = rebuildPaths.Add(depPath);
                    }
                }
            }
        }

        // Edges owned by the files being rebuilt are replaced; edges to stale targets are dropped.
        List<string> rebuildSourceIds = types.Where(t => rebuildPaths.Contains(t.FilePath))
            .Select(t => t.SymbolId).ToList();

        Dictionary<string, string> contents = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in rebuildPaths)
        {
            try
            {
                contents[path] = await File.ReadAllTextAsync(Path.Combine(root, path), ct);
            }
            catch (IOException)
            {
                // Deleted or locked since indexing — its edges are simply not rebuilt.
            }
        }

        List<SymbolReferenceEntity> edges = CSharpReferenceGraph.Build(types, contents);

        // Insert in small chunks: one huge SaveChanges batch ran past its time limit on a real
        // workspace (TaskCanceledException after ~2 min) and left the scan failed. Each chunk
        // releases the DB lock and clears the change tracker.
        await _accessor.WithDbLockAsync(async () =>
        {
            try
            {
                if (fullRebuild)
                {
                    _ = await ctx.SymbolReferences.ExecuteDeleteAsync(ct);
                }
                else
                {
                    _ = await ctx.SymbolReferences.Where(e => rebuildSourceIds.Contains(e.SourceSymbolId))
                        .ExecuteDeleteAsync(ct);
                    _ = await ctx.SymbolReferences.Where(e => staleTargets.Contains(e.TargetSymbolId))
                        .ExecuteDeleteAsync(ct);
                }
                for (int i = 0; i < edges.Count; i += ReferenceInsertChunk)
                {
                    int count = Math.Min(ReferenceInsertChunk, edges.Count - i);
                    ctx.SymbolReferences.AddRange(edges.GetRange(i, count));
                    _ = await ctx.SaveChangesAsync(ct);
                    ctx.ChangeTracker.Clear();
                }
            }
            finally
            {
                ctx.ChangeTracker.Clear();
            }
        }, ct);

        return rebuildPaths.Count;
    }

    private async Task<(int Indexed, int Skipped, int SymbolCount, int Failed)> IndexFilesAsync(
        WorkspaceIndexContext ctx, string root, List<string> files,
        Dictionary<string, int>? prebuiltFolderIds, CancellationToken ct)
    {
        int indexed = 0;
        int skipped = 0;
        int symbolCount = 0;
        int failed = 0;
        SimpleModelServerCapacity capacity = await _capacityClient.GetCapacityAsync(ct);
        using SemaphoreSlim aiThrottle = new(capacity.MaxConcurrentSummaryRequests);

        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = CpuDegree, CancellationToken = ct },
            async (relPath, innerCt) =>
            {
                try
                {
                    string abs = Path.Combine(root, relPath);
                    if (!File.Exists(abs))
                    {
                        await RemoveFileAsync(ctx, relPath, innerCt);
                        return;
                    }

                    long lastMod = new DateTimeOffset(File.GetLastWriteTimeUtc(abs)).ToUnixTimeMilliseconds();
                    FileEntity? existing = await GetFileRowAsync(ctx, relPath, innerCt);
                    if (existing != null && existing.LastModified == lastMod && !await NeedsReindexAsync(ctx, existing, innerCt))
                    {
                        _ = Interlocked.Increment(ref skipped);
                        return;
                    }

                    // Background/bulk indexing prefers a backup endpoint (when configured and
                    // healthy) so it doesn't compete with interactive requests for the primary
                    // endpoint's capacity.
                    int symbols = await IndexOneFileAsync(ctx, root, relPath, lastMod, prebuiltFolderIds, aiThrottle,
                        innerCt, preferBackupEndpoint: true);
                    _ = Interlocked.Increment(ref indexed);
                    _ = Interlocked.Add(ref symbolCount, symbols);
                    ClearFailure(root, relPath);
                }
                catch (Exception ex)
                {
                    _ = Interlocked.Increment(ref failed);
                    RecordFailure(root, relPath);
                    LogFileSkipped(_logger, relPath, ex.Message);
                }
            });

        return (indexed, skipped, symbolCount, failed);
    }

    /// <summary>
    ///     Sweeps every indexed <see cref="FileEntity" /> row and removes ones that no longer belong
    ///     in the index: files deleted from disk, and files now covered by .gitignore or
    ///     .ignore-additional (e.g. a pattern added after the file was originally indexed). Symbol
    ///     rows are removed via the FK cascade (see WorkspaceIndexContext.OnModelCreating) — the
    ///     explicit Symbols delete here exists only to report an accurate pruned-symbol count, since
    ///     ExecuteDeleteAsync on Files wouldn't tell us how many child rows cascaded.
    ///     Folders are intentionally left alone: an empty folder is still valid structure, and the
    ///     folder-description refresh already reports "no files" / "empty folder" for it.
    /// </summary>
    private async Task<(int FilesPruned, int SymbolsPruned)> PruneRemovedAndIgnoredFiles(
        WorkspaceIndexContext ctx, string root, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        WorkspaceIgnoreFilter ignore = WorkspaceIgnoreFilter.Load(root);

        List<FileEntity> rows = await _accessor.WithDbLockAsync(
            () => ctx.Files.AsNoTracking().ToListAsync(ct), ct);

        List<int> idsToRemove = [];
        int removedFromDisk = 0;
        int removedIgnored = 0;

        foreach (FileEntity row in rows)
        {
            ct.ThrowIfCancellationRequested();

            string abs = Path.Combine(root, row.RelativePath);
            if (!File.Exists(abs))
            {
                idsToRemove.Add(row.Id);
                removedFromDisk++;
                continue;
            }

            if (ignore.IsIgnored(row.RelativePath, false))
            {
                idsToRemove.Add(row.Id);
                removedIgnored++;
            }
        }

        int prunedSymbols = 0;
        if (idsToRemove.Count > 0)
        {
            prunedSymbols = await _accessor.WithDbLockAsync(async () =>
            {
                int deleted = await ctx.Symbols.Where(s => idsToRemove.Contains(s.FileId)).ExecuteDeleteAsync(ct);
                _ = await ctx.Files.Where(f => idsToRemove.Contains(f.Id)).ExecuteDeleteAsync(ct);
                return deleted;
            }, ct);

            LogPruneSummary(_logger, idsToRemove.Count, prunedSymbols, removedFromDisk, removedIgnored,
                sw.ElapsedMilliseconds);
        }

        return (idsToRemove.Count, prunedSymbols);
    }

    /// <summary>
    ///     Indexes exactly one file (used when BuildConciseContext is given an explicit
    ///     requestedFiles entry that a git-diff pass might not have caught, e.g. a gitignored path
    ///     the user just confirmed via includeIgnored).
    /// </summary>
    internal async Task EnsureFileIndexedAsync(string root, string relPath, CancellationToken ct)
    {
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);
        string abs = Path.Combine(root, relPath);
        if (!File.Exists(abs))
        {
            return;
        }

        long lastMod = new DateTimeOffset(File.GetLastWriteTimeUtc(abs)).ToUnixTimeMilliseconds();
        FileEntity? existing = await GetFileRowAsync(ctx, relPath, ct);
        if (existing != null && existing.LastModified == lastMod && !await NeedsReindexAsync(ctx, existing, ct))
        {
            return;
        }

        SimpleModelServerCapacity capacity = await _capacityClient.GetCapacityAsync(ct);
        using SemaphoreSlim throttle = new(capacity.MaxConcurrentSummaryRequests);
        _ = await IndexOneFileAsync(ctx, root, relPath, lastMod, null, throttle, ct);
        await _folderTree.RefreshTouchedFolderDescriptionsAsync(ctx, [relPath], ct);
    }

    /// <summary>UTF-8 decode that drops a leading BOM, matching File.ReadAllText's text output.</summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        return Encoding.UTF8.GetString(span);
    }

    /// <summary>
    ///     Reads, describes, and (if a parser matches) parses symbols for one file, then upserts
    ///     File + Symbol rows. Everything before the DB section runs outside the DB lock
    ///     so file I/O / AI summarization for different files can run concurrently.
    /// </summary>
    private async Task<int> IndexOneFileAsync(
        WorkspaceIndexContext ctx, string root, string relPath, long lastMod,
        Dictionary<string, int>? prebuiltFolderIds, SemaphoreSlim aiThrottle, CancellationToken ct,
        bool preferBackupEndpoint = false)
    {
        string abs = Path.Combine(root, relPath);
        string ext = Path.GetExtension(relPath);
        ISymbolParser? parser = _parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase));

        // Read the raw bytes once and hash them before any model call. A file whose bytes match
        // the stored hash (touch, checkout, formatter no-op) only gets its mtime refreshed — no
        // summary, no parse. Unreadable/binary files hash their relative path, as before.
        byte[]? bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(abs, ct);
        }
        catch
        {
            bytes = null;
        }

        string hash = bytes is null
            ? WorkspaceIndexDbAccessor.ComputeHash(relPath)
            : Convert.ToHexString(SHA256.HashData(bytes));

        FileEntity? stored = await GetFileRowAsync(ctx, relPath, ct);
        if (stored != null && stored.ContentHash == hash && !await NeedsReindexAsync(ctx, stored, ct))
        {
            await _accessor.WithDbLockAsync(async () =>
            {
                _ = await ctx.Files.Where(f => f.Id == stored.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.LastModified, lastMod), ct);
            }, ct);
            return 0;
        }

        string content = bytes is null ? "" : DecodeUtf8(bytes);
        string description = await _aiSummarization.GetFileDescriptionAsync(abs, relPath, ext, aiThrottle, preferBackupEndpoint, ct);
        string fileType = ext.TrimStart('.') is { Length: > 0 } e ? e : "none";
        long size = bytes?.LongLength ?? 0;

        string folderRel = FolderTreeManager.GetParentFolderRelPath(relPath);
        int folderId = await _folderTree.GetOrCreateFolderIdAsync(ctx, folderRel, prebuiltFolderIds, ct);

        List<SymbolEntry> symbolEntries = [];
        if (parser != null && content.Length > 0)
        {
            try
            {
                symbolEntries = parser.Parse(relPath, content).ToList();
            }
            catch (Exception ex)
            {
                LogSymbolParseFailed(_logger, ex, relPath);
            }
        }

        return await _accessor.WithDbLockAsync(async () =>
        {
            // IndexOneFileAsync runs from Parallel.ForEachAsync — one shared ctx across every
            // file in the scan, all serialized through WithDbLockAsync. Without clearing here,
            // every file's/symbol's entities stay in the change tracker for the whole scan
            // (thousands of rows), and the raw ExecuteDeleteAsync below bypasses the tracker
            // entirely — the combination left stale/Detached entries that later broke
            // FolderTreeManager.RefreshTouchedFolderDescriptionsAsync's own SaveChangesAsync with
            // "Unexpected entry.EntityState: Detached". The clear must run in `finally`, not just
            // after a successful save: a per-file failure (timeout/cancellation mid-SaveChanges)
            // is caught and swallowed by the Parallel.ForEachAsync delegate's own try/catch in
            // IndexWorkspaceAsync, which skipped a same-scope-only clear and left that file's
            // partially-applied entities tracked for the rest of the scan. Each iteration
            // re-queries what it needs fresh, so nothing here needs to stay tracked afterward.
            try
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                FileEntity? fileRow = await ctx.Files.FirstOrDefaultAsync(f => f.RelativePath == relPath, ct);
                if (fileRow == null)
                {
                    fileRow = new FileEntity { RelativePath = relPath, FolderId = folderId };
                    _ = ctx.Files.Add(fileRow);
                }

                fileRow.FolderId = folderId;
                fileRow.FileType = fileType;
                fileRow.Description = description;
                fileRow.ContentHash = hash;
                fileRow.Size = size;
                fileRow.LastModified = lastMod;
                fileRow.UpdatedAt = nowMs;
                _ = await ctx.SaveChangesAsync(ct);

                _ = await ctx.Symbols.Where(s => s.FileId == fileRow.Id).ExecuteDeleteAsync(ct);
                foreach (SymbolEntry entry in symbolEntries)
                {
                    _ = ctx.Symbols.Add(new SymbolEntity
                    {
                        Id = entry.Id,
                        FileId = fileRow.Id,
                        Name = entry.Name,
                        FullName = entry.FullName,
                        Kind = (int)entry.Kind,
                        Line = entry.Line,
                        Col = entry.Column,
                        ContainingType = entry.ContainingType,
                        Description = entry.Summary,
                        LastModified = lastMod,
                        UpdatedAt = nowMs
                    });
                }

                _ = await ctx.SaveChangesAsync(ct);

                return symbolEntries.Count;
            }
            finally
            {
                ctx.ChangeTracker.Clear();
            }
        }, ct);
    }

    private async Task RemoveFileAsync(WorkspaceIndexContext ctx, string relPath, CancellationToken ct)
    {
        await _accessor.WithDbLockAsync(async () =>
        {
            try
            {
                FileEntity? row = await ctx.Files.FirstOrDefaultAsync(f => f.RelativePath == relPath, ct);
                if (row == null)
                {
                    return;
                }

                _ = await ctx.Symbols.Where(s => s.FileId == row.Id).ExecuteDeleteAsync(ct);
                _ = ctx.Files.Remove(row);
                _ = await ctx.SaveChangesAsync(ct);
            }
            finally
            {
                ctx.ChangeTracker.Clear();
            }
        }, ct);
    }

    private async Task<FileEntity?> GetFileRowAsync(WorkspaceIndexContext ctx, string relPath, CancellationToken ct)
    {
        return await _accessor.WithDbLockAsync(
            () => ctx.Files.AsNoTracking().FirstOrDefaultAsync(f => f.RelativePath == relPath, ct), ct);
    }

    /// <summary>
    ///     An unchanged file is normally skipped, but a file with a parser that has no symbol rows
    ///     was indexed while its symbol insert failed (e.g. id collisions before ids included the
    ///     kind). Those are re-parsed so the failure heals. Files with no parser are never retried.
    /// </summary>
    private async Task<bool> NeedsReindexAsync(WorkspaceIndexContext ctx, FileEntity row, CancellationToken ct)
    {
        string ext = Path.GetExtension(row.RelativePath);
        if (!_parsers.Any(p => p.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        bool hasSymbols = await _accessor.WithDbLockAsync(
            () => ctx.Symbols.AnyAsync(s => s.FileId == row.Id, ct), ct);
        return !hasSymbols;
    }
}

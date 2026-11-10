using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.Database.Context;
using TokenSaver.TokenEconomy.Models.TokenEconomy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;

namespace TokenSaver.TokenEconomy.Services;

public enum SemanticIndexTriggerStatus
{
    /// <summary>The Chunks table already has rows for this workspace — nothing to do.</summary>
    AlreadyIndexed,

    /// <summary>A background build was already in flight for this workspace when asked again.</summary>
    AlreadyRunning,

    /// <summary>The Chunks table was empty and a background build was just kicked off.</summary>
    Started
}

/// <summary>
///     First-run trigger for RepoIndexer's chunk/embedding index (the Chunks table SemanticGrep
///     reads). BuildConciseContext calls <see cref="EnsureStartedAsync" /> on every request; it
///     does one cheap `Chunks.AnyAsync` check and, only the first time a workspace has zero rows,
///     fires IndexRepoAsync in the background — deliberately NOT awaited and NOT tied to the
///     caller's CancellationToken, because SemanticIndexTools.IndexRepo sharing the request's `ct`
///     is exactly what causes a real indexing run to die mid-flight the moment the calling client's
///     connection is torn down (observed directly: a multi-minute run aborted with
///     OperationCanceledException as soon as an MCP tool call's idle timeout dropped the request).
///     RepoIndexer's own per-chunk SaveChangesAsync + content-hash skip means a second trigger for
///     the same workspace while one is already running is safe to just no-op (see AlreadyRunning) —
///     nothing here needs to cancel or join the first run.
/// </summary>
public sealed partial class SemanticIndexBackgroundTrigger(
    IRepoIndexer indexer, TokenEconomyDbAccessor db, ILogger<SemanticIndexBackgroundTrigger> logger)
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);

    // Once a workspace is known to have chunks, every later call can skip the DB round-trip
    // entirely — the answer never goes back to "no chunks" once RepoIndexer has written any.
    private readonly ConcurrentDictionary<string, byte> _knownIndexed = new(StringComparer.OrdinalIgnoreCase);

    // Roots already given their once-per-process incremental run by EnsureStartedAsync.
    private readonly ConcurrentDictionary<string, byte> _startedRoots = new(StringComparer.OrdinalIgnoreCase);

    // Last completed run's stats and last failure message per workspace, for IndexStatus.
    private readonly ConcurrentDictionary<string, RepoIndexStats> _lastStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastError = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Starts one incremental run for this root per process, on its first connect. The run
    ///     never rebuilds: RepoIndexer skips chunks whose content hash is unchanged, so an
    ///     up-to-date index costs only a file walk. A partially built index is completed the same way.
    /// </summary>
    public Task<SemanticIndexTriggerStatus> EnsureStartedAsync(string workspaceRoot, CancellationToken ct)
    {
        if (!_startedRoots.TryAdd(workspaceRoot, 0))
        {
            return Task.FromResult(SemanticIndexTriggerStatus.AlreadyIndexed);
        }

        if (!_running.TryAdd(workspaceRoot, 0))
        {
            return Task.FromResult(SemanticIndexTriggerStatus.AlreadyRunning);
        }

        // CancellationToken.None, deliberately: this task must outlive the request that spawned
        // it. See the class doc comment for why sharing `ct` here would be wrong.
        _ = RunInBackgroundAsync(workspaceRoot, "incremental run on connect", CancellationToken.None);
        return Task.FromResult(SemanticIndexTriggerStatus.Started);
    }

    /// <summary>
    ///     Explicit (manual) index run, detached from the calling request. Unlike
    ///     <see cref="EnsureStartedAsync" />, this runs even when chunks already exist, so a caller
    ///     can re-index after edits. Returns <see cref="SemanticIndexTriggerStatus.AlreadyRunning" />
    ///     if a run for this root is already in flight.
    /// </summary>
    public SemanticIndexTriggerStatus StartManual(string workspaceRoot, string[]? extensions, int concurrency)
    {
        if (!_running.TryAdd(workspaceRoot, 0))
        {
            return SemanticIndexTriggerStatus.AlreadyRunning;
        }

        _ = _lastError.TryRemove(workspaceRoot, out _);
        _ = RunManualInBackgroundAsync(workspaceRoot, extensions, concurrency);
        return SemanticIndexTriggerStatus.Started;
    }

    /// <summary>Number of chunk rows stored for this root; 0 means the index has never been built.</summary>
    public async Task<int> CountChunksAsync(string workspaceRoot, CancellationToken ct)
    {
        WorkspaceIndexContext ctx = await db.GetDbAsync(workspaceRoot, ct);
        return await db.WithDbLockAsync(() => ctx.Chunks.CountAsync(ct), ct);
    }

    /// <summary>
    ///     Chunk count and the newest chunk write time (Unix ms, from the database so it survives a
    ///     restart); both are null-valued when the index is empty.
    /// </summary>
    public async Task<(int Count, long? LastUpdatedMs)> ChunkSummaryAsync(string workspaceRoot, CancellationToken ct)
    {
        WorkspaceIndexContext ctx = await db.GetDbAsync(workspaceRoot, ct);
        return await db.WithDbLockAsync(async () =>
        {
            int count = await ctx.Chunks.CountAsync(ct);
            long? last = count == 0 ? null : await ctx.Chunks.MaxAsync(c => (long?)c.UpdatedAt, ct);
            return (count, last);
        }, ct);
    }

    /// <summary>True while a background run (manual or first-run) is in flight for this root.</summary>
    public bool IsRunning(string workspaceRoot) => _running.ContainsKey(workspaceRoot);

    /// <summary>Stats from the most recent completed run for this root, if any.</summary>
    public bool TryGetLastStats(string workspaceRoot, out RepoIndexStats? stats)
    {
        bool found = _lastStats.TryGetValue(workspaceRoot, out RepoIndexStats? value);
        stats = value;
        return found;
    }

    /// <summary>Error message from the most recent failed run for this root, if any.</summary>
    public string? GetLastError(string workspaceRoot) => _lastError.TryGetValue(workspaceRoot, out string? message) ? message : null;

    private async Task RunManualInBackgroundAsync(string workspaceRoot, string[]? extensions, int concurrency)
    {
        LogStarted(logger, workspaceRoot, "manual run");
        try
        {
            RepoIndexStats stats = await indexer.IndexRepoAsync(workspaceRoot, extensions, concurrency, CancellationToken.None);
            if (stats.ChunksIndexed > 0 || stats.ChunksSkipped > 0)
            {
                _knownIndexed[workspaceRoot] = 0;
            }

            RecordCompleted(workspaceRoot, stats);
        }
        catch (Exception ex)
        {
            RecordFailed(workspaceRoot, ex);
        }
        finally
        {
            _ = _running.TryRemove(workspaceRoot, out _);
        }
    }

    private async Task RunInBackgroundAsync(string workspaceRoot, string reason, CancellationToken ct)
    {
        LogStarted(logger, workspaceRoot, reason);
        try
        {
            RepoIndexStats stats = await indexer.IndexRepoAsync(workspaceRoot, ct: ct);
            if (stats.ChunksIndexed > 0)
            {
                _knownIndexed[workspaceRoot] = 0;
            }

            RecordCompleted(workspaceRoot, stats);
        }
        catch (Exception ex)
        {
            RecordFailed(workspaceRoot, ex);
        }
        finally
        {
            _ = _running.TryRemove(workspaceRoot, out _);
        }
    }

    /// <summary>Stores the run's stats for IndexStatus and writes the completion line.</summary>
    private void RecordCompleted(string workspaceRoot, RepoIndexStats stats)
    {
        _lastStats[workspaceRoot] = stats;
        _ = _lastError.TryRemove(workspaceRoot, out _);
        LogCompleted(logger, workspaceRoot, stats.FilesScanned, stats.ChunksIndexed, stats.ChunksSkipped,
            stats.ChunksDeleted, stats.ChunksFailed, stats.Elapsed.TotalSeconds);
    }

    /// <summary>Stores the failure for IndexStatus and writes the warning line.</summary>
    private void RecordFailed(string workspaceRoot, Exception ex)
    {
        _lastError[workspaceRoot] = ex.Message;
        LogFailed(logger, workspaceRoot, ex);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Background semantic index for '{WorkspaceRoot}' started ({Reason}).")]
    private static partial void LogStarted(ILogger logger, string workspaceRoot, string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Background semantic index for '{WorkspaceRoot}' complete: {Files} files, {Indexed} chunks embedded, {Skipped} unchanged, {Deleted} stale removed, {Failed} failed, {Seconds:F1}s.")]
    private static partial void LogCompleted(ILogger logger, string workspaceRoot, int files, int indexed, int skipped,
        int deleted, int failed, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background semantic index for '{WorkspaceRoot}' failed.")]
    private static partial void LogFailed(ILogger logger, string workspaceRoot, Exception ex);
}

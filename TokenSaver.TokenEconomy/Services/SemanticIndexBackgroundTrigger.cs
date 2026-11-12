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
    private readonly ConcurrentDictionary<string, byte> _needsRebuild = new(StringComparer.OrdinalIgnoreCase);

    // Cancellation source of the in-flight run per root, so a forced reset can stop it.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ForceStopWait = TimeSpan.FromSeconds(60);

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

        // Not the request's `ct`, deliberately: this task must outlive the request that spawned
        // it. See the class doc comment for why sharing `ct` here would be wrong. The run's own
        // source is cancelled only by a forced reset.
        CancellationTokenSource cts = new();
        _cts[workspaceRoot] = cts;
        _ = RunInBackgroundAsync(workspaceRoot, "incremental run on connect", cts.Token);
        return Task.FromResult(SemanticIndexTriggerStatus.Started);
    }

    /// <summary>
    ///     Forced clean rebuild: cancels any in-flight run for this root, waits for it to stop,
    ///     deletes every stored chunk, then starts a fresh manual run. Returns
    ///     <see cref="SemanticIndexTriggerStatus.AlreadyRunning" /> if the old run did not stop in time.
    /// </summary>
    public async Task<SemanticIndexTriggerStatus> ForceRestartAsync(
        string workspaceRoot, string[]? extensions, int concurrency, CancellationToken ct)
    {
        if (_cts.TryGetValue(workspaceRoot, out CancellationTokenSource? running))
        {
            try
            {
                await running.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The run just finished on its own.
            }
        }

        DateTime deadline = DateTime.UtcNow + ForceStopWait;
        while (_running.ContainsKey(workspaceRoot))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return SemanticIndexTriggerStatus.AlreadyRunning;
            }

            await Task.Delay(100, ct);
        }

        WorkspaceIndexContext ctx = await db.GetDbAsync(workspaceRoot, ct);
        if (!await ChunkSchemaRecovery.HasCurrentChunkSchemaAsync(ctx, ct))
        {
            // Stored Chunks table predates the current columns: back up, then recreate it so the rebuild can run.
            _ = await ChunkSchemaRecovery.BackupAndRecreateChunksAsync(ctx, ct);
        }

        _ = await db.WithDbLockAsync(() => ctx.Chunks.ExecuteDeleteAsync(ct), ct);

        _ = _knownIndexed.TryRemove(workspaceRoot, out _);
        _ = _lastStats.TryRemove(workspaceRoot, out _);
        _ = _lastError.TryRemove(workspaceRoot, out _);
        LogReset(logger, workspaceRoot);

        return StartManual(workspaceRoot, extensions, concurrency);
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
        CancellationTokenSource cts = new();
        _cts[workspaceRoot] = cts;
        _ = RunManualInBackgroundAsync(workspaceRoot, extensions, concurrency, cts.Token);
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

    /// <summary>True when the last run failed because the stored index schema is older than the code (a rebuild is needed).</summary>
    public bool NeedsRebuild(string workspaceRoot) => _needsRebuild.ContainsKey(workspaceRoot);

    private async Task RunManualInBackgroundAsync(
        string workspaceRoot, string[]? extensions, int concurrency, CancellationToken ct)
    {
        LogStarted(logger, workspaceRoot, "manual run");
        try
        {
            RepoIndexStats stats = await indexer.IndexRepoAsync(workspaceRoot, extensions, concurrency, ct);
            if (stats.ChunksIndexed > 0 || stats.ChunksSkipped > 0)
            {
                _knownIndexed[workspaceRoot] = 0;
            }

            RecordCompleted(workspaceRoot, stats);
        }
        catch (OperationCanceledException)
        {
            LogCancelled(logger, workspaceRoot);
        }
        catch (Exception ex)
        {
            RecordFailed(workspaceRoot, ex);
        }
        finally
        {
            FinishRun(workspaceRoot);
        }
    }

    /// <summary>Releases the run's cancellation source and clears its running flag (in that order).</summary>
    private void FinishRun(string workspaceRoot)
    {
        if (_cts.TryRemove(workspaceRoot, out CancellationTokenSource? cts))
        {
            cts.Dispose();
        }

        _ = _running.TryRemove(workspaceRoot, out _);
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
        catch (OperationCanceledException)
        {
            LogCancelled(logger, workspaceRoot);
        }
        catch (Exception ex)
        {
            RecordFailed(workspaceRoot, ex);
        }
        finally
        {
            FinishRun(workspaceRoot);
        }
    }

    /// <summary>Stores the run's stats for IndexStatus and writes the completion line.</summary>
    private void RecordCompleted(string workspaceRoot, RepoIndexStats stats)
    {
        _lastStats[workspaceRoot] = stats;
        _ = _lastError.TryRemove(workspaceRoot, out _);
        _ = _needsRebuild.TryRemove(workspaceRoot, out _);
        LogCompleted(logger, workspaceRoot, stats.FilesScanned, stats.ChunksIndexed, stats.ChunksSkipped,
            stats.ChunksDeleted, stats.ChunksFailed, stats.Elapsed.TotalSeconds);
    }

    /// <summary>Stores the failure for IndexStatus and writes the warning line.</summary>
    private void RecordFailed(string workspaceRoot, Exception ex)
    {
        _lastError[workspaceRoot] = ex.Message;
        if (ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
        {
            _needsRebuild[workspaceRoot] = 0;
        }
        LogFailed(logger, workspaceRoot, ex);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Background semantic index for '{WorkspaceRoot}' started ({Reason}).")]
    private static partial void LogStarted(ILogger logger, string workspaceRoot, string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Background semantic index for '{WorkspaceRoot}' complete: {Files} files, {Indexed} chunks embedded, {Skipped} unchanged, {Deleted} stale removed, {Failed} failed, {Seconds:F1}s.")]
    private static partial void LogCompleted(ILogger logger, string workspaceRoot, int files, int indexed, int skipped,
        int deleted, int failed, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Background semantic index for '{WorkspaceRoot}' cancelled.")]
    private static partial void LogCancelled(ILogger logger, string workspaceRoot);

    [LoggerMessage(Level = LogLevel.Information, Message = "Semantic index for '{WorkspaceRoot}' reset: all chunks deleted (forced rebuild).")]
    private static partial void LogReset(ILogger logger, string workspaceRoot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background semantic index for '{WorkspaceRoot}' failed.")]
    private static partial void LogFailed(ILogger logger, string workspaceRoot, Exception ex);
}

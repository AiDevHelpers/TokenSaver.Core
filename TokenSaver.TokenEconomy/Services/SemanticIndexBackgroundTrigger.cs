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

    public async Task<SemanticIndexTriggerStatus> EnsureStartedAsync(string workspaceRoot, CancellationToken ct)
    {
        if (_knownIndexed.ContainsKey(workspaceRoot))
        {
            return SemanticIndexTriggerStatus.AlreadyIndexed;
        }

        if (!_running.TryAdd(workspaceRoot, 0))
        {
            return SemanticIndexTriggerStatus.AlreadyRunning;
        }

        bool hasChunks;
        try
        {
            WorkspaceIndexContext ctx = await db.GetDbAsync(workspaceRoot, ct);
            hasChunks = await db.WithDbLockAsync(() => ctx.Chunks.AnyAsync(ct), ct);
        }
        catch
        {
            _ = _running.TryRemove(workspaceRoot, out _);
            throw;
        }

        if (hasChunks)
        {
            _knownIndexed[workspaceRoot] = 0;
            _ = _running.TryRemove(workspaceRoot, out _);
            return SemanticIndexTriggerStatus.AlreadyIndexed;
        }

        // CancellationToken.None, deliberately: this task must outlive the request that spawned
        // it. See the class doc comment for why sharing `ct` here would be wrong.
        _ = RunInBackgroundAsync(workspaceRoot, CancellationToken.None);
        return SemanticIndexTriggerStatus.Started;
    }

    private async Task RunInBackgroundAsync(string workspaceRoot, CancellationToken ct)
    {
        try
        {
            RepoIndexStats stats = await indexer.IndexRepoAsync(workspaceRoot, ct: ct);
            if (stats.ChunksIndexed > 0)
            {
                _knownIndexed[workspaceRoot] = 0;
            }

            LogCompleted(logger, workspaceRoot, stats.FilesScanned, stats.ChunksIndexed);
        }
        catch (Exception ex)
        {
            LogFailed(logger, workspaceRoot, ex);
        }
        finally
        {
            _ = _running.TryRemove(workspaceRoot, out _);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Background semantic index for '{WorkspaceRoot}' complete: {Files} files, {Chunks} chunks.")]
    private static partial void LogCompleted(ILogger logger, string workspaceRoot, int files, int chunks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background semantic index for '{WorkspaceRoot}' failed.")]
    private static partial void LogFailed(ILogger logger, string workspaceRoot, Exception ex);
}

using TokenSaver.Database.Context;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Shared DB-access seam for the split WorkspaceIndexService: owns one cached
///     <see cref="WorkspaceIndexContext" /> per workspace root (a process can legitimately be
///     asked about more than one root — e.g. an internal warm-up call against the default root
///     racing an explicit caller-supplied <c>workspaceRoot</c> for a different one) plus the lock
///     that serializes all access to it (EF Core's DbContext isn't thread-safe). Every other
///     extracted indexing service depends on this and calls <see cref="GetDbAsync" />; DB
///     reads/writes go through <see cref="WithDbLockAsync{T}(Func{Task{T}},CancellationToken)" />
///     (or the void overload) instead of touching a raw semaphore field directly.
///     Previously this cached a single context/root pair and disposed+replaced it whenever a
///     different root came in — safe only as long as nothing else held a reference to the old
///     context. Once two roots were in flight close together, the swap disposed a context a
///     concurrent caller was still using mid-walk, throwing ObjectDisposedException (observed
///     directly: FolderTreeManager.RefreshTouchedFolderDescriptionsAsync failing this way while
///     WorkspaceIndexService.IndexWorkspaceAsync was still iterating touched files for the root
///     that got swapped out). Each root now keeps its own entry for the accessor's lifetime
///     instead — see TokenSaver.Database.Context.TokenEconomyDbAccessor for the identical fix
///     applied to its sibling.
/// </summary>
public sealed partial class WorkspaceIndexDbAccessor : IDisposable
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} workspace path(s) not covered by .gitignore{Flag}: {Entries}. Run ContextDiag(action='gitignore') for details and to add missing entries.")]
    private static partial void LogGitignoreGaps(ILogger logger, int count, string flag, string entries);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Gitignore scan failed")]
    private static partial void LogGitignoreScanFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to open workspace index DB for '{Root}': {Msg}")]
    private static partial void LogDbOpenFailed(ILogger logger, string root, string msg, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Detected corrupt/mismatched workspace index at '{Root}' — deleting and rebuilding")]
    private static partial void LogIndexRebuild(ILogger logger, string root);

    // Guards all WorkspaceIndexContext access — EF Core's DbContext isn't thread-safe.
    // File I/O, parsing, and AI-summary calls happen OUTSIDE this lock (in parallel);
    // only the actual DB read/write is serialized. Never acquire this lock re-entrantly.
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly SemaphoreSlim _ctxOpenLock = new(1, 1);
    private readonly ILogger<WorkspaceIndexDbAccessor> _logger;
    private readonly ConcurrentDictionary<string, WorkspaceIndexContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _gitignoreWarnedRoots = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceIndexDbAccessor(ILogger<WorkspaceIndexDbAccessor> logger)
    {
        _logger = logger;
    }

    public void Dispose()
    {
        foreach (WorkspaceIndexContext ctx in _contexts.Values)
        {
            ctx.Dispose();
        }

        _ctxOpenLock.Dispose();
        _dbLock.Dispose();
    }

    public async Task<WorkspaceIndexContext> GetDbAsync(string workspaceRoot, CancellationToken ct)
    {
        if (_contexts.TryGetValue(workspaceRoot, out WorkspaceIndexContext? existing))
        {
            return existing;
        }

        await _ctxOpenLock.WaitAsync(ct);
        try
        {
            if (_contexts.TryGetValue(workspaceRoot, out WorkspaceIndexContext? existingAfterLock))
            {
                return existingAfterLock;
            }

            WorkspaceIndexContext ctx;
            try
            {
                ctx = await WorkspaceIndexContextFactory.CreateAsync(workspaceRoot, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Attempt one auto-recovery: delete the corrupt index and rebuild.
                // This handles stale schema, corruption, and WAL locks left by crashed processes.
                LogIndexRebuild(_logger, workspaceRoot);
                try
                {
                    DeleteIndexFiles(workspaceRoot);
                    ctx = await WorkspaceIndexContextFactory.CreateAsync(workspaceRoot, ct);
                }
                catch (Exception retryEx) when (retryEx is not OperationCanceledException)
                {
                    // Recovery failed — log both the original and retry exceptions and give up.
                    LogDbOpenFailed(_logger, workspaceRoot, ex.Message, ex);
                    LogDbOpenFailed(_logger, workspaceRoot, retryEx.Message, retryEx);
                    throw new InvalidOperationException(
                        $"Failed to open workspace index DB at '{WorkspaceIndexContextFactory.GetDbPath(workspaceRoot)}': {ex.Message}. " +
                        "Auto-recovery (delete and rebuild) also failed: " + retryEx.Message, retryEx);
                }
            }

            _contexts[workspaceRoot] = ctx;
            WarnIfGitignoreMissing(workspaceRoot);
            return ctx;
        }
        finally
        {
            _ = _ctxOpenLock.Release();
        }
    }

    /// <summary>
    ///     Runs <paramref name="action" /> while holding the DB lock, returning its result.
    ///     Preferred wrapper for all DB reads/writes instead of acquiring the raw semaphore
    ///     directly — keeps lock acquire/release symmetric across every extracted service.
    /// </summary>
    public async Task<T> WithDbLockAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            _ = _dbLock.Release();
        }
    }

    /// <summary>Void-returning overload of <see cref="WithDbLockAsync{T}(Func{Task{T}},CancellationToken)" />.</summary>
    public async Task WithDbLockAsync(Func<Task> action, CancellationToken ct)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            _ = _dbLock.Release();
        }
    }

    public static string ComputeHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static void DeleteIndexFiles(string workspaceRoot)
    {
        string dbPath = WorkspaceIndexContextFactory.GetDbPath(workspaceRoot);
        string dir = Path.GetDirectoryName(dbPath) ?? ".tokensaver";
        string baseName = Path.GetFileName(dbPath);

        // Delete index.db and its WAL/SHM sidecar files that SQLite creates in WAL mode.
        foreach (string pattern in new[] { baseName, $"{baseName}-wal", $"{baseName}-shm" })
        {
            string fullPath = Path.Combine(dir, pattern);
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch
            {
                // Silently ignore deletion failures — the next CreateAsync will report
                // if the file still can't be opened.
            }
        }
    }

    private void WarnIfGitignoreMissing(string workspaceRoot)
    {
        if (!_gitignoreWarnedRoots.TryAdd(workspaceRoot, 0))
        {
            return;
        }

        try
        {
            IReadOnlyList<GitignoreIssue> issues = GitignoreScanner.Scan(workspaceRoot);
            if (issues.Count == 0)
            {
                return;
            }

            string secretsFlag = issues.Any(i => i.ContainsSecrets) ? " (may contain secrets)" : "";
            LogGitignoreGaps(_logger, issues.Count, secretsFlag, string.Join(", ", issues.Select(i => i.Entry)));
        }
        catch (Exception ex)
        {
            LogGitignoreScanFailed(_logger, ex);
        }
    }
}

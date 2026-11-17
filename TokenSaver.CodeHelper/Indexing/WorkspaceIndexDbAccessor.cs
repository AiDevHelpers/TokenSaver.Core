using TokenSaver.Database.Context;
using TokenSaver.Utilities.Services;

using Microsoft.Data.Sqlite;
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Workspace index at '{Root}' declared corrupt (SQLite error {SqliteCode}: {Message}) — deleting and rebuilding")]
    private static partial void LogIndexRebuild(ILogger logger, string root, int sqliteCode, string message, Exception ex);

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
        // One context per workspace, however the root was spelled (trailing separator, relative path).
        workspaceRoot = WorkspaceIndexContextFactory.NormalizeRoot(workspaceRoot);
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
            catch (SqliteException ex) when (IsCorruptDatabase(ex))
            {
                // Only a corrupt or unreadable file is deleted and rebuilt. Any other open failure
                // (locks, I/O) propagates unchanged so existing chunk embeddings are not discarded.
                LogIndexRebuild(_logger, workspaceRoot, ex.SqliteErrorCode, ex.Message, ex);
                DeleteIndexFiles(workspaceRoot);
                ctx = await WorkspaceIndexContextFactory.CreateAsync(workspaceRoot, ct);
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

    // SQLITE_CORRUPT (11) and SQLITE_NOTADB (26): the file exists but is not a valid database.
    private static bool IsCorruptDatabase(SqliteException ex) => ex.SqliteErrorCode is 11 or 26;

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not delete index file '{Path}' while rebuilding a corrupt index")]
    private static partial void LogIndexFileDeleteFailed(ILogger logger, string path, Exception ex);

    private void DeleteIndexFiles(string workspaceRoot)
    {
        // Microsoft.Data.Sqlite pools connections: a disposed context can still hold the file open
        // through the pool, which blocks deletion on Windows. Release the pool before deleting.
        SqliteConnection.ClearAllPools();

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
            catch (Exception ex)
            {
                // Logged at Error so a stuck (locked) corrupt index is visible. The retry
                // CreateAsync still runs and surfaces the SQLite error if the file remains.
                LogIndexFileDeleteFailed(_logger, fullPath, ex);
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

using TokenSaver.Database.Context;
using TokenSaver.Library.Services;

using Microsoft.Extensions.Logging;

using System.Security.Cryptography;
using System.Text;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Shared DB-access seam for the split WorkspaceIndexService: owns the single cached
///     <see cref="WorkspaceIndexContext" /> plus the lock that serializes all access to it
///     (EF Core's DbContext isn't thread-safe). Every other extracted indexing service depends
///     on this and calls <see cref="GetDbAsync" />; DB reads/writes go through
///     <see cref="WithDbLockAsync{T}(Func{Task{T}},CancellationToken)" /> (or the void overload)
///     instead of touching a raw semaphore field directly.
/// </summary>
public sealed partial class WorkspaceIndexDbAccessor : IDisposable
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} workspace path(s) not covered by .gitignore{Flag}: {Entries}. Run ContextDiag(action='gitignore') for details and to add missing entries.")]
    private static partial void LogGitignoreGaps(ILogger logger, int count, string flag, string entries);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Gitignore scan failed")]
    private static partial void LogGitignoreScanFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to open workspace index DB for '{Root}': {Msg}")]
    private static partial void LogDbOpenFailed(ILogger logger, string root, string msg, Exception ex);

    // Guards all WorkspaceIndexContext access — EF Core's DbContext isn't thread-safe.
    // File I/O, parsing, and AI-summary calls happen OUTSIDE this lock (in parallel);
    // only the actual DB read/write is serialized. Never acquire this lock re-entrantly.
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly SemaphoreSlim _ctxOpenLock = new(1, 1);
    private readonly ILogger<WorkspaceIndexDbAccessor> _logger;
    private WorkspaceIndexContext? _ctx;
    private string? _ctxWorkspaceRoot;
    private bool _gitignoreWarned;

    public WorkspaceIndexDbAccessor(ILogger<WorkspaceIndexDbAccessor> logger)
    {
        _logger = logger;
    }

    public void Dispose()
    {
        _ctx?.Dispose();
        _ctxOpenLock.Dispose();
        _dbLock.Dispose();
    }

    public async Task<WorkspaceIndexContext> GetDbAsync(string workspaceRoot, CancellationToken ct)
    {
        if (_ctx != null && _ctxWorkspaceRoot == workspaceRoot)
        {
            return _ctx;
        }

        await _ctxOpenLock.WaitAsync(ct);
        try
        {
            if (_ctx != null && _ctxWorkspaceRoot == workspaceRoot)
            {
                return _ctx;
            }

            if (_ctx is not null)
            {
                await _ctx.DisposeAsync();
                _ctx = null;
                _ctxWorkspaceRoot = null;
            }

            try
            {
                _ctx = await WorkspaceIndexContextFactory.CreateAsync(workspaceRoot, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Surfaced all the way up through every indexing tool (CodeIndex, SemanticGrep,
                // ManagePlan/CodeWork's PlanBuilder, ContextDiag) as an opaque "An error occurred
                // invoking '<tool>'" with zero diagnostic info otherwise — a stale/mismatched
                // schema or a locked index.db (WAL files left by another process) both throw here
                // unguarded. Log the real exception and rethrow a message callers can act on.
                LogDbOpenFailed(_logger, workspaceRoot, ex.Message, ex);
                throw new InvalidOperationException(
                    $"Failed to open workspace index DB at '{WorkspaceIndexContextFactory.GetDbPath(workspaceRoot)}': {ex.Message}. " +
                    "The index.db may be locked by another process or have a mismatched schema — " +
                    "try deleting the .tokensaver/index.db* files to force a rebuild.", ex);
            }

            _ctxWorkspaceRoot = workspaceRoot;
            WarnIfGitignoreMissing(workspaceRoot);
            return _ctx;
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

    private void WarnIfGitignoreMissing(string workspaceRoot)
    {
        if (_gitignoreWarned)
        {
            return;
        }

        _gitignoreWarned = true;

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

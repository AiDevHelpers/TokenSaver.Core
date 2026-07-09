using TokenSaver.Config;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.Library.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Git-aware staleness resolution (§2 of the design doc): figures out which workspace-relative
///     files need reindexing without a full filesystem walk when a git baseline exists, and reports
///     files whose on-disk mtime has moved past what's indexed.
/// </summary>
public sealed class GitStalenessService
{
    private static readonly HashSet<string> AlwaysSkipDirs =
        new(StringComparer.OrdinalIgnoreCase) { ".git", ".tokensaver", "node_modules", "bin", "obj", "dist", "out" };

    private readonly WorkspaceIndexDbAccessor _accessor;
    private readonly GitStalenessChecker _gitStaleness;
    private readonly ILogger<GitStalenessService> _logger;

    public GitStalenessService(
        WorkspaceIndexDbAccessor accessor,
        GitStalenessChecker gitStaleness,
        ILogger<GitStalenessService> logger)
    {
        _accessor = accessor;
        _gitStaleness = gitStaleness;
        _logger = logger;
    }

    /// <summary>
    ///     Returns the exact workspace-relative files that need reindexing, or null if there's no
    ///     git anywhere under the workspace (caller should do a full walk — mtime is the only signal
    ///     available, and IndexOneFileAsync's own LastModified check handles that per-file).
    /// </summary>
    public async Task<List<string>?> ResolveTargetFilesAsync(WorkspaceIndexContext ctx, string root,
        CancellationToken ct)
    {
        List<string> repoRoots = _gitStaleness.DiscoverRepoRoots(root).ToList();
        if (repoRoots.Count == 0)
        {
            return null;
        }

        HashSet<string> toReindex = new(StringComparer.OrdinalIgnoreCase);
        List<string> fullRescanRoots = [];

        foreach (string repoRelPath in repoRoots)
        {
            RepoStalenessResult result = await _gitStaleness.CheckAsync(ctx, root, repoRelPath, ct);
            if (result.Unchanged)
            {
                continue;
            }

            if (result.IsFirstRun)
            {
                fullRescanRoots.Add(repoRelPath);
                continue;
            }

            foreach (string f in result.ChangedFiles)
            {
                _ = toReindex.Add(f);
            }
        }

        foreach (string repoRelPath in fullRescanRoots)
        {
            string dir = repoRelPath == "." ? root : Path.Combine(root, repoRelPath);
            foreach (string file in EnumerateAllFiles(dir, root))
            {
                _ = toReindex.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        return toReindex.Count > 0 ? toReindex.ToList() : [];
    }

    /// <summary>
    ///     Full-tree walk honoring .gitignore/.ignore-additional/AlwaysSkipDirs. Shared with the
    ///     orchestrator's bootstrap indexing path (see WorkspaceIndexService.IndexWorkspaceAsync).
    /// </summary>
    public static IEnumerable<string> EnumerateAllFiles(string startDir, string workspaceRoot)
    {
        WorkspaceIgnoreFilter ignore = WorkspaceIgnoreFilter.Load(workspaceRoot);
        Queue<string> queue = new();
        queue.Enqueue(startDir);

        while (queue.Count > 0)
        {
            string dir = queue.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch
            {
                continue;
            }

            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                if (AlwaysSkipDirs.Contains(name))
                {
                    continue;
                }

                string relPath = Path.GetRelativePath(workspaceRoot, entry).Replace('\\', '/');

                if (Directory.Exists(entry))
                {
                    if (ignore.IsIgnored(relPath, true))
                    {
                        continue;
                    }

                    queue.Enqueue(entry);
                }
                else
                {
                    if (ignore.IsIgnored(relPath, false))
                    {
                        continue;
                    }

                    yield return entry;
                }
            }
        }
    }

    public async Task<string[]> GetStaleFilesAsync(string root, CancellationToken ct)
    {
        WorkspaceIndexContext ctx = await _accessor.GetDbAsync(root, ct);
        List<FileEntity> rows = await _accessor.WithDbLockAsync(
            () => ctx.Files.AsNoTracking().ToListAsync(ct), ct);

        List<string> stale = [];
        foreach (FileEntity row in rows)
        {
            string abs = Path.Combine(root, row.RelativePath);
            if (!File.Exists(abs))
            {
                continue;
            }

            try
            {
                long writeMs = new DateTimeOffset(File.GetLastWriteTimeUtc(abs)).ToUnixTimeMilliseconds();
                if (writeMs > row.LastModified)
                {
                    stale.Add(row.RelativePath);
                }
            }
            catch
            {
                /* transient FS error — skip */
            }
        }

        return stale.ToArray();
    }
}

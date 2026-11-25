#pragma warning disable CA1034, CA1002 // StalenessPlan is the public result record of PlanTargetFilesAsync; its list is owned by the plan.
using AiDevTools.TokenSaver.Config;
using AiDevTools.TokenSaver.Database.Context;
using AiDevTools.TokenSaver.Database.Context.Entities;
using AiDevTools.TokenSaver.Utilities.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiDevTools.TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Git-aware staleness resolution (§2 of the design doc): figures out which workspace-relative
///     files need reindexing without a full filesystem walk when a git baseline exists, and reports
///     files whose on-disk mtime has moved past what's indexed.
/// </summary>
public sealed class GitStalenessService
{

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
        StalenessPlan plan = await PlanTargetFilesAsync(ctx, root, ct);
        await CommitBaselinesAsync(ctx, plan, ct);
        return plan.Targets;
    }

    /// <summary>The files to index plus the per-repo baselines to persist once indexing succeeds.</summary>
    public sealed record StalenessPlan(List<string>? Targets, IReadOnlyList<RepoStalenessResult> Checked);

    /// <summary>
    ///     Plans the reindex without persisting any git baseline. Pair with
    ///     <see cref="CommitBaselinesAsync" /> after the planned files are indexed; a run that fails
    ///     or is cancelled then leaves the baseline behind, so the same changes are retried.
    /// </summary>
    public async Task<StalenessPlan> PlanTargetFilesAsync(WorkspaceIndexContext ctx, string root,
        CancellationToken ct)
    {
        List<string> repoRoots = _gitStaleness.DiscoverRepoRoots(root).ToList();
        if (repoRoots.Count == 0)
        {
            return new StalenessPlan(null, []);
        }

        HashSet<string> toReindex = new(StringComparer.OrdinalIgnoreCase);
        List<string> fullRescanRoots = [];
        List<RepoStalenessResult> checkedRoots = [];

        foreach (string repoRelPath in repoRoots)
        {
            RepoStalenessResult result = await _gitStaleness.CheckAsync(ctx, root, repoRelPath, commitBaseline: false, ct: ct);
            checkedRoots.Add(result);
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

        return new StalenessPlan(toReindex.Count > 0 ? toReindex.ToList() : [], checkedRoots);
    }

    /// <summary>Persists each checked repo's baseline. Call only after the planned files are indexed.</summary>
    public async Task CommitBaselinesAsync(WorkspaceIndexContext ctx, StalenessPlan plan, CancellationToken ct)
    {
        foreach (RepoStalenessResult r in plan.Checked)
        {
            if (r.HeadSha != null)
            {
                await _gitStaleness.CommitBaselineAsync(ctx, r.RepoRelPath, r.HeadSha, r.IsDirty, ct);
            }
        }
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
                if (AlwaysSkipDirectories.Contains(name))
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
}

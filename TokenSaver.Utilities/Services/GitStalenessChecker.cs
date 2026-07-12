using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;

using Microsoft.Extensions.Logging;

using System.Diagnostics;

namespace TokenSaver.Utilities.Services;

/// <summary>
///     Result of a staleness check for one repo root. <see cref="IsFirstRun" /> and
///     <see cref="Unchanged" /> are mutually exclusive with a non-empty <see cref="ChangedFiles" />:
///     first run or "git unavailable" → caller should do a full scan under this repo root;
///     unchanged → skip entirely; otherwise → only re-index <see cref="ChangedFiles" />.
/// </summary>
public sealed record RepoStalenessResult
{
    public required string RepoRelPath { get; init; }
    public required bool IsFirstRun { get; init; }
    public required bool Unchanged { get; init; }
    public required IReadOnlyList<string> ChangedFiles { get; init; }
}

/// <summary>
///     Detects which files changed since the last index run using git as the staleness source of
///     truth (HEAD sha + working-tree dirty state) instead of walking and hashing every file on
///     every startup. Falls back to <see cref="RepoStalenessResult.IsFirstRun" /> semantics when git
///     isn't available or the folder isn't a repo — callers should then fall back to comparing each
///     tracked file's last-write-time against its stored value.
/// </summary>
public sealed partial class GitStalenessChecker(ILogger<GitStalenessChecker> logger)
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Staleness check for repo '{Repo}': firstRun={FirstRun} unchanged={Unchanged} changedFiles={Count}")]
    private static partial void LogStalenessCheckResult(ILogger logger, string repo, bool firstRun, bool unchanged, int count);

    private static readonly HashSet<string> SkipWalk =
        new(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", "dist", "out", ".git" };

    /// <summary>
    ///     Finds every git repo root under the workspace: the workspace root itself (if it is one)
    ///     plus any nested/vendored repos (monorepo submodules, etc.). Returns workspace-relative
    ///     paths, "." for the workspace root.
    /// </summary>
    public IEnumerable<string> DiscoverRepoRoots(string workspaceRoot)
    {
        if (IsGitRepoRoot(workspaceRoot))
        {
            yield return ".";
        }

        Queue<string> queue = new();
        queue.Enqueue(workspaceRoot);
        while (queue.Count > 0)
        {
            string dir = queue.Dequeue();
            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (string sub in subdirs)
            {
                string name = Path.GetFileName(sub);
                if (SkipWalk.Contains(name))
                {
                    continue;
                }

                if (sub != workspaceRoot && IsGitRepoRoot(sub))
                {
                    yield return Path.GetRelativePath(workspaceRoot, sub).Replace('\\', '/');
                }

                queue.Enqueue(sub);
            }
        }
    }

    private static bool IsGitRepoRoot(string dir)
    {
        return File.Exists(Path.Combine(dir, ".git")) || Directory.Exists(Path.Combine(dir, ".git"));
    }

    /// <summary>
    ///     Compares the repo's current HEAD sha + dirty state against the stored
    ///     <see cref="RepoStalenessEntity" /> row, computes the exact changed-file set when
    ///     something differs, and persists the new sha/dirty/CheckedAt as a side effect.
    /// </summary>
    public async Task<RepoStalenessResult> CheckAsync(
        WorkspaceIndexContext ctx, string workspaceRoot, string repoRelPath, CancellationToken ct = default)
    {
        string repoDir = repoRelPath == "." ? workspaceRoot : Path.Combine(workspaceRoot, repoRelPath);

        string? headShaRaw = await RunGitAsync(repoDir, "rev-parse HEAD", ct);
        if (headShaRaw == null)
        {
            // git missing / not resolvable — caller falls back to mtime-based staleness.
            return new RepoStalenessResult
            { RepoRelPath = repoRelPath, IsFirstRun = true, Unchanged = false, ChangedFiles = [] };
        }

        string headSha = headShaRaw.Trim();

        string statusOutput = await RunGitAsync(repoDir, "status --porcelain", ct) ?? "";
        bool isDirty = statusOutput.Trim().Length > 0;

        RepoStalenessEntity? existing = await ctx.RepoStaleness.FindAsync([repoRelPath], ct);

        HashSet<string> changedFiles = new(StringComparer.OrdinalIgnoreCase);
        bool isFirstRun = existing == null;
        bool unchanged = false;

        if (existing != null)
        {
            if (existing.LastHeadSha == headSha && !isDirty && !existing.LastDirty)
            {
                unchanged = true;
            }
            else if (existing.LastHeadSha != headSha)
            {
                string? diffOutput = await RunGitAsync(repoDir, $"diff --name-only {existing.LastHeadSha} {headSha}", ct);
                if (diffOutput != null)
                {
                    AddRepoRelativePaths(changedFiles, SplitLines(diffOutput), repoRelPath);
                }
                else
                {
                    isFirstRun =
                        true; // old sha no longer resolvable (rebase/gc/shallow clone) — safest is a full rescan
                }
            }
        }

        if (isDirty)
        {
            AddRepoRelativePaths(changedFiles, ParsePorcelainPaths(statusOutput), repoRelPath);
        }

        if (existing == null)
        {
            _ = ctx.RepoStaleness.Add(new RepoStalenessEntity
            {
                RepoRootPath = repoRelPath,
                LastHeadSha = headSha,
                LastDirty = isDirty,
                CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        else
        {
            existing.LastHeadSha = headSha;
            existing.LastDirty = isDirty;
            existing.CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        _ = await ctx.SaveChangesAsync(ct);

        LogStalenessCheckResult(logger, repoRelPath, isFirstRun, unchanged, changedFiles.Count);

        return new RepoStalenessResult
        {
            RepoRelPath = repoRelPath,
            IsFirstRun = isFirstRun,
            Unchanged = unchanged,
            ChangedFiles = changedFiles.ToArray()
        };
    }

    private static string[] SplitLines(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void AddRepoRelativePaths(HashSet<string> into, IEnumerable<string> gitRelativePaths,
        string repoRelPath)
    {
        foreach (string p in gitRelativePaths)
        {
            string clean = p.Trim();
            if (clean.Length == 0)
            {
                continue;
            }

            string workspaceRelative = repoRelPath == "." ? clean : $"{repoRelPath}/{clean}";
            _ = into.Add(workspaceRelative.Replace('\\', '/'));
        }
    }

    /// <summary>Parses `git status --porcelain` lines: "XY path" or "XY orig -> path" for renames.</summary>
    private static IEnumerable<string> ParsePorcelainPaths(string porcelainOutput)
    {
        foreach (string rawLine in SplitLines(porcelainOutput))
        {
            if (rawLine.Length < 4)
            {
                continue;
            }

            string pathPart = rawLine[3..].Trim();
            int arrowIdx = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIdx >= 0)
            {
                pathPart = pathPart[(arrowIdx + 4)..];
            }

            yield return pathPart.Trim('"');
        }
    }

    private static async Task<string?> RunGitAsync(string workingDir, string arguments, CancellationToken ct)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using Process? process = Process.Start(psi);
            if (process == null)
            {
                return null;
            }

            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(5));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }

                return null;
            }

            string output = await outputTask;
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}

using AiDevTools.TokenSaver.Database.Context;
using AiDevTools.TokenSaver.Database.Context.Entities;

using Microsoft.Extensions.Logging;

using System.Diagnostics;
using AiDevTools.TokenSaver.Common.Core.Processes;

namespace AiDevTools.TokenSaver.Utilities.Services;

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

    /// <summary>HEAD sha observed by the check; null when git could not resolve HEAD.</summary>
    public string? HeadSha { get; init; }

    /// <summary>Working tree had uncommitted changes at check time.</summary>
    public bool IsDirty { get; init; }
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
                if (AlwaysSkipDirectories.Contains(name))
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
    ///     <see cref="RepoStalenessEntity" /> row and computes the exact changed-file set when
    ///     something differs. Persists the new baseline only when <paramref name="commitBaseline" />
    ///     is true; callers that index the changed files should pass false and call
    ///     <see cref="CommitBaselineAsync" /> after indexing succeeds, so a failed run is retried.
    /// </summary>
    public async Task<RepoStalenessResult> CheckAsync(
        WorkspaceIndexContext ctx, string workspaceRoot, string repoRelPath,
        bool commitBaseline = true, CancellationToken ct = default)
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

        // --no-optional-locks: a status check must not rewrite .git/index, or the git watcher would
        // retrigger on its own output.
        string statusOutput = await RunGitAsync(repoDir, "--no-optional-locks status --porcelain", ct) ?? "";
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
                string? diffOutput = await RunGitAsync(repoDir, $"diff --name-status -M {existing.LastHeadSha} {headSha}", ct);
                if (diffOutput != null)
                {
                    AddRepoRelativePaths(changedFiles, ParseNameStatusPaths(diffOutput), repoRelPath);
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

        if (commitBaseline)
        {
            await CommitBaselineAsync(ctx, repoRelPath, headSha, isDirty, ct);
        }

        LogStalenessCheckResult(logger, repoRelPath, isFirstRun, unchanged, changedFiles.Count);

        return new RepoStalenessResult
        {
            RepoRelPath = repoRelPath,
            IsFirstRun = isFirstRun,
            Unchanged = unchanged,
            ChangedFiles = changedFiles.ToArray(),
            HeadSha = headSha,
            IsDirty = isDirty
        };
    }

    /// <summary>
    ///     Persists HEAD sha + dirty state as the repo's baseline. Call only after every file the
    ///     baseline covers has been indexed successfully, otherwise a failed run loses its changes.
    /// </summary>
    public async Task CommitBaselineAsync(
        WorkspaceIndexContext ctx, string repoRelPath, string headSha, bool isDirty, CancellationToken ct)
    {
        RepoStalenessEntity? existing = await ctx.RepoStaleness.FindAsync([repoRelPath], ct);
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
    }

    /// <summary>
    ///     Parses `git diff --name-status -M` lines. A rename ("R100 old new") contributes both paths,
    ///     so the old path is dropped from the index; a copy contributes both; everything else its path.
    /// </summary>
    private static string[] ParseNameStatusPaths(string output)
    {
        List<string> paths = [];
        foreach (string line in SplitLines(output))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            if ((parts[0].StartsWith('R') || parts[0].StartsWith('C')) && parts.Length >= 3)
            {
                paths.Add(parts[1]);
                paths.Add(parts[2]);
            }
            else
            {
                paths.Add(parts[1]);
            }
        }

        return paths.ToArray();
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
            CommandResult result = await CommandRunner.RunAsync(
                new CommandRequest
                {
                    FileName = "git",
                    Arguments = CommandLine.Split(arguments),
                    WorkingDirectory = workingDir,
                    Timeout = TimeSpan.FromSeconds(5),
                },
                ct);

            if (result.TimedOut)
            {
                Debug.WriteLine($"git {arguments} timed out after 5s in {workingDir}; treating as unavailable.");
            }

            return result.Success ? result.StdOut : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

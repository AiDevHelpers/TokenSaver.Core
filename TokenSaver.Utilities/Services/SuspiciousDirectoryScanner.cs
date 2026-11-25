namespace AiDevTools.TokenSaver.Utilities.Services;

/// <summary>How the path should be handled if the user agrees it is generated/cached.</summary>
public enum SuspiciousDirRecommendation
{
    /// <summary>Add to .gitignore (also excludes from context, since context honours gitignore).</summary>
    AddToGitignore,

    /// <summary>Exclude from AI context only; keep tracking in git (e.g. generated docs the team commits).</summary>
    ExcludeFromContext,

    /// <summary>Both: add to .gitignore AND to the context exclusion list.</summary>
    Both
}

/// <summary>A workspace directory that looks like a cache or build artefact but wasn't positively identified.</summary>
public sealed record SuspiciousDirectory(
    string RelativePath,
    string Reason,
    float Confidence, // 0.0 – 1.0
    SuspiciousDirRecommendation Recommendation);

/// <summary>
///     Scans a workspace for directories that look like caches or build outputs but are
///     not covered by the known-good list in <see cref="GitignoreScanner" /> (for workspace-root entries)
///     or by .gitignore.
///     <para>
///         Two complementary passes are used:
///         <list type="bullet">
///             <item>Heuristic pass — name patterns and content sampling, up to <c>maxDepth</c> levels deep.</item>
///             <item>
///                 Deep build-artifact pass — targeted BFS for known build output names (<c>bin</c>, <c>obj</c>, etc.)
///                 at any depth; uses the fixed gitignore pattern rules to determine coverage.
///             </item>
///         </list>
///     </para>
/// </summary>
public static class SuspiciousDirectoryScanner
{
    private const string CacheDirTagFile = "CACHEDIR.TAG";
    // ── Known-cache name tokens ────────────────────────────────────────────────

    /// <summary>Exact directory names that are highly likely to be caches/build output.</summary>
    private static readonly HashSet<string> HighConfidenceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Build artifacts — also in GitignoreScanner.Candidates; only flagged at depth > 0
        "bin", "obj", "artifacts", "publish",

        // Common cache dirs not in GitignoreScanner
        "htmlcov", "lcov-report", ".nyc_output", "storybook-static",
        ".tox", ".eggs", "coverage", ".cache",
        "__generated__", ".turbo", ".parcel-cache", ".webpack",
        ".rollup.cache", ".esbuild", ".fusebox", ".brunch",
        ".sass-cache", ".stylelint-cache", ".eslintcache",
        ".angular", ".svelte-kit"
    };

    /// <summary>
    ///     Names that are well-known build outputs but could also be intentional source dirs —
    ///     flagged at medium confidence.
    /// </summary>
    private static readonly HashSet<string> MediumConfidenceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "release", "debug", "packages", "out", "build",
        "Release", "Debug", "x64", "x86"
    };

    /// <summary>Substrings in a directory name that suggest it is a cache.</summary>
    private static readonly string[] CacheSubstrings =
    [
        "cache", "Cache", "-cache", "_cache",
        "generated", "Generated",
        ".egg-info",
        "-dist", "_dist"
    ];

    /// <summary>
    ///     File extensions that dominate generated / compiled output directories.
    ///     A directory where >80% of sampled files have these extensions is suspicious.
    /// </summary>
    private static readonly HashSet<string> CompiledExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pyc", ".pyo", ".class", ".o", ".obj", ".a", ".so", ".dll",
        ".pdb", ".ilk", ".exp", ".lib", ".cache", ".cached",
        ".map" // source maps — generated
    };

    /// <summary>
    ///     Names targeted in the deep BFS pass; these appear at arbitrary nesting depth in
    ///     multi-project repos.  The workspace-root occurrence is already handled by
    ///     <see cref="GitignoreScanner" />, so we only report nested occurrences here.
    /// </summary>
    private static readonly HashSet<string> DeepBuildNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "artifacts", "publish"
    };

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Scans <paramref name="workspaceRoot" /> and returns directories that look like
    ///     caches/build outputs but are not covered by .gitignore.
    /// </summary>
    public static IReadOnlyList<SuspiciousDirectory> Scan(string workspaceRoot)
    {
        List<SuspiciousDirectory> results = [];

        try
        {
            // Pass 1: heuristic scan, up to 4 levels deep
            ScanDirectory(workspaceRoot, workspaceRoot, 0, 4, results);

            // Pass 2: deep targeted scan for known build artifact names at any depth
            DeepScanBuildArtifacts(workspaceRoot, results);
        }
        catch
        {
            /* best-effort */
        }

        return results;
    }

    // ── Pass 1: heuristic scan ────────────────────────────────────────────────

    private static void ScanDirectory(
        string workspaceRoot, string dir, int depth, int maxDepth,
        List<SuspiciousDirectory> results)
    {
        if (depth >= maxDepth)
        {
            return;
        }

        foreach (string subDir in SafeEnumDirs(dir))
        {
            string name = Path.GetFileName(subDir);
            string relPath = Path.GetRelativePath(workspaceRoot, subDir).Replace('\\', '/');

            // At workspace root only: skip entries already handled by GitignoreScanner
            // so we don't duplicate their reports.  At deeper levels we fully assess them
            // (a nested bin/ that's not gitignored is a real find).
            if (depth == 0 && GitignoreScanner.IsKnownEntry(name))
            {
                continue;
            }

            // Skip: already covered by workspace .gitignore
            if (GitignoreFilter.EntryIsCovered(workspaceRoot, relPath))
            {
                continue;
            }

            // Skip: already found by DeepBuildNames pass (avoid duplicates)
            if (depth > 0 && DeepBuildNames.Contains(name))
            {
                continue;
            }

            (float confidence, string? reason, SuspiciousDirRecommendation recommendation) = Assess(name, subDir, depth);

            if (confidence >= 0.35f)
            {
                results.Add(new SuspiciousDirectory(relPath, reason, confidence, recommendation));
                // Don't recurse into flagged directories
            }
            else if (IsSourceDirectory(subDir))
            {
                ScanDirectory(workspaceRoot, subDir, depth + 1, maxDepth, results);
            }
            else
            {
                // Non-source, low-confidence: recurse to catch nested caches
                if (depth + 1 < maxDepth)
                {
                    ScanDirectory(workspaceRoot, subDir, depth + 1, maxDepth, results);
                }
            }
        }
    }

    // ── Pass 2: deep targeted build-artifact scan ─────────────────────────────

    private static void DeepScanBuildArtifacts(string workspaceRoot, List<SuspiciousDirectory> results)
    {
        HashSet<string> alreadyReported = results.Select(r => r.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Queue<string> queue = new();
        queue.Enqueue(workspaceRoot);
        int depth = 0;
        Queue<string> nextLevel = new();

        // BFS, max 8 levels deep; skip .git and known always-skip dirs
        const int MaxDepth = 8;

        while ((queue.Count > 0 || nextLevel.Count > 0) && depth <= MaxDepth)
        {
            if (queue.Count == 0)
            {
                queue = nextLevel;
                nextLevel = new Queue<string>();
                depth++;
                continue;
            }

            string dir = queue.Dequeue();

            foreach (string subDir in SafeEnumDirs(dir))
            {
                string name = Path.GetFileName(subDir);
                string relPath = Path.GetRelativePath(workspaceRoot, subDir).Replace('\\', '/');

                // Skip hidden repo infra
                if (name is ".git" or ".tokensaver")
                {
                    continue;
                }

                if (DeepBuildNames.Contains(name) && relPath.Contains('/', StringComparison.InvariantCulture))
                {
                    // Nested build artifact — skip if already covered or reported
                    if (!GitignoreFilter.EntryIsCovered(workspaceRoot, relPath) &&
                        !alreadyReported.Contains(relPath))
                    {
                        results.Add(new SuspiciousDirectory(
                            relPath,
                            $"'{name}/' is a known build-output directory",
                            0.90f,
                            SuspiciousDirRecommendation.AddToGitignore));
                        _ = alreadyReported.Add(relPath);
                    }
                    // Don't recurse into build dirs
                }
                else
                {
                    nextLevel.Enqueue(subDir);
                }
            }
        }
    }

    // ── Assessment ─────────────────────────────────────────────────────────────

    private static (float Confidence, string Reason, SuspiciousDirRecommendation Rec)
        Assess(string name, string fullPath, int depth)
    {
        // ── Tier 1: CACHE DIR.TAG standard marker (highest signal) ─────────────
        if (File.Exists(Path.Combine(fullPath, CacheDirTagFile)))
        {
            return (0.97f, $"Contains {CacheDirTagFile} (standard cache-directory marker)",
                SuspiciousDirRecommendation.Both);
        }

        // ── Tier 2: exact high-confidence name ────────────────────────────────
        if (HighConfidenceNames.Contains(name))
        {
            return (0.90f, $"'{name}' is a well-known cache/build-output pattern", SuspiciousDirRecommendation.Both);
        }

        // ── Tier 2b: medium-confidence build names ────────────────────────────
        if (MediumConfidenceNames.Contains(name))
        {
            return (0.65f, $"'{name}/' looks like a build output directory", SuspiciousDirRecommendation.Both);
        }

        // ── Tier 3: name contains a cache/generated substring ─────────────────
        string? matchedSub = CacheSubstrings.FirstOrDefault(s =>
            name.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (matchedSub != null)
        {
            float conf = name.EndsWith(matchedSub, StringComparison.OrdinalIgnoreCase) ? 0.72f : 0.55f;
            return (conf, $"Directory name contains '{matchedSub}'", SuspiciousDirRecommendation.Both);
        }

        // ── Tier 4: content heuristics ────────────────────────────────────────
        (float compiledRatio, int fileCount) = SampleCompiledRatio(fullPath);

        if (fileCount <= 0)
        {
            return (0.0f, "", SuspiciousDirRecommendation.Both);
        }

        if (compiledRatio >= 0.90f && fileCount >= 20)
        {
            return (0.80f,
                $"{fileCount} files, {compiledRatio:P0} are compiled/generated extensions",
                SuspiciousDirRecommendation.Both);
        }

        if (compiledRatio >= 0.70f && fileCount >= 50)
        {
            return (0.60f,
                $"{fileCount} files, {compiledRatio:P0} appear compiled or generated",
                SuspiciousDirRecommendation.ExcludeFromContext);
        }

        // No strong signal
        return (0.0f, "", SuspiciousDirRecommendation.Both);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool IsSourceDirectory(string dir)
    {
        static bool IsSourceExt(string ext)
        {
            return ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".go"
                or ".java" or ".kt" or ".rs" or ".cpp" or ".c" or ".h" or ".hpp"
                or ".rb" or ".php" or ".swift" or ".md" or ".html" or ".css";
        }

        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Take(20)
                .Select(f => Path.GetExtension(f).ToUpperInvariant())
                .Any(IsSourceExt);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Samples up to 100 files and returns the fraction with compiled extensions.</summary>
    private static (float Ratio, int Count) SampleCompiledRatio(string dir)
    {
        try
        {
            List<string> files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Take(100)
                .ToList();

            if (files.Count == 0)
            {
                return (0f, 0);
            }

            int compiled = files.Count(f =>
            {
                string ext = Path.GetExtension(f).ToUpperInvariant();
                string name = Path.GetFileName(f);
                return CompiledExtensions.Contains(ext) ||
                       name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase);
            });

            return ((float)compiled / files.Count, files.Count);
        }
        catch
        {
            return (0f, 0);
        }
    }

    private static IEnumerable<string> SafeEnumDirs(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch
        {
            return [];
        }
    }
}

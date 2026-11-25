namespace AiDevTools.TokenSaver.Utilities.Services;

/// <summary>Describes a workspace path that exists but is not covered by .gitignore.</summary>
public sealed record GitignoreIssue(
    string Entry,
    string Reason,
    bool ContainsSecrets,
    bool IsFile = false);

/// <summary>
///     Scans a workspace and reports paths that exist on disk but are not covered by .gitignore.
///     Only paths that actually exist are reported — the scanner does not produce noise for
///     toolchains that aren't in use.
/// </summary>
public static class GitignoreScanner
{
    // (entry, isFile, reason, containsSecrets)
    private static readonly (string Entry, bool IsFile, string Reason, bool Secrets)[] Candidates =
    [
        // ── TokenSaver generated dirs ──────────────────────────────────────
        (".tokensaver", false,
            "TokenSaver context cache, code index, calibration data, and patch backups — may contain sensitive workspace content",
            true),

        // ── C# / .NET ───────────────────────────────────────────────────────
        ("bin", false, "C# / .NET build output", false),
        ("obj", false, "C# / .NET build intermediates", false),
        (".vs", false, "Visual Studio IDE settings", false),

        // ── Python ──────────────────────────────────────────────────────────
        ("__pycache__", false, "Python bytecode cache", false),
        (".mypy_cache", false, "mypy type-checking cache", false),
        (".pytest_cache", false, "pytest cache directory", false),
        (".ruff_cache", false, "ruff linter cache", false),
        (".venv", false, "Python virtual environment", false),
        ("venv", false, "Python virtual environment", false),

        // ── Node.js / JS ────────────────────────────────────────────────────
        ("node_modules", false, "Node.js / npm dependencies", false),
        (".next", false, "Next.js build output", false),
        (".nuxt", false, "Nuxt.js build output", false),
        (".svelte-kit", false, "SvelteKit build output", false),
        ("dist", false, "Build output directory", false),

        // ── Java / Kotlin / Gradle ──────────────────────────────────────────
        ("target", false, "Maven build output", false),
        (".gradle", false, "Gradle cache", false),

        // ── Rust ────────────────────────────────────────────────────────────
        // 'target/' already in list above; Rust uses the same pattern

        // ── Secrets / env ───────────────────────────────────────────────────
        (".env", true, "Environment variables file — may contain API keys and secrets", true),
        (".env.local", true, "Local environment overrides — may contain secrets", true)
    ];

    // Fast lookup used by SuspiciousDirectoryScanner to avoid double-reporting workspace-root entries.
    private static readonly HashSet<string> KnownNames =
        new(Candidates.Select(c => Path.GetFileName(c.Entry.TrimEnd('/'))),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns true if <paramref name="name" /> is already tracked by this scanner.</summary>
    public static bool IsKnownEntry(string name)
    {
        return KnownNames.Contains(name);
    }

    /// <summary>
    ///     Returns a list of <see cref="GitignoreIssue" /> for paths that actually exist in
    ///     <paramref name="workspaceRoot" /> but are not covered by the workspace .gitignore.
    /// </summary>
    public static IReadOnlyList<GitignoreIssue> Scan(string workspaceRoot)
    {
        List<GitignoreIssue> issues = [];

        foreach ((string? entry, bool isFile, string? reason, bool secrets) in Candidates)
        {
            // Only flag if the path actually exists — no noise for absent toolchains
            string fullPath = Path.Combine(workspaceRoot, entry);
            bool exists = isFile ? File.Exists(fullPath) : Directory.Exists(fullPath);
            if (!exists)
            {
                continue;
            }

            // Skip if already covered
            if (GitignoreFilter.EntryIsCovered(workspaceRoot, entry))
            {
                continue;
            }

            issues.Add(new GitignoreIssue(entry, reason, secrets, isFile));
        }

        return issues;
    }
}

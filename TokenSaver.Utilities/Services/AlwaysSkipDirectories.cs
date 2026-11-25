namespace AiDevTools.TokenSaver.Utilities.Services;

/// <summary>
///     Directory names that every indexer skips, anywhere in the tree: VCS metadata, our own
///     <c>.tokensaver</c> folder, build output, and virtual environments. One list shared by the
///     workspace walkers, the git staleness checker, RepoIndexer, and WorkspaceIgnoreFilter.
/// </summary>
public static class AlwaysSkipDirectories
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".tokensaver", ".vs", ".venv", "__pycache__", "bin", "build", "dist", "node_modules", "obj", "out", "venv",
    };

    /// <summary>True when a single directory name is on the list.</summary>
    public static bool Contains(string directoryName) => Names.Contains(directoryName);

    /// <summary>
    ///     True when a workspace-relative path sits under a skipped directory. For a file path only
    ///     its parent directories are checked, so a file named <c>out</c> is not skipped.
    /// </summary>
    private static readonly char[] PathSeparators = ['/', '\\'];

    public static bool IsUnderSkippedDirectory(string relativePath, bool isDirectory)
    {
        string[] segments = relativePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        int checkCount = isDirectory ? segments.Length : segments.Length - 1;
        for (int i = 0; i < checkCount; i++)
        {
            if (Names.Contains(segments[i]))
            {
                return true;
            }
        }

        return false;
    }
}

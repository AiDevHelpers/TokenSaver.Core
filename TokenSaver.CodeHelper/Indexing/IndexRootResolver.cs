using AiDevTools.TokenSaver.Database.Context;

namespace AiDevTools.TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Resolves which workspace index covers a requested folder. Each index lives at
///     <c>&lt;root&gt;/.tokensaver/index.db</c>, so a query against a root that was never scanned
///     itself (e.g. a subfolder of an indexed workspace) must be routed to the nearest ancestor index.
/// </summary>
public static class IndexRootResolver
{
    /// <summary>
    ///     Returns the nearest ancestor-or-self index root and the requested folder's path inside it
    ///     (empty, or ending in "/"). Falls back to the requested root with an empty prefix when no
    ///     index exists yet.
    /// </summary>
    public static (string IndexRoot, string Prefix) Resolve(string requested)
    {
        string full = Path.GetFullPath(requested).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? dir = full;
        while (dir != null)
        {
            if (File.Exists(WorkspaceIndexContextFactory.GetDbPath(dir)))
            {
                string rel = Path.GetRelativePath(dir, full).Replace('\\', '/');
                return (dir, rel == "." ? "" : rel + "/");
            }

            dir = Path.GetDirectoryName(dir);
        }

        return (requested, "");
    }

    /// <summary>
    ///     True when the stored (index-relative) path matches the requested path, accepting an exact
    ///     match or either side being a trailing path segment of the other (so "src/Services/X.cs"
    ///     matches a stored "Services/X.cs").
    /// </summary>
    public static bool MatchesFilePath(string storedRelativePath, string requested)
    {
        string stored = storedRelativePath.Replace('\\', '/');
        string req = requested.Replace('\\', '/').TrimStart('/');
        return string.Equals(stored, req, StringComparison.OrdinalIgnoreCase)
            || req.EndsWith("/" + stored, StringComparison.OrdinalIgnoreCase)
            || stored.EndsWith("/" + req, StringComparison.OrdinalIgnoreCase);
    }
}

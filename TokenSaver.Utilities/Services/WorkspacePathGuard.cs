namespace AiDevTools.TokenSaver.Utilities.Services;

/// <summary>
///     Confines a caller-supplied path to a workspace root. The path is made absolute, symbolic links and
///     junctions are followed, and the real location must be inside the root (or one of the extra roots).
///     Used by every tool that reads or writes a path a client or model chose.
/// </summary>
/// <summary>Outcome of a <see cref="WorkspacePathGuard.Resolve" /> call.</summary>
public sealed record PathCheck(bool Ok, string FullPath, string? Error)
{
    internal static PathCheck Pass(string fullPath) => new(true, fullPath, null);

    internal static PathCheck Fail(string error) => new(false, "", error);
}

public static class WorkspacePathGuard
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    ///     Resolves <paramref name="path" /> (absolute, or relative to <paramref name="workspaceRoot" />) and
    ///     checks it lies under the root or one of <paramref name="extraRoots" />. On success
    ///     <see cref="PathCheck.FullPath" /> is the normalized path (links not substituted); otherwise
    ///     <see cref="PathCheck.Error" /> says why and nothing should be read or written.
    /// </summary>
    public static PathCheck Resolve(
        string? path,
        string? workspaceRoot,
        params string[] extraRoots)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PathCheck.Fail("A path is required.");
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return PathCheck.Fail("No workspace root is known for this session. Pass 'root', or connect a client that provides MCP roots.");
        }

        string normalized;
        string realRoot;
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            normalized = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            realRoot = RealPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return PathCheck.Fail($"Invalid path: {ex.Message}");
        }

        string real = RealPath(normalized);
        if (IsUnder(real, realRoot))
        {
            return PathCheck.Pass(normalized);
        }

        foreach (string extra in extraRoots)
        {
            if (!string.IsNullOrWhiteSpace(extra) &&
                IsUnder(real, RealPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(extra)))))
            {
                return PathCheck.Pass(normalized);
            }
        }

        return PathCheck.Fail($"Path '{path}' is outside the workspace root '{workspaceRoot}'.");
    }

    /// <summary>True when <paramref name="path" /> is <paramref name="root" /> itself or inside it.</summary>
    public static bool IsUnder(string path, string root)
    {
        string trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        if (path.Equals(trimmedRoot, PathComparison))
        {
            return true;
        }

        return path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, PathComparison) ||
               path.StartsWith(trimmedRoot + Path.AltDirectorySeparatorChar, PathComparison);
    }

    /// <summary>
    ///     The path with every existing symbolic link or junction replaced by its final target. Segments that do
    ///     not exist yet (a file about to be created) are kept as they are.
    /// </summary>
    private static string RealPath(string fullPath)
    {
        string? pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
        {
            return fullPath;
        }

        string current = pathRoot;
        string[] segments = fullPath[pathRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments)
        {
            string next = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null)
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                next = target is null ? next : Path.GetFullPath(target.FullName);
            }

            current = next;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }
}

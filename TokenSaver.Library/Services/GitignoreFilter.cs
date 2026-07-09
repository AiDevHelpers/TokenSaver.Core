using System.Text;
using System.Text.RegularExpressions;

namespace TokenSaver.Library.Services;

/// <summary>
///     Parses one or more .gitignore files and answers whether a workspace-relative
///     path should be excluded from scanning.
/// </summary>
public sealed class GitignoreFilter
{
    private readonly List<Rule> _rules = [];

    private GitignoreFilter()
    {
    }

    /// <summary>
    ///     Loads the .gitignore at <paramref name="workspaceRoot" />.
    ///     Returns an empty (pass-all) filter if the file doesn't exist.
    /// </summary>
    public static GitignoreFilter Load(string workspaceRoot)
    {
        return LoadFrom(Path.Combine(workspaceRoot, ".gitignore"));
    }

    /// <summary>
    ///     Loads gitignore-syntax rules from an arbitrary file (e.g. <c>.tokensaver/.ignore-additional</c>
    ///     or <c>.tokensaver/.ignore-bypass</c>). Returns an empty (match-nothing) filter if the file
    ///     doesn't exist. Rules use the same glob/anchoring/negation semantics as <c>.gitignore</c>.
    /// </summary>
    public static GitignoreFilter LoadFrom(string filePath)
    {
        GitignoreFilter filter = new();
        if (!File.Exists(filePath))
        {
            return filter;
        }

        foreach (string raw in File.ReadAllLines(filePath))
        {
            filter.AddRule(raw);
        }

        return filter;
    }

    /// <summary>True if any parsed rule matches — i.e. whether this filter carries any rules at all.</summary>
    public bool HasRules => _rules.Count > 0;

    /// <summary>
    ///     Returns true if the path should be excluded.
    ///     <paramref name="relativePath" /> must use forward slashes and must NOT start with '/'.
    ///     <paramref name="isDirectory" /> must be true when the path refers to a directory.
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');

        bool ignored = false;
        foreach (Rule rule in _rules)
        {
            if (rule.DirOnly && !isDirectory)
            {
                continue;
            }

            if (rule.Pattern.IsMatch(relativePath))
            {
                ignored = !rule.Negate;
            }
        }

        return ignored;
    }

    // ── Rule parsing ───────────────────────────────────────────────────────────

    private void AddRule(string line)
    {
        // Strip inline comments (trailing unescaped #) and trailing spaces
        line = line.TrimEnd();
        if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
        {
            return;
        }

        bool negate = line.StartsWith('!');
        if (negate)
        {
            line = line[1..];
        }

        bool dirOnly = line.EndsWith('/');
        if (dirOnly)
        {
            line = line.TrimEnd('/');
        }

        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        // A pattern containing '/' (other than a leading slash) is anchored to root.
        // A pattern with no '/' matches the basename at any depth.
        string stripped = line.TrimStart('/');
        bool anchored = line.StartsWith('/') || stripped.Contains('/', StringComparison.Ordinal);

        string regexBody = GlobToRegex(stripped);
        string fullPattern = anchored
            ? $"^{regexBody}$"
            : $"(?:^|/){regexBody}$";

        try
        {
            _rules.Add(new Rule(
                new Regex(fullPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                negate,
                dirOnly));
        }
        catch (RegexParseException)
        {
            // Malformed gitignore line — skip it
        }
    }

    // ── Static helpers ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Returns true if <paramref name="entry" /> (a workspace-relative path such as ".tokensaver"
    ///     or ".local-mcp/indices") is already covered by a rule in the workspace .gitignore.
    ///     Handles parent-directory rules: ".local-mcp/" covers ".local-mcp/indices".
    /// </summary>
    public static bool EntryIsCovered(string workspaceRoot, string entry)
    {
        string normalised = entry.Replace('\\', '/').Trim('/');
        string gitignorePath = Path.Combine(workspaceRoot, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            return false;
        }

        foreach (string raw in File.ReadAllLines(gitignorePath))
        {
            string line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('!'))
            {
                continue;
            }

            string stripped = line.TrimEnd('/').TrimStart('/');
            if (string.IsNullOrEmpty(stripped))
            {
                continue;
            }

            // Exact match, or the gitignore entry is an anchored parent path component
            if (normalised.Equals(stripped, StringComparison.OrdinalIgnoreCase) ||
                normalised.StartsWith(stripped + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Non-anchored patterns (no '/' in pattern) match at ANY depth per gitignore spec.
            // e.g. "bin/" in .gitignore covers "DraftModel/DraftModel.Loader/bin".
            if (!stripped.Contains('/', StringComparison.Ordinal) &&
                (normalised.EndsWith("/" + stripped, StringComparison.OrdinalIgnoreCase) ||
                 normalised.Contains("/" + stripped + "/", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Appends one or more entries to the workspace .gitignore (creating the file if needed).
    ///     Each entry is written as a directory glob ("entry/") unless <paramref name="IsFile" /> is true.
    ///     A shared header comment is written before the block.
    /// </summary>
    public static void AddEntries(
        string workspaceRoot,
        IEnumerable<(string Entry, bool IsFile, string Reason)> entries)
    {
        string gitignorePath = Path.Combine(workspaceRoot, ".gitignore");
        string existing = File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : "";
        StringBuilder sb = new();

        if (existing.Length > 0 && !existing.EndsWith('\n'))
        {
            _ = sb.AppendLine();
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("# TokenSaver — generated / tool-cache (do not commit; may contain secrets)");

        foreach ((string? entry, bool isFile, string _) in entries)
        {
            string normalised = entry.Replace('\\', '/').Trim('/');
            _ = sb.AppendLine(isFile ? normalised : $"{normalised}/");
        }

        File.AppendAllText(gitignorePath, sb.ToString());
    }

    // ── Glob → Regex conversion ────────────────────────────────────────────────

    private static string GlobToRegex(string glob)
    {
        StringBuilder sb = new();
        int i = 0;
        while (i < glob.Length)
        {
            // ** — matches any number of path segments
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    _ = sb.Append("(?:[^/]+/)*");
                    i += 3;
                }
                else
                {
                    _ = sb.Append(".*");
                    i += 2;
                }
            }
            // * — matches anything except a path separator
            else if (glob[i] == '*')
            {
                _ = sb.Append("[^/]*");
                i++;
            }
            // ? — matches one character except a path separator
            else if (glob[i] == '?')
            {
                _ = sb.Append("[^/]");
                i++;
            }
            else
            {
                _ = sb.Append(Regex.Escape(glob[i].ToString()));
                i++;
            }
        }

        return sb.ToString();
    }

    private readonly record struct Rule(Regex Pattern, bool Negate, bool DirOnly);
}

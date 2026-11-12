using TokenSaver.Utilities.Services;

namespace TokenSaver.Config;

/// <summary>
///     Unified "should this path be indexed/included in context?" decision, composing three
///     gitignore-syntax sources rooted at the workspace:
///     <list type="number">
///         <item><c>.gitignore</c> — the baseline exclusions.</item>
///         <item>
///             <c>.tokensaver/.ignore-additional</c> — extra paths (gitignore glob syntax) to ignore
///             that are <b>not</b> in <c>.gitignore</c>. ANDed on: these expand what is ignored.
///             This is also where <c>ContextDiag(action: "context-exclude")</c> writes
///             "hide from AI context but keep in git" paths — there is a single gitignore-style
///             mechanism for that, not a separate JSON-format one.
///         </item>
///         <item>
///             <c>.tokensaver/.ignore-bypass</c> — paths (gitignore glob syntax) that ARE ignored by
///             <c>.gitignore</c>/<c>.ignore-additional</c> but should still be indexed. This wins:
///             a bypass match forces the path to be included.
///         </item>
///     </list>
///     Net rule: <c>ignored = (gitignore ∨ additional) ∧ ¬bypass</c>.
/// </summary>
public sealed class WorkspaceIgnoreFilter
{
    private const string LocalFolder = ".tokensaver";
    private const string AdditionalFile = ".ignore-additional";
    private const string BypassFile = ".ignore-bypass";

    private readonly GitignoreFilter _gitignore;
    private readonly GitignoreFilter _additional;
    private readonly GitignoreFilter _bypass;

    private WorkspaceIgnoreFilter(
        GitignoreFilter gitignore,
        GitignoreFilter additional,
        GitignoreFilter bypass)
    {
        _gitignore = gitignore;
        _additional = additional;
        _bypass = bypass;
    }

    /// <summary>Path to <c>.tokensaver/.ignore-additional</c> for <paramref name="workspaceRoot" />.</summary>
    public static string AdditionalPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, LocalFolder, AdditionalFile);

    /// <summary>Path to <c>.tokensaver/.ignore-bypass</c> for <paramref name="workspaceRoot" />.</summary>
    public static string BypassPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, LocalFolder, BypassFile);

    /// <summary>
    ///     Appends gitignore-syntax <paramref name="patterns" /> to one of the two override files,
    ///     de-duplicating against lines already present. Creates the <c>.tokensaver</c> folder and
    ///     file as needed. Returns the patterns that were actually added (new ones).
    /// </summary>
    public static IReadOnlyList<string> Append(string filePath, IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        HashSet<string> existing = File.Exists(filePath)
            ? new HashSet<string>(File.ReadAllLines(filePath).Select(l => l.Trim()), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<string> added = [];
        foreach (string raw in patterns)
        {
            string norm = raw.Replace('\\', '/').Trim();
            if (!string.IsNullOrWhiteSpace(norm) && existing.Add(norm))
            {
                added.Add(norm);
            }
        }

        if (added.Count == 0)
        {
            return added;
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        bool needsLeadingNewline = File.Exists(filePath) &&
            File.ReadAllText(filePath) is { Length: > 0 } t && !t.EndsWith('\n');
        File.AppendAllText(filePath, (needsLeadingNewline ? Environment.NewLine : "") + string.Join(Environment.NewLine, added) + Environment.NewLine);
        return added;
    }

    /// <summary>Loads all three sources for <paramref name="workspaceRoot" />.</summary>
    public static WorkspaceIgnoreFilter Load(string workspaceRoot)
    {
        string localDir = Path.Combine(workspaceRoot, LocalFolder);
        return new WorkspaceIgnoreFilter(
            GitignoreFilter.Load(workspaceRoot),
            GitignoreFilter.LoadFrom(Path.Combine(localDir, AdditionalFile)),
            GitignoreFilter.LoadFrom(Path.Combine(localDir, BypassFile)));
    }

    /// <summary>
    ///     True if <paramref name="relativePath" /> should be excluded from indexing/context.
    ///     A path matched by <c>.ignore-bypass</c> is always included (returns false), overriding
    ///     every other source.
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        if (_bypass.HasRules && _bypass.IsIgnored(relativePath, isDirectory))
        {
            return false;
        }

        return AlwaysSkipDirectories.IsUnderSkippedDirectory(relativePath, isDirectory)
            || _gitignore.IsIgnored(relativePath, isDirectory)
            || _additional.IsIgnored(relativePath, isDirectory);
    }
}

using System.Text.Json;

namespace TokenSaver.Library.Services;

/// <summary>
///     Persists workspace-relative directory paths that should be excluded from AI context
///     and code indexing, but NOT necessarily from git.  Stored in
///     <c>.local-mcp/context-exclusions.json</c>.
/// </summary>
public static class ContextExclusions
{
    private const string LocalMcpFolder = ".tokensaver";
    private const string ExclusionsFile = "context-exclusions.json";
    private static readonly JsonSerializerOptions WriteIndentedOptions = new() { WriteIndented = true };

    private static string FilePath(string workspaceRoot)
    {
        return Path.Combine(workspaceRoot, LocalMcpFolder, ExclusionsFile);
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Loads saved exclusions from <c>.local-mcp/context-exclusions.json</c>.
    ///     Returns an empty list if the file doesn't exist.
    /// </summary>
    public static IReadOnlyList<string> Load(string workspaceRoot)
    {
        string path = FilePath(workspaceRoot);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(path);
            JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("excluded", out JsonElement arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                return arr.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Replace('\\', '/').Trim('/'))
                    .ToList();
            }
        }
        catch
        {
            /* best-effort */
        }

        return [];
    }

    // ── Add ───────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Adds <paramref name="paths" /> to the exclusion list (de-duped, sorted).
    ///     Creates the file if it doesn't exist; updates it if it does.
    /// </summary>
    public static void Add(string workspaceRoot, IEnumerable<string> paths)
    {
        HashSet<string> existing = Load(workspaceRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string p in paths)
        {
            string norm = p.Replace('\\', '/').Trim('/');
            if (!string.IsNullOrWhiteSpace(norm))
            {
                _ = existing.Add(norm);
            }
        }

        string[] sorted = existing.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        string json = JsonSerializer.Serialize(
            new { excluded = sorted, updated = DateTimeOffset.UtcNow.ToString("O") },
            WriteIndentedOptions);

        string mcpDir = Path.Combine(workspaceRoot, LocalMcpFolder);
        _ = Directory.CreateDirectory(mcpDir);
        File.WriteAllText(FilePath(workspaceRoot), json);
    }

    // ── Check ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Returns true if <paramref name="relPath" /> is covered by any saved exclusion.
    ///     Supports parent-path coverage: excluding <c>some/dir</c> also excludes
    ///     <c>some/dir/nested/file.cs</c>.
    /// </summary>
    public static bool IsCovered(string relPath, IReadOnlyList<string> exclusions)
    {
        string norm = relPath.Replace('\\', '/').Trim('/');
        foreach (string excl in exclusions)
        {
            if (norm.Equals(excl, StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith(excl + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Convenience overload that loads from disk before checking.</summary>
    public static bool IsCovered(string workspaceRoot, string relPath)
    {
        return IsCovered(relPath, Load(workspaceRoot));
    }
}

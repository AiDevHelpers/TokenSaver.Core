using System;
using System.IO;

namespace TokenSaver.Client.Helpers.Configuration;

/// <summary>
/// Helper for reading .gitignore files and checking if .tokensaver/ is already covered.
/// Mirrors GitignoreFilter.EntryIsCovered from TokenSaver.Library.
/// </summary>
public static class GitignoreHelper
{
    /// <summary>
    /// Checks if .tokensaver/ is already covered by .gitignore in the workspace.
    /// </summary>
    public static bool IsTokenSaverInGitignore(string workspaceRoot)
    {
        var gitignorePath = Path.Combine(workspaceRoot, ".gitignore");
        if (!File.Exists(gitignorePath))
            return false;

        try
        {
            var content = File.ReadAllText(gitignorePath);
            return IsEntryCovered(content, ".tokensaver");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds .tokensaver/ to the workspace .gitignore if not already present.
    /// </summary>
    public static void AddTokenSaverToGitignore(string workspaceRoot)
    {
        var gitignorePath = Path.Combine(workspaceRoot, ".gitignore");
        var existing = File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : "";

        if (string.IsNullOrEmpty(existing))
        {
            File.WriteAllText(gitignorePath, ".tokensaver/\n");
        }
        else if (!existing.EndsWith("\n"))
        {
            File.AppendAllText(gitignorePath, "\n.tokensaver/\n");
        }
        else
        {
            File.AppendAllText(gitignorePath, ".tokensaver/\n");
        }
    }

    /// <summary>
    /// Checks if a pattern in .gitignore covers the given entry.
    /// </summary>
    private static bool IsEntryCovered(string gitignoreContent, string entry)
    {
        var normalized = entry.ToLowerInvariant();

        foreach (var rawLine in gitignoreContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();

            // Skip comments and empty lines
            if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("!"))
                continue;

            // Normalize the pattern by removing leading/trailing slashes
            var stripped = line.Replace("/", "");
            if (string.IsNullOrEmpty(stripped))
                continue;

            // Check if this pattern matches the entry
            if (normalized == stripped.ToLowerInvariant() ||
                normalized.StartsWith(stripped.ToLowerInvariant() + "/"))
            {
                return true;
            }
        }

        return false;
    }
}

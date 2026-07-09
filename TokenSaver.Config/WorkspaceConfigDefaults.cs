using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace TokenSaver.Config;

/// <summary>
///     Creates the <c>.tokensaver/</c> folder and its default config files for a workspace the
///     first time the MCP server loads it, so the files exist on disk (discoverable/editable)
///     instead of only ever being implied by "file not found = defaults". Idempotent: an existing
///     file is never overwritten.
/// </summary>
public static partial class WorkspaceConfigDefaults
{
    private const string LocalFolder = ".tokensaver";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    ///     Ensures <c>.tokensaver/config.json</c>, <c>.ignore-additional</c>, <c>.ignore-bypass</c>,
    ///     and <c>context-exclusions.json</c> exist under <paramref name="workspaceRoot" />, creating
    ///     each with empty/commented defaults if missing. Best-effort: I/O failures are logged and
    ///     swallowed so a locked/read-only workspace never blocks server startup.
    /// </summary>
    public static void EnsureDefaults(string workspaceRoot, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string localDir = Path.Combine(workspaceRoot, LocalFolder);

        try
        {
            _ = Directory.CreateDirectory(localDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogEnsureDefaultsFailed(logger, localDir, ex);
            return;
        }

        EnsureFile(logger, Path.Combine(localDir, BehaviorConfigService.ConfigFileName), "{}" + Environment.NewLine);

        EnsureFile(
            logger,
            Path.Combine(localDir, ".ignore-additional"),
            "# TokenSaver: extra gitignore-syntax patterns to exclude from indexing/context," + Environment.NewLine +
            "# in addition to .gitignore. One pattern per line; same syntax as .gitignore." + Environment.NewLine);

        EnsureFile(
            logger,
            Path.Combine(localDir, ".ignore-bypass"),
            "# TokenSaver: gitignore-syntax patterns that force-include paths otherwise ignored" + Environment.NewLine +
            "# by .gitignore/.ignore-additional. One pattern per line; same syntax as .gitignore." + Environment.NewLine);

        EnsureFile(
            logger,
            Path.Combine(localDir, "context-exclusions.json"),
            JsonSerializer.Serialize(new { excluded = Array.Empty<string>() }, WriteOptions) + Environment.NewLine);
    }

    private static void EnsureFile(ILogger logger, string path, string defaultContent)
    {
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, defaultContent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogEnsureDefaultsFailed(logger, path, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create default TokenSaver config at {Path}; continuing without it")]
    private static partial void LogEnsureDefaultsFailed(ILogger logger, string path, Exception ex);
}

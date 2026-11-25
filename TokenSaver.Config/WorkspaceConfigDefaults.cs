using Microsoft.Extensions.Logging;

namespace AiDevTools.TokenSaver.Config;

/// <summary>
///     Creates the <c>.tokensaver/</c> folder and its default config files for a workspace —
///     both the first time the MCP server loads a workspace, and again on every later MCP
///     session connect if a file has since been deleted — so the files exist on disk
///     (discoverable/editable) instead of only ever being implied by "file not found = defaults".
///     Idempotent: an existing file is never overwritten, only ever re-created when missing.
///     <see cref="ConfigUpgradeService" /> is the complementary piece: it patches nodes (section,
///     nested object, or leaf setting) missing from a config.json that already exists — e.g. one
///     created by an older TokenSaver version — using the same <see cref="ConfigSchemaTemplates" />
///     tree this class renders its fresh-file templates from.
/// </summary>
public static partial class WorkspaceConfigDefaults
{
    private const string LocalFolder = ".tokensaver";

    /// <summary>
    ///     Ensures <c>.tokensaver/config.json</c>, <c>.ignore-additional</c>, and
    ///     <c>.ignore-bypass</c> exist under <paramref name="workspaceRoot" />, and that the
    ///     machine-global <c>%ProgramData%\TokenSaver\config.json</c> exists too, (re)creating each
    ///     with its documented default content if missing. Best-effort: I/O failures are logged
    ///     and swallowed so a locked/read-only workspace never blocks server startup or a session
    ///     connect.
    ///     <para>
    ///     The local <c>config.json</c> is written as a JSON-with-comments template: every known
    ///     setting is present, commented out, each preceded by a one-line <c>//</c> comment
    ///     describing what it does and what its default value is. This makes the file
    ///     self-documenting and directly editable — uncomment a line and change the value to
    ///     override it. The reader (<see cref="BehaviorConfigService" />) uses
    ///     <c>JsonCommentHandling.Skip</c>, so the comments and commented-out lines are safely
    ///     ignored until uncommented.
    ///     </para>
    ///     <para>
    ///     The global <c>config.json</c> uses the same structure and description comments, but
    ///     every setting is written active (uncommented) with its real default value, since the
    ///     global file's whole purpose is to be the machine-wide baseline every workspace falls
    ///     back to — a fully commented-out global file would be indistinguishable from having no
    ///     global file at all.
    ///     </para>
    ///     Does not create <c>context-exclusions.json</c> — that JSON-format exclusion list was
    ///     retired in favor of the gitignore-syntax <c>.ignore-additional</c> file, which already
    ///     supports everything it could express (plus real glob patterns). A pre-existing
    ///     <c>context-exclusions.json</c> from an older workspace is left on disk untouched but is
    ///     no longer read by anything; move any entries into <c>.ignore-additional</c> by hand.
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

        EnsureFile(logger, Path.Combine(localDir, BehaviorConfigService.ConfigFileName), ConfigSchemaTemplates.CommentedFileTemplate);

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

        string globalPath = BehaviorConfigService.GlobalConfigPath;
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(globalPath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogEnsureDefaultsFailed(logger, globalPath, ex);
            return;
        }

        EnsureFile(logger, globalPath, ConfigSchemaTemplates.UncommentedFileTemplate);
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

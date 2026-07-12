using TokenSaver.Library.Services;
using TokenSaver.Utilities.Services;

namespace TokenSaver.Config;

/// <summary>
///     Decides whether to surface the "add <c>.tokensaver/</c> to <c>.gitignore</c>?" question to
///     the calling AI, and builds the prompt text. Because an MCP server has no direct user I/O, the
///     question is delivered as a note appended to a tool result: the AI relays it, then calls
///     <c>ManageConfig</c> (action <c>set-gitignore-pref</c>) to persist the answer.
/// </summary>
public static class GitignorePrompt
{
    /// <summary>The workspace-relative entry the prompt is about.</summary>
    public const string Entry = ".tokensaver";

    /// <summary>
    ///     True when the user has never answered (<see cref="RepoHelperBehaviorConfig.AddTokenSaverToGitignore" />
    ///     is null) AND <c>.tokensaver</c> is not already covered by the workspace <c>.gitignore</c>.
    /// </summary>
    public static bool ShouldAsk(BehaviorConfigService config, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.GetConfig(workspaceRoot).RepoHelper?.AddTokenSaverToGitignore is not null)
        {
            return false;
        }

        return !GitignoreFilter.EntryIsCovered(workspaceRoot, Entry);
    }

    /// <summary>The message an AI should relay to the user when <see cref="ShouldAsk" /> is true.</summary>
    public static string PromptText =>
        "TokenSaver writes its cache to `.tokensaver/`, which is not currently in this workspace's " +
        ".gitignore. Ask the user whether they'd like to add `.tokensaver/` to .gitignore, and whether " +
        "that choice should be remembered globally (for every workspace on this machine) or just here. " +
        "Then record the answer by calling ManageConfig(action: \"set-gitignore-pref\", add: <true|false>, " +
        "scope: \"local\"|\"global\"). This note stops appearing once answered.";
}

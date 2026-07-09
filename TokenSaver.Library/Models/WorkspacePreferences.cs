namespace TokenSaver.Library.Models;

/// <summary>
///     Per-workspace, per-agent user preferences persisted to ".tokensaver/preferences.json".
///     All settings are optional; absent keys fall back to adapter defaults. Moved out of the old
///     LocalContextService (now WorkspaceIndexService) so it isn't nested inside the indexing service.
/// </summary>
public sealed class WorkspacePreferences
{
    /// <summary>
    ///     Per-agent overrides. Key = agent family name (e.g. "Copilot", "Continue", "Cursor", "Anthropic").
    /// </summary>
    public Dictionary<string, AgentPreferences> Agents { get; set; } = [];
}

public sealed class AgentPreferences
{
    /// <summary>
    ///     When true, send the full file content instead of a summary for files that fit within the
    ///     per-file token limit. Set this for agents that won't ask for missing context (Copilot, Continue).
    /// </summary>
    public bool? PreferFullFileContent { get; set; }

    /// <summary>Override the adapter's default per-file token limit. 0 = no limit.</summary>
    public int? MaxFileSizeTokens { get; set; }

    /// <summary>
    ///     When true, split files that exceed MaxFileSizeTokens at logical boundaries
    ///     rather than truncating them.
    /// </summary>
    public bool? SplitLargeFiles { get; set; }
}

namespace AiDevTools.TokenSaver.CodeHelper.Research;

/// <summary>
///     Describes how the calling AI client prefers to receive research context.
///     Resolved by the MCP tool layer (which has AgentHelpers) and passed into
///     ResearchContextService so this library stays free of AiDevHelpers.AgentHelpers.Core.
/// </summary>
public sealed record ResearchContextFormat
{
    /// <summary>Safe default: markdown, will-ask, generous budget.</summary>
    public static readonly ResearchContextFormat Default = new();

    /// <summary>
    ///     True when the calling AI will proactively re-read files it needs
    ///     (Claude Code, Cursor). Send summaries + retrieval hints.
    ///     False means embed full content — the AI sees only what we send (Copilot, Continue).
    /// </summary>
    public bool WillAskForMissingContext { get; init; } = true;

    /// <summary>Wrap sections in XML tags (Anthropic models).</summary>
    public bool UseXmlSectionTags { get; init; }

    /// <summary>Use Markdown ## headers to separate sections.</summary>
    public bool UseMarkdownHeaders { get; init; } = true;

    /// <summary>Advisory total char budget (tokens * 4).</summary>
    public int CharBudget { get; init; } = 60_000 * 4;

    /// <summary>Hard per-file char limit. 0 = no enforced limit.</summary>
    public int PerFileCharLimit { get; init; }
}

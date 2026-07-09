using TokenSaver.Library.Models.AiProviders;

namespace TokenSaver.Library.Services;

/// <summary>
///     Formerly auto-detected installed AI chat providers (VS Code Copilot, Claude Code,
///     Continue, Cursor, Cline, Sixth) and silently wrote/rewrote this MCP server's entry
///     into each provider's config file on every server startup.
///     That approach is removed: writing MCP client configuration files programmatically
///     proved unreliable across providers — different tools use different file formats and
///     root keys, some are JSONC with comments/trailing commas, and concurrent edits by the
///     user's editor could race with an automated writer and clobber each other's changes.
///     Auto-detection/auto-write is now a no-op. Use the "TokenSaver: Open Install Prompt"
///     VS Code command instead, which opens <c>INSTALL_PROMPT.md</c> — copy-paste
///     instructions (for httpStreaming or stdio transport) that an AI agent or the user can
///     follow to wire up any of these providers manually, with full context and verification.
///     Note: Claude Desktop / Cowork (claude_desktop_config.json) was never handled here —
///     that one-time registration still happens via the installer script (setup.ps1).
/// </summary>
public class AiProviderConfigService
{
    /// <summary>
    ///     Default MCP server key name used in all provider config files.
    ///     Changing this renames the entry everywhere on next update.
    /// </summary>
    public const string DefaultServerKey = "tokensaver";

    // ── Detection ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Formerly scanned the local machine for installed AI providers (VS Code Copilot,
    ///     Cline/Roo-Code, Sixth AI, Claude Code, Cursor) so their config files could be
    ///     auto-written. Detection/auto-write is removed for all providers — always returns
    ///     an empty list now. See the class doc comment and INSTALL_PROMPT.md for the
    ///     manual-install flow that replaces this.
    /// </summary>
    public IReadOnlyList<DetectedProvider> DetectProviders()
    {
        return [];
    }

    // ── Update ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Formerly detected all providers and upserted the TokenSaver MCP server entry into
    ///     each config. Since <see cref="DetectProviders" /> now always returns an empty list,
    ///     this returns an empty result list — kept only so existing callers (the
    ///     <c>/api/register-providers</c> endpoint, <c>RegisterMcpProviders</c> MCP tool) don't
    ///     need to change their call sites. See INSTALL_PROMPT.md for the manual-install flow.
    /// </summary>
    public Task<IReadOnlyList<ConfigUpdateResult>> UpdateAllProvidersAsync(
        int mcpPort,
        string serverKey = DefaultServerKey,
        string? workspaceRoot = null,
        string? serverExePath = null,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ConfigUpdateResult>>([]);
    }

    /// <summary>
    ///     Formerly upserted the TokenSaver MCP server entry into a single provider's config
    ///     file. Auto-write is removed for all providers — always reports no-op now. See
    ///     INSTALL_PROMPT.md and the "TokenSaver: Open Install Prompt" VS Code command for
    ///     the manual replacement.
    /// </summary>
    public Task<ConfigUpdateResult> UpdateProviderAsync(
        DetectedProvider provider,
        int mcpPort,
        string serverKey = DefaultServerKey,
        string? workspaceRoot = null,
        string? serverExePath = null,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ConfigUpdateResult
        {
            Provider = provider.Type,
            Success = false,
            Message = "Auto-install removed — see INSTALL_PROMPT.md / 'TokenSaver: Open Install Prompt'."
        });
    }
}

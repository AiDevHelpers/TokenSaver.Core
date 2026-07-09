namespace TokenSaver.Library.Models.AiProviders;

public enum ProviderType
{
    /// <summary>GitHub Copilot Chat in VS Code. Config: %APPDATA%\Code\User\mcp.json, root key "servers".</summary>
    VsCodeCopilot,

    /// <summary>
    ///     Claude Desktop and Cowork (share the same config file). Config: %APPDATA%\Claude\claude_desktop_config.json,
    ///     root key "mcpServers".
    /// </summary>
    ClaudeDesktop,

    /// <summary>Claude Code CLI. Config: ~/.claude.json, root key "mcpServers".</summary>
    ClaudeCode,

    /// <summary>
    ///     Continue extension. Config: ~/.continue/mcpServers/tokensaver.json (drop-folder, works with both yaml and
    ///     json versions).
    /// </summary>
    Continue,

    /// <summary>Cursor editor. Config: ~/.cursor/mcp.json, root key "mcpServers".</summary>
    Cursor,

    /// <summary>Cline / Roo-Code VS Code extension. Shares VS Code's mcp.json with Copilot, root key "servers".</summary>
    Cline,

    /// <summary>Sixth AI VS Code extension. Shares VS Code's mcp.json with Copilot, root key "servers".</summary>
    Sixth
}

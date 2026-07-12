namespace TokenSaver.Config.Contracts;

/// <summary>
/// Service for managing behavior configuration that merges local and global settings.
/// Implementations read and write configuration from workspace and global config files.
/// </summary>
public interface IBehaviorConfigService
{
    /// <summary>
    /// Gets the current merged behavior configuration for a workspace root.
    /// Local config values take precedence over global values.
    /// </summary>
    TokenSaverBehaviorConfig GetConfig(string workspaceRoot);

    /// <summary>
    /// Updates the behavior configuration in a specific scope.
    /// </summary>
    void Set(string workspaceRoot, ConfigScope scope, Func<TokenSaverBehaviorConfig, TokenSaverBehaviorConfig> mutate);
}

/// <summary>
/// Configuration scope for behavior settings.
/// </summary>
public enum ConfigScope
{
    /// <summary>Workspace-local configuration file (.tokensaver/config.json).</summary>
    Local,

    /// <summary>Machine-global configuration file (%ProgramData%\TokenSaver\config.json).</summary>
    Global
}

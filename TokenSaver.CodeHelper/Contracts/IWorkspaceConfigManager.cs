namespace TokenSaver.CodeHelper.Contracts;

/// <summary>
/// Service for managing workspace-specific configuration and root path resolution.
/// Handles locating and accessing workspace preferences files.
/// </summary>
public interface IWorkspaceConfigManager
{
    /// <summary>
    /// Gets the workspace root directory.
    /// Resolves from nearest .git or solution file, or from configuration.
    /// </summary>
    string GetWorkspaceRoot();
}

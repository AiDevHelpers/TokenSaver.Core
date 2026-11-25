namespace AiDevTools.TokenSaver.WslControl.Contracts;

using AiDevTools.TokenSaver.WslControl.Models;

/// <summary>
///     Prepares a WSL distribution as a debug destination: verifies the debugger backend is
///     present for the target runtime and emits (optionally writes) the VS Code launch
///     configuration that runs the program inside WSL, so the existing debugger-control tools
///     can drive a session that executes in the distro.
/// </summary>
public interface IWslDebugConfigService
{
    /// <summary>
    ///     Prepares <paramref name="distro" /> for debugging <paramref name="programPath" /> under
    ///     <paramref name="runtime" />. When <paramref name="writeToWorkspace" /> is true, merges
    ///     the generated configuration into <c>{workspaceRoot}/.vscode/launch.json</c>.
    /// </summary>
    Task<WslDebugSetupResult> PrepareDebugTargetAsync(
        WslRuntime runtime,
        string programPath,
        string? distro = null,
        string? workspaceRoot = null,
        bool writeToWorkspace = false,
        CancellationToken ct = default);
}

namespace TokenSaver.WslControl.Models;

/// <summary>
///     Language/runtime a WSL debug target is prepared for. Determines which debugger backend
///     must be present in the distro and which VS Code launch configuration is emitted.
/// </summary>
public enum WslRuntime
{
    /// <summary>.NET (uses <c>vsdbg</c> inside WSL, <c>coreclr</c> launch type).</summary>
    DotNet,

    /// <summary>Python (uses <c>debugpy</c>).</summary>
    Python,

    /// <summary>Node.js (uses the built-in <c>node</c> inspector).</summary>
    Node
}

/// <summary>
///     Result of preparing a WSL distribution as a debug destination: whether the debugger
///     backend is present, and the VS Code <c>launch.json</c> configuration fragment that targets
///     the distro so "start debugging" runs the program inside WSL.
/// </summary>
/// <param name="Distro">Distribution prepared as the debug target.</param>
/// <param name="Runtime">Runtime the configuration targets.</param>
/// <param name="BackendReady">True when the required debugger backend is installed in the distro.</param>
/// <param name="BackendDetail">What was found/missing for the backend (e.g. "vsdbg present at ~/.vsdbg").</param>
/// <param name="LaunchConfigName">The <c>name</c> of the emitted launch configuration.</param>
/// <param name="LaunchConfigJson">The launch configuration object, ready to merge into launch.json.</param>
/// <param name="ConfigWritten">True when the configuration was written into the workspace launch.json.</param>
/// <param name="InstallHint">When the backend is missing, a natural-language request the caller can pass back to install it.</param>
public sealed record WslDebugSetupResult(
    string Distro,
    WslRuntime Runtime,
    bool BackendReady,
    string BackendDetail,
    string LaunchConfigName,
    string LaunchConfigJson,
    bool ConfigWritten,
    string? InstallHint = null);

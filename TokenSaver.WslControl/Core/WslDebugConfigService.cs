namespace TokenSaver.WslControl.Core;

using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using TokenSaver.WslControl.Contracts;
using TokenSaver.WslControl.Models;

/// <summary>
///     Default <see cref="IWslDebugConfigService" />. Verifies the runtime's debugger backend is
///     present in the target distro and produces a VS Code launch configuration that runs the
///     program inside WSL over <c>pipeTransport</c> (classic <c>wsl.exe -d &lt;distro&gt;</c>
///     model). Writing into an existing <c>launch.json</c> is intentionally conservative: it only
///     creates the file when absent, otherwise it returns the fragment for the caller to merge —
///     never clobbering a user's hand-authored (possibly JSONC) config.
/// </summary>
public sealed partial class WslDebugConfigService(
    IWslProvider provider,
    ILogger<WslDebugConfigService> logger) : IWslDebugConfigService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Preparing WSL debug target: {Runtime} in {Distro}")]
    private static partial void LogPreparing(ILogger logger, WslRuntime runtime, string distro);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write launch.json at {Path}")]
    private static partial void LogWriteFailed(ILogger logger, string path, Exception ex);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly IWslProvider _provider = provider;
    private readonly ILogger<WslDebugConfigService> _logger = logger;

    public async Task<WslDebugSetupResult> PrepareDebugTargetAsync(
        WslRuntime runtime,
        string programPath,
        string? distro = null,
        string? workspaceRoot = null,
        bool writeToWorkspace = false,
        CancellationToken ct = default)
    {
        WslDistroInfo? resolved = distro is { Length: > 0 }
            ? new WslDistroInfo(distro, false, 2)
            : await _provider.GetDefaultDistroAsync(ct);

        if (resolved is null)
        {
            return new WslDebugSetupResult(
                distro ?? "(default)", runtime, BackendReady: false,
                BackendDetail: "No WSL distribution is registered.",
                LaunchConfigName: "", LaunchConfigJson: "", ConfigWritten: false,
                InstallHint: "Install a WSL distribution first, e.g. `wsl --install -d Ubuntu`.");
        }

        string distroName = resolved.Name;
        LogPreparing(_logger, runtime, distroName);

        (string checkCmd, string ready, string missing, string installHint) = BackendProbe(runtime);
        WslCommandResult probe = await _provider.RunBashAsync(checkCmd, distroName, TimeSpan.FromSeconds(30), ct);
        bool backendReady = probe.Succeeded && probe.Stdout.Contains("READY", StringComparison.Ordinal);
        string backendDetail = backendReady ? ready : (probe.ExecutionError ?? missing);

        (string name, JsonObject config) = BuildLaunchConfig(runtime, distroName, programPath);
        string configJson = config.ToJsonString(Indented);

        bool written = false;
        if (writeToWorkspace && !string.IsNullOrWhiteSpace(workspaceRoot))
        {
            written = TryWriteLaunchConfig(workspaceRoot, config, name);
        }

        return new WslDebugSetupResult(
            Distro: distroName,
            Runtime: runtime,
            BackendReady: backendReady,
            BackendDetail: backendDetail,
            LaunchConfigName: name,
            LaunchConfigJson: configJson,
            ConfigWritten: written,
            InstallHint: backendReady ? null : installHint);
    }

    // ── Backend detection ───────────────────────────────────────────────────────

    private static (string CheckCmd, string ReadyDetail, string MissingDetail, string InstallHint) BackendProbe(
        WslRuntime runtime) => runtime switch
    {
        WslRuntime.DotNet => (
            "test -x \"$HOME/.vsdbg/vsdbg\" && echo READY || echo MISSING",
            "vsdbg is installed at ~/.vsdbg/vsdbg.",
            "vsdbg (.NET debugger) is not installed in this distro.",
            "install the .NET debugger vsdbg into ~/.vsdbg by running: " +
            "curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l ~/.vsdbg"),

        WslRuntime.Python => (
            "python3 -c 'import debugpy' >/dev/null 2>&1 && echo READY || echo MISSING",
            "debugpy is importable.",
            "debugpy is not installed for python3 in this distro.",
            "install debugpy by running: python3 -m pip install --user debugpy"),

        WslRuntime.Node => (
            "command -v node >/dev/null 2>&1 && echo READY || echo MISSING",
            "node is on PATH; the built-in inspector is available.",
            "node is not installed in this distro.",
            "install Node.js, e.g. via apt: apt-get install -y nodejs npm"),

        _ => ("echo MISSING", "", "Unknown runtime.", "")
    };

    // ── Launch configuration ────────────────────────────────────────────────────

    private static (string Name, JsonObject Config) BuildLaunchConfig(
        WslRuntime runtime, string distro, string programPath)
    {
        switch (runtime)
        {
            case WslRuntime.DotNet:
            {
                const string name = "WSL: .NET Launch";
                JsonObject cfg = new()
                {
                    ["name"] = name,
                    ["type"] = "coreclr",
                    ["request"] = "launch",
                    ["program"] = programPath,
                    ["cwd"] = "${workspaceFolder}",
                    ["stopAtEntry"] = false,
                    ["console"] = "internalConsole",
                    ["pipeTransport"] = new JsonObject
                    {
                        ["pipeCwd"] = "${workspaceFolder}",
                        ["pipeProgram"] = "wsl.exe",
                        ["pipeArgs"] = new JsonArray("-d", distro, "--"),
                        ["debuggerPath"] = "~/.vsdbg/vsdbg"
                    }
                };
                return (name, cfg);
            }

            case WslRuntime.Python:
            {
                const string name = "WSL: Python Launch";
                JsonObject cfg = new()
                {
                    ["name"] = name,
                    ["type"] = "debugpy",
                    ["request"] = "launch",
                    ["program"] = programPath,
                    ["console"] = "integratedTerminal",
                    ["python"] = "wsl.exe",
                    ["pythonArgs"] = new JsonArray("-d", distro, "--", "python3")
                };
                return (name, cfg);
            }

            case WslRuntime.Node:
            default:
            {
                const string name = "WSL: Node Launch";
                JsonObject cfg = new()
                {
                    ["name"] = name,
                    ["type"] = "node",
                    ["request"] = "launch",
                    ["program"] = programPath,
                    ["cwd"] = "${workspaceFolder}",
                    ["useWSL"] = true
                };
                return (name, cfg);
            }
        }
    }

    private bool TryWriteLaunchConfig(string workspaceRoot, JsonObject config, string name)
    {
        string vscodeDir = Path.Combine(workspaceRoot, ".vscode");
        string launchPath = Path.Combine(vscodeDir, "launch.json");

        try
        {
            // Conservative: only author the file when it does not already exist. An existing
            // launch.json may be JSONC with comments/trailing commas that a round-trip would
            // destroy, so we never rewrite it — the caller merges the returned fragment instead.
            if (File.Exists(launchPath))
            {
                return false;
            }

            Directory.CreateDirectory(vscodeDir);
            JsonObject root = new()
            {
                ["version"] = "0.2.0",
                ["configurations"] = new JsonArray(config.DeepClone())
            };
            File.WriteAllText(launchPath, root.ToJsonString(Indented));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(_logger, launchPath, ex);
            return false;
        }
    }
}

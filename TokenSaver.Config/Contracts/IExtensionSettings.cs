namespace TokenSaver.Config.Contracts;

/// <summary>
///     Settings surface shared by the TokenSaver client extensions (VS Code, Visual Studio) that
///     manage the TokenSaver.MCP server and DraftModel.Loader router. This is the union of the
///     VS Code extension's <c>tokenSaver.*</c> <c>package.json</c> settings and the settings the
///     Visual Studio extension needs — one contract so both extensions describe the same knobs,
///     even though each reads/writes them through its own host mechanism (VS Code's native
///     settings store vs. <c>.tokensaver/config.json</c> / <c>%ProgramData%\TokenSaver\config.json</c>).
/// </summary>
public interface IExtensionSettings
{
    /// <summary>Automatically start the TokenSaver MCP server when the extension activates.</summary>
    bool? Autostart { get; }

    /// <summary>Enable verbose logging in the extension's output channel.</summary>
    bool? VerboseLogging { get; }

    /// <summary>Enable CodeHelper auto-workflow interception of code-build prompts.</summary>
    bool? AutoCodeHelperEnabled { get; }

    /// <summary>
    ///     On first activation, ask once whether to install the bundled DraftModel/TokenSaver
    ///     Windows services if they aren't already installed.
    /// </summary>
    bool? AutoInstallPrompt { get; }

    /// <summary>Absolute path to TokenSaver.MCP.exe. Empty/null defaults to the bundled MSI install location.</summary>
    string? McpExePath { get; }

    /// <summary>Server lifecycle settings — see <see cref="IExtensionServerSettings" />.</summary>
    IExtensionServerSettings? Server { get; }

    /// <summary>How the extension launches TokenSaver.MCP from source — see <see cref="IExtensionDotnetSettings" />.</summary>
    IExtensionDotnetSettings? Dotnet { get; }

    /// <summary>Notification display settings — see <see cref="IExtensionNotificationsSettings" />.</summary>
    IExtensionNotificationsSettings? Notifications { get; }

    /// <summary>DraftModel.Loader router settings — see <see cref="IExtensionDraftModelSettings" />.</summary>
    IExtensionDraftModelSettings? DraftModel { get; }
}

/// <summary>TokenSaver.MCP server lifecycle settings.</summary>
public interface IExtensionServerSettings
{
    /// <summary>Port the TokenSaver MCP server listens on.</summary>
    int? Port { get; }

    /// <summary>Seconds to wait for the server to become reachable after launch.</summary>
    int? StartTimeout { get; }

    /// <summary>Stop the server after this many seconds of inactivity. 0 disables idle shutdown.</summary>
    int? IdleTimeout { get; }
}

/// <summary>How the extension launches TokenSaver.MCP directly from source (dev-loop fallback).</summary>
public interface IExtensionDotnetSettings
{
    /// <summary>The dotnet executable used to launch the TokenSaver.MCP server.</summary>
    string? Command { get; }

    /// <summary>Absolute path to the TokenSaver.MCP project directory.</summary>
    string? ProjectPath { get; }
}

/// <summary>Extension notification display settings.</summary>
public interface IExtensionNotificationsSettings
{
    /// <summary>Enable or disable TokenSaver notifications.</summary>
    bool? Enabled { get; }

    /// <summary>"all" or "errors" — whether to show only error notifications or every TokenSaver notification.</summary>
    string? Level { get; }
}

/// <summary>DraftModel.Loader router settings.</summary>
public interface IExtensionDraftModelSettings
{
    /// <summary>Absolute path to DraftModel.Loader.exe. Empty/null defaults to the bundled MSI install location.</summary>
    string? ExePath { get; }

    /// <summary>Health check URL for DraftModel.Loader.</summary>
    string? HealthUrl { get; }

    /// <summary>Seconds to wait for DraftModel.Loader to become reachable after launch.</summary>
    int? StartTimeout { get; }
}

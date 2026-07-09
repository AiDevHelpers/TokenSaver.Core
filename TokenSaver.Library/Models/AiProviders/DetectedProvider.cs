namespace TokenSaver.Library.Models.AiProviders;

public class DetectedProvider
{
    /// <summary>Which AI provider this is.</summary>
    public ProviderType Type { get; init; }

    /// <summary>Human-readable name for display/logging.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    ///     Installed version, parsed from the VS Code extension folder name (e.g. "0.27.2")
    ///     or null if unavailable or not applicable.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>Absolute path to the config file that will be read/written.</summary>
    public string ConfigPath { get; init; } = "";

    /// <summary>True if the config file already exists on disk.</summary>
    public bool IsConfigPresent { get; init; }

    /// <summary>
    ///     For Continue: whether the installed version uses config.yaml (current)
    ///     or config.json (deprecated). Null for other providers.
    /// </summary>
    public ContinueConfigFormat? ContinueFormat { get; init; }

    public override string ToString()
    {
        return
            $"{DisplayName}{(Version != null ? $" v{Version}" : "")} — {ConfigPath}{(IsConfigPresent ? "" : " (not yet created)")}";
    }
}

/// <summary>Indicates which config file format the Continue extension is using.</summary>
public enum ContinueConfigFormat
{
    /// <summary>config.yaml is present — current format.</summary>
    Yaml,

    /// <summary>Only config.json is present — deprecated format.</summary>
    Json,

    /// <summary>Neither config file is present yet.</summary>
    None
}

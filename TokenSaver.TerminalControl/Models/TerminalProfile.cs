namespace TokenSaver.TerminalControl.Models;

public sealed record TerminalProfile(
    string Name,
    string Guid,
    string? CommandLine = null,
    string? StartingDirectory = null,
    string? TabTitle = null,
    string? Icon = null,
    bool UseAcrylic = false,
    double AcrylicOpacity = 0.5,
    string? ColorScheme = null,
    string? FontFace = null,
    int? FontSize = null,
    bool Hidden = false,
    TerminalProfileSource Source = TerminalProfileSource.SettingsJson)
{
    public bool IsDefault { get; init; }
}

public enum TerminalProfileSource
{
    SettingsJson,
    WslDistribution,
    PowerShell,
    CommandPrompt,
    AzureCloudShell,
    Custom
}

namespace TokenSaver.WslControl.Models;

/// <summary>
///     Result of probing whether WSL is usable on this machine.
/// </summary>
/// <param name="IsInstalled">True when the Windows Subsystem for Linux is present and supported.</param>
/// <param name="Reason">Human-readable explanation when <paramref name="IsInstalled" /> is false, else null.</param>
/// <param name="DefaultDistro">Name of the default distribution, when one is registered.</param>
/// <param name="DistroCount">Number of registered distributions.</param>
/// <param name="InstallUrl">Docs URL the caller can surface when WSL is missing.</param>
public sealed record WslAvailability(
    bool IsInstalled,
    string? Reason = null,
    string? DefaultDistro = null,
    int DistroCount = 0,
    Uri? InstallUrl = null)
{
    /// <summary>True when WSL is installed and at least one distribution is registered.</summary>
    public bool IsUsable => IsInstalled && DistroCount > 0;

    public static readonly Uri DefaultInstallUrl = new("https://learn.microsoft.com/windows/wsl/install");
}

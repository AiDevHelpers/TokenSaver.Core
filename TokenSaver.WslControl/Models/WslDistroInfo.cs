namespace AiDevTools.TokenSaver.WslControl.Models;

/// <summary>
///     A registered WSL distribution, projected from the SDK's <c>DistroInfo</c> so callers
///     never take a direct dependency on the Community.Wsl.Sdk types.
/// </summary>
/// <param name="Name">Distribution name, e.g. <c>Ubuntu-22.04</c>.</param>
/// <param name="IsDefault">True when this is the default distribution.</param>
/// <param name="WslVersion">1 or 2 (the WSL kernel version backing the distro).</param>
/// <param name="BasePath">Windows path to the distribution's backing files, when known.</param>
/// <param name="DefaultUid">Default Linux user id the distro launches as.</param>
public sealed record WslDistroInfo(
    string Name,
    bool IsDefault,
    int WslVersion,
    string? BasePath = null,
    int DefaultUid = 0);

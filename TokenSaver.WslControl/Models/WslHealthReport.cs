namespace AiDevTools.TokenSaver.WslControl.Models;

/// <summary>
///     A "is this distro actually working" snapshot: the report answers the question a caller
///     asks when it wants to know whether it can rely on the distro before doing real work.
/// </summary>
/// <param name="Distro">Distribution that was probed.</param>
/// <param name="IsHealthy">True when the probe command ran and returned the expected fields.</param>
/// <param name="OsDescription"><c>PRETTY_NAME</c> from <c>/etc/os-release</c>, e.g. "Ubuntu 22.04.4 LTS".</param>
/// <param name="Kernel">Kernel string from <c>uname -sr</c>.</param>
/// <param name="DefaultUser">Login name the distro runs as (<c>id -un</c>).</param>
/// <param name="HasSudo">True when the user can run <c>sudo</c> non-interactively (needed for apt).</param>
/// <param name="HasApt">True when <c>apt-get</c> is on PATH (Debian/Ubuntu family).</param>
/// <param name="Detail">Diagnostic text when unhealthy, else null.</param>
public sealed record WslHealthReport(
    string Distro,
    bool IsHealthy,
    string? OsDescription = null,
    string? Kernel = null,
    string? DefaultUser = null,
    bool HasSudo = false,
    bool HasApt = false,
    string? Detail = null);

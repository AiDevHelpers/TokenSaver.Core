namespace AiDevTools.TokenSaver.WslControl.Contracts;

using AiDevTools.TokenSaver.WslControl.Models;

/// <summary>
///     The WSL provider: detects WSL, enumerates distributions, reports health, and runs
///     commands (including the common apt and build operations) inside a distribution.
///     Mirrors the role <c>ITerminalProvider</c> plays for terminals.
/// </summary>
public interface IWslProvider
{
    /// <summary>Checks whether WSL is installed/supported and how many distros are registered.</summary>
    Task<WslAvailability> CheckAvailabilityAsync(CancellationToken ct = default);

    /// <summary>Lists all registered distributions.</summary>
    Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken ct = default);

    /// <summary>Gets the default distribution, or null when none is registered.</summary>
    Task<WslDistroInfo?> GetDefaultDistroAsync(CancellationToken ct = default);

    /// <summary>
    ///     Probes whether <paramref name="distro" /> (or the default distro when null) is
    ///     actually working: OS, kernel, user, and whether sudo/apt are usable.
    /// </summary>
    Task<WslHealthReport> CheckHealthAsync(string? distro = null, CancellationToken ct = default);

    /// <summary>
    ///     Runs a bash command line inside <paramref name="distro" /> (or the default distro).
    ///     The line is executed via <c>/bin/bash -c</c>, so pipes, redirection, and env work.
    /// </summary>
    Task<WslCommandResult> RunBashAsync(
        string commandLine,
        string? distro = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    /// <summary>Refreshes apt package lists (<c>apt-get update</c>). Requires sudo.</summary>
    Task<WslCommandResult> AptUpdateAsync(string? distro = null, CancellationToken ct = default);

    /// <summary>Upgrades installed apt packages (<c>apt-get upgrade -y</c>). Requires sudo.</summary>
    Task<WslCommandResult> AptUpgradeAsync(string? distro = null, CancellationToken ct = default);

    /// <summary>Installs apt packages (<c>apt-get install -y</c>). Requires sudo.</summary>
    Task<WslCommandResult> AptInstallAsync(
        IReadOnlyList<string> packages,
        string? distro = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Runs a build command in <paramref name="workingDirectory" />. The directory may be a
    ///     Windows path (translated via <c>wslpath</c>) or an existing Linux path.
    /// </summary>
    Task<WslCommandResult> BuildAsync(
        string workingDirectory,
        string buildCommand,
        string? distro = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}

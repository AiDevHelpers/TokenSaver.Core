namespace AiDevTools.TokenSaver.WslControl.Models;

/// <summary>
///     The category of a planned WSL action. Drives confirmation and routing:
///     read-only actions run freely; anything that mutates the distro is gated.
/// </summary>
public enum WslActionKind
{
    /// <summary>A read-only or otherwise safe shell command.</summary>
    RunCommand,

    /// <summary>Install one or more apt packages (mutates the system).</summary>
    AptInstall,

    /// <summary>Refresh the apt package lists (<c>apt-get update</c>).</summary>
    AptUpdate,

    /// <summary>Upgrade installed apt packages (<c>apt-get upgrade</c>).</summary>
    AptUpgrade,

    /// <summary>Run a build/compile command in a working directory.</summary>
    Build,

    /// <summary>The local model could not confidently translate the request.</summary>
    Unknown
}

/// <summary>
///     A single WSL action synthesized from a natural-language request by the local model.
///     The calling AI describes intent ("update the packages", "install ripgrep and build the
///     project"); the planner turns that into an exact command plus a safety verdict — so the
///     caller never has to author shell syntax itself.
/// </summary>
/// <param name="Kind">Category of the action.</param>
/// <param name="Command">The exact bash command line to run (empty when <see cref="WslActionKind.Unknown" />).</param>
/// <param name="Packages">Parsed package names for <see cref="WslActionKind.AptInstall" />.</param>
/// <param name="Explanation">Short natural-language description of what the command does.</param>
/// <param name="RequiresConfirmation">
///     True when the action mutates the distro (installs/removes software, uses sudo, writes or
///     deletes files, changes system state) and must not be run without explicit user approval.
/// </param>
/// <param name="Warning">Optional caveat surfaced to the caller (e.g. destructive-looking command).</param>
public sealed record WslActionPlan(
    WslActionKind Kind,
    string Command,
    IReadOnlyList<string> Packages,
    string Explanation,
    bool RequiresConfirmation,
    string? Warning = null)
{
    public static WslActionPlan UnknownPlan(string reason) =>
        new(WslActionKind.Unknown, "", [], reason, false);
}

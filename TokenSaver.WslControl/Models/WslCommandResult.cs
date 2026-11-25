namespace AiDevTools.TokenSaver.WslControl.Models;

/// <summary>
///     Outcome of running a single command inside a WSL distribution.
/// </summary>
/// <param name="Distro">Distribution the command ran in.</param>
/// <param name="CommandLine">The command line that was executed (for display/audit).</param>
/// <param name="ExitCode">Process exit code; 0 conventionally means success.</param>
/// <param name="Stdout">Captured standard output (trimmed of a single trailing newline).</param>
/// <param name="Stderr">Captured standard error.</param>
/// <param name="TimedOut">True when the command was cancelled/killed for exceeding its timeout.</param>
/// <param name="ExecutionError">Set when the command could not be launched at all (vs. a non-zero exit).</param>
public sealed record WslCommandResult(
    string Distro,
    string CommandLine,
    int ExitCode,
    string Stdout = "",
    string Stderr = "",
    bool TimedOut = false,
    string? ExecutionError = null)
{
    /// <summary>True when the command launched and returned exit code 0 without timing out.</summary>
    public bool Succeeded => ExecutionError is null && !TimedOut && ExitCode == 0;
}

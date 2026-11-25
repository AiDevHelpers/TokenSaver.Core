namespace AiDevTools.TokenSaver.WslControl.Contracts;

using AiDevTools.TokenSaver.WslControl.Models;

/// <summary>
///     Low-level seam over "run one program in a WSL distro and capture its output". The default
///     implementation wraps Community.Wsl.Sdk's <c>Command</c>; tests substitute a fake so the
///     provider's command-building logic can be exercised without a real WSL install.
/// </summary>
public interface IWslCommandExecutor
{
    /// <summary>
    ///     Runs <paramref name="fileName" /> with <paramref name="args" /> inside
    ///     <paramref name="distro" /> and returns the captured result. Implementations must not
    ///     throw for a non-zero exit code — that is reported via <see cref="WslCommandResult" />.
    /// </summary>
    /// <param name="distro">Distribution name to run in.</param>
    /// <param name="fileName">Executable to launch (e.g. <c>/bin/bash</c>).</param>
    /// <param name="args">Arguments passed verbatim (no shell splitting).</param>
    /// <param name="timeout">Kill the command if it runs longer than this.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WslCommandResult> ExecuteAsync(
        string distro,
        string fileName,
        string[] args,
        TimeSpan timeout,
        CancellationToken ct = default);
}

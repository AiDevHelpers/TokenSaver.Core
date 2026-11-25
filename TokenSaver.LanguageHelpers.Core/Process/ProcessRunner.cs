using Microsoft.Extensions.Logging;

using AiDevTools.TokenSaver.Common.Core.Processes;

namespace AiDevTools.TokenSaver.LanguageHelpers.Core.Process;

/// <summary>Captured result of running an external process to completion.</summary>
public sealed record ProcessResult
{
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }

    /// <summary>True when the executable could not be found / launched at all (as opposed to running and failing).</summary>
    public bool Launched { get; init; } = true;

    /// <summary>True when the process was killed because it exceeded its timeout.</summary>
    public bool TimedOut { get; init; }

    public bool Success => Launched && !TimedOut && ExitCode == 0;

    /// <summary>Combined stdout + stderr, convenient for logging and <c>RawOutput</c> capture.</summary>
    public string CombinedOutput =>
        string.IsNullOrEmpty(StandardError) ? StandardOutput : $"{StandardOutput}\n{StandardError}";
}

/// <summary>
///     Runs external CLI tools (dotnet, pwsh, ruff, ...) that every language helper shells out to.
///     Centralizes the "redirect + read-to-end + wait-with-timeout" dance and the "is this tool even on
///     PATH?" probe so the language projects stay focused on argument construction and output parsing.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    ///     Runs <paramref name="fileName" /> with <paramref name="arguments" /> in
    ///     <paramref name="workingDirectory" /> and captures its output. Never throws for ordinary process
    ///     failures — inspect <see cref="ProcessResult.Launched" /> / <see cref="ProcessResult.ExitCode" />.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    /// <summary>Returns true when <paramref name="fileName" /> can be launched (resolvable on PATH).</summary>
    Task<bool> IsAvailableAsync(string fileName, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        CommandResult result = await CommandRunner.RunAsync(
            new CommandRequest
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                Timeout = timeout ?? DefaultTimeout,
            },
            ct);

        if (!result.Launched)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Failed to launch {FileName}: {Error}", fileName, result.LaunchError);
            }

            return NotLaunched(fileName);
        }

        if (result.TimedOut)
        {
            logger.LogWarning("{FileName} timed out after {Timeout}", fileName, timeout ?? DefaultTimeout);
        }

        return new ProcessResult
        {
            ExitCode = result.ExitCode,
            StandardOutput = result.StdOut,
            StandardError = result.StdErr,
            TimedOut = result.TimedOut,
        };
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(string fileName, CancellationToken ct = default)
    {
        // "--version" is understood by dotnet, pwsh, ruff, python, and virtually every CLI we target.
        // We only care whether the executable launches, not what it prints.
        ProcessResult result = await RunAsync(
            fileName,
            ["--version"],
            Directory.GetCurrentDirectory(),
            TimeSpan.FromSeconds(20),
            ct);
        return result.Launched;
    }

    private static ProcessResult NotLaunched(string fileName) => new()
    {
        ExitCode = -1,
        StandardOutput = string.Empty,
        StandardError = $"Executable '{fileName}' could not be launched (not found on PATH?).",
        Launched = false
    };
}

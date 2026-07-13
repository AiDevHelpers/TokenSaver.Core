using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text;

namespace TokenSaver.LanguageHelpers.Core.Process;

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
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using System.Diagnostics.Process process = new() { StartInfo = psi };

        StringBuilder stdout = new();
        StringBuilder stderr = new();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return NotLaunched(fileName);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Failed to launch {FileName}", fileName);
            }

            return NotLaunched(fileName);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeoutCts = new(timeout ?? DefaultTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process);
            logger.LogWarning("{FileName} timed out after {Timeout}", fileName, timeout ?? DefaultTimeout);
            return new ProcessResult
            {
                ExitCode = -1,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString(),
                TimedOut = true
            };
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString()
        };
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(string fileName, CancellationToken ct = default)
    {
        // "--version" is understood by dotnet, pwsh, ruff, python, and virtually every CLI we target.
        // We only care whether the executable launches, not what it prints.
        try
        {
            ProcessResult result = await RunAsync(
                fileName,
                ["--version"],
                Directory.GetCurrentDirectory(),
                TimeSpan.FromSeconds(20),
                ct);
            return result.Launched;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Availability probe for {FileName} threw", fileName);
            }

            return false;
        }
    }

    private static ProcessResult NotLaunched(string fileName) => new()
    {
        ExitCode = -1,
        StandardOutput = string.Empty,
        StandardError = $"Executable '{fileName}' could not be launched (not found on PATH?).",
        Launched = false
    };

    private void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Failed to kill process {Id}", process.Id);
            }
        }
    }
}

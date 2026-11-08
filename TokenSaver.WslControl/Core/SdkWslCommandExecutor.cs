namespace TokenSaver.WslControl.Core;

using Community.Wsl.Sdk;

using Microsoft.Extensions.Logging;

using TokenSaver.WslControl.Contracts;
using TokenSaver.WslControl.Models;

/// <summary>
///     Default <see cref="IWslCommandExecutor" />: runs a command with Community.Wsl.Sdk's
///     <c>Command</c>, capturing stdout/stderr as strings and never throwing for a non-zero exit
///     (<see cref="CommandExecutionOptions.FailOnNegativeExitCode" /> is off). A timeout is
///     enforced cooperatively via <see cref="TaskExtensions" />; if it fires the result is marked
///     <see cref="WslCommandResult.TimedOut" />.
/// </summary>
public sealed partial class SdkWslCommandExecutor(ILogger<SdkWslCommandExecutor> logger) : IWslCommandExecutor
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "WSL exec [{Distro}]: {FileName} ({ArgCount} args)")]
    private static partial void LogExec(ILogger logger, string distro, string fileName, int argCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WSL command in {Distro} failed to launch")]
    private static partial void LogLaunchFailure(ILogger logger, string distro, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WSL command in {Distro} timed out after {Seconds}s")]
    private static partial void LogTimeout(ILogger logger, string distro, double seconds);

    private readonly ILogger<SdkWslCommandExecutor> _logger = logger;

    public async Task<WslCommandResult> ExecuteAsync(
        string distro,
        string fileName,
        string[] args,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        string display = fileName + " " + string.Join(' ', args);
        LogExec(_logger, distro, fileName, args.Length);

        CommandExecutionOptions options = new()
        {
            FailOnNegativeExitCode = false,
            StdoutDataProcessingMode = DataProcessingMode.String,
            StdErrDataProcessingMode = DataProcessingMode.String,
            StdInDataProcessingMode = DataProcessingMode.Drop
        };

        try
        {
            using Command command = new(distro, fileName, args, options);
            Task<CommandResult> exec = command.StartAndGetResultsAsync();

            CommandResult result;
            try
            {
                result = await exec.WaitAsync(timeout, ct);
            }
            catch (TimeoutException)
            {
                LogTimeout(_logger, distro, timeout.TotalSeconds);
                return new WslCommandResult(
                    distro, display, ExitCode: -1, TimedOut: true,
                    ExecutionError: $"Command exceeded the {timeout.TotalSeconds:0}s timeout.");
            }

            return new WslCommandResult(
                distro,
                display,
                result.ExitCode,
                Stdout: (result.Stdout ?? string.Empty).TrimEnd('\n', '\r'),
                Stderr: (result.Stderr ?? string.Empty).TrimEnd('\n', '\r'));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogLaunchFailure(_logger, distro, ex);
            return new WslCommandResult(
                distro, display, ExitCode: -1, ExecutionError: ex.Message);
        }
    }
}

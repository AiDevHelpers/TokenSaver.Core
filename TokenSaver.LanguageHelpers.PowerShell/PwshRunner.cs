using AiDevTools.TokenSaver.LanguageHelpers.Core.Process;

using Microsoft.Extensions.Logging;

using System.Text.Json;

namespace AiDevTools.TokenSaver.LanguageHelpers.PowerShell;

/// <summary>
///     Runs short PowerShell scripts out of process via <c>pwsh</c> and deserializes their JSON stdout.
///     Each language operation is expressed as an embedded script (parse-check, analyze, fix, format) that
///     the C# helper hands a list of files as positional arguments; keeping the scripts tiny and the file
///     enumeration on the C# side means the scripts just loop over <c>$args</c>.
/// </summary>
public sealed class PwshRunner(IProcessRunner processRunner, ILogger logger)
{
    /// <summary>Returns true when a <c>pwsh</c> executable is resolvable on PATH.</summary>
    public Task<bool> IsPwshAvailableAsync(CancellationToken ct) => processRunner.IsAvailableAsync("pwsh", ct);

    /// <summary>Returns true when the PSScriptAnalyzer module is installed for the resolved pwsh.</summary>
    public async Task<bool> IsScriptAnalyzerAvailableAsync(string workingDirectory, CancellationToken ct)
    {
        ProcessResult result = await RunInlineAsync(
            "if (Get-Module -ListAvailable -Name PSScriptAnalyzer) { 'yes' } else { 'no' }",
            [], workingDirectory, ct);
        return result.Launched && result.StandardOutput.Contains("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Writes <paramref name="scriptBody" /> to a temp <c>.ps1</c>, runs it with <c>-File</c> passing
    ///     <paramref name="arguments" /> as positional args, and returns the captured result. The temp file is
    ///     always cleaned up.
    /// </summary>
    public async Task<ProcessResult> RunScriptAsync(
        string scriptBody,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        string scriptPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tokensaver-ps-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, scriptBody, ct);

        try
        {
            List<string> args = ["-NoProfile", "-NonInteractive", "-File", scriptPath, .. arguments];
            return await processRunner.RunAsync("pwsh", args, workingDirectory, TimeSpan.FromMinutes(5), ct);
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    private async Task<ProcessResult> RunInlineAsync(
        string command, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct)
    {
        List<string> args = ["-NoProfile", "-NonInteractive", "-Command", command, .. arguments];
        return await processRunner.RunAsync("pwsh", args, workingDirectory, TimeSpan.FromSeconds(60), ct);
    }

    /// <summary>Deserializes a script's JSON stdout into <typeparamref name="T" />, tolerating empty output.</summary>
    public T? Deserialize<T>(string stdout)
    {
        string trimmed = stdout.Trim();
        if (trimmed.Length == 0)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(trimmed, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse pwsh JSON output");
            return default;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Could not delete temp script {Path}", path);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

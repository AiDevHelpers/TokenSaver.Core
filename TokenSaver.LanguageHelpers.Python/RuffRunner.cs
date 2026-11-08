using TokenSaver.LanguageHelpers.Core.Models;
using TokenSaver.LanguageHelpers.Core.Process;

using Microsoft.Extensions.Logging;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSaver.LanguageHelpers.Python;

/// <summary>A single ruff diagnostic from <c>ruff check --output-format json</c>.</summary>
public sealed record RuffDiagnostic
{
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("filename")] public string? Filename { get; init; }
    [JsonPropertyName("location")] public RuffPosition? Location { get; init; }
    [JsonPropertyName("fix")] public RuffFix? Fix { get; init; }

    public bool IsFixable => Fix is not null;

    public CodeIssue ToCodeIssue() => new()
    {
        // ruff has no severity dimension; syntax errors (E9xx) are the only true errors.
        Severity = Code is not null && Code.StartsWith("E9", StringComparison.Ordinal)
            ? CodeIssueSeverity.Error
            : CodeIssueSeverity.Warning,
        Message = Message ?? "ruff finding.",
        RuleId = Code,
        IsFixable = IsFixable,
        Location = Filename is null
            ? null
            : new CodeLocation { FilePath = Filename, Line = Location?.Row ?? 0, Column = Location?.Column ?? 0 }
    };
}

/// <summary>Row/column pair from a ruff diagnostic (1-based).</summary>
public sealed record RuffPosition
{
    [JsonPropertyName("row")] public int Row { get; init; }
    [JsonPropertyName("column")] public int Column { get; init; }
}

/// <summary>The fix payload ruff attaches to a diagnostic when an automatic fix is available.</summary>
public sealed record RuffFix
{
    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>Outcome of a <c>ruff check</c> run: the process result plus its parsed diagnostics.</summary>
public sealed record RuffCheckOutcome
{
    public required bool ToolAvailable { get; init; }
    public required ProcessResult Process { get; init; }
    public IReadOnlyList<RuffDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
///     Thin wrapper over the <c>ruff</c> CLI. All lint/fix/import operations go through
///     <see cref="CheckAsync" /> (JSON output), and formatting through <see cref="FormatAsync" /> /
///     <see cref="FormatCheckAsync" />. ruff discovers its own config (<c>ruff.toml</c> /
///     <c>pyproject.toml</c>) from the working directory, so the helper just points it at the scope.
/// </summary>
public sealed class RuffRunner(IProcessRunner processRunner, ILogger logger)
{
    /// <summary>Returns true when a <c>ruff</c> executable is resolvable on PATH.</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct) => processRunner.IsAvailableAsync("ruff", ct);

    /// <summary>Runs <c>ruff check</c> over the scope, optionally restricting rules and/or applying fixes.</summary>
    public async Task<RuffCheckOutcome> CheckAsync(
        AnalysisScope scope,
        IReadOnlyList<string>? select = null,
        bool fix = false,
        CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct))
        {
            return new RuffCheckOutcome { ToolAvailable = false, Process = NotLaunched() };
        }

        List<string> args = ["check", "--output-format", "json", "--no-cache"];
        if (fix)
        {
            args.Add("--fix");
        }

        if (select is { Count: > 0 })
        {
            args.Add("--select");
            args.Add(string.Join(",", select));
        }

        args.AddRange(TargetArgs(scope));

        ProcessResult result = await processRunner.RunAsync(
            "ruff", args, scope.WorkspaceRoot, TimeSpan.FromMinutes(5), ct);

        return new RuffCheckOutcome
        {
            ToolAvailable = true,
            Process = result,
            Diagnostics = ParseDiagnostics(result.StandardOutput)
        };
    }

    /// <summary>Runs <c>ruff format</c> to reformat the scope in place.</summary>
    public async Task<ProcessResult> FormatAsync(AnalysisScope scope, CancellationToken ct = default)
    {
        List<string> args = ["format", .. TargetArgs(scope)];
        return await processRunner.RunAsync("ruff", args, scope.WorkspaceRoot, TimeSpan.FromMinutes(5), ct);
    }

    /// <summary>Runs <c>ruff format --check</c> and returns the files ruff reports it *would* reformat.</summary>
    public async Task<IReadOnlyList<string>> FormatCheckAsync(AnalysisScope scope, CancellationToken ct = default)
    {
        List<string> args = ["format", "--check", .. TargetArgs(scope)];
        ProcessResult result = await processRunner.RunAsync("ruff", args, scope.WorkspaceRoot, TimeSpan.FromMinutes(5), ct);

        // ruff prints one "Would reformat: <path>" line per file that isn't already formatted.
        const string prefix = "Would reformat:";
        List<string> files = [];
        foreach (string line in result.StandardOutput.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                files.Add(trimmed[prefix.Length..].Trim());
            }
        }

        return files;
    }

    private static List<string> TargetArgs(AnalysisScope scope) =>
        scope.Targets.Count > 0 ? scope.Targets.ToList() : ["."];

    private List<RuffDiagnostic> ParseDiagnostics(string stdout)
    {
        string trimmed = stdout.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '[')
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RuffDiagnostic>>(trimmed, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse ruff JSON output");
            return [];
        }
    }

    private static ProcessResult NotLaunched() => new()
    {
        ExitCode = -1,
        StandardOutput = string.Empty,
        StandardError = "ruff was not found on PATH.",
        Launched = false
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

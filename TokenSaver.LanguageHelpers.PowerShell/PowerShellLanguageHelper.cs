using TokenSaver.LanguageHelpers.Core;
using TokenSaver.LanguageHelpers.Core.Configuration;
using TokenSaver.LanguageHelpers.Core.Models;
using TokenSaver.LanguageHelpers.Core.Process;

using Microsoft.Extensions.Logging;

using System.Globalization;
using System.Text.Json.Serialization;

namespace TokenSaver.LanguageHelpers.PowerShell;

/// <summary>
///     PowerShell implementation of the language-helper operations, driven by <c>pwsh</c> and
///     PSScriptAnalyzer. The syntax checker uses the PowerShell language <c>Parser</c>; lint/fix use
///     <c>Invoke-ScriptAnalyzer</c>; format uses <c>Invoke-Formatter</c>. PowerShell has no import
///     organization or C#-style namespaces, so <c>FixImports</c> and <c>FixNamespace</c> inherit the base
///     class's <see cref="OperationStatus.NotApplicable" /> results.
/// </summary>
public sealed class PowerShellLanguageHelper : LanguageHelperBase
{
    private const string SettingsFileName = "PSScriptAnalyzerSettings.psd1";
    private const string SettingsBegin = "# >>> TokenSaver PowerShell analysis (managed) >>>";
    private const string SettingsEnd = "# <<< TokenSaver PowerShell analysis (managed) <<<";

    private readonly PwshRunner _pwsh;

    public PowerShellLanguageHelper(IProcessRunner processRunner, ILogger<PowerShellLanguageHelper> logger)
        : base(processRunner, logger) =>
        _pwsh = new PwshRunner(processRunner, logger);

    /// <inheritdoc />
    public override LanguageId Language => LanguageId.PowerShell;

    /// <inheritdoc />
    public override IReadOnlyCollection<string> FileExtensions { get; } = [".ps1", ".psm1", ".psd1"];

    /// <inheritdoc />
    public override Task<EnableAnalysisResult> EnableCodeAnalysisAsync(
        AnalysisScope scope, CodeStandard? standard = null, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            CodeStandard std = standard ?? CodeStandard.Default;
            string settingsPath = System.IO.Path.Combine(scope.WorkspaceRoot, SettingsFileName);

            ConfigBlockResult block = await ConfigBlockWriter.UpsertAsync(
                settingsPath, SettingsBegin, SettingsEnd, BuildSettings(std), ct);

            return new EnableAnalysisResult
            {
                Status = block.Changed ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = SettingsFileName,
                ConfigFilesWritten = block.Changed ? [block.Path] : [],
                ChangedFiles = block.Changed ? [block.Path] : [],
                PackagesEnsured = ["PSScriptAnalyzer (PowerShell module)"],
                Messages =
                [
                    $"Wrote {SettingsFileName}. Install the analyzer with: Install-Module PSScriptAnalyzer -Scope CurrentUser",
                    "Invoke-ScriptAnalyzer and Invoke-Formatter honor this settings file automatically."
                ]
            };
        }, ct);

    /// <inheritdoc />
    public override Task<SyntaxCheckReport> RunSyntaxCheckerAsync(AnalysisScope scope, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            IReadOnlyList<string> files = EnumerateFiles(scope);
            if (files.Count == 0)
            {
                return new SyntaxCheckReport { Status = OperationStatus.NoChanges, Messages = ["No PowerShell files in scope."] };
            }

            if (!await _pwsh.IsPwshAvailableAsync(ct))
            {
                return ToolUnavailable<SyntaxCheckReport>("pwsh");
            }

            ProcessResult result = await _pwsh.RunScriptAsync(ParseScript, files, scope.WorkspaceRoot, ct);
            List<PsIssue> issues = _pwsh.Deserialize<List<PsIssue>>(result.StandardOutput) ?? [];

            return new SyntaxCheckReport
            {
                Status = OperationStatus.Succeeded,
                Tool = "PowerShell Parser",
                Issues = issues.Select(i => i.ToCodeIssue(CodeIssueSeverity.Error)).ToList(),
                RawOutput = result.CombinedOutput,
                Messages = [$"Parsed {files.Count} file(s); found {issues.Count} parse error(s)."]
            };
        }, ct);

    /// <inheritdoc />
    public override Task<FixResult> FixSyntaxAsync(AnalysisScope scope, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            IReadOnlyList<string> files = EnumerateFiles(scope);
            if (files.Count == 0)
            {
                return new FixResult { Status = OperationStatus.NoChanges, Messages = ["No PowerShell files in scope."] };
            }

            if (!await _pwsh.IsPwshAvailableAsync(ct))
            {
                return ToolUnavailable<FixResult>("pwsh");
            }

            if (!await _pwsh.IsScriptAnalyzerAvailableAsync(scope.WorkspaceRoot, ct))
            {
                return ToolUnavailable<FixResult>("PSScriptAnalyzer");
            }

            string settings = SettingsArg(scope);
            ProcessResult result = await _pwsh.RunScriptAsync(FixScript, [settings, .. files], scope.WorkspaceRoot, ct);
            PsFixReport report = _pwsh.Deserialize<PsFixReport>(result.StandardOutput) ?? new PsFixReport();

            List<CodeIssue> remaining = (report.Remaining ?? []).Select(i => i.ToCodeIssue()).ToList();
            return new FixResult
            {
                Status = report.Changed is { Count: > 0 } ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = "Invoke-ScriptAnalyzer -Fix",
                ChangedFiles = report.Changed ?? [],
                FixedCount = report.Changed?.Count ?? 0,
                RemainingIssues = remaining,
                Issues = remaining,
                RawOutput = result.CombinedOutput
            };
        }, ct);

    /// <inheritdoc />
    public override Task<LintReport> RunLintAsync(AnalysisScope scope, LintOptions? options = null, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            IReadOnlyList<string> files = EnumerateFiles(scope);
            if (files.Count == 0)
            {
                return new LintReport { Status = OperationStatus.NoChanges, Messages = ["No PowerShell files in scope."] };
            }

            if (!await _pwsh.IsPwshAvailableAsync(ct))
            {
                return ToolUnavailable<LintReport>("pwsh");
            }

            if (!await _pwsh.IsScriptAnalyzerAvailableAsync(scope.WorkspaceRoot, ct))
            {
                return ToolUnavailable<LintReport>("PSScriptAnalyzer");
            }

            string settings = SettingsArg(scope);
            ProcessResult result = await _pwsh.RunScriptAsync(AnalyzeScript, [settings, .. files], scope.WorkspaceRoot, ct);
            List<PsIssue> issues = _pwsh.Deserialize<List<PsIssue>>(result.StandardOutput) ?? [];

            return new LintReport
            {
                Status = OperationStatus.Succeeded,
                Tool = "Invoke-ScriptAnalyzer",
                Issues = issues.Select(i => i.ToCodeIssue()).ToList(),
                RawOutput = result.CombinedOutput,
                Messages = [$"{issues.Count} analyzer finding(s)."]
            };
        }, ct);

    /// <inheritdoc />
    public override Task<FormatResult> FormatCodeAsync(AnalysisScope scope, FormatOptions? options = null, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            IReadOnlyList<string> files = EnumerateFiles(scope);
            if (files.Count == 0)
            {
                return new FormatResult { Status = OperationStatus.NoChanges, Messages = ["No PowerShell files in scope."] };
            }

            if (!await _pwsh.IsPwshAvailableAsync(ct))
            {
                return ToolUnavailable<FormatResult>("pwsh");
            }

            if (!await _pwsh.IsScriptAnalyzerAvailableAsync(scope.WorkspaceRoot, ct))
            {
                return ToolUnavailable<FormatResult>("PSScriptAnalyzer");
            }

            string settings = SettingsArg(scope);
            ProcessResult result = await _pwsh.RunScriptAsync(FormatScript, [settings, .. files], scope.WorkspaceRoot, ct);
            List<string> changed = _pwsh.Deserialize<List<string>>(result.StandardOutput) ?? [];

            return new FormatResult
            {
                Status = changed.Count > 0 ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = "Invoke-Formatter",
                ChangedFiles = changed,
                RawOutput = result.CombinedOutput
            };
        }, ct);

    /// <summary>Returns the settings-file path when it exists, else an empty arg the scripts treat as "no settings".</summary>
    private static string SettingsArg(AnalysisScope scope)
    {
        string path = System.IO.Path.Combine(scope.WorkspaceRoot, SettingsFileName);
        return File.Exists(path) ? path : string.Empty;
    }

    /// <summary>Builds the managed <c>PSScriptAnalyzerSettings.psd1</c> hashtable from the neutral standard.</summary>
    private static string BuildSettings(CodeStandard std)
    {
        string kind = std.UseTabs ? "tab" : "space";
        string indent = std.IndentSize.ToString(CultureInfo.InvariantCulture);
        return $$"""
            @{
                IncludeDefaultRules = $true
                Rules = @{
                    PSUseConsistentIndentation = @{
                        Enable          = $true
                        IndentationSize = {{indent}}
                        Kind            = '{{kind}}'
                    }
                    PSUseConsistentWhitespace = @{
                        Enable = $true
                    }
                    PSAvoidTrailingWhitespace = @{
                        Enable = $true
                    }
                }
            }
            """;
    }

    // ----- Embedded pwsh scripts. Each loops over its file arguments and emits JSON on stdout. -----

    private const string ParseScript = """
        $results = @()
        foreach ($file in $args) {
            if (-not (Test-Path -LiteralPath $file)) { continue }
            $tokens = $null; $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors) | Out-Null
            foreach ($e in $errors) {
                $results += [pscustomobject]@{
                    File    = $file
                    Line    = $e.Extent.StartLineNumber
                    Column  = $e.Extent.StartColumnNumber
                    Message = $e.Message
                    RuleId  = $e.ErrorId
                }
            }
        }
        $results | ConvertTo-Json -AsArray -Depth 4
        """;

    private const string AnalyzeScript = """
        $settingsPath = $args[0]
        $files = if ($args.Count -gt 1) { $args[1..($args.Count - 1)] } else { @() }
        $results = @()
        foreach ($file in $files) {
            $params = @{ Path = $file }
            if ($settingsPath -and (Test-Path -LiteralPath $settingsPath)) { $params.Settings = $settingsPath }
            foreach ($r in (Invoke-ScriptAnalyzer @params)) {
                $results += [pscustomobject]@{
                    File     = $r.ScriptPath
                    Line     = $r.Line
                    Column   = $r.Column
                    Message  = $r.Message
                    RuleId   = $r.RuleName
                    Severity = "$($r.Severity)"
                }
            }
        }
        $results | ConvertTo-Json -AsArray -Depth 4
        """;

    private const string FixScript = """
        $settingsPath = $args[0]
        $files = if ($args.Count -gt 1) { $args[1..($args.Count - 1)] } else { @() }
        $changed = @()
        foreach ($file in $files) {
            $before = Get-Content -Raw -LiteralPath $file
            $params = @{ Path = $file; Fix = $true }
            if ($settingsPath -and (Test-Path -LiteralPath $settingsPath)) { $params.Settings = $settingsPath }
            Invoke-ScriptAnalyzer @params | Out-Null
            $after = Get-Content -Raw -LiteralPath $file
            if ($before -ne $after) { $changed += $file }
        }
        $remaining = @()
        foreach ($file in $files) {
            $params = @{ Path = $file }
            if ($settingsPath -and (Test-Path -LiteralPath $settingsPath)) { $params.Settings = $settingsPath }
            foreach ($r in (Invoke-ScriptAnalyzer @params)) {
                $remaining += [pscustomobject]@{
                    File     = $r.ScriptPath
                    Line     = $r.Line
                    Column   = $r.Column
                    Message  = $r.Message
                    RuleId   = $r.RuleName
                    Severity = "$($r.Severity)"
                }
            }
        }
        [pscustomobject]@{ Changed = @($changed); Remaining = @($remaining) } | ConvertTo-Json -Depth 5
        """;

    private const string FormatScript = """
        $settingsPath = $args[0]
        $files = if ($args.Count -gt 1) { $args[1..($args.Count - 1)] } else { @() }
        $changed = @()
        foreach ($file in $files) {
            $before = Get-Content -Raw -LiteralPath $file
            if ($settingsPath -and (Test-Path -LiteralPath $settingsPath)) {
                $after = Invoke-Formatter -ScriptDefinition $before -Settings $settingsPath
            } else {
                $after = Invoke-Formatter -ScriptDefinition $before
            }
            if ($before -ne $after) {
                Set-Content -LiteralPath $file -Value $after -NoNewline
                $changed += $file
            }
        }
        @($changed) | ConvertTo-Json -AsArray
        """;

    /// <summary>One finding as emitted by the pwsh scripts (parse errors and analyzer records share this shape).</summary>
    private sealed record PsIssue
    {
        [JsonPropertyName("File")] public string? File { get; init; }
        [JsonPropertyName("Line")] public int Line { get; init; }
        [JsonPropertyName("Column")] public int Column { get; init; }
        [JsonPropertyName("Message")] public string? Message { get; init; }
        [JsonPropertyName("RuleId")] public string? RuleId { get; init; }
        [JsonPropertyName("Severity")] public string? Severity { get; init; }

        public CodeIssue ToCodeIssue(CodeIssueSeverity? forced = null) => new()
        {
            Severity = forced ?? MapSeverity(Severity),
            Message = Message ?? "PowerShell finding.",
            RuleId = RuleId,
            Location = File is null ? null : new CodeLocation { FilePath = File, Line = Line, Column = Column }
        };

        private static CodeIssueSeverity MapSeverity(string? severity) => severity switch
        {
            "Error" or "ParseError" => CodeIssueSeverity.Error,
            "Warning" => CodeIssueSeverity.Warning,
            _ => CodeIssueSeverity.Info
        };
    }

    /// <summary>Result of the fix script: which files changed and what findings remain.</summary>
    private sealed record PsFixReport
    {
        [JsonPropertyName("Changed")] public List<string>? Changed { get; init; }
        [JsonPropertyName("Remaining")] public List<PsIssue>? Remaining { get; init; }
    }
}

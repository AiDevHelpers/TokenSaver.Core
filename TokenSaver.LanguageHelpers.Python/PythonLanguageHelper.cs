using TokenSaver.LanguageHelpers.Core;
using TokenSaver.LanguageHelpers.Core.Configuration;
using TokenSaver.LanguageHelpers.Core.Models;
using TokenSaver.LanguageHelpers.Core.Process;

using Microsoft.Extensions.Logging;

using System.Globalization;

namespace TokenSaver.LanguageHelpers.Python;

/// <summary>
///     Python implementation of the language-helper operations, driven by the <c>ruff</c> CLI (lint,
///     import sorting, and formatting in one fast tool). Python has no C#-style namespaces, so
///     <c>FixNamespace</c> inherits the base class's <see cref="OperationStatus.NotApplicable" /> result;
///     import organization, by contrast, is first-class via ruff's isort (<c>I</c>) and unused-import
///     (<c>F401</c>) rules.
/// </summary>
public sealed class PythonLanguageHelper : LanguageHelperBase
{
    private const string RuffConfigName = "ruff.toml";
    private const string RuffConfigBegin = "# >>> TokenSaver Python analysis (managed) >>>";
    private const string RuffConfigEnd = "# <<< TokenSaver Python analysis (managed) <<<";

    private readonly RuffRunner _ruff;

    public PythonLanguageHelper(IProcessRunner processRunner, ILogger<PythonLanguageHelper> logger)
        : base(processRunner, logger) =>
        _ruff = new RuffRunner(processRunner, logger);

    /// <inheritdoc />
    public override LanguageId Language => LanguageId.Python;

    /// <inheritdoc />
    public override IReadOnlyCollection<string> FileExtensions { get; } = [".py", ".pyi"];

    /// <inheritdoc />
    public override Task<EnableAnalysisResult> EnableCodeAnalysisAsync(
        AnalysisScope scope, CodeStandard? standard = null, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            CodeStandard std = standard ?? CodeStandard.Default;
            string configPath = System.IO.Path.Combine(scope.WorkspaceRoot, RuffConfigName);

            ConfigBlockResult block = await ConfigBlockWriter.UpsertAsync(
                configPath, RuffConfigBegin, RuffConfigEnd, BuildRuffConfig(std), ct);

            return new EnableAnalysisResult
            {
                Status = block.Changed ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = RuffConfigName,
                ConfigFilesWritten = block.Changed ? [block.Path] : [],
                ChangedFiles = block.Changed ? [block.Path] : [],
                PackagesEnsured = ["ruff (pip install ruff)"],
                Messages =
                [
                    $"Wrote {RuffConfigName}. Install the tool with: pip install ruff",
                    "ruff auto-discovers this config from the working directory for check and format."
                ]
            };
        }, ct);

    /// <inheritdoc />
    public override Task<SyntaxCheckReport> RunSyntaxCheckerAsync(AnalysisScope scope, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            // E9/F63/F7/F82 is the classic "syntax + hard errors" subset — a real validator pass
            // that ignores style so RunSyntaxChecker stays about correctness, not conventions.
            RuffCheckOutcome outcome = await _ruff.CheckAsync(
                scope, select: ["E9", "F63", "F7", "F82"], fix: false, ct: ct);
            if (!outcome.ToolAvailable)
            {
                return ToolUnavailable<SyntaxCheckReport>("ruff");
            }

            List<CodeIssue> issues = outcome.Diagnostics.Select(d => d.ToCodeIssue()).ToList();
            return new SyntaxCheckReport
            {
                Status = OperationStatus.Succeeded,
                Tool = "ruff check (E9,F63,F7,F82)",
                Issues = issues,
                RawOutput = outcome.Process.CombinedOutput,
                Messages = [$"{issues.Count} syntax/hard-error finding(s)."]
            };
        }, ct);

    /// <inheritdoc />
    public override Task<FixResult> FixSyntaxAsync(AnalysisScope scope, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            RuffCheckOutcome before = await _ruff.CheckAsync(scope, fix: false, ct: ct);
            if (!before.ToolAvailable)
            {
                return ToolUnavailable<FixResult>("ruff");
            }

            IReadOnlyList<RuffDiagnostic> fixable = before.Diagnostics.Where(d => d.IsFixable).ToList();

            RuffCheckOutcome after = await _ruff.CheckAsync(scope, fix: true, ct: ct);
            List<CodeIssue> remaining = after.Diagnostics.Select(d => d.ToCodeIssue()).ToList();
            IReadOnlyList<string> changed = fixable
                .Select(d => d.Filename)
                .Where(f => f is not null)
                .Select(f => f!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new FixResult
            {
                Status = changed.Count > 0 ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = "ruff check --fix",
                FixedCount = fixable.Count,
                ChangedFiles = changed,
                RemainingIssues = remaining,
                Issues = remaining,
                RawOutput = after.Process.CombinedOutput
            };
        }, ct);

    /// <inheritdoc />
    public override Task<ImportFixResult> FixImportsAsync(AnalysisScope scope, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            // I = isort (ordering), F401 = unused import removal.
            RuffCheckOutcome before = await _ruff.CheckAsync(scope, select: ["I", "F401"], fix: false, ct: ct);
            if (!before.ToolAvailable)
            {
                return ToolUnavailable<ImportFixResult>("ruff");
            }

            int removed = before.Diagnostics.Count(d => d.Code == "F401" && d.IsFixable);
            int reordered = before.Diagnostics.Count(d => d.Code is not null && d.Code.StartsWith('I') && d.IsFixable);

            RuffCheckOutcome after = await _ruff.CheckAsync(scope, select: ["I", "F401"], fix: true, ct: ct);
            IReadOnlyList<string> changed = before.Diagnostics
                .Where(d => d.IsFixable)
                .Select(d => d.Filename)
                .Where(f => f is not null)
                .Select(f => f!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ImportFixResult
            {
                Status = changed.Count > 0 ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = "ruff check --select I,F401 --fix",
                ImportsRemoved = removed,
                ImportsReordered = reordered,
                ChangedFiles = changed,
                RawOutput = after.Process.CombinedOutput
            };
        }, ct);

    /// <inheritdoc />
    public override Task<LintReport> RunLintAsync(AnalysisScope scope, LintOptions? options = null, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            RuffCheckOutcome outcome = await _ruff.CheckAsync(scope, fix: false, ct: ct);
            if (!outcome.ToolAvailable)
            {
                return ToolUnavailable<LintReport>("ruff");
            }

            List<CodeIssue> issues = outcome.Diagnostics.Select(d => d.ToCodeIssue()).ToList();
            return new LintReport
            {
                Status = OperationStatus.Succeeded,
                Tool = "ruff check",
                Issues = issues,
                RawOutput = outcome.Process.CombinedOutput,
                Messages = [$"{issues.Count} lint finding(s)."]
            };
        }, ct);

    /// <inheritdoc />
    public override Task<FormatResult> FormatCodeAsync(AnalysisScope scope, FormatOptions? options = null, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            FormatOptions opts = options ?? FormatOptions.Default;

            // Optional extension formatter: black, when the caller prefers it and it's installed.
            if (opts.PreferExtensionTool && await ProcessRunner.IsAvailableAsync("black", ct))
            {
                IReadOnlyList<string> targets = scope.Targets.Count > 0 ? scope.Targets : [scope.WorkspaceRoot];
                ProcessResult bp = await ProcessRunner.RunAsync(
                    "black", [.. targets], scope.WorkspaceRoot, TimeSpan.FromMinutes(5), ct);
                return new FormatResult
                {
                    Status = bp.Success ? OperationStatus.SucceededWithChanges : OperationStatus.Failed,
                    Tool = "black",
                    UsedExtensionTool = true,
                    RawOutput = bp.CombinedOutput
                };
            }

            if (!await _ruff.IsAvailableAsync(ct))
            {
                return ToolUnavailable<FormatResult>("ruff");
            }

            // Discover which files will change first (ruff format itself doesn't list them), then apply.
            IReadOnlyList<string> willChange = await _ruff.FormatCheckAsync(scope, ct);
            ProcessResult result = await _ruff.FormatAsync(scope, ct);

            return new FormatResult
            {
                Status = willChange.Count > 0 ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = "ruff format",
                ChangedFiles = willChange,
                RawOutput = result.CombinedOutput
            };
        }, ct);

    /// <summary>Builds the managed <c>ruff.toml</c> body from the neutral coding standard.</summary>
    private static string BuildRuffConfig(CodeStandard std)
    {
        string lineLength = std.MaxLineLength.ToString(CultureInfo.InvariantCulture);
        string indentWidth = std.IndentSize.ToString(CultureInfo.InvariantCulture);
        string indentStyle = std.UseTabs ? "tab" : "space";
        return $"""
            line-length = {lineLength}
            indent-width = {indentWidth}

            [lint]
            # E/W = pycodestyle, F = pyflakes, I = isort (import ordering).
            select = ["E", "W", "F", "I"]

            [format]
            indent-style = "{indentStyle}"
            """;
    }
}

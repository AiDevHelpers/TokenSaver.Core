using AiDevTools.TokenSaver.LanguageHelpers.Core;
using AiDevTools.TokenSaver.LanguageHelpers.Core.Configuration;
using AiDevTools.TokenSaver.LanguageHelpers.Core.Models;
using AiDevTools.TokenSaver.LanguageHelpers.Core.Process;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

using System.Globalization;

namespace AiDevTools.TokenSaver.LanguageHelpers.CSharp;

/// <summary>
///     C# implementation of the seven language-helper operations. The syntax checker runs in-process on
///     Roslyn; every fix/lint/format operation maps to a scoped <c>dotnet format</c> subcommand via
///     <see cref="DotnetFormatRunner" />. <c>EnableCodeAnalysis</c> writes an <c>.editorconfig</c> section
///     — .NET analyzers themselves ship with the SDK (no NuGet package needed on net5+), so "ensuring the
///     analysis package" reduces to enabling and configuring them through editorconfig.
/// </summary>
public sealed class CSharpLanguageHelper : LanguageHelperBase
{
    private const string EditorConfigBegin = "# >>> TokenSaver C# analysis (managed) >>>";
    private const string EditorConfigEnd = "# <<< TokenSaver C# analysis (managed) <<<";

    private readonly DotnetFormatRunner _format;

    public CSharpLanguageHelper(IProcessRunner processRunner, ILogger<CSharpLanguageHelper> logger)
        : base(processRunner, logger) =>
        _format = new DotnetFormatRunner(processRunner, logger);

    /// <inheritdoc />
    public override LanguageId Language => LanguageId.CSharp;

    /// <inheritdoc />
    public override IReadOnlyCollection<string> FileExtensions { get; } = [".cs", ".csx"];

    /// <inheritdoc />
    public override Task<EnableAnalysisResult> EnableCodeAnalysisAsync(
        AnalysisScope scope, CodeStandard? standard = null, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            CodeStandard std = standard ?? CodeStandard.Default;
            string editorConfigPath = System.IO.Path.Combine(scope.WorkspaceRoot, ".editorconfig");

            ConfigBlockResult block = await ConfigBlockWriter.UpsertAsync(
                editorConfigPath, EditorConfigBegin, EditorConfigEnd, BuildEditorConfig(std), ct);

            List<string> messages =
            [
                ".NET analyzers ship with the SDK — enabled via EnableNETAnalyzers/AnalysisLevel and this " +
                ".editorconfig. No NuGet package is required for the built-in analyzers.",
                "For extra rule sets, add a package such as Roslynator.Analyzers or StyleCop.Analyzers to the project."
            ];

            return new EnableAnalysisResult
            {
                Status = block.Changed ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = ".editorconfig",
                ConfigFilesWritten = block.Changed ? [block.Path] : [],
                ChangedFiles = block.Changed ? [block.Path] : [],
                PackagesEnsured = ["Microsoft.NET.Sdk analyzers (built-in)"],
                Messages = messages
            };
        }, ct);

    /// <inheritdoc />
    public override Task<SyntaxCheckReport> RunSyntaxCheckerAsync(AnalysisScope scope, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            IReadOnlyList<string> files = EnumerateFiles(scope);
            List<CodeIssue> issues = [];

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                string text = await File.ReadAllTextAsync(file, ct);
                SyntaxTree tree = CSharpSyntaxTree.ParseText(SourceText.From(text), path: file, cancellationToken: ct);

                foreach (Diagnostic diag in tree.GetDiagnostics(ct))
                {
                    // A pure parse pass only surfaces real syntax problems (compiler CS diagnostics),
                    // not semantic ones — exactly the "basic syntax issues" this operation is scoped to.
                    if (diag.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                    {
                        issues.Add(ToIssue(diag));
                    }
                }
            }

            return new SyntaxCheckReport
            {
                Status = OperationStatus.Succeeded,
                Tool = "Roslyn (Microsoft.CodeAnalysis.CSharp)",
                Issues = issues,
                Messages = [$"Parsed {files.Count} file(s); found {issues.Count} syntax issue(s)."]
            };
        }, ct);

    /// <inheritdoc />
    public override Task<FixResult> FixSyntaxAsync(AnalysisScope scope, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            // First surface unfixable parse errors (Roslyn cannot invent missing tokens),
            // then apply the code-style fixers dotnet format can automate.
            SyntaxCheckReport check = await RunSyntaxCheckerAsync(scope, ct);
            List<CodeIssue> parseErrors = check.Issues.Where(i => i.Severity == CodeIssueSeverity.Error).ToList();

            DotnetFormatOutcome outcome = await _format.RunAsync("style", scope, verifyOnly: false, severity: "info", ct: ct);
            if (!outcome.ToolAvailable)
            {
                return ToolUnavailable<FixResult>("dotnet format");
            }

            return new FixResult
            {
                Status = outcome.Files.Count > 0 ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = "dotnet format style",
                FixedCount = outcome.Issues.Count,
                ChangedFiles = outcome.Files,
                Issues = outcome.Issues,
                RemainingIssues = parseErrors,
                RawOutput = outcome.RawOutput,
                Messages = parseErrors.Count > 0
                    ? [$"{parseErrors.Count} syntax error(s) require manual fixes (not auto-fixable)."]
                    : []
            };
        }, ct);

    /// <inheritdoc />
    public override Task<ImportFixResult> FixImportsAsync(AnalysisScope scope, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            // IDE0005 = "using directive is unnecessary". Sorting is applied by the editorconfig
            // dotnet_sort_system_directives_first setting during the same style pass.
            DotnetFormatOutcome outcome = await _format.RunAsync(
                "style", scope, verifyOnly: false, diagnostics: ["IDE0005"], severity: "info", ct: ct);
            if (!outcome.ToolAvailable)
            {
                return ToolUnavailable<ImportFixResult>("dotnet format");
            }

            int removed = outcome.Issues.Count(i => i.RuleId == "IDE0005");
            return new ImportFixResult
            {
                Status = outcome.Files.Count > 0 ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = "dotnet format style (IDE0005)",
                ImportsRemoved = removed,
                ChangedFiles = outcome.Files,
                Issues = outcome.Issues,
                RawOutput = outcome.RawOutput
            };
        }, ct);

    /// <inheritdoc />
    public override Task<NamespaceFixResult> FixNamespaceAsync(AnalysisScope scope, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            // IDE0130 = "namespace does not match folder structure"; its fixer rewrites the namespace.
            DotnetFormatOutcome outcome = await _format.RunAsync(
                "style", scope, verifyOnly: false, diagnostics: ["IDE0130"], severity: "info", ct: ct);
            if (!outcome.ToolAvailable)
            {
                return ToolUnavailable<NamespaceFixResult>("dotnet format");
            }

            int corrected = outcome.Issues.Count(i => i.RuleId == "IDE0130");
            return new NamespaceFixResult
            {
                Status = outcome.Files.Count > 0 ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = "dotnet format style (IDE0130)",
                NamespacesCorrected = corrected,
                ChangedFiles = outcome.Files,
                Issues = outcome.Issues,
                RawOutput = outcome.RawOutput
            };
        }, ct);

    /// <inheritdoc />
    public override Task<LintReport> RunLintAsync(AnalysisScope scope, LintOptions? options = null, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            // Read-only: --verify-no-changes reports what the analyzers would flag without editing.
            DotnetFormatOutcome outcome = await _format.RunAsync(
                "analyzers", scope, verifyOnly: true, severity: "info", ct: ct);
            if (!outcome.ToolAvailable)
            {
                return ToolUnavailable<LintReport>("dotnet format");
            }

            return new LintReport
            {
                Status = OperationStatus.Succeeded,
                Tool = "dotnet format analyzers --verify-no-changes",
                Issues = outcome.Issues,
                RawOutput = outcome.RawOutput,
                Messages = [$"{outcome.Issues.Count} analyzer finding(s)."]
            };
        }, ct);

    /// <inheritdoc />
    public override Task<FormatResult> FormatCodeAsync(AnalysisScope scope, FormatOptions? options = null, CancellationToken ct = default) =>
        TimedAsync(async () =>
        {
            FormatOptions opts = options ?? FormatOptions.Default;

            // Optional extension formatter: CSharpier, when the caller prefers it and it's installed.
            if (opts.PreferExtensionTool && await ProcessRunner.IsAvailableAsync("csharpier", ct))
            {
                IReadOnlyList<string> targets = scope.Targets.Count > 0 ? scope.Targets : [scope.WorkspaceRoot];
                ProcessResult cp = await ProcessRunner.RunAsync(
                    "csharpier", ["format", .. targets], scope.WorkspaceRoot, TimeSpan.FromMinutes(5), ct);
                return new FormatResult
                {
                    Status = cp.Success ? OperationStatus.SucceededWithChanges : OperationStatus.Failed,
                    Tool = "csharpier",
                    UsedExtensionTool = true,
                    RawOutput = cp.CombinedOutput
                };
            }

            DotnetFormatOutcome outcome = await _format.RunAsync("whitespace", scope, verifyOnly: false, ct: ct);
            if (!outcome.ToolAvailable)
            {
                return ToolUnavailable<FormatResult>("dotnet format");
            }

            return new FormatResult
            {
                Status = outcome.Files.Count > 0 ? OperationStatus.SucceededWithChanges : OperationStatus.NoChanges,
                Tool = "dotnet format whitespace",
                ChangedFiles = outcome.Files,
                Issues = outcome.Issues,
                RawOutput = outcome.RawOutput
            };
        }, ct);

    private static CodeIssue ToIssue(Diagnostic diag)
    {
        FileLinePositionSpan span = diag.Location.GetLineSpan();
        return new CodeIssue
        {
            Severity = diag.Severity == DiagnosticSeverity.Error ? CodeIssueSeverity.Error : CodeIssueSeverity.Warning,
            Message = diag.GetMessage(CultureInfo.InvariantCulture),
            RuleId = diag.Id,
            Location = new CodeLocation
            {
                FilePath = span.Path,
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
                EndLine = span.EndLinePosition.Line + 1,
                EndColumn = span.EndLinePosition.Character + 1
            }
        };
    }

    /// <summary>Renders the managed <c>.editorconfig</c> block for C# from the neutral coding standard.</summary>
    private static string BuildEditorConfig(CodeStandard std)
    {
        // Format numbers invariantly — a locale-grouped "1 200" would corrupt the editorconfig.
        string indentStyle = std.UseTabs ? "tab" : "space";
        string indentSize = std.IndentSize.ToString(CultureInfo.InvariantCulture);
        string maxLineLength = std.MaxLineLength.ToString(CultureInfo.InvariantCulture);
        string finalNewline = Lower(std.InsertFinalNewline);
        string trimTrailing = Lower(std.TrimTrailingWhitespace);

        return $$"""
            [*.{cs,csx}]
            indent_style = {{indentStyle}}
            indent_size = {{indentSize}}
            max_line_length = {{maxLineLength}}
            insert_final_newline = {{finalNewline}}
            trim_trailing_whitespace = {{trimTrailing}}
            dotnet_sort_system_directives_first = true
            dotnet_separate_import_directive_groups = false
            # Surface unnecessary usings and folder/namespace mismatches so the fixers can act on them.
            dotnet_diagnostic.IDE0005.severity = warning
            dotnet_diagnostic.IDE0130.severity = warning
            csharp_using_directive_placement = outside_namespace
            """;
    }

    private static string Lower(bool value) => value ? "true" : "false";
}

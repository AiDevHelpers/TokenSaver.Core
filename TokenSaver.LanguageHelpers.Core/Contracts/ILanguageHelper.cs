using TokenSaver.LanguageHelpers.Core.Models;

namespace TokenSaver.LanguageHelpers.Core.Contracts;

/// <summary>
///     The per-language capability surface. Every <c>TokenSaver.LanguageHelpers.&lt;Language&gt;</c>
///     project provides exactly one implementation. Operations a given language cannot express (e.g.
///     namespace fixing in Python, import organizing in PowerShell) return a result with
///     <see cref="OperationStatus.NotApplicable" /> rather than throwing, so an orchestrator can call the
///     full set uniformly across languages.
/// </summary>
public interface ILanguageHelper
{
    /// <summary>The language this helper handles.</summary>
    LanguageId Language { get; }

    /// <summary>File extensions this helper owns, each including the leading dot (e.g. <c>.cs</c>).</summary>
    IReadOnlyCollection<string> FileExtensions { get; }

    /// <summary>True when <paramref name="filePath" />'s extension is one this helper handles.</summary>
    bool CanHandle(string filePath);

    /// <summary>
    ///     (1) Sets up or extends the language's analysis configuration — writes/merges an
    ///     <c>.editorconfig</c> section and ensures any analyzer package or tool config the language needs
    ///     is present — using the supplied <paramref name="standard" /> (or its default).
    /// </summary>
    Task<EnableAnalysisResult> EnableCodeAnalysisAsync(
        AnalysisScope scope, CodeStandard? standard = null, CancellationToken ct = default);

    /// <summary>(2) Runs a validator over the scope and reports basic syntax/parse issues without changing files.</summary>
    Task<SyntaxCheckReport> RunSyntaxCheckerAsync(AnalysisScope scope, CancellationToken ct = default);

    /// <summary>(3) Runs the syntax checker and then auto-fixes the issues it can (e.g. Roslyn fixers, <c>ruff --fix</c>).</summary>
    Task<FixResult> FixSyntaxAsync(AnalysisScope scope, CancellationToken ct = default);

    /// <summary>(4) Organizes imports/usings: removes unused, adds missing where resolvable, and sorts them.</summary>
    Task<ImportFixResult> FixImportsAsync(AnalysisScope scope, CancellationToken ct = default);

    /// <summary>(5) Verifies and corrects namespaces so they match the folder/module layout (ReSharper-style).</summary>
    Task<NamespaceFixResult> FixNamespaceAsync(AnalysisScope scope, CancellationToken ct = default);

    /// <summary>(6) Runs the linter and reports findings; may defer to an extension-provided linter per options.</summary>
    Task<LintReport> RunLintAsync(AnalysisScope scope, LintOptions? options = null, CancellationToken ct = default);

    /// <summary>(7) Formats code to the standard; may defer to an extension-provided formatter per options.</summary>
    Task<FormatResult> FormatCodeAsync(AnalysisScope scope, FormatOptions? options = null, CancellationToken ct = default);
}

namespace AiDevTools.TokenSaver.LanguageHelpers.Core.Models;

/// <summary>
///     The languages for which a <c>AiDevTools.TokenSaver.LanguageHelpers.*</c> implementation exists. Kept as a
///     small closed enum (rather than an open string) so callers can switch exhaustively; add a value
///     here when a new language project is introduced.
/// </summary>
public enum LanguageId
{
    CSharp,
    PowerShell,
    Python
}

/// <summary>
///     Severity of a single <see cref="CodeIssue" />, normalized across the very different vocabularies
///     of the underlying tools (Roslyn diagnostics, PSScriptAnalyzer records, ruff rules).
/// </summary>
public enum CodeIssueSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
///     Outcome of any one language-helper operation. Distinguishes "did work, no changes needed" from
///     "did work and changed files" from "the language/tool doesn't support this operation" so that an
///     orchestrator can report honestly instead of collapsing everything to success/failure.
/// </summary>
public enum OperationStatus
{
    /// <summary>The operation ran and the code was already clean — nothing to change.</summary>
    NoChanges,

    /// <summary>The operation ran and produced changes (files written / fixes applied).</summary>
    SucceededWithChanges,

    /// <summary>The operation ran successfully and reported findings but made no changes (a check/report).</summary>
    Succeeded,

    /// <summary>The operation is not meaningful for this language (e.g. namespace fixing in Python).</summary>
    NotApplicable,

    /// <summary>The required external tool (dotnet, pwsh, ruff, ...) was not found on PATH.</summary>
    ToolUnavailable,

    /// <summary>The operation ran but failed (non-zero exit, parse failure, or an unexpected error).</summary>
    Failed
}

namespace AiDevTools.TokenSaver.TokenEconomy.Models.TokenEconomy;

/// <summary>
///     Which role-specific compression prompt DistillationService uses. Each role targets a
///     different kind of bulk text — see DistillationService's role-prompt table (normal + terse
///     variants) for the exact wording.
/// </summary>
public enum DistillRole
{
    /// <summary>Test runner output: which tests failed, one-line reason each, likely fix location.</summary>
    TestFailure,

    /// <summary>Log output: new/anomalous error or warning lines only, deduplicated.</summary>
    LogErrors,

    /// <summary>A git diff: one-line commit-style summary plus a few bullets.</summary>
    DiffSummary,

    /// <summary>Linter output: grouped by rule, one representative file:line per rule.</summary>
    LintSummary,

    /// <summary>Fallback for anything else: minimum content needed to decide the next action.</summary>
    GenericCondense
}

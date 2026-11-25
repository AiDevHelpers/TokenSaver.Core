namespace AiDevTools.TokenSaver.LanguageHelpers.Core.Models;

/// <summary>
///     A location inside a source file. All coordinates are 1-based to match how editors and the
///     underlying CLI tools report them. <see cref="Column" /> and the <c>End*</c> members are optional
///     because not every tool reports a precise span.
/// </summary>
public sealed record CodeLocation
{
    public required string FilePath { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
}

/// <summary>
///     One normalized diagnostic produced by a language helper — a Roslyn diagnostic, a PSScriptAnalyzer
///     record, or a ruff violation, all projected onto a common shape so callers don't have to special-
///     case the tool. <see cref="RuleId" /> is the tool's native rule identifier (e.g. <c>IDE0005</c>,
///     <c>PSAvoidUsingCmdletAliases</c>, <c>F401</c>).
/// </summary>
public sealed record CodeIssue
{
    public required CodeIssueSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? RuleId { get; init; }
    public CodeLocation? Location { get; init; }

    /// <summary>True when the underlying tool advertised an automatic fix for this issue.</summary>
    public bool IsFixable { get; init; }
}

namespace AiDevTools.TokenSaver.LanguageHelpers.Core.Models;

/// <summary>
///     Common shape shared by every language-helper operation result. Carries the normalized
///     <see cref="Status" />, the findings (<see cref="Issues" />), what changed on disk
///     (<see cref="ChangedFiles" />), human-readable <see cref="Messages" />, the raw tool output for
///     debugging, and timing. Individual operations derive from this to add operation-specific counters.
/// </summary>
public abstract record OperationResult
{
    /// <summary>
    ///     Normalized outcome. Not <c>required</c> so the generic result factories on
    ///     <c>LanguageHelperBase</c> can use a <c>new()</c> constraint; concrete code always sets it
    ///     explicitly. Defaults to <see cref="OperationStatus.Failed" /> so a forgotten assignment surfaces
    ///     as a failure rather than a silent success.
    /// </summary>
    public OperationStatus Status { get; init; } = OperationStatus.Failed;

    /// <summary>The external tool that produced this result (e.g. <c>dotnet format</c>, <c>ruff</c>, <c>pwsh</c>).</summary>
    public string? Tool { get; init; }

    public IReadOnlyList<CodeIssue> Issues { get; init; } = [];

    /// <summary>Absolute paths of files the operation created or modified. Empty for read-only operations.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public IReadOnlyList<string> Messages { get; init; } = [];

    /// <summary>Raw stdout/stderr from the underlying tool, retained for diagnostics.</summary>
    public string? RawOutput { get; init; }

    /// <summary>Wall-clock time the operation took. Settable so the timing wrapper can stamp it last.</summary>
    public TimeSpan Duration { get; set; }

    public int ErrorCount => Issues.Count(i => i.Severity == CodeIssueSeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == CodeIssueSeverity.Warning);

    /// <summary>
    ///     True when the operation did not fail — including the "not applicable" and "already clean" cases.
    ///     Only <see cref="OperationStatus.Failed" /> and <see cref="OperationStatus.ToolUnavailable" /> are
    ///     treated as unsuccessful.
    /// </summary>
    public bool IsSuccess => Status is not (OperationStatus.Failed or OperationStatus.ToolUnavailable);
}

/// <summary>Result of <c>EnableCodeAnalysis</c>: config files written and analyzer packages ensured.</summary>
public sealed record EnableAnalysisResult : OperationResult
{
    public IReadOnlyList<string> ConfigFilesWritten { get; init; } = [];
    public IReadOnlyList<string> PackagesEnsured { get; init; } = [];
}

/// <summary>Result of <c>RunSyntaxChecker</c>: a read-only report of syntax/parse problems.</summary>
public sealed record SyntaxCheckReport : OperationResult
{
}

/// <summary>Result of <c>FixSyntax</c>: how many issues were auto-fixed and what still remains.</summary>
public sealed record FixResult : OperationResult
{
    public int FixedCount { get; init; }

    /// <summary>Issues that survived the fix pass and still need manual attention.</summary>
    public IReadOnlyList<CodeIssue> RemainingIssues { get; init; } = [];
}

/// <summary>Result of <c>FixImports</c>: usings/imports removed, added, or reordered.</summary>
public sealed record ImportFixResult : OperationResult
{
    public int ImportsRemoved { get; init; }
    public int ImportsAdded { get; init; }
    public int ImportsReordered { get; init; }
}

/// <summary>Result of <c>FixNamespace</c>: namespaces corrected to match folder/module layout.</summary>
public sealed record NamespaceFixResult : OperationResult
{
    public int NamespacesCorrected { get; init; }
}

/// <summary>Result of <c>RunLint</c>: the linter's findings (read-only).</summary>
public sealed record LintReport : OperationResult
{
    /// <summary>True when the extension-provided linter was used instead of the built-in one.</summary>
    public bool UsedExtensionTool { get; init; }
}

/// <summary>Result of <c>FormatCode</c>: files reformatted to the coding standard.</summary>
public sealed record FormatResult : OperationResult
{
    /// <summary>True when the extension-provided formatter was used instead of the built-in one.</summary>
    public bool UsedExtensionTool { get; init; }
}

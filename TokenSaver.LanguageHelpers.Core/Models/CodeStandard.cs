namespace TokenSaver.LanguageHelpers.Core.Models;

/// <summary>
///     The configurable coding standard applied by <c>EnableCodeAnalysis</c> and honored by the lint /
///     format operations. This is the seam for the future "configurable standards" feature: today a
///     small set of knobs with sensible defaults, later populated from <c>.tokensaver</c> config. A
///     language helper maps these neutral knobs onto its tool's native settings (an <c>.editorconfig</c>
///     section, a <c>PSScriptAnalyzerSettings.psd1</c>, a <c>[tool.ruff]</c> table).
/// </summary>
public sealed record CodeStandard
{
    /// <summary>Indentation width in spaces. Ignored when <see cref="UseTabs" /> is true.</summary>
    public int IndentSize { get; init; } = 4;

    /// <summary>Indent with hard tabs instead of spaces.</summary>
    public bool UseTabs { get; init; }

    /// <summary>Soft line-length target the formatter/linter should aim for.</summary>
    public int MaxLineLength { get; init; } = 120;

    /// <summary>Require a final newline at end of file.</summary>
    public bool InsertFinalNewline { get; init; } = true;

    /// <summary>Strip trailing whitespace from lines.</summary>
    public bool TrimTrailingWhitespace { get; init; } = true;

    /// <summary>
    ///     Minimum severity a lint finding must have to be surfaced. Findings below this are dropped from
    ///     reports (but a helper may still auto-fix them).
    /// </summary>
    public CodeIssueSeverity MinimumReportedSeverity { get; init; } = CodeIssueSeverity.Info;

    /// <summary>
    ///     Free-form per-language overrides keyed by <see cref="LanguageId" /> name, passed opaquely to the
    ///     helper. Lets a caller inject tool-specific settings the neutral knobs above don't cover without
    ///     the Core layer having to know about them.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToolOverrides { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The built-in default standard used when no configuration is supplied.</summary>
    public static CodeStandard Default { get; } = new();
}

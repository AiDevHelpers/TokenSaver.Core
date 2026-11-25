namespace AiDevTools.TokenSaver.LanguageHelpers.Core.Models;

/// <summary>
///     Options for <c>RunLint</c>. <see cref="PreferExtensionTool" /> asks the helper to defer to an
///     editor-extension-provided linter (e.g. the OmniSharp/ruff LSP the user already has configured)
///     when one is available, instead of the helper's built-in CLI invocation.
/// </summary>
public sealed record LintOptions
{
    public bool PreferExtensionTool { get; init; }
    public CodeStandard Standard { get; init; } = CodeStandard.Default;

    public static LintOptions Default { get; } = new();
}

/// <summary>
///     Options for <c>FormatCode</c>. <see cref="PreferExtensionTool" /> asks the helper to defer to an
///     editor-extension-provided formatter when available, otherwise the built-in CLI formatter is used.
/// </summary>
public sealed record FormatOptions
{
    public bool PreferExtensionTool { get; init; }
    public CodeStandard Standard { get; init; } = CodeStandard.Default;

    public static FormatOptions Default { get; } = new();
}

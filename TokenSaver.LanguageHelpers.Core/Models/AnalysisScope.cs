namespace AiDevTools.TokenSaver.LanguageHelpers.Core.Models;

/// <summary>
///     Describes what a language-helper operation should act on: a set of target paths (files and/or
///     directories) resolved against a <see cref="WorkspaceRoot" />. The root anchors config discovery
///     (nearest <c>.editorconfig</c>, <c>pyproject.toml</c>, project file, ...) and is the working
///     directory external tools are launched from.
/// </summary>
public sealed record AnalysisScope
{
    /// <summary>
    ///     Absolute path to the workspace/project root. External tools run with this as their working
    ///     directory and config files are written/discovered relative to it.
    /// </summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>
    ///     Files and/or directories to operate on. Empty means "the whole workspace root". Paths may be
    ///     absolute or relative to <see cref="WorkspaceRoot" />.
    /// </summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>When a target is a directory, whether to descend into subdirectories. Defaults to true.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Convenience factory for scoping an operation to a single path.</summary>
    public static AnalysisScope ForPath(string workspaceRoot, string target) =>
        new() { WorkspaceRoot = workspaceRoot, Targets = [target] };

    /// <summary>Convenience factory for scoping an operation to the whole workspace root.</summary>
    public static AnalysisScope ForWorkspace(string workspaceRoot) =>
        new() { WorkspaceRoot = workspaceRoot };
}

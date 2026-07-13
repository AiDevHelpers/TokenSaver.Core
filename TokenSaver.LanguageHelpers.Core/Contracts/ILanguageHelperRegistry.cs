using TokenSaver.LanguageHelpers.Core.Models;

namespace TokenSaver.LanguageHelpers.Core.Contracts;

/// <summary>
///     Resolves the right <see cref="ILanguageHelper" /> for a file or a <see cref="LanguageId" />. The
///     host registers every language project's helper; callers that don't know the language up front
///     (e.g. an MCP tool handed an arbitrary path) go through <see cref="TryGetForFile" />.
/// </summary>
public interface ILanguageHelperRegistry
{
    /// <summary>All registered helpers.</summary>
    IReadOnlyCollection<ILanguageHelper> Helpers { get; }

    /// <summary>Gets the helper for a language, or null when that language isn't registered.</summary>
    ILanguageHelper? Resolve(LanguageId language);

    /// <summary>Finds the helper that owns <paramref name="filePath" />'s extension, or null.</summary>
    ILanguageHelper? TryGetForFile(string filePath);
}

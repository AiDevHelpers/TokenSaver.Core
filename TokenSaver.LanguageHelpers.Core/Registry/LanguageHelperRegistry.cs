using AiDevTools.TokenSaver.LanguageHelpers.Core.Contracts;
using AiDevTools.TokenSaver.LanguageHelpers.Core.Models;

namespace AiDevTools.TokenSaver.LanguageHelpers.Core.Registry;

/// <summary>
///     Default <see cref="ILanguageHelperRegistry" /> backed by whatever <see cref="ILanguageHelper" />
///     implementations the DI container resolved. Lookups are by <see cref="LanguageId" /> and by file
///     extension; the extension index is built once at construction.
/// </summary>
public sealed class LanguageHelperRegistry : ILanguageHelperRegistry
{
    private readonly Dictionary<LanguageId, ILanguageHelper> _byLanguage;
    private readonly Dictionary<string, ILanguageHelper> _byExtension;

    public LanguageHelperRegistry(IEnumerable<ILanguageHelper> helpers)
    {
        _byLanguage = [];
        _byExtension = new Dictionary<string, ILanguageHelper>(StringComparer.OrdinalIgnoreCase);

        foreach (ILanguageHelper helper in helpers)
        {
            // Last registration wins per language, mirroring TryAdd-style override semantics.
            _byLanguage[helper.Language] = helper;
            foreach (string ext in helper.FileExtensions)
            {
                _byExtension[ext] = helper;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ILanguageHelper> Helpers => _byLanguage.Values;

    /// <inheritdoc />
    public ILanguageHelper? Resolve(LanguageId language) =>
        _byLanguage.TryGetValue(language, out ILanguageHelper? helper) ? helper : null;

    /// <inheritdoc />
    public ILanguageHelper? TryGetForFile(string filePath)
    {
        string ext = System.IO.Path.GetExtension(filePath);
        return _byExtension.TryGetValue(ext, out ILanguageHelper? helper) ? helper : null;
    }
}

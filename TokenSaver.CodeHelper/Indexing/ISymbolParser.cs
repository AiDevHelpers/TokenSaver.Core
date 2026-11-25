using AiDevTools.TokenSaver.CodeHelper.Models.Index;

namespace AiDevTools.TokenSaver.CodeHelper.Indexing;

public interface ISymbolParser
{
    IReadOnlyList<string> SupportedExtensions { get; }
    IEnumerable<SymbolEntry> Parse(string filePath, string content);
}

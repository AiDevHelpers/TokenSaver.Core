using AiDevTools.TokenSaver.TokenEconomy.Models.TokenEconomy;

namespace AiDevTools.TokenSaver.TokenEconomy.Contracts;

/// <summary>
///     Splits a file's content into function/class/markdown-section-sized chunks for RepoIndexer.
///     Heuristic regex boundary detection, not a real parser — good enough for semantic-search
///     granularity, cheap, and language-agnostic-ish. Deliberately independent of the existing
///     ISymbolParser (used by WorkspaceIndexService for exact-name symbol search) rather than
///     sharing it — that parser doesn't expose chunk end-lines/spans, and unifying the two would
///     risk the working, already-relied-on symbol index for a feature (semantic chunk search)
///     that doesn't need it.
/// </summary>
public interface IChunkExtractor
{
    /// <summary>
    ///     Returns one or more chunks covering the whole file. Falls back to a single "module"
    ///     chunk spanning the entire file when the extension is unrecognized or no boundaries
    ///     are found within it.
    /// </summary>
    IReadOnlyList<CodeChunk> ExtractChunks(string filePath, string content);
}

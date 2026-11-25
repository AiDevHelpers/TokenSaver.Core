namespace AiDevTools.TokenSaver.TokenEconomy.Models.TokenEconomy;

/// <summary>One SemanticGrep result — Claude gets this, not the raw chunk text; it reads the actual file only for hits it decides matter.</summary>
public sealed record SemanticSearchHit(
    string RelativePath,
    string ChunkType,
    string Name,
    int StartLine,
    int EndLine,
    string Summary,
    double Score);

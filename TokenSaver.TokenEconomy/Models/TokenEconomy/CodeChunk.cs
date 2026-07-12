namespace TokenSaver.TokenEconomy.Models.TokenEconomy;

/// <summary>One function/class/markdown-section/whole-file chunk produced by IChunkExtractor, before summarization/embedding.</summary>
/// <param name="ChunkType">"function" | "class" | "md_section" | "module" (whole-file fallback).</param>
/// <param name="Name">Function/class name, markdown heading text, or file base name for a "module" chunk.</param>
/// <param name="StartLine">1-based, inclusive.</param>
/// <param name="EndLine">1-based, inclusive.</param>
/// <param name="Text">The chunk's own source text (not the whole file).</param>
public sealed record CodeChunk(string ChunkType, string Name, int StartLine, int EndLine, string Text);

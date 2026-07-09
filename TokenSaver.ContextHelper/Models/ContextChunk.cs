namespace TokenSaver.ContextHelper.Models;

public enum ContentKind
{
    Code,
    Log,
    File,
    Conversation,
    ToolCall,
    Other
}

public class ContextChunk
{
    public string ChunkId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Source { get; set; } = ""; // file path or "conversation"
    public string SectionName { get; set; } = ""; // heading, method name, or turn index
    public ContentKind Kind { get; set; }
    public string Content { get; set; } = "";
    public int TokenCount { get; set; }
    public double Relevance { get; set; } = 1.0; // cosine similarity, or 1.0 if not filtered
    public IReadOnlyList<float>? Embedding { get; set; }
    public bool WasCollapsed { get; set; }
    public bool WasSummarized { get; set; }
    public bool WasDropped { get; set; }
    public bool PreserveVerbatim { get; set; } // set by pre-strip overlap analysis
}

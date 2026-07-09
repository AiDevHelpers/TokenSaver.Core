namespace TokenSaver.ContextHelper.Models.Diagnostics;

public class ManifestEntry
{
    public string ChunkId { get; set; } = "";
    public string Source { get; set; } = "";
    public string SectionName { get; set; } = "";
    public ContentKind Kind { get; set; }
    public int TokenCount { get; set; }
    public double Relevance { get; set; }
    public bool WasCollapsed { get; set; }
    public bool WasSummarized { get; set; }
    public bool WasDropped { get; set; }
    public string? DroppedReason { get; set; }
}

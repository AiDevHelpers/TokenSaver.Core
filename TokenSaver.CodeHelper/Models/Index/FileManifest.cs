namespace TokenSaver.CodeHelper.Models.Index;

public record FileManifest
{
    public string FilePath { get; init; } = "";
    public long LastModified { get; init; }
    public long IndexedAt { get; init; }
    public int SymbolCount { get; init; }
}

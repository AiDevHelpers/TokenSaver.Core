namespace TokenSaver.CodeHelper.Models.Index;

public record IndexStats
{
    public int FilesIndexed { get; init; }
    public int SymbolsIndexed { get; init; }
    public int FilesSkipped { get; init; }
    public TimeSpan Elapsed { get; init; }
    public int FilesPruned { get; init; }
    public int SymbolsPruned { get; init; }

    public override string ToString()
    {
        string prunedSuffix = FilesPruned > 0 ? $", pruned {FilesPruned} files/{SymbolsPruned} symbols" : "";
        return $"{FilesIndexed} files indexed, {SymbolsIndexed} symbols found, " +
               $"{FilesSkipped} skipped in {Elapsed.TotalSeconds:F1}s{prunedSuffix}";
    }
}

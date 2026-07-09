namespace TokenSaver.CodeHelper.Models.Index;

public enum ReferenceKind
{
    Call,
    Inherit,
    Implement,
    Use,
    Import,
    Override
}

public record SymbolReference
{
    public string SourceId { get; init; } = "";
    public string TargetId { get; init; } = "";
    public ReferenceKind Kind { get; init; }
    public int Line { get; init; }
}

namespace TokenSaver.CodeHelper.Models.Patch;

public enum InsertPosition
{
    Before,
    After
}

public record InsertOperation : PatchOperation
{
    /// <summary>Existing text to locate in the file.</summary>
    public string Anchor { get; init; } = "";

    public string Content { get; init; } = "";
    public InsertPosition Position { get; init; } = InsertPosition.After;
}

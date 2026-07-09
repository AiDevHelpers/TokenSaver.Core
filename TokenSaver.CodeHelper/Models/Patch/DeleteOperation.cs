namespace TokenSaver.CodeHelper.Models.Patch;

public record DeleteOperation : PatchOperation
{
    /// <summary>Literal text or regex pattern to delete.</summary>
    public string Pattern { get; init; } = "";

    public bool IsRegex { get; init; }
}

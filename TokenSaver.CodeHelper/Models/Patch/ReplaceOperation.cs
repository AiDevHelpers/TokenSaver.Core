namespace AiDevTools.TokenSaver.CodeHelper.Models.Patch;

public record ReplaceOperation : PatchOperation
{
    public string OldText { get; init; } = "";
    public string NewText { get; init; } = "";

    /// <summary>0 = replace all occurrences; 1 = first only; N = Nth occurrence.</summary>
    public int Occurrence { get; init; }
}

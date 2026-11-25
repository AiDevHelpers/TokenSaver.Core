namespace AiDevTools.TokenSaver.CodeHelper.Models.Patch;

public record RenameOperation : PatchOperation
{
    public string OldName { get; init; } = "";
    public string NewName { get; init; } = "";
    public bool ReplaceInComments { get; init; }

    /// <summary>When true, only replaces whole-word occurrences (word boundary regex).</summary>
    public bool WholeWordOnly { get; init; } = true;
}

namespace AiDevTools.TokenSaver.CodeHelper.Models.Patch;

/// <summary>Base record for all patch operations. All concrete operations are records.</summary>
public abstract record PatchOperation
{
    /// <summary>Workspace-relative file path.</summary>
    public string FilePath { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>When true the operation is validated but the file is not written.</summary>
    public bool DryRun { get; init; }
}

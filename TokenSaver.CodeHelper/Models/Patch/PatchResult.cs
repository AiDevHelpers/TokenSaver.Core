namespace TokenSaver.CodeHelper.Models.Patch;

public record PatchResult
{
    public bool Success { get; init; }
    public string FilePath { get; init; } = "";
    public int LinesChanged { get; init; }
    public string? BackupPath { get; init; }
    public string? Error { get; init; }
    public bool WasDryRun { get; init; }
    public string Description { get; init; } = "";
}

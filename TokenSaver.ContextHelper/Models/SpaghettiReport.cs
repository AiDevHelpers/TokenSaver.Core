namespace TokenSaver.ContextHelper.Models;

public class SpaghettiReport
{
    public string FilePath { get; set; } = "";
    public int DuplicateBlockCount { get; set; }
    public IReadOnlyList<string> BlockHashes { get; set; } = [];
    public string Suggestion { get; set; } = "";
}

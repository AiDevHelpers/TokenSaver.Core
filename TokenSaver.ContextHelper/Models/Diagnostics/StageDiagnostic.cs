namespace TokenSaver.ContextHelper.Models.Diagnostics;

public class StageDiagnostic
{
    public string StageName { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TokensSaved => InputTokens - OutputTokens;
    public double SavingsPercent => InputTokens == 0 ? 0 : Math.Round((double)TokensSaved / InputTokens * 100, 1);
    public int ItemsDropped { get; set; }
    public int ItemsCollapsed { get; set; }
    public TimeSpan Elapsed { get; set; }
    public IReadOnlyList<string> Notes { get; set; } = [];
}

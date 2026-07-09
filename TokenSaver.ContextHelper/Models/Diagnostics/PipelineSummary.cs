namespace TokenSaver.ContextHelper.Models.Diagnostics;

public class PipelineSummary
{
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int TotalTokensSaved => TotalInputTokens - TotalOutputTokens;

    public double TotalSavingsPercent => TotalInputTokens == 0
        ? 0
        : Math.Round((double)TotalTokensSaved / TotalInputTokens * 100, 1);

    public TimeSpan TotalElapsed { get; set; }
    public IReadOnlyList<StageDiagnostic> Stages { get; set; } = [];
    public string NarrativeSummary { get; set; } = "";
    public ContextManifest Manifest { get; set; } = new();

    /// <summary>Task type determined by <see cref="PromptAssessor" /> for this pipeline run.</summary>
    public TaskType TaskType { get; set; } = TaskType.General;
}

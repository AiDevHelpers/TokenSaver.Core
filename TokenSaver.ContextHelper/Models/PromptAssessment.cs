namespace TokenSaver.ContextHelper.Models;

public class PromptAssessment
{
    public TaskType TaskType { get; set; } = TaskType.General;
    public WeightProfile WeightProfile { get; set; } = WeightProfile.For(TaskType.General);
    public IReadOnlyList<string> KeyFiles { get; set; } = [];
    public IReadOnlyList<string> KeyConcepts { get; set; } = [];
    public IReadOnlyList<string> Constraints { get; set; } = [];

    /// <summary>0–1 confidence in the task-type classification.</summary>
    public double Confidence { get; set; }
}

namespace TokenSaver.ContextHelper.Models;

/// <summary>
///     Per-content-kind retention weights. 0.0 = strip aggressively; 1.0 = preserve fully.
///     Weights scale semantic thresholds, collapse thresholds, and token budget allocation
///     for each content kind through the pipeline.
/// </summary>
public class WeightProfile
{
    public double CodeWeight { get; set; }
    public double LogWeight { get; set; }
    public double FileWeight { get; set; }
    public double ConversationWeight { get; set; }
    public double ToolCallWeight { get; set; }

    public static WeightProfile For(TaskType taskType)
    {
        return taskType switch
        {
            TaskType.Code => new WeightProfile
            { CodeWeight = 0.9, LogWeight = 0.2, FileWeight = 0.7, ConversationWeight = 0.5, ToolCallWeight = 0.4 },
            TaskType.Debug => new WeightProfile
            { CodeWeight = 0.8, LogWeight = 0.9, FileWeight = 0.5, ConversationWeight = 0.6, ToolCallWeight = 0.7 },
            TaskType.Planning => new WeightProfile
            { CodeWeight = 0.4, LogWeight = 0.1, FileWeight = 0.8, ConversationWeight = 0.9, ToolCallWeight = 0.2 },
            TaskType.Review => new WeightProfile
            { CodeWeight = 0.9, LogWeight = 0.3, FileWeight = 0.8, ConversationWeight = 0.6, ToolCallWeight = 0.3 },
            TaskType.Explain => new WeightProfile
            { CodeWeight = 0.8, LogWeight = 0.2, FileWeight = 0.9, ConversationWeight = 0.7, ToolCallWeight = 0.2 },
            TaskType.Refactor => new WeightProfile
            { CodeWeight = 0.9, LogWeight = 0.1, FileWeight = 0.7, ConversationWeight = 0.5, ToolCallWeight = 0.3 },
            TaskType.Test => new WeightProfile
            { CodeWeight = 0.8, LogWeight = 0.5, FileWeight = 0.6, ConversationWeight = 0.5, ToolCallWeight = 0.6 },
            _ => new WeightProfile
            {
                CodeWeight = 0.6,
                LogWeight = 0.3,
                FileWeight = 0.6,
                ConversationWeight = 0.6,
                ToolCallWeight = 0.4
            }
        };
    }
}

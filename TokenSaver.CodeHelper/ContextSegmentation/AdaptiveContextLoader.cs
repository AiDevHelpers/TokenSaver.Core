using TokenSaver.CodeHelper.Planning;
using Microsoft.Extensions.Logging;

namespace TokenSaver.CodeHelper.ContextSegmentation;

/// <summary>
/// Combines task type detection + access patterns to load only relevant segments.
/// Reduces context by ~30-50% on typical tasks while preserving task-critical content.
/// </summary>
public class AdaptiveContextLoader
{
    private readonly ContextSegmentationService _segmentation;
    private readonly AccessPatternTracker _tracker;
    private readonly TaskClassifier _taskClassifier;
    private readonly ILogger<AdaptiveContextLoader> _logger;

    public AdaptiveContextLoader(
        ContextSegmentationService segmentation,
        AccessPatternTracker tracker,
        TaskClassifier taskClassifier,
        ILogger<AdaptiveContextLoader> logger)
    {
        _segmentation = segmentation;
        _tracker = tracker;
        _taskClassifier = taskClassifier;
        _logger = logger;
    }

    /// <summary>
    /// Given a user prompt, determine which segments to load for context.
    /// Returns segments ranked by relevance, along with a session ID for tracking.
    /// </summary>
    public async Task<AdaptiveLoadPlan> PlanContextLoadAsync(
        string userPrompt,
        int tokenBudget = 50000,
        CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var plan = new AdaptiveLoadPlan { SessionId = sessionId };

        // Classify task type
        try
        {
            var (kind, confidence) = await _taskClassifier.ClassifyAsync(userPrompt, ct);
            plan.DetectedTaskType = kind.ToString();
            plan.TaskTypeConfidence = confidence;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Task classification failed, defaulting to General");
            plan.DetectedTaskType = "General";
            plan.TaskTypeConfidence = 0.0;
        }

        // Select segments by task type affinity + priority
        var segments = await _segmentation.GetSegmentsForTaskTypeAsync(
            plan.DetectedTaskType,
            tokenBudget,
            ct);

        foreach (ContextSegment seg in segments) { plan.SegmentsToLoad.Add(seg); }

        // Ask access patterns for related segments we might have missed
        var segIds = segments.Select(s => s.Id).ToList();
        var recommended = await _tracker.RecommendRelatedSegmentsAsync(segIds, ct);

        var allSegments = await _segmentation.GetSegmentsAsync(ct);
        int tokensUsed = segments.Sum(s => s.EstimatedTokens);

        foreach (var recId in recommended)
        {
            var seg = allSegments.FirstOrDefault(s => s.Id == recId);
            if (seg != null && tokensUsed + seg.EstimatedTokens <= tokenBudget)
            {
                plan.SegmentsToLoad.Add(seg);
                plan.RecommendedSegmentIds.Add(recId);
                tokensUsed += seg.EstimatedTokens;
            }
        }

        plan.TotalEstimatedTokens = plan.SegmentsToLoad.Sum(s => s.EstimatedTokens);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Adaptive load plan for {TaskType} (confidence {Confidence:F2}): " +
                "{Count} segments, {Tokens} tokens, recommended: {Recommended}",
                plan.DetectedTaskType,
                plan.TaskTypeConfidence,
                plan.SegmentsToLoad.Count,
                plan.TotalEstimatedTokens,
                string.Join(", ", plan.RecommendedSegmentIds));
        }

        return plan;
    }

    /// <summary>
    /// Record which segments were actually loaded and used in this session.
    /// Call this at session end to train future recommendations.
    /// </summary>
    public async Task RecordSessionAsync(
        AdaptiveLoadPlan plan,
        IEnumerable<string> segmentsUsed,
        IEnumerable<string> segmentsModified,
        SessionOutcome outcome = SessionOutcome.Succeeded,
        CancellationToken ct = default)
    {
        var pattern = new AccessPattern
        {
            SessionId = plan.SessionId,
            Timestamp = DateTime.UtcNow,
            TaskType = plan.DetectedTaskType,
            SegmentsLoaded = plan.SegmentsToLoad.Select(s => s.Id).ToArray(),
            SegmentsUsed = segmentsUsed.ToArray(),
            SegmentsModified = segmentsModified.ToArray(),
            Outcome = outcome
        };

        await _tracker.RecordAccessAsync(pattern, ct);

        // Update LastAccessed timestamps
        var allSegments = await _segmentation.GetSegmentsAsync(ct);
        foreach (var segId in pattern.SegmentsUsed)
        {
            var seg = allSegments.FirstOrDefault(s => s.Id == segId);
            if (seg != null)
            {
                seg.LastAccessed = DateTime.UtcNow;
            }
        }
    }
}

public class AdaptiveLoadPlan
{
    public string SessionId { get; set; } = null!;
    public string DetectedTaskType { get; set; } = "General";
    public double TaskTypeConfidence { get; set; }
    public IList<ContextSegment> SegmentsToLoad { get; set; } = [];
    public IList<string> RecommendedSegmentIds { get; set; } = [];
    public int TotalEstimatedTokens { get; set; }
}

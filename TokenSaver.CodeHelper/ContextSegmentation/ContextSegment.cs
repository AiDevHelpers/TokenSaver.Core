namespace AiDevTools.TokenSaver.CodeHelper.ContextSegmentation;

/// <summary>
/// A logical partition of codebase context — may span multiple files/folders.
/// Loaded selectively based on task type and access patterns.
/// </summary>
public class ContextSegment
{
    public string Id { get; set; } = null!;

    /// <summary>Concise label: "main_arch", "subsystem_indexing", etc.</summary>
    public string Label { get; set; } = null!;

    /// <summary>Glob patterns: ["src/Core/AiDevTools.TokenSaver.CodeHelper/Indexing/**"], ["*.csproj"]</summary>
    public IReadOnlyList<string> PathPatterns { get; set; } = [];

    /// <summary>Approximate token size; used for budget planning.</summary>
    public int EstimatedTokens { get; set; }

    /// <summary>How essential this segment is: Critical > Important > Optional</summary>
    public SegmentPriority Priority { get; set; } = SegmentPriority.Important;

    /// <summary>Task types this segment is especially relevant for.</summary>
    public IReadOnlyList<string> PrimaryTaskTypes { get; set; } = [];

    /// <summary>Last time this segment was accessed.</summary>
    public DateTime LastAccessed { get; set; }

    /// <summary>Summary of segment contents (cached).</summary>
    public string? CachedSummary { get; set; }
}

public enum SegmentPriority
{
    Critical = 0,
    Important = 1,
    Optional = 2
}

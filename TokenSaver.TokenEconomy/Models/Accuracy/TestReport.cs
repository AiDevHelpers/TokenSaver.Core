namespace TokenSaver.TokenEconomy.Models.Accuracy;

/// <summary>
///     Aggregated results from a batch accuracy test run.
/// </summary>
public sealed class TestReport
{
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public string WorkspaceRoot { get; init; } = "";
    public string AgentType { get; init; } = "code";

    public int TotalTests { get; init; }
    public int PassedTests { get; init; }
    public int FailedTests => TotalTests - PassedTests;
    public double PassRate => TotalTests == 0 ? 0 : (double)PassedTests / TotalTests;
    public double AverageScore { get; init; }
    public TimeSpan AverageLatency { get; init; }

    public IReadOnlyList<TestResult> Results { get; init; } = [];

    /// <summary>Per-category breakdown.</summary>
    public Dictionary<string, CategoryStats> ByCategory { get; init; } = [];

    /// <summary>
    ///     Patterns that most commonly failed — useful for identifying systematic prompt weaknesses.
    ///     Keyed by pattern, value is number of tests it failed.
    /// </summary>
    public Dictionary<string, int> CommonFailures { get; init; } = [];

    /// <summary>
    ///     AI-readable summary of what the results suggest about the current system prompt.
    ///     Empty until the calling AI fills it in after analysis.
    /// </summary>
    public string? AnalysisSummary { get; set; }
}

/// <summary>Per-category pass/fail/score breakdown for a <see cref="TestReport" />.</summary>
public sealed record CategoryStats(int Total, int Passed, double AvgScore);

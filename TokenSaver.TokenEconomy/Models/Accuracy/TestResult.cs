namespace AiDevTools.TokenSaver.TokenEconomy.Models.Accuracy;

/// <summary>
///     The outcome of running a single <see cref="TestCase" /> against the model.
/// </summary>
public sealed record TestResult
{
    public TestCase TestCase { get; init; } = new();

    /// <summary>Raw model response text.</summary>
    public string Response { get; init; } = "";

    /// <summary>
    ///     Score from 0.0 (all criteria failed) to 1.0 (all criteria passed, no forbidden patterns).
    ///     Formula: (passedPatterns / totalRequired) * (1 − forbiddenRatio)
    /// </summary>
    public double Score { get; init; }

    /// <summary>True when Score ≥ 0.7 and no forbidden patterns triggered.</summary>
    public bool Passed { get; init; }

    public IReadOnlyList<string> PassedPatterns { get; init; } = [];
    public IReadOnlyList<string> FailedPatterns { get; init; } = [];
    public IReadOnlyList<string> TriggeredForbidden { get; init; } = [];

    /// <summary>How long the model took to respond.</summary>
    public TimeSpan Latency { get; init; }

    /// <summary>
    ///     Verdict from the judge pass ("YES" / "NO") — the draft model evaluates
    ///     whether the main response correctly solved the task.
    ///     Null if judge evaluation was skipped.
    /// </summary>
    public string? JudgeVerdict { get; init; }

    /// <summary>Human-readable notes from the scoring logic.</summary>
    public string? Notes { get; init; }
}

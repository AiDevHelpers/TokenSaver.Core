namespace AiDevTools.TokenSaver.CodeHelper.Models.Calibration;

/// <summary>Severity of fixes the calling AI applied to CodeHelper's scaffold output.</summary>
public enum FixSeverity
{
    /// <summary>Cosmetic or minor logic corrections.</summary>
    Minor = 0,

    /// <summary>Significant logic errors or missing features.</summary>
    Major = 1,

    /// <summary>Fundamentally wrong approach — large rewrites required.</summary>
    Critical = 2
}

/// <summary>
///     Records one round of feedback after the calling AI reviewed CodeHelper's output.
///     Persisted as a JSON line in <c>.local-mcp/calibration-outcomes.jsonl</c>.
/// </summary>
public record CalibrationOutcome(
    string SessionId,
    string PlanId,
    string TaskKind,
    string Language,
    int AiFixCount,
    FixSeverity AiFixSeverity,
    /// <summary>
    /// 0.0 = AI fixed nothing; 1.0 = AI rewrote everything.
    /// Set by the caller as an estimate of total-work fraction.
    /// </summary>
    double CorrectionRatio,
    string? Notes,
    DateTime RecordedAt
);

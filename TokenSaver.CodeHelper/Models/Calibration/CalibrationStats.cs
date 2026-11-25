namespace AiDevTools.TokenSaver.CodeHelper.Models.Calibration;

/// <summary>Snapshot of calibration state returned by <c>GetCalibrationStats</c>.</summary>
public record CalibrationStats(
    CalibrationWeights CurrentWeights,
    int TotalOutcomes,
    /// <summary>Fraction of the last 20 outcomes where the scaffold was deemed useful.</summary>
    double RecentPassRate,
    /// <summary>Human-readable trend line per TaskKind that has at least 2 outcomes.</summary>
    IReadOnlyList<string> RecentTrends
);

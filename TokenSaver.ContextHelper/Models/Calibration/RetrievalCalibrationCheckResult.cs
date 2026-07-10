namespace TokenSaver.ContextHelper.Models.Calibration;

/// <summary>
///     Returned by <see cref="Calibration.RetrievalCalibrationService.CheckLateRequestAsync" /> when at
///     least one newly-requested file was checked against the previous turn's relevance bar for this
///     session. Null (no result) means there was no previous turn to compare against, or nothing new
///     was requested — the common case, not worth a caller-visible note.
/// </summary>
public sealed record RetrievalCalibrationCheckResult(
    IReadOnlyList<RetrievalCalibrationOutcome> Outcomes,
    bool ThresholdAdjusted,
    double NewThresholdAdjustment)
{
    public bool AnyFailure => Outcomes.Any(o => o.WasFailure);
}

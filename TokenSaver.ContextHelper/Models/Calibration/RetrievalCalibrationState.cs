namespace TokenSaver.ContextHelper.Models.Calibration;

/// <summary>
///     Persisted at <c>.tokensaver/retrieval-calibration.json</c>. Holds the running adjustment
///     applied on top of <c>ContextHelperConfig.SemanticThreshold</c> — see
///     <see cref="Calibration.RetrievalCalibrationService.GetEffectiveThreshold" />.
/// </summary>
public sealed class RetrievalCalibrationState
{
    /// <summary>Added to the configured <c>SemanticThreshold</c>, clamped to a bounded range. Negative = wider (keeps more), positive = narrower (keeps less).</summary>
    public double ThresholdAdjustment { get; set; }

    public int ConsecutiveFailures { get; set; }
    public int ConsecutivePasses { get; set; }
    public int TotalSamples { get; set; }
    public int TotalFailures { get; set; }
    public string LastUpdated { get; set; } = "";
}

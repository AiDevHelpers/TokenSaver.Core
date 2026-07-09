namespace TokenSaver.CodeHelper.Models.Calibration;

/// <summary>
///     Per-task-kind calibration tuning. Allows certain task kinds to learn faster or
///     require more evidence before weights are adjusted.
/// </summary>
public class TaskKindCalibrationPolicy
{
    /// <summary>
    ///     Multiplies the global LearningRate for this task kind.
    ///     > 1.0 = learns faster; &lt; 1.0 = more conservative.
    /// </summary>
    public double LearningRateMultiplier { get; set; } = 1.0;

    /// <summary>
    ///     Minimum number of recorded outcomes for this task kind before any weight
    ///     adjustment is applied. Guards against overcorrecting on sparse data.
    /// </summary>
    public int MinSamplesBeforeAdjust { get; set; } = 1;

    /// <summary>
    ///     Per-kind override for WasteThreshold (correctionRatio below which scaffold is deemed useful).
    ///     Null = use the global WasteThreshold from CodeHelperConfig.
    /// </summary>
    public double? WasteThresholdOverride { get; set; }
}

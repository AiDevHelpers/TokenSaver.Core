namespace TokenSaver.CodeHelper.Models.Calibration;

/// <summary>
///     Per-task-kind and per-language confidence multipliers.
///     Shipped as embedded baseline defaults; diverges per workspace over time.
/// </summary>
public class CalibrationWeights
{
    /// <summary>Fallback multiplier when no task-kind or language weight is found (0.0–1.0).</summary>
    public double GlobalConfidence { get; set; } = 0.70;

    /// <summary>Per-TaskKind confidence weight (key = TaskKind.ToString()).</summary>
    public Dictionary<string, double> ByTaskKind { get; set; } = [];

    /// <summary>Per-language confidence weight (key = file extension, e.g. ".cs").</summary>
    public Dictionary<string, double> ByLanguage { get; set; } = [];

    /// <summary>ISO 8601 timestamp of the last update.</summary>
    public string LastUpdated { get; set; } = "";

    /// <summary>
    ///     Persisted override for CodeHelperConfig.AutoEnabled. Null = no persisted override;
    ///     the in-memory/config-file default applies. Set via CalibrationService.SetAutoEnabled.
    /// </summary>
    public bool? AutoEnabled { get; set; }

    // ── Lookup helpers ────────────────────────────────────────────────────────

    public double GetTaskKindWeight(string taskKind)
    {
        return ByTaskKind.TryGetValue(taskKind, out double w) ? w : GlobalConfidence;
    }

    public double GetLanguageWeight(string extension)
    {
        return ByLanguage.TryGetValue(extension, out double w) ? w : GlobalConfidence;
    }
}

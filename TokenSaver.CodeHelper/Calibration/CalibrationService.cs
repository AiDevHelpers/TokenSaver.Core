using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.CodeHelper.Models.Calibration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Reflection;
using System.Text.Json;

namespace AiDevTools.TokenSaver.CodeHelper.Calibration;

/// <summary>
///     Loads, merges, and updates calibration weights from three sources (priority order):
///     1. Embedded baseline JSON (lowest priority)
///     2. User-level file  (~AppData/TokenSaver/calibration.json)
///     3. Workspace-level file (.local-mcp/calibration.json)  ← highest priority
///     Outcomes are appended to .local-mcp/calibration-outcomes.jsonl for auditability.
///     Only the workspace-level file is ever written back at runtime.
/// </summary>
public sealed partial class CalibrationService
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Calibration: skipping {TaskKind} adjustment — only {Count}/{Min} samples")]
    private static partial void LogSkippingAdjustment(ILogger logger, string taskKind, int count, int min);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Calibration: {Key} {Old:F3} → {New:F3} (useful={Useful}, lr={Lr:F3})")]
    private static partial void LogWeightAdjusted(ILogger logger, string key, double old, double @new, bool useful, double lr);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read calibration file {Path}")]
    private static partial void LogCalibrationReadFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save workspace calibration to {Path}")]
    private static partial void LogCalibrationSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not append calibration outcome to {Path}")]
    private static partial void LogCalibrationOutcomeAppendFailed(ILogger logger, Exception ex, string path);

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly CodeHelperConfig _cfg;
    private readonly ILogger<CalibrationService> _log;

    public CalibrationService(
        IOptions<CodeHelperConfig> config,
        ILogger<CalibrationService> logger)
    {
        _cfg = config.Value;
        _log = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Persists the AutoEnabled flag to disk so the toggle survives process restarts.
    ///     scope="workspace" writes AutoEnabled into the workspace-level calibration file
    ///     (requires workspaceRoot); scope="global" writes it into the user-level file.
    /// </summary>
    public void SetAutoEnabled(bool enabled, string scope, string? workspaceRoot = null)
    {
        string path = string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase)
            ? UserLevelPath()
            : WorkspaceLevelPath(
                string.IsNullOrWhiteSpace(workspaceRoot)
                    ? throw new ArgumentException("workspaceRoot is required when scope is 'workspace'.", nameof(workspaceRoot))
                    : workspaceRoot);

        CalibrationWeights weights = File.Exists(path)
            ? JsonSerializer.Deserialize<CalibrationWeights>(File.ReadAllText(path), _json) ?? new CalibrationWeights()
            : new CalibrationWeights();

        weights.AutoEnabled = enabled;
        weights.LastUpdated = DateTime.UtcNow.ToString("o");

        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(weights, _json));
        }
        catch (Exception ex)
        {
            LogCalibrationSaveFailed(_log, ex, path);
        }
    }

    /// <summary>Returns the merged calibration weights for the given workspace root.</summary>
    public CalibrationWeights GetWeights(string? workspaceRoot = null)
    {
        CalibrationWeights weights = LoadEmbeddedBaseline();
        MergeFrom(weights, UserLevelPath());
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            MergeFrom(weights, WorkspaceLevelPath(workspaceRoot));
        }

        return weights;
    }

    /// <summary>
    ///     Records a post-review outcome and adjusts workspace-level weights accordingly.
    ///     Never touches the user-level file.
    /// </summary>
    public void RecordOutcome(CalibrationOutcome outcome, string? workspaceRoot = null)
    {
        AppendOutcome(outcome, workspaceRoot);

        // Read history AFTER appending so the current outcome is included in the count
        List<CalibrationOutcome> history = ReadRecentOutcomes(workspaceRoot, 100);
        CalibrationWeights weights = GetWeights(workspaceRoot);
        AdjustWeights(weights, outcome, history);
        weights.LastUpdated = DateTime.UtcNow.ToString("o");

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            SaveWorkspaceWeights(weights, workspaceRoot);
        }
    }

    /// <summary>Returns calibration stats for the MCP diagnostics tool.</summary>
    public CalibrationStats GetStats(string? workspaceRoot = null)
    {
        CalibrationWeights weights = GetWeights(workspaceRoot);
        List<CalibrationOutcome> outcomes = ReadRecentOutcomes(workspaceRoot, 20);

        double passRate = outcomes.Count == 0
            ? 0.0
            : (double)outcomes.Count(o => o.CorrectionRatio < _cfg.WasteThreshold) / outcomes.Count;

        List<string> trends = outcomes
            .GroupBy(o => o.TaskKind)
            .Where(g => g.Count() >= 2)
            .Select(g =>
            {
                int useful = g.Count(o => o.CorrectionRatio < _cfg.WasteThreshold);
                double w = weights.GetTaskKindWeight(g.Key);
                return $"{g.Key}: {useful}/{g.Count()} useful, current weight {w:F2}";
            })
            .ToList();

        return new CalibrationStats(weights, outcomes.Count, passRate, trends);
    }

    // ── Weight adjustment ─────────────────────────────────────────────────────

    private void AdjustWeights(CalibrationWeights weights, CalibrationOutcome outcome, List<CalibrationOutcome> history)
    {
        TaskKindCalibrationPolicy policy = _cfg.GetPolicy(outcome.TaskKind);
        double wasteThresh = policy.WasteThresholdOverride ?? _cfg.WasteThreshold;
        bool wasUseful = outcome.CorrectionRatio < wasteThresh;

        // Count prior outcomes for this task kind; skip adjustment if below MinSamples
        int taskKindSamples = history.Count(o => o.TaskKind == outcome.TaskKind);
        if (taskKindSamples < policy.MinSamplesBeforeAdjust)
        {
            LogSkippingAdjustment(_log, outcome.TaskKind, taskKindSamples, policy.MinSamplesBeforeAdjust);
        }
        else
        {
            AdjustKey(weights.ByTaskKind, outcome.TaskKind, wasUseful, outcome.AiFixSeverity,
                policy.LearningRateMultiplier);
        }

        // Language weight always adjusts (no min-sample guard — language signal is coarser)
        AdjustKey(weights.ByLanguage, outcome.Language, wasUseful, outcome.AiFixSeverity,
            0.7); // language learns more conservatively
    }

    private void AdjustKey(
        Dictionary<string, double> dict,
        string key,
        bool wasUseful,
        FixSeverity severity,
        double learningRateMultiplier = 1.0)
    {
        double current = dict.TryGetValue(key, out double v) ? v : _cfg.CanDoThreshold;
        double effectiveLr = _cfg.LearningRate * learningRateMultiplier;

        double newVal;
        if (wasUseful)
        {
            // Nudge toward 1.0
            newVal = Math.Min(1.0, current + (effectiveLr * (1.0 - current)));
        }
        else
        {
            // Nudge down, scaled by fix severity
            double severityMult = severity switch
            {
                FixSeverity.Minor => 0.5,
                FixSeverity.Major => 1.0,
                FixSeverity.Critical => 2.0,
                _ => 1.0
            };
            newVal = Math.Max(0.0, current - (effectiveLr * severityMult * current));
        }

        dict[key] = Math.Round(newVal, 4);
        LogWeightAdjusted(_log, key, current, newVal, wasUseful, effectiveLr);
    }

    // ── Load / merge ──────────────────────────────────────────────────────────

    private static CalibrationWeights LoadEmbeddedBaseline()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("baseline-calibration.json", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return new CalibrationWeights();
        }

        using Stream stream = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<CalibrationWeights>(stream, _json) ?? new CalibrationWeights();
    }

    private void MergeFrom(CalibrationWeights target, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(path);
            CalibrationWeights? source = JsonSerializer.Deserialize<CalibrationWeights>(text, _json);
            if (source is null)
            {
                return;
            }

            if (source.GlobalConfidence > 0)
            {
                target.GlobalConfidence = source.GlobalConfidence;
            }

            foreach ((string? k, double v) in source.ByTaskKind)
            {
                target.ByTaskKind[k] = v;
            }

            foreach ((string? k, double v) in source.ByLanguage)
            {
                target.ByLanguage[k] = v;
            }

            if (!string.IsNullOrWhiteSpace(source.LastUpdated))
            {
                target.LastUpdated = source.LastUpdated;
            }

            if (source.AutoEnabled.HasValue)
            {
                target.AutoEnabled = source.AutoEnabled;
            }
        }
        catch (Exception ex)
        {
            LogCalibrationReadFailed(_log, ex, path);
        }
    }

    /// <summary>
    ///     Resolves the effective AutoEnabled flag: persisted workspace override, then persisted
    ///     user-level override, then the config-file/in-memory default passed in.
    /// </summary>
    public bool ResolveAutoEnabled(bool configDefault, string? workspaceRoot = null)
    {
        CalibrationWeights weights = GetWeights(workspaceRoot);
        return weights.AutoEnabled ?? configDefault;
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    private void SaveWorkspaceWeights(CalibrationWeights weights, string workspaceRoot)
    {
        string path = WorkspaceLevelPath(workspaceRoot);
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(weights, _json));
        }
        catch (Exception ex)
        {
            LogCalibrationSaveFailed(_log, ex, path);
        }
    }

    // ── Outcomes log ──────────────────────────────────────────────────────────

    private void AppendOutcome(CalibrationOutcome outcome, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return;
        }

        string path = Path.Combine(workspaceRoot, ".tokensaver", "calibration-outcomes.jsonl");
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(outcome) + "\n");
        }
        catch (Exception ex)
        {
            LogCalibrationOutcomeAppendFailed(_log, ex, path);
        }
    }

    private List<CalibrationOutcome> ReadRecentOutcomes(string? workspaceRoot, int max)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return [];
        }

        string path = Path.Combine(workspaceRoot, ".tokensaver", "calibration-outcomes.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return File.ReadLines(path)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l =>
                {
                    try
                    {
                        return JsonSerializer.Deserialize<CalibrationOutcome>(l);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(o => o is not null)
                .Cast<CalibrationOutcome>()
                .TakeLast(max)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // ── Paths ─────────────────────────────────────────────────────────────────

    private string UserLevelPath()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.UserCalibrationPath))
        {
            return _cfg.UserCalibrationPath;
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tokensaver",
                "calibration.json")
            : Path.Combine(appData, "TokenSaver", "calibration.json");
    }

    private string WorkspaceLevelPath(string workspaceRoot)
    {
        return Path.Combine(workspaceRoot, _cfg.WorkspaceCalibrationPath);
    }
}

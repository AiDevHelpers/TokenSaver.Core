using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Embedding;
using TokenSaver.ContextHelper.Models.Calibration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace TokenSaver.ContextHelper.Calibration;

/// <summary>
///     Self-tunes <c>ContextHelperConfig.SemanticThreshold</c> from live usage signal: when a
///     caller explicitly requests a file in a later <c>BuildConciseContext</c> turn of the same
///     session that <see cref="Pipeline.Stages.SemanticRelevanceFilterStage" /> never surfaced above
///     the bar it applied last turn, that is direct evidence the filter was too narrow. Enough
///     consecutive misses nudge <see cref="RetrievalCalibrationState.ThresholdAdjustment" /> down
///     (wider); enough consecutive clean turns nudge it back toward the configured baseline.
///     <para>
///     This only ever pulls the effective threshold below the configured baseline from live
///     signal — there is no online signal here for "too wide" (a kept-but-irrelevant chunk never
///     announces itself the way a missing one does via a re-request). Detecting "too wide" is the
///     offline golden-set eval's job (<c>docs/retrieval-benchmark/run_eval.py</c>), not this
///     runtime mechanism — see <c>docs/context-builder-evaluation.md</c>.
///     </para>
///     <para>
///     In-memory per-session "what did the last turn keep, and how permissive was it" state is
///     process-lifetime only (mirrors <see cref="Models.Diagnostics.SessionSignals" />). The
///     threshold adjustment itself, and the outcome audit log, persist to
///     <c>.tokensaver/retrieval-calibration.json</c> / <c>-outcomes.jsonl</c> per workspace.
///     </para>
/// </summary>
public sealed partial class RetrievalCalibrationService
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "RetrievalCalibration: late file '{File}' scored {Score:F2} vs previous bar {Bar:F2} (gap={Gap:F2}, failure={Failure})")]
    private static partial void LogLateFileChecked(ILogger logger, string file, double score, double bar, double gap, bool failure);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "RetrievalCalibration: threshold adjustment {Old:F3} → {New:F3} ({Reason})")]
    private static partial void LogThresholdAdjusted(ILogger logger, double old, double @new, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RetrievalCalibration: embedding call failed for late file '{File}'")]
    private static partial void LogEmbedFailed(ILogger logger, Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RetrievalCalibration: embedding call failed for prompt")]
    private static partial void LogPromptEmbedFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RetrievalCalibration: could not read/write {Path}")]
    private static partial void LogIoFailed(ILogger logger, Exception ex, string path);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly ContextHelperConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<RetrievalCalibrationService> _logger;

    // sessionId -> what the previous BuildConciseContext turn kept for that session.
    private readonly ConcurrentDictionary<string, LastPass> _lastPassBySession = new();

    public RetrievalCalibrationService(HttpClient http, IOptions<ContextHelperConfig> config, ILogger<RetrievalCalibrationService> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    private sealed record LastPass(string Prompt, double MinKeptRelevance, HashSet<string> KeptFiles);

    // ── Effective threshold ─────────────────────────────────────────────────────

    /// <summary>
    ///     <paramref name="baseThreshold" /> (normally <c>ContextHelperConfig.SemanticThreshold</c>)
    ///     plus the persisted workspace adjustment, clamped to a sane range. Falls back to
    ///     <paramref name="baseThreshold" /> unchanged when <paramref name="workspaceRoot" /> is
    ///     empty or no calibration file exists yet.
    /// </summary>
    public double GetEffectiveThreshold(string? workspaceRoot, double baseThreshold)
    {
        RetrievalCalibrationState state = LoadState(workspaceRoot);
        return Math.Clamp(baseThreshold + state.ThresholdAdjustment, 0.05, 0.95);
    }

    // ── Record what a turn kept ──────────────────────────────────────────────────

    /// <summary>
    ///     Called by <see cref="Pipeline.Stages.SemanticRelevanceFilterStage" /> after filtering
    ///     completes, so the next turn in this session has something to compare a late request
    ///     against. No-op without a session id — there is nothing to compare across turns.
    /// </summary>
    public void RecordPass(string? sessionId, string prompt, IReadOnlyCollection<string> keptFiles, double minKeptRelevance)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _lastPassBySession[sessionId] = new LastPass(prompt, minKeptRelevance, new HashSet<string>(keptFiles, StringComparer.OrdinalIgnoreCase));
    }

    // ── Check a new turn's requested files against the previous turn's bar ──────

    /// <summary>
    ///     Diffs <paramref name="requestedFiles" /> against what the previous turn in this session
    ///     kept; for any genuinely new file, embeds it and scores it against
    ///     <paramref name="prompt" /> the same way the filter stage would, then compares that score
    ///     to the previous turn's minimum-kept relevance. Returns <see langword="null" /> when
    ///     there's no session/previous turn to compare against, nothing new was requested, or the
    ///     embedding endpoint is unreachable.
    /// </summary>
    public async Task<RetrievalCalibrationCheckResult?> CheckLateRequestAsync(
        string? sessionId, string? workspaceRoot, string prompt, IReadOnlyCollection<string> requestedFiles, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(workspaceRoot)
            || string.IsNullOrWhiteSpace(_config.EmbeddingModel))
        {
            return null;
        }

        if (!_lastPassBySession.TryGetValue(sessionId, out LastPass? previous))
        {
            return null;
        }

        List<string> lateFiles = requestedFiles
            .Where(f => !previous.KeptFiles.Contains(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (lateFiles.Count == 0)
        {
            return null;
        }

        float[] queryVector;
        try
        {
            queryVector = await EmbeddingUtil.EmbedAsync(_http, _config.EmbeddingModel, prompt, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogPromptEmbedFailed(_logger, ex);
            return null;
        }

        (string File, double? Score)[] scored = await Task.WhenAll(
            lateFiles.Select(async lateFile => (lateFile, await TryScoreFileAsync(workspaceRoot, lateFile, queryVector, ct))));

        List<RetrievalCalibrationOutcome> outcomes = [];
        foreach ((string lateFile, double? score) in scored)
        {
            if (score is null)
            {
                continue;
            }

            double gap = previous.MinKeptRelevance - score.Value;
            bool failure = gap > _config.NarrowFailureMargin;
            LogLateFileChecked(_logger, lateFile, score.Value, previous.MinKeptRelevance, gap, failure);

            outcomes.Add(new RetrievalCalibrationOutcome
            {
                Timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                SessionId = sessionId,
                Prompt = prompt,
                PreviousMinKeptRelevance = previous.MinKeptRelevance,
                LateFile = lateFile,
                LateFileRelevance = score.Value,
                Gap = gap,
                WasFailure = failure
            });
        }

        if (outcomes.Count == 0)
        {
            return null;
        }

        foreach (RetrievalCalibrationOutcome outcome in outcomes)
        {
            AppendOutcome(workspaceRoot, outcome);
        }

        (bool adjusted, double newAdjustment) = ApplyOutcomes(workspaceRoot, outcomes);
        return new RetrievalCalibrationCheckResult(outcomes, adjusted, newAdjustment);
    }

    private async Task<double?> TryScoreFileAsync(string workspaceRoot, string relativeFile, float[] queryVector, CancellationToken ct)
    {
        string abs = Path.Combine(workspaceRoot, relativeFile);
        if (!File.Exists(abs))
        {
            return null;
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(abs, ct);
        }
        catch (IOException)
        {
            return null;
        }

        string capped = content.Length > 2000 ? content[..2000] : content;

        try
        {
            float[] fileVector = await EmbeddingUtil.EmbedAsync(_http, _config.EmbeddingModel, capped, ct);
            return EmbeddingUtil.CosineSimilarity(queryVector, fileVector);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogEmbedFailed(_logger, ex, relativeFile);
            return null;
        }
    }

    // ── Adjustment ────────────────────────────────────────────────────────────

    private (bool Adjusted, double NewAdjustment) ApplyOutcomes(string workspaceRoot, List<RetrievalCalibrationOutcome> outcomes)
    {
        RetrievalCalibrationState state = LoadState(workspaceRoot);
        bool adjusted = false;

        foreach (RetrievalCalibrationOutcome outcome in outcomes)
        {
            state.TotalSamples++;
            if (outcome.WasFailure)
            {
                state.TotalFailures++;
                state.ConsecutiveFailures++;
                state.ConsecutivePasses = 0;

                if (state.ConsecutiveFailures >= _config.CalibrationMinSamplesBeforeAdjust)
                {
                    double old = state.ThresholdAdjustment;
                    state.ThresholdAdjustment = Math.Max(-_config.CalibrationMaxAdjustment,
                        state.ThresholdAdjustment - _config.CalibrationLearningRate);
                    state.ConsecutiveFailures = 0;
                    adjusted = true;
                    LogThresholdAdjusted(_logger, old, state.ThresholdAdjustment, "narrow-filter misses");
                }
            }
            else
            {
                state.ConsecutivePasses++;
                state.ConsecutiveFailures = 0;

                if (state.ConsecutivePasses >= _config.CalibrationMinSamplesBeforeAdjust && state.ThresholdAdjustment < 0)
                {
                    double old = state.ThresholdAdjustment;
                    state.ThresholdAdjustment = Math.Min(0,
                        state.ThresholdAdjustment + (_config.CalibrationLearningRate / 2));
                    state.ConsecutivePasses = 0;
                    adjusted = true;
                    LogThresholdAdjusted(_logger, old, state.ThresholdAdjustment, "recovering toward baseline");
                }
            }
        }

        state.LastUpdated = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        SaveState(workspaceRoot, state);
        return (adjusted, state.ThresholdAdjustment);
    }

    // ── Stats surface ─────────────────────────────────────────────────────────

    public RetrievalCalibrationState GetStats(string? workspaceRoot)
    {
        return LoadState(workspaceRoot);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    // Per-workspace in-memory cache: state is read on every BuildConciseContext call via
    // GetEffectiveThreshold, so re-reading the file each time would put a disk hit on the hot
    // path. SaveState is the only writer and refreshes this cache in the same call.
    private readonly ConcurrentDictionary<string, RetrievalCalibrationState> _stateCache = new();

    private RetrievalCalibrationState LoadState(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return new RetrievalCalibrationState();
        }

        return _stateCache.GetOrAdd(workspaceRoot, LoadStateFromDisk);
    }

    private RetrievalCalibrationState LoadStateFromDisk(string workspaceRoot)
    {
        string path = StatePath(workspaceRoot);
        if (!File.Exists(path))
        {
            return new RetrievalCalibrationState();
        }

        try
        {
            return JsonSerializer.Deserialize<RetrievalCalibrationState>(File.ReadAllText(path), Json)
                   ?? new RetrievalCalibrationState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LogIoFailed(_logger, ex, path);
            return new RetrievalCalibrationState();
        }
    }

    private void SaveState(string workspaceRoot, RetrievalCalibrationState state)
    {
        string path = StatePath(workspaceRoot);
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, Json));
            _stateCache[workspaceRoot] = state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogIoFailed(_logger, ex, path);
        }
    }

    private void AppendOutcome(string workspaceRoot, RetrievalCalibrationOutcome outcome)
    {
        string path = OutcomesPath(workspaceRoot);
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(outcome) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogIoFailed(_logger, ex, path);
        }
    }

    private static string StatePath(string workspaceRoot) => Path.Combine(workspaceRoot, ".tokensaver", "retrieval-calibration.json");

    private static string OutcomesPath(string workspaceRoot) => Path.Combine(workspaceRoot, ".tokensaver", "retrieval-calibration-outcomes.jsonl");
}

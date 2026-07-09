using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TokenSaver.ContextHelper.Pipeline;

/// <summary>
///     Evaluates per-session signals to detect when stripping is too aggressive
///     and produces a session-scoped <see cref="StrippingAdjustment" /> overlay.
///     Never mutates <see cref="ContextHelperConfig" />.
/// </summary>
public partial class StrippingHealthAnalyzer
{
    [LoggerMessage(Level = LogLevel.Information, Message = "StrippingHealth: {Reason}")]
    private static partial void LogStrippingHealthReason(ILogger logger, string reason);

    private readonly ContextHelperConfig _config;
    private readonly ILogger<StrippingHealthAnalyzer> _logger;

    public StrippingHealthAnalyzer(IOptions<ContextHelperConfig> config, ILogger<StrippingHealthAnalyzer> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Analyses signals and returns an adjustment overlay plus a human-readable warning
    ///     if over-stripping is detected (null warning = healthy).
    /// </summary>
    public (StrippingAdjustment Adjustment, string? Warning) Analyze(
        SessionSignals signals, int tokenBudget)
    {
        int? collapseThreshold = null;
        double? semanticThreshold = null;
        int? verbatimTurns = null;
        List<string> reasons = [];

        if (signals.GetCodeBlockCalls > 2)
        {
            collapseThreshold = _config.CodeBlockCollapseThreshold + 20;
            reasons.Add($"GetCodeBlock called {signals.GetCodeBlockCalls}× — raising collapse threshold by 20 lines");
        }

        if (signals.RebuildContextCalls > 1)
        {
            semanticThreshold = Math.Max(0.1, _config.SemanticThreshold - 0.05);
            reasons.Add(
                $"Context rebuilt {signals.RebuildContextCalls}× for same files — lowering semantic threshold by 0.05");
        }

        if (signals.TurnsWithFileRequests > _config.VerbatimTurns)
        {
            verbatimTurns = _config.VerbatimTurns + 2;
            reasons.Add(
                $"Model requested already-seen files in {signals.TurnsWithFileRequests} turns — increasing verbatim window by 2");
        }

        double wasteRatio = tokenBudget > 0 ? signals.EstimatedWastedTokens / tokenBudget : 0;
        string? warning = null;

        if (reasons.Count > 0 || wasteRatio > _config.OverStrippingWasteRatioThreshold)
        {
            string wastedStr = signals.EstimatedWastedTokens > 0
                ? $" (estimated {(int)signals.EstimatedWastedTokens:N0} wasted tokens on re-fetches)"
                : "";
            warning =
                $"ℹ️ Context health: stripping thresholds may be too aggressive this session{wastedStr}. " +
                $"Thresholds relaxed for this build.";

            foreach (string r in reasons)
            {
                LogStrippingHealthReason(_logger, r);
            }
        }

        return (new StrippingAdjustment(collapseThreshold, semanticThreshold, verbatimTurns), warning);
    }

    /// <summary>
    ///     Records a GetCodeBlock call against a session's signals.
    /// </summary>
    public static void RecordCodeBlockRequest(SessionSignals signals)
    {
        signals.GetCodeBlockCalls++;
        signals.LastUpdated = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Records that context was rebuilt for an already-seen set of files.
    /// </summary>
    public static void RecordContextRebuild(SessionSignals signals, int wastedTokens = 0)
    {
        signals.RebuildContextCalls++;
        signals.EstimatedWastedTokens += wastedTokens;
        signals.LastUpdated = DateTimeOffset.UtcNow;
    }
}

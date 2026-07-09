using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Models.Diagnostics;
using TokenSaver.ContextHelper.Pipeline.Stages;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TokenSaver.ContextHelper.Pipeline;

/// <summary>
///     Orchestrates the full context hygiene pipeline. Entry point for all callers.
///     Runtime stage order: 1→2→3→4→5→8→6→7→9→10
/// </summary>
public partial class ContextCleanerService(
    PromptAssessor assessor,
    StrippingHealthAnalyzer healthAnalyzer,
    IOptions<ContextHelperConfig> config,
    ILogger<ContextCleanerService> logger,
    DeduplicationStage stage01,
    LogStrippingStage stage02,
    CodeBlockCollapsingStage stage03,
    ConversationSummarizationStage stage04,
    ToolCallCompressionStage stage05,
    SemanticRelevanceFilterStage stage06,
    TokenBudgetStage stage07,
    ChunkingStage stage08,
    ConversationalGarbageStage stage09,
    FormattingNormalizationStage stage10)
{
    [LoggerMessage(Level = LogLevel.Information, Message = "PromptAssessor: TaskType={Type} Confidence={Conf:P0} KeyFiles={Files}")]
    private static partial void LogPromptAssessment(ILogger logger, TaskType type, double conf, string files);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pipeline: starting stage {Stage}")]
    private static partial void LogPipelineStageStarting(ILogger logger, string stage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline complete: {Raw}→{Final} tokens ({Pct}% saved) in {Ms}ms")]
    private static partial void LogPipelineComplete(ILogger logger, int raw, int final, double pct, long ms);

    private readonly ContextHelperConfig _config = config.Value;

    // Per-session signal tracking (session-id → signals)
    private readonly ConcurrentDictionary<string, SessionSignals> _sessionSignals = new();

    // Stages (injected in conceptual order; orchestrator applies them in runtime order)

    // Last pipeline summary — exposed for DescribeContext
    private volatile PipelineSummary? _lastSummary;

    public PipelineSummary? LastSummary => _lastSummary;

    /// <summary>
    ///     Run the full pipeline and return the cleaned context text plus diagnostics.
    /// </summary>
    public async Task<PipelineResult> CleanContextAsync(
        string prompt,
        string[]? history = null,
        string? sessionId = null,
        int? tokenBudget = null,
        StrippingAdjustment? adj = null,
        bool avoidChainOfThought = false,
        CancellationToken ct = default)
    {
        Stopwatch totalSw = Stopwatch.StartNew();

        // ── 0. Prompt assessment ──────────────────────────────────────────────
        PromptAssessment assessment = await assessor.AssessAsync(prompt, history, ct);
        if (logger.IsEnabled(LogLevel.Information))
        {
            // CA1873 false positive: string.Join here only runs inside the IsEnabled(Information)
            // guard above, so it's never evaluated when logging is disabled — the analyzer
            // doesn't correlate the guard's level with the LoggerMessage's configured level.
#pragma warning disable CA1873
            LogPromptAssessment(logger, assessment.TaskType, assessment.Confidence, string.Join(",", assessment.KeyFiles));
#pragma warning restore CA1873
        }

        // ── 0b. Over-stripping check ──────────────────────────────────────────
        SessionSignals signals = sessionId != null
            ? _sessionSignals.GetOrAdd(sessionId, _ => new SessionSignals())
            : new SessionSignals();

        int budget = tokenBudget ?? _config.ContextSizeLimit;
        (StrippingAdjustment? healthAdj, string? overStrippingWarning) = healthAnalyzer.Analyze(signals, budget);

        // Merge caller-supplied adjustment on top of health adjustment
        StrippingAdjustment effectiveAdj = MergeAdjustments(healthAdj, adj);

        // ── Build context object ──────────────────────────────────────────────
        // Strip any previously-generated BuildConciseContext blocks from history before
        // running the pipeline. Identified by the <!-- context: marker emitted on every
        // response. This applies for all calling AIs — fresh concise context replaces stale.
        string[]? cleanedHistory = history?
            .Where(h => !h.Contains("<!-- context:", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        PipelineContext ctx = new()
        {
            Query = prompt,
            History = [.. cleanedHistory ?? []],
            TokenBudget = ApplyAdjustment(budget, effectiveAdj),
            SessionId = sessionId,
            Assessment = assessment,
            AvoidChainOfThought = avoidChainOfThought
        };

        int rawTokens = ctx.EstimateTokens();

        // ── Pre-strip verbatim analysis ───────────────────────────────────────
        // (nothing to mark yet — chunks don't exist; handled per-stage)

        // ── Runtime pipeline order: 1→2→3→4→5→8→6→7→9→10 ───────────────────
        IPipelineStage[] stages =
        [
            stage01, stage02, stage03, stage04, stage05,
            stage08, stage06, stage07,
            stage09, stage10
        ];

        foreach (IPipelineStage stage in stages)
        {
            LogPipelineStageStarting(logger, stage.Name);
            ctx = await stage.ProcessAsync(ctx, ct);
        }

        // ── Assemble final text ───────────────────────────────────────────────
        string contextText = AssembleContext(ctx);
        int finalTokens = PipelineContext.EstimateTokens(contextText);

        // ── Build pipeline summary ────────────────────────────────────────────
        PipelineSummary summary = BuildSummary(ctx, rawTokens, finalTokens, totalSw.Elapsed);
        _lastSummary = summary;

        LogPipelineComplete(logger, rawTokens, finalTokens, summary.TotalSavingsPercent, totalSw.ElapsedMilliseconds);

        // ── Update session signals ────────────────────────────────────────────
        if (sessionId != null)
        {
            signals.RebuildContextCalls++;
            signals.LastUpdated = DateTimeOffset.UtcNow;
        }

        return new PipelineResult
        {
            ContextText = contextText,
            TokenCount = finalTokens,
            Alert = finalTokens > budget,
            AlertMessage = finalTokens > budget
                ? $"Context {finalTokens} tokens exceeds limit of {budget}."
                : null,
            SpaghettiWarning = ctx.SpaghettiReport,
            OverStrippingWarning = overStrippingWarning,
            Summary = summary
        };
    }

    /// <summary>
    ///     Lightweight entry point that returns only the cleaned prompt string.
    ///     Used by <c>CleanAndChat</c> where full diagnostics aren't needed by the caller.
    /// </summary>
    public async Task<string> CleanContextTextAsync(
        string prompt,
        string[]? history = null,
        string? sessionId = null,
        int? tokenBudget = null,
        bool avoidChainOfThought = false,
        CancellationToken ct = default)
    {
        PipelineResult result = await CleanContextAsync(
            prompt, history, sessionId, tokenBudget,
            avoidChainOfThought: avoidChainOfThought, ct: ct);
        return result.ContextText;
    }

    public SessionSignals GetOrCreateSignals(string sessionId)
    {
        return _sessionSignals.GetOrAdd(sessionId, _ => new SessionSignals());
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string AssembleContext(PipelineContext ctx)
    {
        StringBuilder sb = new();

        // Reassemble: history → chunks → query
        // Chunks are already filtered and ordered; stitch with minimal separators
        foreach (ContextChunk? chunk in ctx.Chunks.Where(c => !c.WasDropped && c.Source != "conversation:query"))
        {
            if (sb.Length > 0)
            {
                _ = sb.AppendLine();
            }

            _ = sb.AppendLine(chunk.Content);
        }

        if (sb.Length > 0)
        {
            _ = sb.AppendLine();
        }

        _ = sb.Append(ctx.Query);

        return sb.ToString().Trim();
    }

    private static PipelineSummary BuildSummary(
        PipelineContext ctx, int rawTokens, int finalTokens, TimeSpan elapsed)
    {
        StageDiagnostic[] stageArray = [.. ctx.StageDiagnostics];

        // Per-kind token accounting from manifest
        Dictionary<ContentKind, (int Total, int Dropped)> byKind = ctx.Manifest.Entries
            .GroupBy(e => e.Kind)
            .ToDictionary(g => g.Key, g => (
                Total: g.Sum(e => e.TokenCount),
                Dropped: g.Where(e => e.WasDropped).Sum(e => e.TokenCount)
            ));

        string narrative = BuildNarrative(ctx, rawTokens, finalTokens, elapsed, byKind);

        return new PipelineSummary
        {
            TotalInputTokens = rawTokens,
            TotalOutputTokens = finalTokens,
            TotalElapsed = elapsed,
            Stages = stageArray,
            NarrativeSummary = narrative,
            Manifest = ctx.Manifest,
            TaskType = ctx.Assessment?.TaskType ?? TaskType.General
        };
    }

    private static string BuildNarrative(
        PipelineContext ctx, int rawTokens, int finalTokens, TimeSpan elapsed,
        Dictionary<ContentKind, (int Total, int Dropped)> byKind)
    {
        int saved = rawTokens - finalTokens;
        double savePct = rawTokens == 0 ? 0 : Math.Round((double)saved / rawTokens * 100, 1);

        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Context built in {elapsed.TotalMilliseconds:N0}ms | {rawTokens:N0} → {finalTokens:N0} tokens  saved {savePct}% ({saved:N0} tokens)");

        // Conversation
        StageDiagnostic? diagSummarize = ctx.StageDiagnostics.FirstOrDefault(d => d.StageName == "ConversationSummarization");
        int summarized = diagSummarize?.ItemsCollapsed ?? 0;
        int verbatim = Math.Max(0, ctx.History.Count - summarized);
        int convTokensIn = diagSummarize?.InputTokens ?? 0;
        int convTokensOut = diagSummarize?.OutputTokens ?? convTokensIn;
        double convSavePct = convTokensIn == 0
            ? 0
            : Math.Round((double)(convTokensIn - convTokensOut) / convTokensIn * 100, 1);
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Conversation : {ctx.History.Count} turns — {summarized} summarized, {verbatim} verbatim  saved {convSavePct}%");

        // Code
        StageDiagnostic? diagCode = ctx.StageDiagnostics.FirstOrDefault(d => d.StageName == "CodeBlockCollapsing");
        int collapsed = diagCode?.ItemsCollapsed ?? 0;
        int inline = ctx.Chunks.Count(c => c.Kind == ContentKind.Code && !c.WasCollapsed);
        int codeIn = diagCode?.InputTokens ?? 0;
        int codeOut = diagCode?.OutputTokens ?? codeIn;
        double codeSavePct = codeIn == 0 ? 0 : Math.Round((double)(codeIn - codeOut) / codeIn * 100, 1);
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Code         : {collapsed + inline} blocks — {collapsed} collapsed, {inline} inline  saved {codeSavePct}%");

        // Files
        List<ManifestEntry> fileChunks = [.. ctx.Manifest.Entries.Where(e => e.Kind == ContentKind.File)];
        if (fileChunks.Count > 0)
        {
            IEnumerable<string> fileNames = fileChunks.Select(e => Path.GetFileName(e.Source)).Distinct().Take(5);
            int fileIn = byKind.GetValueOrDefault(ContentKind.File).Total;
            int fileDropped = byKind.GetValueOrDefault(ContentKind.File).Dropped;
            double fileSavePct = fileIn == 0 ? 0 : Math.Round((double)fileDropped / fileIn * 100, 1);
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Files        : {string.Join(", ", fileNames)}  saved {fileSavePct}%");
        }

        // Logs
        StageDiagnostic? diagLogs = ctx.StageDiagnostics.FirstOrDefault(d => d.StageName == "LogStripping");
        if (diagLogs?.ItemsDropped > 0)
        {
            double logSavePct = diagLogs.InputTokens == 0
                ? 0
                : Math.Round((double)diagLogs.TokensSaved / diagLogs.InputTokens * 100, 1);
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Logs         : {diagLogs.ItemsDropped:N0} lines stripped, failures + tail preserved  saved {logSavePct}%");
        }

        // Tool calls
        StageDiagnostic? diagTools = ctx.StageDiagnostics.FirstOrDefault(d => d.StageName == "ToolCallCompression");
        if (diagTools?.ItemsCollapsed > 0)
        {
            double toolSavePct = diagTools.InputTokens == 0
                ? 0
                : Math.Round((double)diagTools.TokensSaved / diagTools.InputTokens * 100, 1);
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Tool calls   : {diagTools.ItemsCollapsed} compressed  saved {toolSavePct}%");
        }

        // Dropped (semantic filter)
        StageDiagnostic? diagSemantic = ctx.StageDiagnostics.FirstOrDefault(d => d.StageName == "SemanticRelevanceFilter");
        if (diagSemantic?.ItemsDropped > 0)
        {
            IEnumerable<string> droppedEntries = ctx.Manifest.Entries
                .Where(e => e.WasDropped && e.DroppedReason?.StartsWith("Relevance", StringComparison.Ordinal) == true)
                .OrderBy(e => e.Relevance)
                .Take(4)
                .Select(e => $"{e.Relevance:F2}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture,
                $"  Dropped (low relevance): {diagSemantic.ItemsDropped} chunks (scores: {string.Join(", ", droppedEntries)})");
        }

        return sb.ToString().TrimEnd();
    }

    private static int ApplyAdjustment(int budget, StrippingAdjustment? adj)
    {
        return budget;
    }

    private static StrippingAdjustment MergeAdjustments(
        StrippingAdjustment health, StrippingAdjustment? caller)
    {
        return new StrippingAdjustment(
            caller?.CodeBlockCollapseThreshold ?? health.CodeBlockCollapseThreshold,
            caller?.SemanticThreshold ?? health.SemanticThreshold,
            caller?.VerbatimTurns ?? health.VerbatimTurns
        );
    }
}

using TokenSaver.ContextHelper.Calibration;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Embedding;
using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Models.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Diagnostics;
using System.Net.Http.Json;

namespace TokenSaver.ContextHelper.Pipeline.Stages;

/// <summary>
///     Stage 6 — Semantic Relevance Filtering (runs after Stage 8 in actual pipeline order).
///     Embeds each chunk and the current query, then drops chunks below the similarity threshold.
///     Key files from <see cref="PromptAssessment" /> always get a minimum floor of 0.5.
///     Degrades gracefully if the embedding endpoint is unavailable.
/// </summary>
public partial class SemanticRelevanceFilterStage : IPipelineStage
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{Stage}: embedding model '{Model}' unavailable (404) — semantic filter disabled for this session. " +
                   "Pull the model with: ollama pull {Model2}")]
    private static partial void LogEmbeddingModelUnavailable(ILogger logger, string stage, string model, string model2);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage}: embedding error detail")]
    private static partial void LogEmbeddingErrorDetail(ILogger logger, Exception ex, string stage);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "{Stage}: dropped chunk {Id} from {Source} (score={Score:F2}, threshold={Threshold:F2})")]
    private static partial void LogChunkDropped(ILogger logger, string stage, string id, string source, double score, double threshold);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage}: {Kept} kept, {Dropped} dropped, saved {Saved} tokens")]
    private static partial void LogFilterSummary(ILogger logger, string stage, int kept, int dropped, int saved);

    private readonly ContextHelperConfig _config;

    private readonly HttpClient _http;
    private readonly ILogger<SemanticRelevanceFilterStage> _logger;
    private readonly RetrievalCalibrationService _calibration;

    // null = not yet probed; true = available; false = permanently unavailable this session
    private bool? _embeddingAvailable;

    public SemanticRelevanceFilterStage(HttpClient http, IOptions<ContextHelperConfig> config,
        ILogger<SemanticRelevanceFilterStage> logger, RetrievalCalibrationService calibration)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
        _calibration = calibration;
    }

    public string Name => "SemanticRelevanceFilter";

    public async Task<PipelineContext> ProcessAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StageDiagnostic diag = new() { StageName = Name, InputTokens = ctx.EstimateTokens() };

        // Fast-skip: embedding model not configured or already known unavailable
        if (string.IsNullOrWhiteSpace(_config.EmbeddingModel) || _embeddingAvailable == false)
        {
            string reason = string.IsNullOrWhiteSpace(_config.EmbeddingModel)
                ? "EmbeddingModel not configured"
                : $"'{_config.EmbeddingModel}' unavailable (pull it to enable semantic filtering)";
            diag.OutputTokens = ctx.EstimateTokens();
            diag.Elapsed = sw.Elapsed;
            diag.Notes = [$"Skipped ({reason})"];
            ctx.StageDiagnostics.Add(diag);
            return ctx;
        }

        // Try to embed the query
        float[]? queryEmbedding = null;
        try
        {
            queryEmbedding = await EmbedAsync(ctx.Query, ct);
            _embeddingAvailable = true;
        }
        catch (Exception ex)
        {
            _embeddingAvailable = false;
            LogEmbeddingModelUnavailable(_logger, Name, _config.EmbeddingModel, _config.EmbeddingModel);
            LogEmbeddingErrorDetail(_logger, ex, Name);
        }

        if (queryEmbedding == null)
        {
            diag.OutputTokens = ctx.EstimateTokens();
            diag.Elapsed = sw.Elapsed;
            diag.Notes = ["Skipped (embedding endpoint unavailable)"];
            ctx.StageDiagnostics.Add(diag);
            return ctx;
        }

        // Compute per-kind thresholds from weight profile, adjusted by any live self-tuning
        // this workspace has accumulated (see RetrievalCalibrationService).
        double baseThreshold = _calibration.GetEffectiveThreshold(ctx.WorkspaceRoot, _config.SemanticThreshold);
        HashSet<string> keyFilePaths = new(ctx.Assessment.KeyFiles, StringComparer.OrdinalIgnoreCase);

        // Embed all chunks in parallel, throttled by semaphore
        IEnumerable<Task> embedTasks = ctx.Chunks.Select(async chunk =>
        {
            await PipelineParallelism.Semaphore.WaitAsync(ct);
            try
            {
                chunk.Embedding = await EmbedAsync(chunk.Content, ct);
            }
            catch
            {
                chunk.Embedding = null;
            }
            finally
            {
                _ = PipelineParallelism.Semaphore.Release();
            }
        });
        await Task.WhenAll(embedTasks);

        // Filter chunks
        int dropped = 0;
        List<ContextChunk> kept = [];
        HashSet<string> keptFiles = new(StringComparer.OrdinalIgnoreCase);
        double minKeptRelevance = 1.0;

        foreach (ContextChunk chunk in ctx.Chunks)
        {
            // Determine effective threshold for this chunk's kind
            double weight = GetWeight(ctx.Assessment.WeightProfile, chunk.Kind);
            double threshold = baseThreshold * (2.0 - weight); // high weight → lower threshold

            // Key files get a floor of 0.5
            bool isKeyFile = keyFilePaths.Any(kf =>
                chunk.Source.Contains(kf, StringComparison.OrdinalIgnoreCase));
            if (isKeyFile)
            {
                threshold = Math.Min(threshold, 0.5);
            }

            double similarity = 1.0;
            if (chunk.Embedding != null)
            {
                similarity = EmbeddingUtil.CosineSimilarity(queryEmbedding, chunk.Embedding);
            }

            chunk.Relevance = similarity;

            // The live query and explicitly pre-strip-preserved/requested-file chunks are always
            // kept — an explicit ask should always be honored — but similarity is still computed
            // and recorded above for diagnostics/calibration signal rather than skipped outright.
            bool alwaysKeep = chunk.Source == "conversation:query" || chunk.PreserveVerbatim;

            if (alwaysKeep || similarity >= threshold)
            {
                kept.Add(chunk);
                keptFiles.Add(chunk.Source);
                minKeptRelevance = Math.Min(minKeptRelevance, similarity);
            }
            else
            {
                chunk.WasDropped = true;
                dropped++;
                LogChunkDropped(_logger, Name, chunk.ChunkId, chunk.Source, similarity, threshold);

                // Update manifest entry
                ManifestEntry? entry = ctx.Manifest.Entries.FirstOrDefault(e => e.ChunkId == chunk.ChunkId);
                if (entry != null)
                {
                    entry.WasDropped = true;
                    entry.DroppedReason = $"Relevance {similarity:F2} < threshold {threshold:F2}";
                    entry.Relevance = similarity;
                }
            }
        }

        ctx.Chunks = kept;

        if (keptFiles.Count > 0)
        {
            _calibration.RecordPass(ctx.SessionId, ctx.Query, keptFiles, minKeptRelevance);
        }

        diag.OutputTokens = ctx.EstimateTokens();
        diag.ItemsDropped = dropped;
        diag.Elapsed = sw.Elapsed;
        if (dropped > 0)
        {
            diag.Notes = [$"{dropped} chunks dropped (low relevance)"];
        }

        ctx.StageDiagnostics.Add(diag);

        LogFilterSummary(_logger, Name, kept.Count, dropped, diag.TokensSaved);
        return ctx;
    }

    private Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        EmbeddingUtil.EmbedAsync(_http, _config.EmbeddingModel, text, ct);

    private static double GetWeight(WeightProfile profile, ContentKind kind)
    {
        return kind switch
        {
            ContentKind.Code => profile.CodeWeight,
            ContentKind.Log => profile.LogWeight,
            ContentKind.File => profile.FileWeight,
            ContentKind.Conversation => profile.ConversationWeight,
            ContentKind.ToolCall => profile.ToolCallWeight,
            _ => 0.6
        };
    }

}

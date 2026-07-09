using TokenSaver.ContextHelper.Configuration;
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

    // null = not yet probed; true = available; false = permanently unavailable this session
    private bool? _embeddingAvailable;

    public SemanticRelevanceFilterStage(HttpClient http, IOptions<ContextHelperConfig> config,
        ILogger<SemanticRelevanceFilterStage> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
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

        // Compute per-kind thresholds from weight profile
        double baseThreshold = _config.SemanticThreshold;
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

        foreach (ContextChunk chunk in ctx.Chunks)
        {
            // Always keep the query itself
            if (chunk.Source == "conversation:query")
            {
                kept.Add(chunk);
                continue;
            }

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

            // Pre-strip verbatim override
            if (chunk.PreserveVerbatim)
            {
                kept.Add(chunk);
                continue;
            }

            double similarity = 1.0;
            if (chunk.Embedding != null)
            {
                similarity = CosineSimilarity(queryEmbedding, chunk.Embedding);
            }

            chunk.Relevance = similarity;

            if (similarity >= threshold)
            {
                kept.Add(chunk);
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

    private async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var payload = new { model = _config.EmbeddingModel, input = text };
        HttpResponseMessage response = await _http.PostAsJsonAsync("/api/embed", payload, ct);
        _ = response.EnsureSuccessStatusCode();
        EmbedResponse? result = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct);
        return result?.Embeddings?.FirstOrDefault() ?? [];
    }

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

    private static double CosineSimilarity(float[] a, IReadOnlyList<float> b)
    {
        if (a.Length != b.Count || a.Length == 0)
        {
            return 0.0;
        }

        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        double denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom == 0 ? 0 : dot / denom;
    }

    private sealed record EmbedResponse(float[][]? Embeddings);
}

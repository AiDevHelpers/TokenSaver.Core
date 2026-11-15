using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models;
using TokenSaver.ContextHelper.Models.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Diagnostics;
using System.Net.Http.Json;

namespace TokenSaver.ContextHelper.Pipeline.Stages;

/// <summary>
///     Stage 7 — Token Budget Enforcement.
///     Trims context to fit within the configured budget (with safety margin).
///     Drop order: lowest-relevance chunks first, then summarize oldest non-verbatim chunks.
/// </summary>
public partial class TokenBudgetStage : IPipelineStage
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Stage}: still over budget after trimming; issuing alert")]
    private static partial void LogStillOverBudget(ILogger logger, string stage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage}: budget={Budget}, final={Final}, saved {Saved} tokens")]
    private static partial void LogBudgetSummary(ILogger logger, string stage, int budget, int final, int saved);

    private readonly ContextHelperConfig _config;

    private readonly HttpClient _http;
    private readonly ILogger<TokenBudgetStage> _logger;

    public TokenBudgetStage(HttpClient http, IOptions<ContextHelperConfig> config, ILogger<TokenBudgetStage> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    public string Name => "TokenBudget";

    public async Task<PipelineContext> ProcessAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StageDiagnostic diag = new() { StageName = Name, InputTokens = ctx.EstimateTokens() };

        int effectiveBudget = (int)(ctx.TokenBudget * _config.TokenBudgetMargin);
        List<string> notes = [];
        int dropped = 0;

        // Phase 1: drop lowest-relevance non-pinned chunks
        while (ctx.EstimateTokens() > effectiveBudget && ctx.Chunks.Count > 1)
        {
            ContextChunk? candidate = ctx.Chunks
                .Where(c => !c.PreserveVerbatim && c.Source != "conversation:query")
                .OrderBy(c => c.Relevance)
                .FirstOrDefault();

            if (candidate == null)
            {
                break;
            }

            _ = ctx.Chunks.Remove(candidate);
            candidate.WasDropped = true;
            dropped++;

            ManifestEntry? entry = ctx.Manifest.Entries.FirstOrDefault(e => e.ChunkId == candidate.ChunkId);
            if (entry != null)
            {
                entry.WasDropped = true;
                entry.DroppedReason = "Token budget — low relevance";
            }
        }

        if (dropped > 0)
        {
            notes.Add($"{dropped} low-relevance chunks dropped for budget");
        }

        // Phase 2: if still over budget, attempt to summarize oldest conversation chunks
        if (ctx.EstimateTokens() > effectiveBudget)
        {
            List<ContextChunk> oldest = [.. ctx.Chunks
                .Where(c => c.Kind == ContentKind.Conversation && !c.WasSummarized && c.Source != "conversation:query")
                .OrderBy(c => c.SectionName)
                .Take(3)];

            foreach (ContextChunk chunk in oldest)
            {
                string summary = await SummarizeAsync(chunk.Content, ct);
                if (summary.Length < chunk.Content.Length)
                {
                    chunk.Content = summary;
                    chunk.TokenCount = PipelineContext.EstimateTokens(summary);
                    chunk.WasSummarized = true;
                }
            }

            if (oldest.Count > 0)
            {
                notes.Add($"{oldest.Count} conversation chunks summarized for budget");
            }
        }

        // Phase 3: hard stop with alert
        if (ctx.EstimateTokens() > effectiveBudget)
        {
            LogStillOverBudget(_logger, Name);
            notes.Add("Hard limit reached — context truncated");
        }

        diag.OutputTokens = ctx.EstimateTokens();
        diag.ItemsDropped = dropped;
        diag.Elapsed = sw.Elapsed;
        diag.Notes = [.. notes];
        ctx.StageDiagnostics.Add(diag);

        LogBudgetSummary(_logger, Name, effectiveBudget, diag.OutputTokens, diag.TokensSaved);
        return ctx;
    }

    private async Task<string> SummarizeAsync(string text, CancellationToken ct)
    {
        try
        {
            string prompt = $"Summarize concisely, preserving all technical details:\n\n{text}";
            var payload = new { model = "HelperModel", prompt, stream = false };
            HttpResponseMessage response = await _http.PostAsJsonAsync("/api/generate", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                return text;
            }

            GenerateResponse? result = await response.Content.ReadFromJsonAsync<GenerateResponse>(ct);
            return result?.Response?.Trim() ?? text;
        }
        catch
        {
            return text;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record GenerateResponse(string? Response);
}

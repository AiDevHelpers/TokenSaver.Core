using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace TokenSaver.ContextHelper.Pipeline.Stages;

/// <summary>
///     Stage 4 — Conversation Summarization.
///     Keeps the last N turns verbatim; summarizes older turns via the local model.
///     Preserves decisions, constraints, file names, and errors in the summary.
/// </summary>
public partial class ConversationSummarizationStage(
    HttpClient http,
    IOptions<ContextHelperConfig> config,
    ILogger<ConversationSummarizationStage> logger)
    : IPipelineStage
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage}: summarized {N} turns, saved {Saved} tokens")]
    private static partial void LogSummarizationSummary(ILogger logger, string stage, int n, int saved);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ConversationSummarization model call failed; keeping original text")]
    private static partial void LogSummarizationModelCallFailed(ILogger logger, Exception ex);

    // Garbage patterns to strip before summarizing to reduce token cost
    [GeneratedRegex(@"(?:Sure,?\s*|Of course[,!]?\s*|Certainly[,!]?\s*|Absolutely[,!]?\s*|Great[,!]?\s*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex GreetingFillerPattern();

    [GeneratedRegex(@"Let me know if you need\s+\w+.*?\.", RegexOptions.IgnoreCase)]
    private static partial Regex LetMeKnowPattern();

    [GeneratedRegex(@"As an AI\b.*?\.", RegexOptions.IgnoreCase)]
    private static partial Regex AsAnAiPattern();

    [GeneratedRegex(@"I apologize\b.*?\.", RegexOptions.IgnoreCase)]
    private static partial Regex ApologyPattern();

    [GeneratedRegex(@"Hope that helps.*?\.", RegexOptions.IgnoreCase)]
    private static partial Regex HopeThatHelpsPattern();

    private static readonly Func<Regex>[] GarbagePatterns =
    [
        GreetingFillerPattern,
        LetMeKnowPattern,
        AsAnAiPattern,
        ApologyPattern,
        HopeThatHelpsPattern
    ];

    private readonly ContextHelperConfig _config = config.Value;

    public string Name => "ConversationSummarization";

    public async Task<PipelineContext> ProcessAsync(PipelineContext ctx, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StageDiagnostic diag = new() { StageName = Name, InputTokens = ctx.EstimateTokens() };

        // Weight adjustment: high conversation weight → keep more turns verbatim
        int verbatimTurns = _config.VerbatimTurns;
        if (ctx.Assessment.WeightProfile.ConversationWeight >= 0.8)
        {
            verbatimTurns += 2;
        }

        IReadOnlyList<string> history = ctx.History;
        if (history.Count <= verbatimTurns)
        {
            // Nothing to summarize
            diag.OutputTokens = ctx.EstimateTokens();
            diag.Elapsed = sw.Elapsed;
            ctx.StageDiagnostics.Add(diag);
            return ctx;
        }

        List<string> recent = [.. history.TakeLast(verbatimTurns)];
        List<string> older = [.. history.Take(history.Count - verbatimTurns)];

        // Strip garbage from older turns before summarizing to reduce model call cost
        string[] cleaned = older.Select(PreClean).ToArray();
        string combined = string.Join("\n\n", cleaned);

        string summary = await SummarizeAsync(combined, ctx.Assessment.Constraints, ctx.AvoidChainOfThought, ct);

        ctx.History = [$"<summary turns={older.Count}>\n{summary}\n</summary>", .. recent];

        diag.OutputTokens = ctx.EstimateTokens();
        diag.ItemsCollapsed = older.Count;
        diag.Elapsed = sw.Elapsed;
        diag.Notes = [$"{older.Count} older turns summarized, {verbatimTurns} kept verbatim"];
        ctx.StageDiagnostics.Add(diag);

        LogSummarizationSummary(logger, Name, older.Count, diag.TokensSaved);
        return ctx;
    }

    private static string PreClean(string text)
    {
        foreach (Func<Regex> p in GarbagePatterns)
        {
            text = p().Replace(text, "");
        }

        return text.Trim();
    }

    private async Task<string> SummarizeAsync(
        string text, IReadOnlyList<string> constraints, bool avoidChainOfThought, CancellationToken ct)
    {
        string constraintNote = constraints.Count > 0
            ? $"\nPreserve these constraints exactly: {string.Join("; ", constraints)}"
            : "";

        // Reasoning models (DeepSeek R1, Qwen3-thinking) do internal CoT deliberation.
        // A step-by-step prompt competes with their built-in reasoning and can cause
        // verbose or looping outputs.  Use a direct extraction prompt instead.
        string prompt = avoidChainOfThought
            ? $"""
               Extract a concise technical summary from the conversation below.
               Output only: file names, decisions, constraints, instructions, errors, code.
               No preamble, no explanation, no meta-commentary.{constraintNote}

               Conversation:
               {text}
               """
            : $"""
               Summarize the following conversation into a concise technical summary.
               Preserve: file names, decisions, stated constraints, explicit instructions, error messages, and code artifacts.
               Strip: pleasantries, hedging, meta-commentary about the AI.{constraintNote}

               Conversation:
               {text}
               """;

        try
        {
            var payload = new { model = "HelperModel", prompt, stream = false };
            HttpResponseMessage response = await http.PostAsJsonAsync("/api/generate", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                return text; // fallback: keep original
            }

            GenerateResponse? result = await response.Content.ReadFromJsonAsync<GenerateResponse>(ct);
            return result?.Response?.Trim() ?? text;
        }
        catch (Exception ex)
        {
            LogSummarizationModelCallFailed(logger, ex);
            return text;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record GenerateResponse(string? Response);
}

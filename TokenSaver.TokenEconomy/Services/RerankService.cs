using TokenSaver.TokenEconomy.Contracts;

using System.Text;
using System.Text.Json;

namespace TokenSaver.TokenEconomy.Services;

/// <summary>
///     Default <see cref="IRerankService" /> — asks the "rerank" model route (see
///     <c>TokenEconomyConfig.ModelRoutes</c>) to judge relevance directly via a chat prompt,
///     rather than running a dedicated cross-encoder model. This is deliberately a different
///     approach from the candidate Qwen3-Reranker-0.6B pair benchmarked in
///     <c>docs/retrieval-benchmark/run_benchmark.py</c>: that model needs its own inference
///     runtime (llama.cpp/transformers) with no existing integration point in this codebase, while
///     TokenSaver only ever reaches models through Ollama/DraftModel's HTTP API (see CLAUDE.md's
///     "External dependency, no build coupling" note). Judged scoring is lower-fidelity than a
///     true cross-encoder but requires no new inference infrastructure. Degrades to <c>null</c> on
///     any call/parse failure so callers can fall back to embed-only ranking, matching the
///     graceful-degradation pattern <c>SemanticRelevanceFilterStage</c> uses for embedding calls.
/// </summary>
public sealed class RerankService(OllamaService ollama, ModelRouteResolver modelRoutes) : IRerankService
{
    public async Task<IReadOnlyList<double>?> ScoreAsync(
        string query, IReadOnlyList<string> candidateSummaries, CancellationToken ct = default)
    {
        if (candidateSummaries.Count == 0)
        {
            return [];
        }

        string prompt = BuildPrompt(query, candidateSummaries);
        string model = modelRoutes.Resolve("rerank");

        string response;
        try
        {
            response = await ollama.CallModelAsync(model, prompt, systemPrompt: null, ct: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }

        return ParseScores(response, candidateSummaries.Count);
    }

    private static string BuildPrompt(string query, IReadOnlyList<string> candidateSummaries)
    {
        StringBuilder sb = new();
        sb.AppendLine(
            "Score how relevant each numbered candidate is to the query, on a 0.0-1.0 scale " +
            "(1.0 = directly answers the query, 0.0 = unrelated). Reply with ONLY a JSON array of " +
            "numbers, one per candidate, in the same order as listed — no other text.");
        sb.AppendLine();
        sb.Append("QUERY: ").AppendLine(query);
        sb.AppendLine();
        for (int i = 0; i < candidateSummaries.Count; i++)
        {
            sb.Append(i + 1).Append(". ").AppendLine(candidateSummaries[i]);
        }

        return sb.ToString();
    }

    private static IReadOnlyList<double>? ParseScores(string response, int expectedCount)
    {
        string trimmed = response.Trim();
        int start = trimmed.IndexOf('[');
        int end = trimmed.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            double[]? scores = JsonSerializer.Deserialize<double[]>(trimmed[start..(end + 1)]);
            if (scores is null || scores.Length != expectedCount)
            {
                return null;
            }

            return Array.ConvertAll(scores, s => Math.Clamp(s, 0.0, 1.0));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

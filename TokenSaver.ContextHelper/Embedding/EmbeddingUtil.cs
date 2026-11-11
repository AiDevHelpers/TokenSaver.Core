using System.Net.Http.Json;

namespace TokenSaver.ContextHelper.Embedding;

/// <summary>
///     Shared Ollama <c>/api/embed</c> call + cosine similarity math used by both
///     <see cref="Pipeline.Stages.SemanticRelevanceFilterStage" /> and
///     <see cref="Calibration.RetrievalCalibrationService" /> so the two score text the same way.
/// </summary>
internal static class EmbeddingUtil
{
    public static async Task<float[]> EmbedAsync(HttpClient http, string model, string text, CancellationToken ct)
    {
        var payload = new { model, input = text };
        HttpResponseMessage response = await http.PostAsJsonAsync("/api/embed", payload, ct);
        _ = response.EnsureSuccessStatusCode();
        EmbedResponse? result = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct);
        return result?.Embeddings?.FirstOrDefault() ?? [];
    }

    public static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count == 0 || b.Count == 0 || a.Count != b.Count)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        double denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record EmbedResponse(float[][]? Embeddings);
}

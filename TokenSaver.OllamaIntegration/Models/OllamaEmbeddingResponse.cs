using System.Text.Json.Serialization;

namespace AiDevTools.TokenSaver.OllamaIntegration.Models.Ollama;

/// <summary>
///     Response shape for Ollama's <c>POST /api/embed</c> endpoint (the current API — not the
///     legacy singular <c>/api/embeddings</c>). Ollama returns vectors under the
///     <c>embeddings</c> key (plural) as an array-of-arrays, one per input sent; <see cref="OllamaService.EmbedAsync" />
///     only ever sends one input, so it takes the first entry.
/// </summary>
public class OllamaEmbeddingResponse
{
    [JsonPropertyName("embeddings")] public IReadOnlyList<float[]> Embeddings { get; set; } = [];
}

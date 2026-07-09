using System.Text.Json.Serialization;

namespace TokenSaver.Library.Models.Ollama;

/// <summary>
///     Response shape for Ollama's <c>POST /api/embeddings</c> endpoint. Ollama returns the vector
///     under the <c>embedding</c> key (singular), independent of how many inputs were sent, since
///     this endpoint only accepts one input string per call.
/// </summary>
public class OllamaEmbeddingResponse
{
    [JsonPropertyName("embedding")] public float[] Embedding { get; set; } = [];
}

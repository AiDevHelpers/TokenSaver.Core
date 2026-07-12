using System.Text.Json.Serialization;

namespace TokenSaver.OllamaIntegration.Models.Ollama;

public class OllamaTagsResponse
{
#pragma warning disable CA1819 // JSON DTO wire-format property; array required for straightforward System.Text.Json binding
    [JsonPropertyName("models")] public OllamaModelEntry[]? Models { get; set; }
#pragma warning restore CA1819
}

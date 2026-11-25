using System.Text.Json.Serialization;

namespace AiDevTools.TokenSaver.OllamaIntegration.Models.Ollama;

public class OllamaPsModelEntry
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

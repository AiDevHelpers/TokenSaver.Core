using System.Text.Json.Serialization;

namespace TokenSaver.OllamaIntegration.Models.Ollama;

public class OllamaPsModelEntry
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

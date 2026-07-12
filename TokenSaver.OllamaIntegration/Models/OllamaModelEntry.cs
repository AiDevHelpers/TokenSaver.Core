using System.Text.Json.Serialization;

namespace TokenSaver.OllamaIntegration.Models.Ollama;

public class OllamaModelEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

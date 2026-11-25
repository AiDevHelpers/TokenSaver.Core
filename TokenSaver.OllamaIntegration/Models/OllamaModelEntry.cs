using System.Text.Json.Serialization;

namespace AiDevTools.TokenSaver.OllamaIntegration.Models.Ollama;

public class OllamaModelEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

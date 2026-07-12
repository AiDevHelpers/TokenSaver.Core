using System.Text.Json.Serialization;

namespace TokenSaver.OllamaIntegration.Models.Ollama;

public class OllamaGenerateResponse
{
    [JsonPropertyName("response")] public string Response { get; set; } = "";
}

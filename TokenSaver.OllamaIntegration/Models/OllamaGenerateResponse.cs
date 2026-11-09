using System.Text.Json.Serialization;

namespace TokenSaver.OllamaIntegration.Models.Ollama;

public class OllamaGenerateResponse
{
    // Null when the body has no "response" field (e.g. the router's {"status":"warming_up"} reply).
    [JsonPropertyName("response")] public string? Response { get; set; }
}

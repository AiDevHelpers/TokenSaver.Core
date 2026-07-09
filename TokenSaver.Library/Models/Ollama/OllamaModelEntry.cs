using System.Text.Json.Serialization;

namespace TokenSaver.Library.Models.Ollama;

public class OllamaModelEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

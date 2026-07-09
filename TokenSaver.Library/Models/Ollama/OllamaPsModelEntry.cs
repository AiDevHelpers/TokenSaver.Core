using System.Text.Json.Serialization;

namespace TokenSaver.Library.Models.Ollama;

public class OllamaPsModelEntry
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

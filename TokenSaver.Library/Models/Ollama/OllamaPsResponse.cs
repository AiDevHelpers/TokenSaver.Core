using System.Text.Json.Serialization;

namespace TokenSaver.Library.Models.Ollama;

public class OllamaPsResponse
{
#pragma warning disable CA1819 // JSON DTO wire-format property; array required for straightforward System.Text.Json binding
    [JsonPropertyName("models")] public OllamaPsModelEntry[]? Models { get; set; }
#pragma warning restore CA1819
}

namespace TokenSaver.Library.Models.Ollama;

public class OllamaStatusResult
{
    public bool Running { get; set; }

#pragma warning disable CA1819 // JSON DTO wire-format property; array required for straightforward System.Text.Json binding
    public string[] Models { get; set; } = [];
    public string[] LoadedInVram { get; set; } = [];
#pragma warning restore CA1819
}

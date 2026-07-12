using TokenSaver.Library.Contracts;

using Microsoft.Extensions.Logging;

namespace TokenSaver.OllamaIntegration.Services;

/// <summary>
///     Implements <see cref="ICodeSummarizer" /> by calling the configured Ollama model
///     (uses <see cref="OllamaService.EffectiveModel" /> — typically the draft/helper model)
///     with a short summarisation prompt.
///     Results are NOT cached here; caching is handled by the caller
///     (<see cref="LocalContextService" />) via the SQLite <c>summaries</c> table so the
///     cache survives server restarts and is keyed on content hash rather than file path.
///     The prompt intentionally avoids chain-of-thought framing and asks for a single
///     sentence.  This keeps latency low (the draft model is fast) and the output
///     dense enough to fit comfortably in an index JSON <c>explanation</c> field.
/// </summary>
public sealed partial class OllamaCodeSummarizer : ICodeSummarizer
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "OllamaCodeSummarizer failed for {File}")]
    private static partial void LogSummarizeFailed(ILogger logger, Exception ex, string file);

    private const string SystemPrompt =
        "You are a code indexer. Output exactly one sentence (max 150 characters) that describes " +
        "what the file does and names its key types or functions. No preamble, no explanation, " +
        "no markdown. Output only the sentence.";

    private readonly ILogger<OllamaCodeSummarizer> _logger;
    private readonly OllamaService _ollama;
    private readonly OllamaEndpointRegistry _endpoints;

    public OllamaCodeSummarizer(OllamaService ollama, OllamaEndpointRegistry endpoints, ILogger<OllamaCodeSummarizer> logger)
    {
        _ollama = ollama;
        _endpoints = endpoints;
        _logger = logger;
    }

    public async Task<string?> SummarizeAsync(
        string fileContent,
        string fileName,
        string fileHash,
        bool preferBackupEndpoint = false,
        CancellationToken ct = default)
    {
        try
        {
            string ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
            string preview = fileContent.Length > 3000 ? fileContent[..3000] : fileContent;

            string prompt =
                $"Summarise this {ext} file in one sentence (max 150 chars). " +
                $"Name the key types/functions it defines.\n\n```\n{preview}\n```";

            string? endpointName = preferBackupEndpoint ? FindBackupEndpointName() : null;

            string raw = endpointName is null
                ? await _ollama.CallModelAsync(_ollama.EffectiveModel, prompt, SystemPrompt, ct)
                : await _ollama.CallModelAsync(_ollama.EffectiveModel, prompt, SystemPrompt, endpointName, ct);

            // Strip any accidental markdown or leading/trailing whitespace
            string cleaned = raw.Trim().TrimStart('`').TrimEnd('`').Trim();

            // Reject obviously bad outputs (too long, looks like JSON, still has code fences)
            return cleaned.Length > 250 || cleaned.Contains("```", StringComparison.Ordinal) || cleaned.StartsWith('{')
                ? null
                : cleaned.Length > 0 ? cleaned : null;
        }
        catch (Exception ex)
        {
            LogSummarizeFailed(_logger, ex, fileName);
            return null;
        }
    }

    /// <summary>
    ///     Picks a healthy, non-primary configured endpoint by name for background indexing to
    ///     use, so bulk summarization doesn't compete with interactive requests for the primary
    ///     endpoint's capacity. Returns null (meaning "use normal selection") when only a single
    ///     endpoint is configured/synthesized, or when no non-primary endpoint is currently healthy.
    /// </summary>
    private string? FindBackupEndpointName()
    {
        IReadOnlyList<OllamaEndpointStatus> all = _endpoints.GetAllEndpoints();
        OllamaEndpointStatus? backup = all.FirstOrDefault(e => !e.Primary && e.Healthy);
        return backup?.Name;
    }
}

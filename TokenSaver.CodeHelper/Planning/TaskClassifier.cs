using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.CodeHelper.Models.Planning;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Net.Http.Json;

namespace AiDevTools.TokenSaver.CodeHelper.Planning;

/// <summary>
///     Two-tier task classifier:
///     Tier 1 — fast keyword matching (always runs).
///     Tier 2 — Ollama model call (only when Tier-1 confidence &lt; threshold).
/// </summary>
public sealed partial class TaskClassifier
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Tier-2 classification failed: {Msg}")]
    private static partial void LogTier2ClassificationFailed(ILogger logger, string msg);

    private static readonly (PlanTaskKind Kind, string[] Keywords)[] Patterns =
    [
        (PlanTaskKind.Refactor, ["refactor", "clean up", "cleanup", "reorganize", "restructure", "simplify", "tidy"]),
        (PlanTaskKind.AddFeature, ["add", "implement", "create", "build", "new feature", "support"]),
        (PlanTaskKind.BugFix, ["fix", "bug", "broken", "error", "doesn't work", "crash", "fail", "wrong"]),
        (PlanTaskKind.AddTests, ["test", "spec", "coverage", "unit test", "xunit", "nunit", "jest"]),
        (PlanTaskKind.Rename, ["rename", "move", "relocate", "change name"]),
        (PlanTaskKind.Extract, ["extract", "split", "separate", "pull out", "move to"]),
        (PlanTaskKind.DeleteDead, ["delete", "remove", "dead code", "unused", "clean dead"]),
        (PlanTaskKind.Reorganize, ["reorganize", "move files", "restructure folders", "project layout"]),
        (PlanTaskKind.Research,
        [
            "research", "investigate", "find out", "look into", "explore", "study", "analyze", "understand", "what is",
            "how does", "why does"
        ]),
        (PlanTaskKind.Planning,
        [
            "plan", "design", "architect", "roadmap", "strategy", "approach", "how should", "what should", "proposal",
            "spec", "specification"
        ])
    ];

    private readonly CodeHelperConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<TaskClassifier> _logger;

    public TaskClassifier(
        HttpClient http,
        IOptions<CodeHelperConfig> config,
        ILogger<TaskClassifier> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<(PlanTaskKind Kind, double Confidence)> ClassifyAsync(string prompt,
        CancellationToken ct = default)
    {
        // Tier 1
        PlanTaskKind bestKind = PlanTaskKind.General;
        double bestScore = 0.0;

        foreach ((PlanTaskKind kind, string[]? keywords) in Patterns)
        {
            int hits = keywords.Count(k => prompt.Contains(k, StringComparison.OrdinalIgnoreCase));
            double score = (double)hits / keywords.Length;
            if (score > bestScore)
            {
                bestScore = score;
                bestKind = kind;
            }
        }

        double confidence = Math.Min(1.0, bestScore * 3.0); // scale: 1 hit → 0.33, 2 → 0.67, 3+ → 1.0

        if (confidence >= _config.ModelCallThreshold)
        {
            return (bestKind, confidence);
        }

        // Tier 2 — ask the model
        return await ClassifyViaModelAsync(prompt, bestKind, confidence, ct);
    }

    private async Task<(PlanTaskKind Kind, double Confidence)> ClassifyViaModelAsync(
        string prompt, PlanTaskKind fallback, double fallbackConfidence, CancellationToken ct)
    {
        try
        {
            string kinds = string.Join(", ", Enum.GetNames<PlanTaskKind>());
            var payload = new
            {
                model = "HelperModel",
                prompt = $"Classify this coding task into exactly one category.\n" +
                         $"Categories: {kinds}\n" +
                         $"Task: {prompt}\n\n" +
                         $"Reply with exactly one word from the list above.",
                stream = false
            };

            HttpResponseMessage resp = await _http.PostAsJsonAsync("/api/generate", payload, ct);
            OllamaResponse? result = await resp.Content.ReadFromJsonAsync<OllamaResponse>(ct);
            string text = result?.Response?.Trim() ?? "";

            if (Enum.TryParse<PlanTaskKind>(text, true, out PlanTaskKind parsed))
            {
                return (parsed, 0.8);
            }
        }
        catch (Exception ex)
        {
            LogTier2ClassificationFailed(_logger, ex.Message);
        }

        return (fallback, fallbackConfidence);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed class OllamaResponse
    {
        public string? Response { get; set; }
    }
}

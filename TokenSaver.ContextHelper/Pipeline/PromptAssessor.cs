using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace TokenSaver.ContextHelper.Pipeline;

/// <summary>
///     Classifies an incoming prompt into a <see cref="TaskType" />, extracts key files,
///     constraints, and key concepts. Runs before Stage 1. Fast-path is regex-only;
///     falls back to a local model call when confidence &lt; threshold.
/// </summary>
public partial class PromptAssessor(
    HttpClient http,
    IOptions<ContextHelperConfig> config,
    ILogger<PromptAssessor> logger)
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "PromptAssessor: Tier-1 classified as {Type} (confidence={Conf:P0})")]
    private static partial void LogTier1Classified(ILogger logger, TaskType type, double conf);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PromptAssessor: Tier-2 model reclassified as {Type}")]
    private static partial void LogTier2Reclassified(ILogger logger, TaskType type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PromptAssessor Tier-2 model call failed; keeping Tier-1 result")]
    private static partial void LogTier2CallFailed(ILogger logger, Exception ex);

    // ── Keyword patterns ordered by specificity ────────────────────────────────

    private static readonly (TaskType Type, string[] Keywords)[] Patterns =
    [
        (TaskType.Debug,
        [
            "fix", "error", "exception", "crash", "stack trace", "why is", "not working", "fails", "failure", "broken"
        ]),
        (TaskType.Test,
            ["test", "spec", "assert", "mock", "coverage", "unit test", "integration test", "xunit", "nunit"]),
        (TaskType.Refactor,
            ["refactor", "extract", "clean up", "cleanup", "rename", "reorganize", "restructure", "move"]),
        (TaskType.Review, ["review", "look at", "check", "what do you think", "feedback", "critique"]),
        (TaskType.Explain, ["explain", "how does", "what is", "walk me through", "describe", "understand"]),
        (TaskType.Planning, ["plan", "design", "architecture", "approach", "strategy", "how should", "where should"]),
        (TaskType.Code, ["write", "implement", "add", "create", "build", "generate", "make"])
    ];
    private static readonly Regex FilePathRegex = new(
        @"(?:`([^`]+)`|(\S+?(?:\.cs|\.json|\.md|\.ts|\.js|\.py|\.yaml|\.yml|\.xml|\.csproj|\.slnx|\.sln|\.txt|\.config)))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ConstraintRegex = new(
        @"(?:don'?t|do not|never|keep|preserve|leave|avoid)\s+(.{5,60}?)(?:[,.]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ContextHelperConfig _config = config.Value;

    public async Task<PromptAssessment> AssessAsync(string prompt, string[]? history, CancellationToken ct = default)
    {
        string combined = string.Join(" ", (history ?? []).TakeLast(2).Append(prompt));

        (TaskType taskType, double confidence) = ClassifyByKeywords(combined);
        LogTier1Classified(logger, taskType, confidence);

        if (confidence < _config.PromptAssessorConfidenceThreshold)
        {
            TaskType? modelType = await ClassifyByModelAsync(prompt, ct);
            if (modelType.HasValue)
            {
                taskType = modelType.Value;
                confidence = 0.8;
                LogTier2Reclassified(logger, taskType);
            }
        }

        return new PromptAssessment
        {
            TaskType = taskType,
            WeightProfile = _config.GetWeightProfile(taskType),
            KeyFiles = ExtractKeyFiles(combined),
            Constraints = ExtractConstraints(combined),
            KeyConcepts = ExtractKeyConcepts(prompt),
            Confidence = confidence
        };
    }

    // ── Tier 1: keyword matching ───────────────────────────────────────────────

    private static (TaskType Type, double Confidence) ClassifyByKeywords(string text)
    {
        Dictionary<TaskType, int> scores = [];

        foreach ((TaskType type, string[]? keywords) in Patterns)
        {
            int hits = keywords.Count(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (hits > 0)
            {
                scores[type] = hits;
            }
        }

        if (scores.Count == 0)
        {
            return (TaskType.General, 0.3);
        }

        KeyValuePair<TaskType, int> best = scores.MaxBy(kv => kv.Value);
        int total = scores.Values.Sum();
        double conf = Math.Min(0.95, ((double)best.Value / total) + 0.2);
        return (best.Key, conf);
    }

    // ── Tier 2: model fallback ─────────────────────────────────────────────────

    private async Task<TaskType?> ClassifyByModelAsync(string prompt, CancellationToken ct)
    {
        try
        {
            string classifyPrompt =
                $"""
                 Classify this prompt into exactly one category. Reply with only the category name.
                 Categories: Code, Debug, Planning, Review, Explain, Refactor, Test, General

                 Prompt: {prompt}

                 Category:
                 """;

            var payload = new { model = "HelperModel", prompt = classifyPrompt, stream = false };
            HttpResponseMessage response = await http.PostAsJsonAsync("/api/generate", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            GenerateResponse? result = await response.Content.ReadFromJsonAsync<GenerateResponse>(ct);
            string text = result?.Response?.Trim() ?? "";
            return Enum.TryParse<TaskType>(text, true, out TaskType parsed) ? parsed : null;
        }
        catch (Exception ex)
        {
            LogTier2CallFailed(logger, ex);
            return null;
        }
    }

    // ── Extraction helpers ─────────────────────────────────────────────────────

    private static string[] ExtractKeyFiles(string text)
    {
        MatchCollection matches = FilePathRegex.Matches(text);
        return [.. matches
            .Select(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim())
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string[] ExtractConstraints(string text)
    {
        return [.. ConstraintRegex.Matches(text)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(c => c.Length > 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string[] ExtractKeyConcepts(string prompt)
    {
        // Extract words > 4 chars that look like technical terms (PascalCase, camelCase, or all-caps)
        return Regex.Matches(prompt, @"\b(?:[A-Z][a-z]+[A-Z]\w*|[a-z]+[A-Z]\w*|[A-Z]{2,})\b")
            .Select(m => m.Value)
            .Distinct()
            .Take(10)
            .ToArray();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed record GenerateResponse(string? Response);
}

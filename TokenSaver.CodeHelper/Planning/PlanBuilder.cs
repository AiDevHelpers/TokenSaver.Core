using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Indexing;
using TokenSaver.CodeHelper.Models.Index;
using TokenSaver.CodeHelper.Models.Patch;
using TokenSaver.CodeHelper.Models.Planning;
using TokenSaver.CodeHelper.Research;
using TokenSaver.Library.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenSaver.CodeHelper.Planning;

/// <summary>
///     Generates a <see cref="CodePlan" /> from a natural-language task description.
///     Uses symbol index for context, then calls the local Ollama model for plan generation.
///     Steps whose confidence falls below <see cref="CodeHelperConfig.ConfidenceThreshold" />
///     are flagged RequiresAIHelp = true.
/// </summary>
public sealed partial class PlanBuilder
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Plan {Id}: {N} steps, overall confidence {Conf:F2}")]
    private static partial void LogPlanBuilt(ILogger logger, string id, int n, double conf);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plan model call failed: {Msg}")]
    private static partial void LogPlanModelCallFailed(ILogger logger, string msg);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse plan JSON: {Msg}")]
    private static partial void LogPlanJsonParseFailed(ILogger logger, string msg);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Plan task: {Kind} (confidence={Conf:F2})")]
    private static partial void LogPlanTask(ILogger logger, PlanTaskKind kind, double conf);

    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly TaskClassifier _classifier;
    private readonly CodeHelperConfig _config;
    private readonly SymbolSearchService _symbolSearch;
    private readonly DependencyTrackingService _dependencies;
    private readonly ILogger<PlanBuilder> _logger;
    private readonly OllamaService _ollama;
    private readonly ResearchContextService _research;

    public PlanBuilder(
        OllamaService ollama,
        SymbolSearchService symbolSearch,
        DependencyTrackingService dependencies,
        TaskClassifier classifier,
        ResearchContextService research,
        IOptions<CodeHelperConfig> config,
        ILogger<PlanBuilder> logger)
    {
        _ollama = ollama;
        _symbolSearch = symbolSearch;
        _dependencies = dependencies;
        _classifier = classifier;
        _research = research;
        _config = config.Value;
        _logger = logger;
    }

    // Regex to extract path-like tokens from the prompt
    [GeneratedRegex(@"[\w./\\-]+\.\w{1,5}", RegexOptions.None)]
    private static partial Regex PathTokenRegex();

    public async Task<CodePlan> BuildPlanAsync(
        string prompt, string sessionId, string workspaceRoot,
        ResearchContextFormat? researchFormat = null, CancellationToken ct = default)
    {
        (PlanTaskKind kind, double confidence) = await _classifier.ClassifyAsync(prompt, ct);
        LogPlanTask(_logger, kind, confidence);

        // For research/planning tasks, enrich the prompt with relevant file context
        if (kind is PlanTaskKind.Research or PlanTaskKind.Planning)
        {
            string researchCtx = await _research.BuildResearchContextAsync(
                prompt, workspaceRoot, researchFormat, ct);
            if (!string.IsNullOrEmpty(researchCtx))
            {
                prompt = researchCtx + "\n\n" + prompt;
            }
        }

        // Gather symbol context
        string[] pathHints = ExtractPathHints(prompt);
        string symbolCtx = await BuildSymbolContextAsync(prompt, pathHints, workspaceRoot, ct);

        string systemPrompt = BuildSystemPrompt(kind, symbolCtx, pathHints);
        string planJson = await CallModelAsync(systemPrompt, prompt, ct);

        PlanStep[] steps = ParseSteps(planJson, workspaceRoot);

        string planId = Guid.NewGuid().ToString("N")[..12];
        CodePlan plan = new()
        {
            PlanId = planId,
            SessionId = sessionId,
            TaskKind = kind,
            OriginalPrompt = prompt,
            Steps = steps,
            OverallConfidence = steps.Length > 0 ? steps.Average(s => s.Confidence) : 0.5,
            Summary = $"{kind} plan: {steps.Length} step(s) generated."
        };

        LogPlanBuilt(_logger, plan.PlanId, steps.Length, plan.OverallConfidence);

        return plan;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string[] ExtractPathHints(string prompt)
    {
        return PathTokenRegex().Matches(prompt)
            .Select(m => m.Value)
            .Distinct()
            .Take(10)
            .ToArray();
    }

    private async Task<string> BuildSymbolContextAsync(string prompt, string[] pathHints, string root,
        CancellationToken ct)
    {
        // Extract candidate symbol names: PascalCase or camelCase words from prompt
        string[] words = Regex.Matches(prompt, @"\b[A-Z][a-zA-Z0-9]+\b|[a-z][a-zA-Z0-9]+\b")
            .Select(m => m.Value)
            .Distinct()
            .Take(8)
            .ToArray();

        StringBuilder sb = new();

        foreach (string? word in words)
        {
            IReadOnlyList<SymbolEntry> symbols = await _symbolSearch.FindSymbolAsync(word, workspaceRoot: root, ct: ct);
            foreach (SymbolEntry? s in symbols.Take(3))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {s.Kind} {s.FullName} at {s.FilePath}:{s.Line}");
            }
        }

        foreach (string path in pathHints)
        {
            IReadOnlyList<string> deps = await _dependencies.GetFileDependenciesAsync(path, root, ct);
            if (deps.Count > 0)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {path} depends on: {string.Join(", ", deps.Take(5))}");
            }
        }

        return sb.Length > 0 ? sb.ToString() : "No symbol context available.";
    }

    private static string BuildSystemPrompt(PlanTaskKind kind, string symbolCtx, string[] pathHints)
    {
        return $"""
                You are a code planning assistant helping to execute a {kind} task.
                Generate a JSON array of steps. Each step must have these exact fields:
                  - description: string — what this step does
                  - filePath: string or null — workspace-relative file to modify
                  - operationType: one of: "replace", "insert", "delete", "rename", "ai_help"
                  - anchor: string or null — the exact existing text to locate in the file
                  - oldText: string or null — text to replace (for replace operations)
                  - newText: string or null — replacement text
                  - confidence: number 0.0–1.0 — how certain you are this step is correct
                  - notes: array of strings — any caveats or assumptions

                Use "ai_help" operationType for steps where you are uncertain what exact change
                to make, or where the change depends on information you don't have.
                Keep steps small and focused — one logical change per step.
                Do not add steps you are not confident about; use ai_help instead.

                Known symbol context:
                {symbolCtx}

                Files mentioned: {(pathHints.Length > 0 ? string.Join(", ", pathHints) : "none")}

                Output ONLY the JSON array, no explanation, no markdown fences.
                """;
    }

    private async Task<string> CallModelAsync(string system, string prompt, CancellationToken ct)
    {
        try
        {
            // Use OllamaService so the prompt is run through IPromptAdapter (AgentHelpers)
            // before being sent. System prompt is passed as-is; user prompt is adapted.
            string response = await _ollama.CallModelAsync(
                _ollama.EffectiveModel, prompt, system, ct);
            return response.Trim();
        }
        catch (Exception ex)
        {
            LogPlanModelCallFailed(_logger, ex.Message);
            return "[]";
        }
    }

    private PlanStep[] ParseSteps(string json, string workspaceRoot)
    {
        try
        {
            // Strip any markdown fences the model might have added
            string clean = json.Trim();
            if (clean.StartsWith("```", StringComparison.Ordinal))
            {
                clean = Regex.Replace(clean, @"^```\w*\n?|\n?```$", "", RegexOptions.Multiline).Trim();
            }

            List<RawStep> raw = JsonSerializer.Deserialize<List<RawStep>>(clean,
                CaseInsensitiveJsonOptions) ?? [];

            return [.. raw.Take(_config.MaxPlanSteps).Select((r, i) =>
            {
                PatchOperation? op = BuildOperation(r);
                double confidence = Math.Clamp(r.Confidence, 0.0, 1.0);
                bool needsAI = r.OperationType == "ai_help" || confidence < _config.ConfidenceThreshold;

                return new PlanStep
                {
                    StepNumber = i + 1,
                    Description = r.Description ?? $"Step {i + 1}",
                    FilePath = r.FilePath,
                    Operation = needsAI ? null : op,
                    Confidence = confidence,
                    RequiresAIHelp = needsAI,
                    AIHandoffPrompt = needsAI ? BuildHandoffPrompt(r, i + 1, raw.Count) : null,
                    Notes = r.Notes ?? []
                };
            })];
        }
        catch (Exception ex)
        {
            LogPlanJsonParseFailed(_logger, ex.Message);
            return [];
        }
    }

    private static PatchOperation? BuildOperation(RawStep r)
    {
        return r.OperationType?.ToUpperInvariant() switch
        {
            "REPLACE" => new ReplaceOperation
            {
                FilePath = r.FilePath ?? "",
                OldText = r.OldText ?? r.Anchor ?? "",
                NewText = r.NewText ?? "",
                Description = r.Description ?? ""
            },
            "INSERT" => new InsertOperation
            {
                FilePath = r.FilePath ?? "",
                Anchor = r.Anchor ?? "",
                Content = r.NewText ?? "",
                Description = r.Description ?? ""
            },
            "DELETE" => new DeleteOperation
            {
                FilePath = r.FilePath ?? "",
                Pattern = r.OldText ?? r.Anchor ?? "",
                Description = r.Description ?? ""
            },
            "RENAME" => new RenameOperation
            {
                FilePath = r.FilePath ?? "",
                OldName = r.OldText ?? r.Anchor ?? "",
                NewName = r.NewText ?? "",
                Description = r.Description ?? ""
            },
            _ => null
        };
    }

    private static string BuildHandoffPrompt(RawStep step, int n, int total)
    {
        return $"Step {n}/{total} requires your input: {step.Description}\n" +
               $"File: {step.FilePath ?? "unknown"}\n" +
               (step.Confidence < 0.65
                   ? $"Confidence is low ({step.Confidence:P0}). Please confirm or provide the exact change.\n"
                   : "This step is marked as requiring your decision.\n") +
               (step.Notes?.Length > 0 ? $"Notes: {string.Join("; ", step.Notes)}\n" : "") +
               "\nPlease provide the replacement content or instructions, then call ExecutePlanStep again.";
    }

    private sealed class RawStep
    {
        public string? Description { get; set; }
        public string? FilePath { get; set; }
        public string? OperationType { get; set; }
        public string? Anchor { get; set; }
        public string? OldText { get; set; }
        public string? NewText { get; set; }
        public double Confidence { get; } = 0.5;
        public string[]? Notes { get; set; }
    }
}

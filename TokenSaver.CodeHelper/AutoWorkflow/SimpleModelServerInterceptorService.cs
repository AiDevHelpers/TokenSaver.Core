using TokenSaver.CodeHelper.Models.Patch;
using TokenSaver.CodeHelper.Patching;
using TokenSaver.OllamaIntegration.Services;

using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenSaver.CodeHelper.AutoWorkflow;

/// <summary>
///     Intercepts code-writing prompts and routes them through the draft model first.
///     The draft model classifies each request as one of:
///     - CODE: simple coding task the draft model can implement directly
///     - ARCHITECTURE: needs design/architecture decisions from the calling AI
///     - UNCERTAIN: draft model not confident enough, should fall back to calling AI
///     For CODE tasks, the draft model generates the implementation, which is then
///     applied via PatchEngine. ARCHITECTURE and UNCERTAIN tasks are deferred back
///     to the calling AI with a structured prompt explaining what guidance is needed.
///     This service is called from <c>ContextInterceptMiddleware</c> alongside the
///     context cleaning pipeline.
/// </summary>
public sealed partial class SimpleModelServerInterceptorService(
    OllamaService ollama,
    PatchEngine patch,
    CodeWorkflowService workflow,
    ILogger<SimpleModelServerInterceptorService> logger)
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Not a code-build intent — passing through")]
    private static partial void LogNotCodeBuildIntent(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Draft model interceptor: classified as code-build intent")]
    private static partial void LogClassifiedAsCodeBuildIntent(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Draft model classification: {Classification}")]
    private static partial void LogSimpleModelServerClassification(ILogger logger, DraftClassification classification);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Draft model returned unexpected classification: '{Raw}' — defaulting to UNCERTAIN")]
    private static partial void LogUnexpectedClassification(ILogger logger, string raw);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Draft model classification call failed — defaulting to UNCERTAIN")]
    private static partial void LogClassificationCallFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Draft model attempting code implementation")]
    private static partial void LogAttemptingCodeImplementation(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Draft model code generation failed")]
    private static partial void LogCodeGenerationFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Draft model returned empty operations — deferring to calling AI")]
    private static partial void LogEmptyOperationsDeferring(ILogger logger);

    private const string SimpleModelServerName = "HelperModel";
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ILogger<SimpleModelServerInterceptorService> _log = logger;
    private readonly OllamaService _ollama = ollama;
    private readonly PatchEngine _patch = patch;
    private readonly CodeWorkflowService _workflow = workflow;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Attempts to intercept a code-writing prompt.
    ///     Returns <see cref="SimpleModelServerInterceptionResult" /> indicating whether the
    ///     draft model handled it, deferred it back to the calling AI, or should be
    ///     passed through as a normal tool call.
    /// </summary>
    public async Task<SimpleModelServerInterceptionResult> TryInterceptAsync(
        string userPrompt,
        string workspaceRoot,
        CancellationToken ct = default)
    {
        // Phase 1: fast keyword check — is this even a code-build request?
        if (!_workflow.DetectCodeBuildIntent(userPrompt))
        {
            LogNotCodeBuildIntent(_log);
            return SimpleModelServerInterceptionResult.PassThrough;
        }

        LogClassifiedAsCodeBuildIntent(_log);

        // Phase 2: ask the draft model to classify the request
        DraftClassification classification = await ClassifyWithSimpleModelServerAsync(userPrompt, ct);

        LogSimpleModelServerClassification(_log, classification);

        return classification switch
        {
            DraftClassification.ArchitectureOrDesign => SimpleModelServerInterceptionResult.Deferred(
                                "This task appears to require architecture or design decisions. " +
                                "The draft model cannot make these without guidance.\n\n" +
                                "Please describe the high-level design, types, and interfaces needed. " +
                                "Once you provide the skeleton, I can implement the details.",
                                true),
            DraftClassification.Uncertain => SimpleModelServerInterceptionResult.Deferred(
                                "The local draft model is not confident about this request. " +
                                "Please provide more detail or implement this directly.\n\n" +
                                "If you describe the approach or skeleton design, I can help with the implementation.",
                                false),
            DraftClassification.CodeTask => await TryImplementAsync(userPrompt, workspaceRoot, ct),
            _ => SimpleModelServerInterceptionResult.PassThrough,
        };
    }

    /// <summary>
    ///     After the calling AI provides architecture guidance, use the draft model
    ///     to implement the code.
    /// </summary>
    public async Task<SimpleModelServerInterceptionResult> ImplementWithGuidanceAsync(
        string originalPrompt,
        string aiGuidance,
        string workspaceRoot,
        CancellationToken ct = default)
    {
        string enrichedPrompt = $"""
                              Architecture guidance from the calling AI:
                              {aiGuidance}

                              Original task:
                              {originalPrompt}

                              Implement the code based on the architecture guidance above.
                              Generate ONLY the code changes needed. Be precise about what text to replace and what to insert.
                              """;

        return await TryImplementAsync(enrichedPrompt, workspaceRoot, ct);
    }

    // ── Classification ────────────────────────────────────────────────────────

    private async Task<DraftClassification> ClassifyWithSimpleModelServerAsync(
        string prompt, CancellationToken ct)
    {
        string classifyPrompt = $"""
                              You are a code task classifier running on a fast local draft model.
                              Classify the following user request into exactly one category.

                              Categories:
                              CODE — A concrete coding task. The user wants to add, create, modify, implement,
                                     write, fix, or scaffold code. The changes are well-defined (add a method,
                                     create a class, fix a bug, write a test, rename a symbol).
                              ARCHITECTURE — The user is asking about design, architecture, planning, what approach
                                             to take, how to structure something, or wants a design decision made.
                                             The task requires understanding trade-offs, system design, or project structure.
                              UNCERTAIN — You cannot confidently categorize the request, or it doesn't fit clearly
                                          into CODE or ARCHITECTURE. Respond UNCERTAIN rather than guessing.

                              Reply with exactly one word: CODE, ARCHITECTURE, or UNCERTAIN.

                              Request: {prompt}

                              Category:
                              """;

        try
        {
            string response = await _ollama.CallModelAsync(SimpleModelServerName, classifyPrompt, ct);
            string trimmed = response.Trim().ToUpperInvariant();

            if (trimmed.StartsWith("CODE", StringComparison.Ordinal))
            {
                return DraftClassification.CodeTask;
            }

            if (trimmed.StartsWith("ARCHITECTURE", StringComparison.Ordinal))
            {
                return DraftClassification.ArchitectureOrDesign;
            }

            if (trimmed.StartsWith("UNCERTAIN", StringComparison.Ordinal))
            {
                return DraftClassification.Uncertain;
            }

            LogUnexpectedClassification(_log, trimmed);
            return DraftClassification.Uncertain;
        }
        catch (Exception ex)
        {
            LogClassificationCallFailed(_log, ex);
            return DraftClassification.Uncertain;
        }
    }

    // ── Implementation ────────────────────────────────────────────────────────

    private async Task<SimpleModelServerInterceptionResult> TryImplementAsync(
        string prompt, string workspaceRoot, CancellationToken ct)
    {
        LogAttemptingCodeImplementation(_log);

        // Ask draft model to generate code with a structured format
        string implementPrompt = $"""
                               Given the following task, generate the exact code changes needed.
                               Output your response as a JSON array of patch operations.

                               Each operation must have:
                                 - "filePath": workspace-relative file path (string, required)
                                 - "operation": "replace", "insert", "delete", or "rename" (string, required)
                                 - "oldText": exact existing text to find (string, required for replace/delete)
                                 - "newText": replacement or new content (string, required for replace/insert/rename)
                                 - "description": what this change does (string, optional)

                               CRITICAL: "oldText" must match the file content EXACTLY — character for character.
                               If you are not absolutely certain about the anchor text, do NOT include that operation.
                               Only include operations you are confident will succeed.

                               Task: {prompt}

                               Output ONLY the JSON array, no explanation, no markdown fences.
                               If you cannot confidently generate the code, respond with: []
                               """;

        string codeJson;
        try
        {
            codeJson = await _ollama.CallModelAsync(SimpleModelServerName, implementPrompt, ct);
        }
        catch (Exception ex)
        {
            LogCodeGenerationFailed(_log, ex);
            return SimpleModelServerInterceptionResult.Deferred(
                "The local model encountered an error generating code. " +
                "Please implement this directly or provide the exact changes needed.",
                false);
        }

        // Parse the JSON response
        List<PatchOperation> ops = ParsePatchOperations(codeJson);

        if (ops.Count == 0)
        {
            LogEmptyOperationsDeferring(_log);
            return SimpleModelServerInterceptionResult.Deferred(
                "The local draft model could not generate confident code changes for this task. " +
                "Please implement this directly or provide the exact changes.",
                false);
        }

        // Apply patches
        List<PatchResult> applied = [];
        List<string> errors = [];

        foreach (PatchOperation op in ops)
        {
            try
            {
                PatchResult result = await _patch.ApplyAsync(op, workspaceRoot, ct);
                applied.Add(result);
                if (!result.Success)
                {
                    errors.Add($"{op.FilePath}: {result.Error}");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{op.FilePath}: {ex.Message}");
            }
        }

        int successes = applied.Count(r => r.Success);
        if (successes == 0)
        {
            return SimpleModelServerInterceptionResult.Deferred(
                $"The draft model generated code but none could be applied:\n{string.Join("\n", errors)}\n\n" +
                "Please implement this directly.",
                false);
        }

        string summary = BuildSummary(applied, errors);
        return SimpleModelServerInterceptionResult.Implemented(summary, applied);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<PatchOperation> ParsePatchOperations(string json)
    {
        List<PatchOperation> ops = [];
        try
        {
            string trimmed = json.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                trimmed = Regex.Replace(
                    trimmed, @"^```\w*\n?|\n?```$", "", RegexOptions.Multiline);
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                List<RawPatchOp>? raw = JsonSerializer.Deserialize<List<RawPatchOp>>(
                    trimmed,
                    CaseInsensitiveJsonOptions);

                if (raw is null)
                {
                    return ops;
                }

                foreach (RawPatchOp r in raw)
                {
                    if (string.IsNullOrWhiteSpace(r.FilePath))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(r.OldText) && r.Operation != "insert")
                    {
                        continue;
                    }

                    string operation = r.Operation?.ToUpperInvariant() ?? "REPLACE";
                    switch (operation)
                    {
                        case "REPLACE":
                            ops.Add(new ReplaceOperation
                            {
                                FilePath = r.FilePath,
                                OldText = r.OldText ?? "",
                                NewText = r.NewText ?? "",
                                Description = r.Description ?? "Draft model auto-generated"
                            });
                            break;
                        case "INSERT":
                            ops.Add(new InsertOperation
                            {
                                FilePath = r.FilePath,
                                Anchor = r.OldText ?? "",
                                Content = r.NewText ?? "",
                                Description = r.Description ?? "Draft model auto-generated"
                            });
                            break;
                        case "DELETE":
                            ops.Add(new DeleteOperation
                            {
                                FilePath = r.FilePath,
                                Pattern = r.OldText ?? "",
                                Description = r.Description ?? "Draft model auto-generated"
                            });
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ParsePatchOperations failed: {ex.Message}");
        }

        return ops;
    }

    private static string BuildSummary(List<PatchResult> applied, List<string> errors)
    {
        StringBuilder sb = new();
        int successes = applied.Count(r => r.Success);
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"**Draft Model Implementation** — {successes} operation(s) applied, {errors.Count} error(s).");
        _ = sb.AppendLine();

        if (successes > 0)
        {
            _ = sb.AppendLine("**Changes applied:**");
            foreach (PatchResult? r in applied.Where(r => r.Success))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- `{r.FilePath}` — {r.LinesChanged} line(s) changed");
            }
        }

        if (errors.Count > 0)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine("**Errors encountered:**");
            foreach (string e in errors)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- {e}");
            }

            _ = sb.AppendLine();
            _ = sb.AppendLine("The remaining changes could not be applied automatically. " +
                          "Review the errors above and apply the changes manually or provide corrected instructions.");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Raw JSON parsing types ────────────────────────────────────────────────

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed class RawPatchOp
    {
        public string? FilePath { get; set; }
        public string? Operation { get; set; }
        public string? OldText { get; set; }
        public string? NewText { get; set; }
        public string? Description { get; set; }
    }
}

// ── Result type ──────────────────────────────────────────────────────────────

/// <summary>
///     Indicates how the draft model interceptor handled a code-writing request.
/// </summary>
public sealed class SimpleModelServerInterceptionResult
{
    // ── Singleton instances for non-parameterized results ──────────────────────

    /// <summary>Singleton: task should pass through as a normal tool call.</summary>
    public static readonly SimpleModelServerInterceptionResult PassThrough = new() { IsPassThrough = true };

    private SimpleModelServerInterceptionResult()
    {
    }

    // ── Properties ────────────────────────────────────────────────────────────

    /// <summary>True when the draft model successfully implemented the task.</summary>
    public bool HandledBySimpleModelServer { get; private init; }

    /// <summary>True when the task should be deferred back to the calling AI.</summary>
    public bool IsDeferred { get; private init; }

    /// <summary>True when the task should pass through as a normal tool call.</summary>
    public bool IsPassThrough { get; private init; }

    /// <summary>True when the deferred task needs architecture/design input from the AI.</summary>
    public bool NeedsArchitectureHelp { get; private init; }

    /// <summary>Summary of what was implemented or why it was deferred.</summary>
    public string? Summary { get; private init; }

    /// <summary>List of applied patch results when the draft model handled it.</summary>
    public IReadOnlyList<PatchResult> AppliedPatches { get; private init; } = [];

    // ── Factory methods ────────────────────────────────────────────────────────

    /// <summary>Creates a deferred result with a summary message.</summary>
    public static SimpleModelServerInterceptionResult Deferred(string summary, bool needsArchitectureHelp)
    {
        return new SimpleModelServerInterceptionResult
        {
            IsDeferred = true,
            Summary = summary,
            NeedsArchitectureHelp = needsArchitectureHelp
        };
    }

    /// <summary>Creates a result indicating the draft model handled the task.</summary>
    public static SimpleModelServerInterceptionResult Implemented(string summary, IReadOnlyList<PatchResult> patches)
    {
        return new SimpleModelServerInterceptionResult
        {
            HandledBySimpleModelServer = true,
            Summary = summary,
            AppliedPatches = patches
        };
    }
}

/// <summary>Draft model's classification of a user request.</summary>
internal enum DraftClassification
{
    /// <summary>Simple coding task the draft model can implement.</summary>
    CodeTask,

    /// <summary>Requires architecture/design decisions from the calling AI.</summary>
    ArchitectureOrDesign,

    /// <summary>Draft model is not confident enough.</summary>
    Uncertain
}

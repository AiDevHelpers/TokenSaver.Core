using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.TokenEconomy.Models.Context;

using ModelContextProtocol.Server;

using System.ComponentModel;

namespace AiDevTools.TokenSaver.Docs.Prompts;

[McpServerPromptType]
public class BuildConciseContextPrompt
{
    private readonly ContextPipelineService _contextPipeline;

    public BuildConciseContextPrompt(ContextPipelineService contextPipeline)
    {
        _contextPipeline = contextPipeline;
    }

    [McpServerPrompt(
        Name = "build-concise-context",
        Title = "Build Concise Context with Ollama")]
    [Description("Skill: build a concise, token-efficient summary of the current workspace using Ollama, " +
                 "then use that context for all subsequent responses in this session. " +
                 "Always run this at the start of every conversation.")]
    public async Task<string> GetPromptAsync(CancellationToken ct = default)
    {
        string contextSummary;
        try
        {
            BuildContextResult result = await _contextPipeline.BuildConciseContextAsync("[prompt] build-concise-context skill invoked", maxTokens: 120_000, ct: ct);

            contextSummary = result.GeneratedFiles.Count > 0
                ? $"Workspace context built: {result.GeneratedFiles.Count} file summaries, {result.TokenCount} tokens used."
                : "Workspace context built (no generated files found).";
        }
        catch (Exception ex)
        {
            contextSummary = $"Context build skipped: {ex.Message}";
        }

        return $"""
                You have access to the TokenSaver MCP server.
                At the start of this conversation, call the `build_concise_context` tool
                to get an up-to-date summary of the current workspace before answering any questions.
                Always prefer workspace context from `build_concise_context` over assumptions.
                Current status: {contextSummary}
                """;
    }
}

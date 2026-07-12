namespace TokenSaver.OllamaIntegration.Services;

/// <summary>
///     Adapts a raw prompt for a specific local model before sending to inference.
///     Implementations apply model-family-specific structuring: role-priming, output schema
///     hints, self-check instructions, and token budget trimming.
///     The interface lives in <c>TokenSaver.Library</c> so low-level services
///     (<see cref="OllamaService" />, PlanBuilder, etc.) can depend on it without
///     pulling in <c>TokenSaver.AgentHelpers.Core</c>. The concrete implementation is registered
///     in <c>TokenSaver.MCP</c> backed by <c>AgentAdapterRegistry</c>.
/// </summary>
public interface IPromptAdapter
{
    /// <summary>
    ///     Adapts <paramref name="prompt" /> for the model identified by <paramref name="modelHint" />.
    ///     Returns the adapted prompt, or <paramref name="prompt" /> unchanged if no adapter matches.
    /// </summary>
    string Adapt(string prompt, string modelHint);
}

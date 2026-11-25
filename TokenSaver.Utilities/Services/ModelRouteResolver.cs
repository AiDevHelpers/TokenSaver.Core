using AiDevTools.TokenSaver.Common.Core.Configuration;

using Microsoft.Extensions.Options;

namespace AiDevTools.TokenSaver.Utilities.Services;

/// <summary>
///     Resolves a task_type string (e.g. "code", "chat", "summarize", "embed", "rerank") to an
///     Ollama model name via TokenEconomyConfig.ModelRoutes — centralizes model choice in one
///     place instead of hardcoding a model name per tool/service. Backs the ModelRoute MCP tool
///     and is used internally by DistillationService/RepoIndexer.
/// </summary>
public sealed class ModelRouteResolver(IOptions<TokenSaverConfig> config)
{
    private readonly TokenSaverConfig _config = config.Value;

    /// <summary>
    ///     Returns the configured model for <paramref name="taskType" />, falling back to the
    ///     "chat" route, then to <see cref="TokenSaverConfig.OllamaModel" />/EffectiveModel if
    ///     even "chat" isn't configured (should not happen with TokenEconomyConfig's defaults,
    ///     but keeps this resolver total rather than throwing on a misconfigured route table).
    /// </summary>
    public string Resolve(string taskType)
    {
        Dictionary<string, string> routes = _config.TokenEconomy.ModelRoutes;
        if (routes.TryGetValue(taskType, out string? model) && !string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        if (routes.TryGetValue("chat", out string? chatModel) && !string.IsNullOrWhiteSpace(chatModel))
        {
            return chatModel;
        }

        return string.IsNullOrWhiteSpace(_config.OllamaModel) ? "HelperModel" : _config.OllamaModel;
    }
}

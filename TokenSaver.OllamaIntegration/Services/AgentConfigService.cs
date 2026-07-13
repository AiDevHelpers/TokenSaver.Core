using TokenSaver.Common.Core.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenSaver.OllamaIntegration.Services;

/// <summary>
///     Reads and writes agent configurations in the DraftModel.config.json file
///     that lives alongside DraftModel.Loader.exe in .tokensaver/ under the workspace.
///     This lets the calling AI update system prompts and temperature settings based
///     on accuracy test results, then save them back to the workspace for persistence.
/// </summary>
public sealed partial class AgentConfigService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "DraftModel config not found at {Path}")]
    private static partial void LogConfigNotFound(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DRAFTMODEL_WORKSPACE is not set; cannot locate {Config}")]
    private static partial void LogWorkspaceNotSet(ILogger logger, string config);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to parse DraftModel config")]
    private static partial void LogParseFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated agent '{Type}' config in {Path}")]
    private static partial void LogAgentUpdated(ILogger logger, string type, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update agent config")]
    private static partial void LogAgentUpdateFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to save proposed prompts")]
    private static partial void LogSavePromptsFailed(ILogger logger, Exception ex);

    private const string ConfigFileName = "DraftModel.config.json";
    private const string TokenSaverDir = ".tokensaver";

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private readonly TokenSaverConfig _config;
    private readonly ILogger<AgentConfigService> _logger;

    public AgentConfigService(IOptions<TokenSaverConfig> config, ILogger<AgentConfigService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    // ── Config path resolution ─────────────────────────────────────────────────

    /// <summary>
    ///     Resolves the DraftModel.config.json path.
    ///     Priority: DRAFTMODEL_WORKSPACE env var → TokenSaverConfig.WorkspaceRoot → null.
    /// </summary>
    public string? GetConfigPath()
    {
        string? workspace = Environment.GetEnvironmentVariable("DRAFTMODEL_WORKSPACE")
                        ?? (string.IsNullOrWhiteSpace(_config.WorkspaceRoot) ? null : _config.WorkspaceRoot);

        if (string.IsNullOrWhiteSpace(workspace))
        {
            LogWorkspaceNotSet(_logger, ConfigFileName);
            return null;
        }

        return Path.Combine(workspace, TokenSaverDir, ConfigFileName);
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Returns the raw JSON content of DraftModel.config.json, or null if not found.
    /// </summary>
    public async Task<string?> GetRawConfigAsync(CancellationToken ct = default)
    {
        string? path = GetConfigPath();
        if (path is null || !File.Exists(path))
        {
            LogConfigNotFound(_logger, path ?? "(unresolved)");
            return null;
        }

        return await File.ReadAllTextAsync(path, ct);
    }

    /// <summary>
    ///     Returns all agent configs as a dictionary keyed by agent type.
    /// </summary>
    public async Task<Dictionary<string, AgentConfigSnapshot>?> GetAgentConfigsAsync(CancellationToken ct = default)
    {
        string? raw = await GetRawConfigAsync(ct);
        if (raw is null)
        {
            return null;
        }

        try
        {
            JsonObject? root = JsonNode.Parse(raw)?.AsObject();
            JsonObject? agents = root?["agents"]?.AsObject();
            return agents is null
                ? []
                : agents.ToDictionary(
                pair => pair.Key,
                pair =>
                {
                    JsonObject? obj = pair.Value?.AsObject();
                    return new AgentConfigSnapshot(
                        obj?["systemPrompt"]?.GetValue<string>() ?? "",
                        obj?["temperature"]?.GetValue<double>(),
                        obj?["sampling"]?["top_p"]?.GetValue<double>());
                },
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogParseFailed(_logger, ex);
            return null;
        }
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Updates one agent's config in DraftModel.config.json.
    ///     Only non-null parameters are changed; existing values are preserved.
    /// </summary>
    /// <returns>
    ///     (success, message) — success=false means the config file wasn't found or the write failed.
    /// </returns>
    public async Task<(bool Success, string Message)> UpdateAgentAsync(
        string agentType,
        string? systemPrompt = null,
        double? temperature = null,
        double? topP = null,
        CancellationToken ct = default)
    {
        string? path = GetConfigPath();
        if (path is null)
        {
            return (false, "DRAFTMODEL_WORKSPACE is not set. Cannot locate DraftModel.config.json.");
        }

        if (!File.Exists(path))
        {
            return (false, $"Config file not found: {path}");
        }

        try
        {
            string raw = await File.ReadAllTextAsync(path, ct);
            JsonObject root = JsonNode.Parse(raw)?.AsObject()
                       ?? throw new InvalidOperationException("Config root must be a JSON object.");

            // Ensure agents section exists
            if (root["agents"] is not JsonObject agents)
            {
                agents = [];
                root["agents"] = agents;
            }

            // Ensure the specific agent entry exists
            if (agents[agentType] is not JsonObject agent)
            {
                agent = [];
                agents[agentType] = agent;
            }

            // Apply non-null updates
            if (systemPrompt is not null)
            {
                agent["systemPrompt"] = JsonValue.Create(systemPrompt);
            }

            if (temperature is not null)
            {
                agent["temperature"] = JsonValue.Create(temperature.Value);
            }

            if (topP is not null)
            {
                if (agent["sampling"] is not JsonObject sampling)
                {
                    sampling = [];
                    agent["sampling"] = sampling;
                }

                sampling["top_p"] = JsonValue.Create(topP.Value);
            }

            string updated = root.ToJsonString(PrettyJson);
            await File.WriteAllTextAsync(path, updated, ct);

            LogAgentUpdated(_logger, agentType, path);
            return (true, $"Saved changes to {agentType} agent in {path}");
        }
        catch (Exception ex)
        {
            LogAgentUpdateFailed(_logger, ex);
            return (false, $"Write failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Saves a complete agent config block to the workspace as a side-file
    ///     (<c>.tokensaver/agent-prompts.json</c>), separate from the main config,
    ///     so the AI's proposed prompt changes can be reviewed and diffed before
    ///     being applied to DraftModel.config.json.
    /// </summary>
    public async Task<(bool Success, string Path)> SaveProposedPromptsAsync(
        Dictionary<string, AgentConfigSnapshot> proposals,
        CancellationToken ct = default)
    {
        string workspace = Environment.GetEnvironmentVariable("DRAFTMODEL_WORKSPACE")
                        ?? _config.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return (false, "");
        }

        string dir = Path.Combine(workspace, TokenSaverDir);
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "agent-prompts.json");

        try
        {
            string json = JsonSerializer.Serialize(proposals, PrettyJson);
            await File.WriteAllTextAsync(path, json, ct);
            return (true, path);
        }
        catch (Exception ex)
        {
            LogSavePromptsFailed(_logger, ex);
            return (false, "");
        }
    }
}

/// <summary>
///     Snapshot of an agent's current config, suitable for serialization and diff.
/// </summary>
public sealed record AgentConfigSnapshot(
    string SystemPrompt,
    double? Temperature,
    double? TopP);

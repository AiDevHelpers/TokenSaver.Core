using TokenSaver.Common.Core.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenSaver.OllamaIntegration.Services;

/// <summary>
///     Reads and writes SimpleModelServer agent configs through the router's admin API
///     (<c>GET /admin/config</c>, <c>POST /admin/config</c>), not the config file. The router is the
///     source of truth for what it is running, so a file-path lookup is no longer needed.
///     The router accepts <c>systemPrompt</c> and <c>temperature</c> per agent; <c>topP</c> is not
///     part of its admin API and is rejected here.
///     Proposed prompt changes still go to a side-file under <c>.tokensaver/</c> for review.
/// </summary>
public sealed partial class AgentConfigService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "SimpleModelServer admin config unavailable at {Url}: {Status}")]
    private static partial void LogAdminUnavailable(ILogger logger, string url, string status);

    [LoggerMessage(Level = LogLevel.Error, Message = "SimpleModelServer admin config request failed at {Url}")]
    private static partial void LogAdminRequestFailed(ILogger logger, Exception ex, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated '{Setting}' via {Url}")]
    private static partial void LogAgentUpdated(ILogger logger, string setting, string url);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to save proposed prompts")]
    private static partial void LogSavePromptsFailed(ILogger logger, Exception ex);

    private const string AdminConfigPath = "/admin/config";
    private const string TokenSaverDir = ".tokensaver";

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private readonly HttpClient _http;
    private readonly TokenSaverConfig _config;
    private readonly ILogger<AgentConfigService> _logger;

    public AgentConfigService(HttpClient http, IOptions<TokenSaverConfig> config, ILogger<AgentConfigService> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Returns the raw JSON from <c>GET /admin/config</c>, or null if the router is unreachable or
    ///     refuses the request (it trusts localhost, and remote callers need a Bearer token).
    /// </summary>
    public async Task<string?> GetRawConfigAsync(CancellationToken ct = default)
    {
        string url = AdminUrl();
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(new Uri(AdminConfigPath, UriKind.Relative), ct);
            if (!response.IsSuccessStatusCode)
            {
                LogAdminUnavailable(_logger, url, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            LogAdminRequestFailed(_logger, ex, url);
            return null;
        }
    }

    /// <summary>
    ///     Returns the agent configs as a dictionary keyed by agent type, or null if the admin API is
    ///     unavailable. <see cref="AgentConfigSnapshot.TopP" /> is always null: the router does not
    ///     report <c>top_p</c> through its admin API.
    /// </summary>
    public async Task<Dictionary<string, AgentConfigSnapshot>?> GetAgentConfigsAsync(CancellationToken ct = default)
    {
        string? raw = await GetRawConfigAsync(ct);
        if (raw is null)
        {
            return null;
        }

        JsonObject? agents = JsonNode.Parse(raw)?["agents"]?.AsObject();
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
                        TopP: null);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Updates one agent through <c>POST /admin/config</c>, one setting per request. Only non-null
    ///     parameters are sent; the router keeps everything else. Stops at the first failure.
    /// </summary>
    /// <returns>
    ///     (success, message). <paramref name="topP" /> is rejected because the admin API has no
    ///     <c>top_p</c> setting.
    /// </returns>
    public async Task<(bool Success, string Message)> UpdateAgentAsync(
        string agentType,
        string? systemPrompt = null,
        double? temperature = null,
        double? topP = null,
        CancellationToken ct = default)
    {
        if (topP is not null)
        {
            return (false, "topP is not supported by the SimpleModelServer admin API. Only systemPrompt and temperature can be set.");
        }

        if (systemPrompt is not null)
        {
            (bool ok, string message) = await PostSettingAsync($"agents.{agentType}.systemPrompt", JsonValue.Create(systemPrompt), ct);
            if (!ok)
            {
                return (false, message);
            }
        }

        if (temperature is not null)
        {
            (bool ok, string message) = await PostSettingAsync($"agents.{agentType}.temperature", JsonValue.Create(temperature.Value), ct);
            if (!ok)
            {
                return (false, message);
            }
        }

        return (true, $"Applied changes to '{agentType}' via {AdminUrl()}");
    }

    private async Task<(bool Ok, string Message)> PostSettingAsync(string setting, JsonNode? value, CancellationToken ct)
    {
        string url = AdminUrl();
        JsonObject body = new() { ["setting"] = setting, ["value"] = value };

        try
        {
            using StringContent content = new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _http.PostAsync(new Uri(AdminConfigPath, UriKind.Relative), content, ct);
            string text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Router rejected '{setting}' ({(int)response.StatusCode}): {text}");
            }

            LogAgentUpdated(_logger, setting, url);
            return (true, text);
        }
        catch (HttpRequestException ex)
        {
            LogAdminRequestFailed(_logger, ex, url);
            return (false, $"Could not reach the SimpleModelServer admin API at {url}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Saves a complete agent config block to the workspace as a side-file
    ///     (<c>.tokensaver/agent-prompts.json</c>), so proposed prompt changes can be reviewed and
    ///     diffed before being applied through the admin API.
    /// </summary>
    public async Task<(bool Success, string Path)> SaveProposedPromptsAsync(
        Dictionary<string, AgentConfigSnapshot> proposals,
        CancellationToken ct = default)
    {
        string workspace = Environment.GetEnvironmentVariable("SIMPLEMODELSERVER_WORKSPACE")
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

    private string AdminUrl() => new Uri(new Uri(_config.OllamaUrl), AdminConfigPath).ToString();
}

/// <summary>
///     Snapshot of an agent's current config, suitable for serialization and diff.
/// </summary>
public sealed record AgentConfigSnapshot(
    string SystemPrompt,
    double? Temperature,
    double? TopP);

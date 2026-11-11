using TokenSaver.Common.Core.Configuration;
using TokenSaver.OllamaIntegration.Models.Ollama;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Diagnostics;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;

namespace TokenSaver.OllamaIntegration.Services;

/// <summary>
///     Key used on <see cref="HttpRequestMessage.Options" /> to request a specific Ollama endpoint
///     by name for a single call. Read by <c>TokenSaver.MCP.Services.EndpointRoutingHandler</c>.
///     Declared here (rather than in the MCP project) so library-level callers like
///     <see cref="OllamaService" /> can set it without a back-reference to TokenSaver.MCP.
/// </summary>
public static class OllamaRequestOptions
{
    public static readonly HttpRequestOptionsKey<string> EndpointName = new("TokenSaver.EndpointName");
}

public partial class OllamaService
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "SimpleModelServer reachable but no models available yet — retrying in {Interval}s.")]
    private static partial void LogSimpleModelServerNoModelsYet(ILogger logger, double interval);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SimpleModelServer not yet reachable — retrying in {Interval}s.")]
    private static partial void LogSimpleModelServerNotReachable(ILogger logger, double interval);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SimpleModelServer probe failed — retrying in {Interval}s.")]
    private static partial void LogSimpleModelServerProbeFailed(ILogger logger, Exception ex, double interval);

    private readonly IPromptAdapter? _adapter;
    private readonly TokenSaverConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(
        HttpClient httpClient,
        IOptions<TokenSaverConfig> config,
        ILogger<OllamaService> logger,
        IPromptAdapter? adapter = null)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _adapter = adapter;
        _logger = logger;
    }

    public string EffectiveModel =>
        string.IsNullOrWhiteSpace(_config.OllamaModel) ? "HelperModel" : _config.OllamaModel;

    /// <summary>
    ///     Calls the local model with a single prompt. The prompt is run through the
    ///     registered <see cref="IPromptAdapter" /> (if any) before sending.
    /// </summary>
    public Task<string> CallModelAsync(string prompt, CancellationToken ct = default)
    {
        return CallModelAsync(EffectiveModel, prompt, null, null, ct);
    }

    /// <summary>
    ///     Calls the local model with a single prompt, optionally pinned to a specific configured
    ///     endpoint (see <see cref="TokenSaverConfig.Endpoints" />). Passing null uses the
    ///     registry's normal selection strategy.
    /// </summary>
    public Task<string> CallModelOnEndpointAsync(string prompt, string? endpointName, CancellationToken ct = default)
    {
        return CallModelAsync(EffectiveModel, prompt, null, endpointName, ct);
    }

    /// <summary>
    ///     Calls the local model with an explicit model name and prompt.
    /// </summary>
    public Task<string> CallModelAsync(string model, string prompt, CancellationToken ct = default)
    {
        return CallModelAsync(model, prompt, null, null, ct);
    }

    /// <summary>
    ///     Calls the local model with a separate system prompt and user prompt.
    ///     The <paramref name="userPrompt" /> is adapted via <see cref="IPromptAdapter" /> if registered;
    ///     the <paramref name="systemPrompt" /> is sent as-is (already well-structured by the caller).
    /// </summary>
    public Task<string> CallModelAsync(
        string model, string userPrompt, string? systemPrompt, CancellationToken ct = default)
    {
        return CallModelAsync(model, userPrompt, systemPrompt, null, ct);
    }

    /// <summary>
    ///     Calls the local model with a separate system prompt and user prompt, optionally pinned
    ///     to a specific configured endpoint. The <paramref name="userPrompt" /> is adapted via
    ///     <see cref="IPromptAdapter" /> if registered; the <paramref name="systemPrompt" /> is sent as-is.
    /// </summary>
    public Task<string> CallModelAsync(
        string model, string userPrompt, string? systemPrompt, string? endpointName, CancellationToken ct = default)
    {
        string adapted = _adapter?.Adapt(userPrompt, model) ?? userPrompt;
        return PostGenerateAsync(model, adapted, systemPrompt, endpointName, ct);
    }

    /// <summary>
    ///     Generates an embedding vector for <paramref name="input" /> via Ollama's
    ///     <c>POST /api/embed</c> endpoint (not to be confused with <c>/api/generate</c> used
    ///     by <see cref="CallModelAsync(string,string,CancellationToken)" />). Requires an
    ///     embedding-capable model to be pulled locally (e.g. <c>nomic-embed-text</c>) — a chat
    ///     model like the configured <see cref="EffectiveModel" /> will not work here.
    ///     Deliberately the current <c>/api/embed</c> API, not the legacy singular
    ///     <c>/api/embeddings</c> — SimpleModelServer's router only proxies the former (see
    ///     RouterHttpServer's <c>/API/EMBED</c> case); calling the legacy endpoint through the
    ///     router 404s.
    /// </summary>
    /// <param name="model">Embedding model name, e.g. <c>"nomic-embed-text"</c>.</param>
    /// <param name="input">Text to embed. Not run through <see cref="IPromptAdapter" /> — embeddings
    ///     are not a chat turn, so no chat-formatting should be applied.</param>
    /// <param name="endpointName">Optional pinned endpoint name, same semantics as
    ///     <see cref="CallModelOnEndpointAsync" />.</param>
    public async Task<float[]> EmbedAsync(string model, string input, string? endpointName = null, CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/embed")
        {
            Content = JsonContent.Create(new { model, input })
        };
        if (!string.IsNullOrWhiteSpace(endpointName))
        {
            request.Options.Set(OllamaRequestOptions.EndpointName, endpointName);
        }

        HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"POST /api/embed returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                null,
                response.StatusCode);
        }

        OllamaEmbeddingResponse? result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(ct);
        return result?.Embeddings is { Count: > 0 } embeddings ? embeddings[0] : [];
    }

    private async Task<string> PostGenerateAsync(
        string model, string prompt, string? system, string? endpointName, CancellationToken ct)
    {
        // Build payload — omit system key entirely when not provided so Ollama
        // uses the model's default system prompt template (if any).
        object payload = system is null
            ? new { model, prompt, stream = false }
            : new { model, system, prompt, stream = false };

        for (int attempt = 1; ; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/generate")
            {
                Content = JsonContent.Create(payload)
            };
            if (!string.IsNullOrWhiteSpace(endpointName))
            {
                request.Options.Set(OllamaRequestOptions.EndpointName, endpointName);
            }

            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"POST /api/generate returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                    null,
                    response.StatusCode);
            }

            // Read the body once so a reply without a "response" field can be reported with its content.
            string okBody = await response.Content.ReadAsStringAsync(ct);

            // The SimpleModelServer router answers 200 with {"status":"warming_up","retry_after":N}
            // while models load. That is transient, so wait and resend instead of failing.
            if (attempt < MaxWarmupAttempts && TryGetWarmupDelay(okBody, out TimeSpan warmupDelay))
            {
                await Task.Delay(warmupDelay, ct);
                continue;
            }

            OllamaGenerateResponse? result = JsonSerializer.Deserialize<OllamaGenerateResponse>(okBody, JsonSerializerOptions.Web);
            return result?.Response
                ?? throw new HttpRequestException($"POST /api/generate returned 200 without a 'response' field: {okBody}");
        }
    }

    private const int MaxWarmupAttempts = 10;
    private const int DefaultWarmupRetrySeconds = 3;

    private static bool TryGetWarmupDelay(string body, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out JsonElement status)
                || status.ValueKind != JsonValueKind.String
                || status.GetString() != "warming_up")
            {
                return false;
            }

            int seconds = root.TryGetProperty("retry_after", out JsonElement retryAfter)
                && retryAfter.ValueKind == JsonValueKind.Number
                && retryAfter.TryGetInt32(out int parsed)
                    ? parsed
                    : DefaultWarmupRetrySeconds;
            delay = TimeSpan.FromSeconds(Math.Max(1, seconds));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Polls until the Ollama router is reachable and has at least one model available,
    ///     or until <paramref name="timeout" /> elapses.
    ///     Returns true if ready, false if timed out.
    /// </summary>
    public async Task<bool> WaitForReadyAsync(
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(10));
        TimeSpan poll = pollInterval ?? TimeSpan.FromSeconds(10);

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                OllamaStatusResult status = await GetOllamaStatusAsync();
                if (status.Running && status.Models.Length > 0)
                {
                    return true;
                }

                if (logger is not null)
                {
                    if (status.Running)
                    {
                        LogSimpleModelServerNoModelsYet(logger, poll.TotalSeconds);
                    }
                    else
                    {
                        LogSimpleModelServerNotReachable(logger, poll.TotalSeconds);
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger is not null)
                {
                    LogSimpleModelServerProbeFailed(logger, ex, poll.TotalSeconds);
                }
            }

            await Task.Delay(poll, ct);
        }

        return false;
    }

    public async Task<OllamaStatusResult> GetOllamaStatusAsync()
    {
        try
        {
            OllamaTagsResponse? tagsResponse = await _httpClient.GetFromJsonAsync<OllamaTagsResponse>("/api/tags");
            string[] models = tagsResponse?.Models?.Select(m => m.Name).ToArray() ?? [];

            string[] loaded = [];
            try
            {
                OllamaPsResponse? psResponse = await _httpClient.GetFromJsonAsync<OllamaPsResponse>("/api/ps");
                loaded = psResponse?.Models?.Select(m => m.Name ?? "").Where(n => !string.IsNullOrEmpty(n)).ToArray() ??
                         [];
            }
            catch
            {
            }

            return new OllamaStatusResult { Running = true, Models = models, LoadedInVram = loaded };
        }
        catch
        {
            return new OllamaStatusResult { Running = false, Models = [], LoadedInVram = [] };
        }
    }

    public async Task<GpuStats> GetGpuStatsAsync()
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process process = new() { StartInfo = startInfo };
            _ = process.Start();
            string stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return new GpuStats { Gpu = "unavailable" };
            }

            string[] lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return new GpuStats { Gpu = "unavailable" };
            }

            string[] parts = lines[0].Split(',', StringSplitOptions.TrimEntries);
            return new GpuStats
            {
                Gpu = parts.Length > 0 ? parts[0] : "unknown",
                VramUsedMb = parts.Length > 1 && int.TryParse(parts[1], out int used) ? used : null,
                VramTotalMb = parts.Length > 2 && int.TryParse(parts[2], out int total) ? total : null,
                GpuUtilPct = parts.Length > 3 && int.TryParse(parts[3], out int util) ? util : null
            };
        }
        catch
        {
            return new GpuStats { Gpu = "unavailable" };
        }
    }
}

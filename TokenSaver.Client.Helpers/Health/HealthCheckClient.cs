using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AiDevTools.TokenSaver.Client.Helpers.Logging;

namespace AiDevTools.TokenSaver.Client.Helpers.Health;

/// <summary>
/// Utilities for checking server health and port availability.
/// Uses shared HttpClient for connection pooling efficiency.
/// </summary>
public static class HealthCheckClient
{
    private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly JsonSerializerOptions _healthJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Checks if a port is open and accepting connections on the specified host.
    /// Supports both IPv4 and IPv6.
    /// </summary>
    public static async Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host cannot be null or empty", nameof(host));
        if (port < 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535");

        if (timeout == default)
            timeout = TimeSpan.FromSeconds(1);

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var socket = new Socket(AddressFamily.Unspecified, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ExtensionLoggerFacade.LogDebug($"Unexpected error checking port {host}:{port}: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Waits for a port to become open, polling at 500ms intervals until timeout.
    /// </summary>
    public static async Task<bool> WaitForPortOpenAsync(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsPortOpenAsync(host, port, TimeSpan.FromMilliseconds(500)))
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>
    /// Verifies the TokenSaver MCP server is responding at /mcp endpoint on localhost.
    /// </summary>
    public static async Task<bool> VerifyMcpServerAsync(int port, TimeSpan timeout = default)
    {
        return await VerifyMcpServerAsync("127.0.0.1", port, timeout);
    }

    /// <summary>
    /// Verifies the TokenSaver MCP server is responding at /mcp endpoint.
    /// </summary>
    public static async Task<bool> VerifyMcpServerAsync(string host, int port, TimeSpan timeout = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host cannot be null or empty", nameof(host));
        if (port < 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535");

        if (timeout == default)
            timeout = TimeSpan.FromSeconds(5);

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var url = new Uri($"http://{host}:{port}/mcp");
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ExtensionLoggerFacade.LogDebug($"Unexpected error verifying MCP server at {host}:{port}: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Polls a health endpoint and checks if it returns 200 "ok" or 503 "degraded".
    /// </summary>
    public static async Task<bool> IsSimpleModelServerHealthyAsync(Uri healthUrl, TimeSpan timeout = default)
    {
        ArgumentNullException.ThrowIfNull(healthUrl);

        if (timeout == default)
            timeout = TimeSpan.FromSeconds(30);

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var response = await _httpClient.GetAsync(healthUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (response.StatusCode != HttpStatusCode.OK &&
                response.StatusCode != HttpStatusCode.ServiceUnavailable)
                return false;

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out var statusElem))
                return false;

            var status = statusElem.GetString();
            return status == "ok" || status == "degraded";
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ExtensionLoggerFacade.LogDebug($"Unexpected error checking health at {healthUrl}: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Gets the full health report from AiDevTools.TokenSaver.MCP /health endpoint on localhost.
    /// </summary>
    public static async Task<McpHealthReport?> GetFullHealthAsync(int port, TimeSpan timeout = default)
    {
        return await GetFullHealthAsync("127.0.0.1", port, timeout);
    }

    /// <summary>
    /// Gets the full health report from AiDevTools.TokenSaver.MCP /health endpoint.
    /// </summary>
    public static async Task<McpHealthReport?> GetFullHealthAsync(string host, int port, TimeSpan timeout = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host cannot be null or empty", nameof(host));
        if (port < 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535");

        if (timeout == default)
            timeout = TimeSpan.FromSeconds(10);

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var url = new Uri($"http://{host}:{port}/health");
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token);
            var json = await response.Content.ReadAsStringAsync(cts.Token);

            return JsonSerializer.Deserialize<McpHealthReport>(json, _healthJsonOptions);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex)
        {
            ExtensionLoggerFacade.LogDebug($"Unexpected error getting full health from {host}:{port}: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Waits for a health endpoint to report ok/degraded, polling at 2s intervals.
    /// </summary>
    public static async Task<bool> WaitForHealthOkAsync(Uri healthUrl, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(healthUrl);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsSimpleModelServerHealthyAsync(healthUrl, TimeSpan.FromSeconds(2)))
                return true;
            await Task.Delay(2000);
        }
        return false;
    }

    /// <summary>
    /// Finds the first open port in a range starting at basePort.
    /// </summary>
    public static async Task<int?> FindOpenPortInRangeAsync(string host, int basePort, int count)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host cannot be null or empty", nameof(host));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be non-negative");

        for (int i = 0; i <= count; i++)
        {
            if (await IsPortOpenAsync(host, basePort + i, TimeSpan.FromMilliseconds(100)))
                return basePort + i;
        }
        return null;
    }
}

/// <summary>
/// Represents a health check report from AiDevTools.TokenSaver.MCP /health endpoint.
/// </summary>
public sealed class McpHealthReport
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("checks")]
    public IReadOnlyList<McpHealthCheckEntry>? Checks { get; set; }
}

/// <summary>
/// A single health check entry in the report.
/// </summary>
public sealed class McpHealthCheckEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

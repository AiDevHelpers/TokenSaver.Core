using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TokenSaver.Client.Helpers.Health;

/// <summary>
/// Utilities for checking server health and port availability.
/// </summary>
public static class HealthCheckClient
{
    /// <summary>
    /// Checks if a port is open and accepting connections on the specified host.
    /// </summary>
    public static async Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout = default)
    {
        if (timeout == default)
            timeout = TimeSpan.FromSeconds(1);

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(timeout);
            await socket.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for a port to become open, polling at 500ms intervals until timeout.
    /// </summary>
    public static async Task<bool> WaitForPortOpenAsync(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            if (await IsPortOpenAsync(host, port))
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>
    /// Verifies the TokenSaver MCP server is responding at /mcp endpoint.
    /// </summary>
    public static async Task<bool> VerifyMcpServerAsync(int port, TimeSpan timeout = default)
    {
        if (timeout == default)
            timeout = TimeSpan.FromSeconds(5);

        try
        {
            using var client = new HttpClient();
            using var cts = new CancellationTokenSource(timeout);
            var response = await client.GetAsync($"http://127.0.0.1:{port}/mcp", cts.Token);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Polls a health endpoint and checks if it returns 200 "ok" or 503 "degraded".
    /// </summary>
    public static async Task<bool> IsDraftModelHealthyAsync(string healthUrl, TimeSpan timeout = default)
    {
        if (timeout == default)
            timeout = TimeSpan.FromSeconds(30);

        try
        {
            using var client = new HttpClient();
            using var cts = new CancellationTokenSource(timeout);
            var response = await client.GetAsync(healthUrl, cts.Token);

            if (response.StatusCode != System.Net.HttpStatusCode.OK &&
                response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
                return false;

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetString();
            return status == "ok" || status == "degraded";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the full health report from TokenSaver.MCP /health endpoint.
    /// </summary>
    public static async Task<McpHealthReport?> GetFullHealthAsync(int port, TimeSpan timeout = default)
    {
        if (timeout == default)
            timeout = TimeSpan.FromSeconds(10);

        try
        {
            using var client = new HttpClient();
            using var cts = new CancellationTokenSource(timeout);
            var response = await client.GetAsync($"http://127.0.0.1:{port}/health", cts.Token);
            var json = await response.Content.ReadAsStringAsync(cts.Token);

            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            return JsonSerializer.Deserialize<McpHealthReport>(json, options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Waits for a health endpoint to report ok/degraded, polling at 2s intervals.
    /// </summary>
    public static async Task<bool> WaitForHealthOkAsync(string healthUrl, TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            if (await IsDraftModelHealthyAsync(healthUrl, TimeSpan.FromSeconds(2)))
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
        for (int i = 0; i <= count; i++)
        {
            if (await IsPortOpenAsync(host, basePort + i))
                return basePort + i;
        }
        return null;
    }
}

/// <summary>
/// Represents a health check report from TokenSaver.MCP /health endpoint.
/// </summary>
public sealed class McpHealthReport
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("checks")]
    public List<McpHealthCheckEntry>? Checks { get; set; }
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

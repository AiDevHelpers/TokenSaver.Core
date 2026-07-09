using Microsoft.Extensions.Logging;

using System.Text.Json;

namespace TokenSaver.Library.Services;

/// <summary>
///     Resolved capacity for background inference calls. Deliberately a plain record parsed from
///     JSON here rather than a shared type from DraftModel.Library — the two stacks are independent
///     (see CLAUDE.md), so TokenSaver talks to the router's HTTP API only, never its assemblies.
/// </summary>
public sealed record DraftModelCapacity
{
    public int? CpuThreads { get; init; }
    public int? GpuLayers { get; init; }
    public bool GpuDetected { get; init; }
    public int MaxConcurrentSummaryRequests { get; init; } = 1;
}

/// <summary>
///     Queries DraftModel.Loader's <c>GET /api/capacity</c> endpoint so background indexing (AI
///     file/folder summaries) can size its concurrency off this machine's real capacity instead of
///     guessing from CPU count. Falls back to a conservative default (1) if the endpoint is
///     unreachable — e.g. DraftModel isn't running, or an older build without this endpoint.
/// </summary>
public sealed class DraftModelCapacityClient(HttpClient http, ILogger<DraftModelCapacityClient> logger)
{
    private DraftModelCapacity? _cached;

    public async Task<DraftModelCapacity> GetCapacityAsync(CancellationToken ct = default)
    {
        if (_cached != null)
        {
            return _cached;
        }

        try
        {
            using HttpResponseMessage response =
                await http.GetAsync(new Uri("/api/capacity", UriKind.Relative), ct);
            if (!response.IsSuccessStatusCode)
            {
                return _cached = new DraftModelCapacity();
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            JsonElement root = doc.RootElement;

            JsonElement gpu = root.TryGetProperty("gpu", out JsonElement gpuNode) ? gpuNode : default;

            _cached = new DraftModelCapacity
            {
                CpuThreads = root.TryGetProperty("cpuThreads", out JsonElement ct2) && ct2.ValueKind == JsonValueKind.Number
                    ? ct2.GetInt32()
                    : null,
                GpuLayers = root.TryGetProperty("gpuLayers", out JsonElement gl) && gl.ValueKind == JsonValueKind.Number
                    ? gl.GetInt32()
                    : null,
                GpuDetected = gpu.ValueKind == JsonValueKind.Object && gpu.TryGetProperty("detected", out JsonElement d) &&
                              d.GetBoolean(),
                MaxConcurrentSummaryRequests = root.TryGetProperty("maxConcurrentSummaryRequests", out JsonElement m) &&
                                               m.ValueKind == JsonValueKind.Number
                    ? Math.Max(1, m.GetInt32())
                    : 1
            };
            return _cached;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "DraftModel /api/capacity unreachable — defaulting to 1 concurrent summary request.");
            return _cached = new DraftModelCapacity();
        }
    }
}

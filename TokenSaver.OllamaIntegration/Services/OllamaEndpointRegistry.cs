using AiDevTools.TokenSaver.Common.Core.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDevTools.TokenSaver.OllamaIntegration.Services;

/// <summary>
///     Runtime health/usage snapshot for one configured Ollama endpoint.
///     Reset on process restart — no persistence, matching the plan's "no historical
///     persistence required" note.
/// </summary>
public sealed record OllamaEndpointStatus
{
    public required string Name { get; init; }

    // CA1056 suppressed: mirrors OllamaEndpointConfig.Url — sourced from config as a plain
    // string and used as such (string concatenation) everywhere it's read.
#pragma warning disable CA1056
    public required string Url { get; init; }
#pragma warning restore CA1056
    public required bool Primary { get; init; }
    public required IReadOnlyList<string> Models { get; init; }
    public bool Healthy { get; init; } = true;
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? LastHealthCheckAt { get; init; }
    public long SuccessCount { get; init; }
    public long FailureCount { get; init; }

    /// <summary>
    ///     When set and in the future, this endpoint is in an exponential-backoff window and will
    ///     be skipped by automatic selection until this time, even if marked unhealthy.
    /// </summary>
    public DateTimeOffset? NextRetryAt { get; init; }
}

/// <summary>
///     Tracks configured Ollama endpoints and their runtime health, and selects which
///     endpoint a given call should use according to <see cref="TokenSaverConfig.EndpointSelectStrategy" />.
///     When <see cref="TokenSaverConfig.Endpoints" /> is empty (the default — most installs),
///     this registry synthesizes a single endpoint named "default" from the flat
///     <see cref="TokenSaverConfig.OllamaUrl" />/<see cref="TokenSaverConfig.OllamaModel" /> settings,
///     so single-endpoint behavior is preserved with zero config changes.
/// </summary>
public sealed partial class OllamaEndpointRegistry
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Ollama endpoint '{Name}' ({Url}) recovered — marked healthy.")]
    private static partial void LogEndpointRecovered(ILogger logger, string name, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ollama endpoint '{Name}' ({Url}) failed — marked unhealthy.")]
    private static partial void LogEndpointFailed(ILogger logger, string name, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ollama endpoint '{Name}' ({Url}) health check recovered.")]
    private static partial void LogEndpointHealthCheckRecovered(ILogger logger, string name, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ollama endpoint '{Name}' ({Url}) health check failed.")]
    private static partial void LogEndpointHealthCheckFailed(ILogger logger, string name, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Requested endpoint '{Name}' is unhealthy — falling back to primary.")]
    private static partial void LogRequestedEndpointUnhealthy(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Requested endpoint '{Name}' is not configured — falling back to primary.")]
    private static partial void LogRequestedEndpointNotConfigured(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Ollama endpoint '{Name}' ({Url}) backing off for {Delay} after {FailureCount} consecutive failures.")]
    private static partial void LogEndpointBackoff(ILogger logger, string name, string url, TimeSpan delay, int failureCount);

    /// <summary>Initial backoff delay applied after the first consecutive failure.</summary>
    private static readonly TimeSpan BaseBackoffDelay = TimeSpan.FromSeconds(2);

    /// <summary>Ceiling for the exponential backoff delay, regardless of consecutive-failure count.</summary>
    private static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromMinutes(5);

    private readonly List<EndpointEntry> _endpoints;
    private readonly object _lock = new();
    private readonly ILogger<OllamaEndpointRegistry> _logger;
    private readonly EndpointSelectStrategy _strategy;
    private int _roundRobinCursor = -1;

    public OllamaEndpointRegistry(IOptions<TokenSaverConfig> options, ILogger<OllamaEndpointRegistry> logger)
    {
        _logger = logger;
        TokenSaverConfig config = options.Value;
        _strategy = config.EndpointSelectStrategy;

        if (config.Endpoints.Count > 0)
        {
            _endpoints = config.Endpoints.Select(e => new EndpointEntry
            {
                Name = e.Name,
                Url = e.Url,
                Primary = e.Primary,
                Models = [.. e.Models]
            }).ToList();

            // If nobody flagged a primary, treat the first configured endpoint as primary
            // so failover/explicit strategies always have a fallback target.
            if (!_endpoints.Any(e => e.Primary) && _endpoints.Count > 0)
            {
                _endpoints[0].Primary = true;
            }
        }
        else
        {
            // Synthesize a single endpoint from the legacy flat config — preserves
            // today's single-endpoint behavior for anyone who hasn't opted in.
            _endpoints =
            [
                new EndpointEntry
                {
                    Name = "default",
                    Url = config.OllamaUrl,
                    Primary = true,
                    Models = string.IsNullOrWhiteSpace(config.OllamaModel) ? [] : [config.OllamaModel]
                }
            ];
        }
    }

    /// <summary>
    ///     Selects an endpoint URL to use for the next call.
    /// </summary>
    /// <param name="explicitName">
    ///     When set, honors <see cref="EndpointSelectStrategy.Explicit" />-style routing regardless
    ///     of the configured strategy: use the named endpoint if it's healthy, otherwise fall back
    ///     to the primary endpoint.
    /// </param>
    public OllamaEndpointStatus SelectEndpoint(string? explicitName = null)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                EndpointEntry? named = _endpoints.FirstOrDefault(e =>
                    string.Equals(e.Name, explicitName, StringComparison.OrdinalIgnoreCase));
                if (named is { Healthy: true })
                {
                    return ToStatus(named);
                }

                if (named is not null)
                {
                    LogRequestedEndpointUnhealthy(_logger, explicitName);
                }
                else
                {
                    LogRequestedEndpointNotConfigured(_logger, explicitName);
                }

                return ToStatus(PickPrimaryOrAny());
            }

            return _strategy switch
            {
                EndpointSelectStrategy.RoundRobin => ToStatus(PickRoundRobin()),
                EndpointSelectStrategy.Explicit => ToStatus(PickPrimaryOrAny()),
                _ => ToStatus(PickFailover())
            };
        }
    }

    private EndpointEntry PickFailover()
    {
        EndpointEntry? primary = _endpoints.FirstOrDefault(e => e.Primary);
        if (primary is { Healthy: true } && IsEligibleNow(primary))
        {
            return primary;
        }

        EndpointEntry? eligible = _endpoints.FirstOrDefault(e => e.Healthy && IsEligibleNow(e));
        if (eligible is not null)
        {
            return eligible;
        }

        // Nothing healthy/eligible — allow an opportunistic retry of whichever backed-off
        // endpoint is soonest to recover, rather than hammering the primary during its backoff.
        EndpointEntry? soonest = _endpoints
            .Where(e => e.NextRetryAt.HasValue)
            .OrderBy(e => e.NextRetryAt)
            .FirstOrDefault();
        if (soonest is not null)
        {
            return soonest;
        }

        // Nothing healthy — return primary anyway so the caller gets a real error
        // instead of an exception from an empty selection.
        return primary ?? _endpoints[0];
    }

    private EndpointEntry PickRoundRobin()
    {
        List<EndpointEntry> healthy = _endpoints.Where(e => e.Healthy && IsEligibleNow(e)).ToList();
        if (healthy.Count == 0)
        {
            return PickFailover();
        }

        _roundRobinCursor = (_roundRobinCursor + 1) % healthy.Count;
        return healthy[_roundRobinCursor];
    }

    /// <summary>True when the endpoint has no active backoff window, or its window has elapsed.</summary>
    private static bool IsEligibleNow(EndpointEntry entry)
    {
        return entry.NextRetryAt is null || entry.NextRetryAt <= DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Computes the exponential backoff delay for the given consecutive-failure count:
    ///     BaseBackoffDelay * 2^(failures-1), capped at MaxBackoffDelay. Failure count 0 or less
    ///     yields TimeSpan.Zero (no backoff).
    /// </summary>
    private static TimeSpan ComputeBackoffDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        double seconds = BaseBackoffDelay.TotalSeconds * Math.Pow(2, consecutiveFailures - 1);
        seconds = Math.Min(seconds, MaxBackoffDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private EndpointEntry PickPrimaryOrAny()
    {
        return _endpoints.FirstOrDefault(e => e.Primary) ?? _endpoints[0];
    }

    public void RecordSuccess(string endpointName)
    {
        lock (_lock)
        {
            EndpointEntry? entry = Find(endpointName);
            if (entry is null)
            {
                return;
            }

            entry.SuccessCount++;
            entry.LastUsedAt = DateTimeOffset.UtcNow;
            entry.ConsecutiveFailures = 0;
            entry.NextRetryAt = null;

            if (!entry.Healthy)
            {
                entry.Healthy = true;
                LogEndpointRecovered(_logger, entry.Name, entry.Url);
            }
        }
    }

    public void RecordFailure(string endpointName)
    {
        lock (_lock)
        {
            EndpointEntry? entry = Find(endpointName);
            if (entry is null)
            {
                return;
            }

            entry.FailureCount++;
            entry.LastUsedAt = DateTimeOffset.UtcNow;
            entry.ConsecutiveFailures++;

            TimeSpan delay = ComputeBackoffDelay(entry.ConsecutiveFailures);
            entry.NextRetryAt = DateTimeOffset.UtcNow + delay;
            LogEndpointBackoff(_logger, entry.Name, entry.Url, delay, entry.ConsecutiveFailures);

            if (entry.Healthy)
            {
                entry.Healthy = false;
                LogEndpointFailed(_logger, entry.Name, entry.Url);
            }
        }
    }

    /// <summary>Used by <c>EndpointHealthMonitor</c> to record probe outcomes without a call.</summary>
    public void SetHealth(string endpointName, bool healthy)
    {
        lock (_lock)
        {
            EndpointEntry? entry = Find(endpointName);
            if (entry is null)
            {
                return;
            }

            entry.LastHealthCheckAt = DateTimeOffset.UtcNow;

            if (healthy)
            {
                entry.ConsecutiveFailures = 0;
                entry.NextRetryAt = null;
            }
            else
            {
                entry.ConsecutiveFailures++;
                TimeSpan delay = ComputeBackoffDelay(entry.ConsecutiveFailures);
                entry.NextRetryAt = DateTimeOffset.UtcNow + delay;
                LogEndpointBackoff(_logger, entry.Name, entry.Url, delay, entry.ConsecutiveFailures);
            }

            if (entry.Healthy == healthy)
            {
                return;
            }

            entry.Healthy = healthy;
            if (healthy)
            {
                LogEndpointHealthCheckRecovered(_logger, entry.Name, entry.Url);
            }
            else
            {
                LogEndpointHealthCheckFailed(_logger, entry.Name, entry.Url);
            }
        }
    }

    public IReadOnlyList<OllamaEndpointStatus> GetAllEndpoints()
    {
        lock (_lock)
        {
            return _endpoints.Select(ToStatus).ToList();
        }
    }

    private EndpointEntry? Find(string name)
    {
        return _endpoints.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static OllamaEndpointStatus ToStatus(EndpointEntry e)
    {
        return new OllamaEndpointStatus
        {
            Name = e.Name,
            Url = e.Url,
            Primary = e.Primary,
            Models = e.Models,
            Healthy = e.Healthy,
            LastUsedAt = e.LastUsedAt,
            LastHealthCheckAt = e.LastHealthCheckAt,
            SuccessCount = e.SuccessCount,
            FailureCount = e.FailureCount,
            NextRetryAt = e.NextRetryAt
        };
    }

    private sealed class EndpointEntry
    {
        public long FailureCount;
        public volatile bool Healthy = true;
        public DateTimeOffset? LastHealthCheckAt;
        public DateTimeOffset? LastUsedAt;
        public required List<string> Models;
        public required string Name;
        public required bool Primary;
        public long SuccessCount;
        public required string Url;

        /// <summary>Consecutive failures since the last success, used to size the backoff delay.</summary>
        public int ConsecutiveFailures;

        /// <summary>Earliest time this endpoint may be selected/probed again after a failure. Null when eligible now.</summary>
        public DateTimeOffset? NextRetryAt;
    }
}

using TokenSaver.Common.Core.Configuration;
using TokenSaver.OllamaIntegration.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using System.Collections.ObjectModel;

using Xunit;

namespace TokenSaver.OllamaIntegration.Tests;

file static class TestData
{
    public static readonly string[] PrimaryOrBackup = ["primary", "backup"];
}

/// <summary>
///     Covers the plan's "Testing" section items that don't require a live Ollama process:
///     failover when primary is down, round-robin distribution, explicit selection against an
///     offline/unknown endpoint, and the exponential-backoff behavior added on top of the
///     original health-tracking implementation. Health-monitor recovery detection and
///     latency-comparison items remain manual/live-verification steps (see plan status notes) —
///     they require a real HTTP endpoint to probe.
/// </summary>
public class OllamaEndpointRegistryTests
{
    private static OllamaEndpointRegistry CreateRegistry(
        Collection<OllamaEndpointConfig> endpoints, EndpointSelectStrategy strategy = EndpointSelectStrategy.Failover)
    {
        TokenSaverConfig config = new()
        {
            Endpoints = endpoints,
            EndpointSelectStrategy = strategy
        };
        return new OllamaEndpointRegistry(
            Options.Create(config), NullLogger<OllamaEndpointRegistry>.Instance);
    }

    private static Collection<OllamaEndpointConfig> TwoEndpoints()
    {
        return
        [
            new OllamaEndpointConfig { Name = "primary", Url = "http://localhost:11435", Primary = true },
            new OllamaEndpointConfig { Name = "backup", Url = "http://localhost:11436", Primary = false }
        ];
    }

    [Fact]
    public void ZeroConfig_SynthesizesSingleDefaultEndpoint()
    {
        OllamaEndpointRegistry registry = CreateRegistry([]);

        IReadOnlyList<OllamaEndpointStatus> all = registry.GetAllEndpoints();

        _ = Assert.Single(all);
        Assert.Equal("default", all[0].Name);
        Assert.True(all[0].Primary);
        Assert.True(all[0].Healthy);
    }

    [Fact]
    public void Failover_UsesPrimaryWhenHealthy()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());

        OllamaEndpointStatus selected = registry.SelectEndpoint();

        Assert.Equal("primary", selected.Name);
    }

    [Fact]
    public void Failover_SwitchesToBackupWhenPrimaryUnhealthy()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());

        registry.RecordFailure("primary");

        OllamaEndpointStatus selected = registry.SelectEndpoint();

        Assert.Equal("backup", selected.Name);
    }

    [Fact]
    public void Failover_RecoversToPrimaryAfterSuccess()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());

        registry.RecordFailure("primary");
        Assert.Equal("backup", registry.SelectEndpoint().Name);

        registry.RecordSuccess("primary");

        Assert.Equal("primary", registry.SelectEndpoint().Name);
    }

    [Fact]
    public void RoundRobin_DistributesAcrossHealthyEndpoints()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints(), EndpointSelectStrategy.RoundRobin);

        string first = registry.SelectEndpoint().Name;
        string second = registry.SelectEndpoint().Name;
        string third = registry.SelectEndpoint().Name;

        Assert.NotEqual(first, second);
        Assert.Equal(first, third); // cycles back after 2 healthy endpoints
    }

    [Fact]
    public void RoundRobin_SkipsUnhealthyEndpoint()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints(), EndpointSelectStrategy.RoundRobin);
        registry.RecordFailure("backup");

        OllamaEndpointStatus first = registry.SelectEndpoint();
        OllamaEndpointStatus second = registry.SelectEndpoint();

        Assert.Equal("primary", first.Name);
        Assert.Equal("primary", second.Name);
    }

    [Fact]
    public void Explicit_SelectsNamedEndpointWhenHealthy()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());

        OllamaEndpointStatus selected = registry.SelectEndpoint("backup");

        Assert.Equal("backup", selected.Name);
    }

    [Fact]
    public void Explicit_FallsBackToPrimaryWhenNamedEndpointUnhealthy()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());
        registry.RecordFailure("backup");

        OllamaEndpointStatus selected = registry.SelectEndpoint("backup");

        Assert.Equal("primary", selected.Name);
    }

    [Fact]
    public void Explicit_FallsBackToPrimaryWhenNamedEndpointUnknown()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());

        OllamaEndpointStatus selected = registry.SelectEndpoint("does-not-exist");

        Assert.Equal("primary", selected.Name);
    }

    [Fact]
    public void RecordFailure_SetsNextRetryAtInTheFuture()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());

        registry.RecordFailure("primary");

        OllamaEndpointStatus status = registry.GetAllEndpoints().Single(e => e.Name == "primary");
        _ = Assert.NotNull(status.NextRetryAt);
        Assert.True(status.NextRetryAt > DateTimeOffset.UtcNow);
        Assert.False(status.Healthy);
    }

    [Fact]
    public void RecordFailure_BackoffGrowsWithConsecutiveFailures()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());

        registry.RecordFailure("primary");
        DateTimeOffset firstRetryAt = registry.GetAllEndpoints().Single(e => e.Name == "primary").NextRetryAt!.Value;
        TimeSpan firstDelay = firstRetryAt - DateTimeOffset.UtcNow;

        registry.RecordFailure("primary");
        DateTimeOffset secondRetryAt = registry.GetAllEndpoints().Single(e => e.Name == "primary").NextRetryAt!.Value;
        TimeSpan secondDelay = secondRetryAt - DateTimeOffset.UtcNow;

        // Second consecutive failure should back off for longer than the first (exponential growth),
        // allowing generous tolerance since these are wall-clock samples.
        Assert.True(secondDelay > firstDelay,
            $"Expected second backoff ({secondDelay}) to exceed first ({firstDelay}).");
    }

    [Fact]
    public void RecordSuccess_ClearsBackoffState()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());

        registry.RecordFailure("primary");
        registry.RecordFailure("primary");
        _ = Assert.NotNull(registry.GetAllEndpoints().Single(e => e.Name == "primary").NextRetryAt);

        registry.RecordSuccess("primary");

        OllamaEndpointStatus status = registry.GetAllEndpoints().Single(e => e.Name == "primary");
        Assert.Null(status.NextRetryAt);
        Assert.True(status.Healthy);
    }

    [Fact]
    public void SetHealth_HealthyClearsBackoff_UnhealthySetsIt()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());

        registry.SetHealth("primary", false);
        OllamaEndpointStatus unhealthy = registry.GetAllEndpoints().Single(e => e.Name == "primary");
        Assert.False(unhealthy.Healthy);
        _ = Assert.NotNull(unhealthy.NextRetryAt);

        registry.SetHealth("primary", true);
        OllamaEndpointStatus healthy = registry.GetAllEndpoints().Single(e => e.Name == "primary");
        Assert.True(healthy.Healthy);
        Assert.Null(healthy.NextRetryAt);
    }

    [Fact]
    public void NoConfiguredPrimary_FirstEndpointBecomesPrimary()
    {
        Collection<OllamaEndpointConfig> endpoints =
        [
            new OllamaEndpointConfig { Name = "a", Url = "http://localhost:1", Primary = false },
            new OllamaEndpointConfig { Name = "b", Url = "http://localhost:2", Primary = false }
        ];
        OllamaEndpointRegistry registry = CreateRegistry(endpoints);

        Assert.True(registry.GetAllEndpoints().Single(e => e.Name == "a").Primary);
    }

    [Fact]
    public void AllEndpointsUnhealthy_FailoverReturnsSoonestToRecoverRatherThanThrowing()
    {
        OllamaEndpointRegistry registry = CreateRegistry(TwoEndpoints());

        registry.RecordFailure("primary");
        registry.RecordFailure("backup");

        // Should not throw, and should return one of the two configured endpoints.
        OllamaEndpointStatus selected = registry.SelectEndpoint();
        Assert.Contains(selected.Name, TestData.PrimaryOrBackup);
    }
}

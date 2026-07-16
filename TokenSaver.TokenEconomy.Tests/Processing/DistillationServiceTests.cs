using TokenSaver.Common.Core.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.TokenEconomy.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.Utilities.Services;

using Xunit;

namespace TokenSaver.TokenEconomy.Tests;

/// <summary>
///     Covers DistillationService's kill-switch/raw-fallback path, which is the one path
///     guaranteed to never call out to a real Ollama process — every other path (actual
///     compression) needs a live model and is out of scope for this unit-test-only suite, same
///     as OllamaEndpointRegistryTests' documented split between offline-testable and
///     live-verification behavior.
/// </summary>
public class DistillationServiceTests
{
    private static DistillationService CreateService(TokenSaverConfig config)
    {
        // Base address is never actually dialed in these tests — EnableDistill=false means
        // DistillAsync returns before any HttpClient call, and EnableSavingsTracking=false means
        // SavingsTracker.RecordAsync returns before ever opening TokenEconomyDbAccessor's DB.
        HttpClient httpClient = new() { BaseAddress = new Uri("http://localhost:1") };
        OllamaService ollama = new(httpClient, Options.Create(config), NullLogger<OllamaService>.Instance);
        ModelRouteResolver modelRoutes = new(Options.Create(config));
        SavingsTracker savingsTracker = new(new TokenEconomyDbAccessor(), Options.Create(config));
        return new DistillationService(ollama, modelRoutes, savingsTracker, Options.Create(config));
    }

    [Fact]
    public async Task DistillAsync_EmptyInput_ReturnsEmptyMarkerWithoutCallingOllama()
    {
        TokenSaverConfig config = new();
        DistillationService service = CreateService(config);

        DistillResult result = await service.DistillAsync("C:\\ws", "testTool", DistillRole.GenericCondense, "   ");

        Assert.Equal("(empty)", result.Summary);
        Assert.Equal(0, result.RawTokens);
        Assert.Equal(0, result.DistilledTokens);
        Assert.True(result.Distilled); // "empty" is treated as trivially "handled", not a failed distillation
    }

    [Fact]
    public async Task DistillAsync_DistillDisabled_ReturnsCappedRawTextAndMarksNotDistilled()
    {
        TokenSaverConfig config = new();
        config.TokenEconomy.EnableDistill = false;
        config.TokenEconomy.EnableSavingsTracking = false;
        config.TokenEconomy.RawTextCapChars = 10;
        DistillationService service = CreateService(config);

        DistillResult result = await service.DistillAsync("C:\\ws", "testTool", DistillRole.LogErrors, "0123456789ABCDEF");

        Assert.Equal("0123456789", result.Summary); // capped to RawTextCapChars
        Assert.False(result.Distilled);
        Assert.Equal(4, result.RawTokens); // ceil(16 chars / 4)
    }

    [Fact]
    public async Task DistillAsync_DistillDisabled_ShortInputUnderCap_ReturnedUnchanged()
    {
        TokenSaverConfig config = new();
        config.TokenEconomy.EnableDistill = false;
        config.TokenEconomy.EnableSavingsTracking = false;
        DistillationService service = CreateService(config);

        DistillResult result = await service.DistillAsync("C:\\ws", "testTool", DistillRole.GenericCondense, "short text");

        Assert.Equal("short text", result.Summary);
        Assert.False(result.Distilled);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("12345678", 2)]
    public void EstimateTokens_UsesCharsDividedByFourRoundedUp(string text, int expected)
    {
        TokenSaverConfig config = new();
        DistillationService service = CreateService(config);

        Assert.Equal(expected, service.EstimateTokens(text));
    }
}

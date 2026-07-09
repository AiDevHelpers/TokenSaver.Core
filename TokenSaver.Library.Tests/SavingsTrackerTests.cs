using TokenSaver.Library.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.Library.Models.TokenEconomy;
using TokenSaver.Library.Services;

using Microsoft.Extensions.Options;

using Xunit;

namespace TokenSaver.Library.Tests;

/// <summary>
///     Verdict() is pure logic (no I/O) and covered directly. RecordAsync/GetReportAsync/
///     GetOneAsync open a real SQLite file under a fresh temp directory per test — cheap and
///     fully deterministic, no Ollama dependency (SavingsTracker never calls Ollama itself).
/// </summary>
public class SavingsTrackerTests : IDisposable
{
    private readonly string _tempWorkspace = Path.Combine(Path.GetTempPath(), "lh-savings-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_tempWorkspace))
        {
            try
            {
                Directory.Delete(_tempWorkspace, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup; SQLite file handles can linger briefly on Windows
            }
        }
    }

    private static SavingsTracker CreateTracker(TokenSaverConfig? config = null)
    {
        return new SavingsTracker(new TokenEconomyDbAccessor(), Options.Create(config ?? new TokenSaverConfig()));
    }

    [Fact]
    public void Verdict_NotEnoughCalls_ReturnsNullWorthIt()
    {
        TokenSaverConfig config = new();
        config.TokenEconomy.WorthItMinCalls = 5;
        SavingsTracker tracker = CreateTracker(config);

        SavingsAggregate aggregate = new("runTests", 2, 1000, 200, 800, 80, 500);

        SavingsVerdict verdict = tracker.Verdict(aggregate);

        Assert.Null(verdict.WorthIt);
        Assert.Contains("not enough calls", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verdict_BelowMinPct_ReturnsFalse()
    {
        TokenSaverConfig config = new();
        config.TokenEconomy.WorthItMinCalls = 1;
        config.TokenEconomy.WorthItMinPct = 30;
        SavingsTracker tracker = CreateTracker(config);

        SavingsAggregate aggregate = new("runTests", 10, 1000, 900, 100, 10, 500); // 10% saved

        SavingsVerdict verdict = tracker.Verdict(aggregate);

        Assert.False(verdict.WorthIt);
        Assert.Contains("threshold", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verdict_AboveMaxLatency_ReturnsFalseEvenWithGoodSavings()
    {
        TokenSaverConfig config = new();
        config.TokenEconomy.WorthItMinCalls = 1;
        config.TokenEconomy.WorthItMinPct = 30;
        config.TokenEconomy.WorthItMaxMs = 4000;
        SavingsTracker tracker = CreateTracker(config);

        SavingsAggregate aggregate = new("runTests", 10, 1000, 100, 900, 90, 9000); // 90% saved but slow

        SavingsVerdict verdict = tracker.Verdict(aggregate);

        Assert.False(verdict.WorthIt);
        Assert.Contains("exceeds", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verdict_ClearsBothThresholds_ReturnsTrue()
    {
        TokenSaverConfig config = new();
        config.TokenEconomy.WorthItMinCalls = 1;
        config.TokenEconomy.WorthItMinPct = 30;
        config.TokenEconomy.WorthItMaxMs = 4000;
        SavingsTracker tracker = CreateTracker(config);

        SavingsAggregate aggregate = new("runTests", 10, 1000, 200, 800, 80, 1000);

        SavingsVerdict verdict = tracker.Verdict(aggregate);

        Assert.True(verdict.WorthIt);
    }

    [Fact]
    public async Task RecordAsync_TrackingDisabled_DoesNotPersistAnything()
    {
        TokenSaverConfig config = new();
        config.TokenEconomy.EnableSavingsTracking = false;
        SavingsTracker tracker = CreateTracker(config);

        await tracker.RecordAsync(_tempWorkspace, "runTests", "test_failure", false, 100, 20, 50);

        // With tracking disabled RecordAsync must return before ever opening the DB — if it had
        // opened one, GetReportAsync below (tracking re-enabled, fresh tracker) would still see
        // zero rows either way, so this test's real assertion is that no exception occurred and
        // no .tokensaver/index.db was created.
        Assert.False(File.Exists(WorkspaceIndexContextFactory.GetDbPath(_tempWorkspace)));
    }

    [Fact]
    public async Task RecordAsync_ThenGetReportAsync_AggregatesByTool()
    {
        SavingsTracker tracker = CreateTracker();

        await tracker.RecordAsync(_tempWorkspace, "runTests", "test_failure", false, 1000, 200, 500);
        await tracker.RecordAsync(_tempWorkspace, "runTests", "test_failure", false, 2000, 400, 700);
        await tracker.RecordAsync(_tempWorkspace, "containerLogs", "log_errors", true, 500, 100, 300);

        IReadOnlyList<SavingsAggregate> report = await tracker.GetReportAsync(_tempWorkspace, sinceDays: 7, groupByRole: false);

        Assert.Equal(2, report.Count);
        SavingsAggregate runTests = Assert.Single(report, r => r.Name == "runTests");
        Assert.Equal(2, runTests.Calls);
        Assert.Equal(3000, runTests.RawTokens);
        Assert.Equal(600, runTests.DistilledTokens);
        Assert.Equal(2400, runTests.SavedTokens);
        // Ranked by saved_tokens descending — runTests (2400 saved) should outrank containerLogs (400 saved).
        Assert.Equal("runTests", report[0].Name);
    }

    [Fact]
    public async Task GetOneAsync_NoMatchingRows_ReturnsEmptyWithZeroCalls()
    {
        SavingsTracker tracker = CreateTracker();

        SavingsAggregate result = await tracker.GetOneAsync(_tempWorkspace, "nonexistentTool", groupByRole: false, sinceDays: 30);

        Assert.Equal(0, result.Calls);
    }
}

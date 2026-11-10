using TokenSaver.Common.Core.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.OllamaIntegration.Tests;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.TokenEconomy.Services;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace TokenSaver.TokenEconomy.Tests;

/// <summary>
///     Covers how <see cref="SemanticIndexBackgroundTrigger" /> marks a background run complete or
///     failed. Each test waits for the run to leave the in-flight set under a deadline, so a run
///     that never completes fails the test instead of hanging it.
/// </summary>
public sealed class SemanticIndexBackgroundTriggerTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sibt-tests-" + Guid.NewGuid());
    private readonly TokenEconomyDbAccessor _db = new();

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup; SQLite handles can linger briefly on Windows
            }
        }
    }

    [Fact]
    public async Task EnsureStartedAsync_CompletedRun_ClearsRunningAndRecordsStats()
    {
        FakeIndexer indexer = new(_ => new RepoIndexStats(2, 5, 0, 0, TimeSpan.FromSeconds(1), 0));
        SemanticIndexBackgroundTrigger trigger = new(indexer, _db, NullLogger<SemanticIndexBackgroundTrigger>.Instance);

        SemanticIndexTriggerStatus status = await trigger.EnsureStartedAsync(_root, CancellationToken.None);

        Assert.Equal(SemanticIndexTriggerStatus.Started, status);
        await WaitUntilIdleAsync(trigger);
        Assert.True(trigger.TryGetLastStats(_root, out RepoIndexStats? stats));
        Assert.Equal(5, stats!.ChunksIndexed);
        Assert.Null(trigger.GetLastError(_root));
    }

    [Fact]
    public async Task EnsureStartedAsync_FailedRun_ClearsRunningAndRecordsError()
    {
        FakeIndexer indexer = new(_ => throw new InvalidOperationException("boom"));
        SemanticIndexBackgroundTrigger trigger = new(indexer, _db, NullLogger<SemanticIndexBackgroundTrigger>.Instance);

        _ = await trigger.EnsureStartedAsync(_root, CancellationToken.None);

        await WaitUntilIdleAsync(trigger);
        Assert.Equal("boom", trigger.GetLastError(_root));
        Assert.False(trigger.TryGetLastStats(_root, out _));
    }

    [Fact]
    public async Task EnsureStartedAsync_SecondCallSameProcess_DoesNotStartAnotherRun()
    {
        FakeIndexer indexer = new(_ => new RepoIndexStats(0, 0, 0, 0, TimeSpan.Zero, 0));
        SemanticIndexBackgroundTrigger trigger = new(indexer, _db, NullLogger<SemanticIndexBackgroundTrigger>.Instance);

        _ = await trigger.EnsureStartedAsync(_root, CancellationToken.None);
        await WaitUntilIdleAsync(trigger);
        SemanticIndexTriggerStatus second = await trigger.EnsureStartedAsync(_root, CancellationToken.None);

        Assert.Equal(SemanticIndexTriggerStatus.AlreadyIndexed, second);
        Assert.Equal(1, indexer.Calls);
    }

    [Fact]
    public async Task EnsureStartedAsync_RealIndexer_MarksCompleteWithStatsMatchingStoredChunks()
    {
        _ = Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Widget.cs"),
            "namespace Foo;\n\npublic sealed class Widget\n{\n    public void DoThing() { }\n}\n");
        FakeOllamaHandler handler = new();
        TokenSaverConfig config = new();
        OllamaService ollama = new(
            new HttpClient(handler) { BaseAddress = new Uri("http://fake-ollama.local") },
            Options.Create(config),
            NullLogger<OllamaService>.Instance);
        RepoIndexer indexer = new(new ChunkExtractor(), ollama, new ModelRouteResolver(Options.Create(config)),
            _db, Options.Create(config));
        SemanticIndexBackgroundTrigger trigger = new(indexer, _db, NullLogger<SemanticIndexBackgroundTrigger>.Instance);

        _ = await trigger.EnsureStartedAsync(_root, CancellationToken.None);
        await WaitUntilIdleAsync(trigger);

        Assert.Null(trigger.GetLastError(_root));
        Assert.True(trigger.TryGetLastStats(_root, out RepoIndexStats? stats));
        Assert.True(stats!.ChunksIndexed > 0);
        WorkspaceIndexContext ctx = await _db.GetDbAsync(_root, CancellationToken.None);
        Assert.Equal(stats.ChunksIndexed, ctx.Chunks.ToList().Count);
    }

    private async Task WaitUntilIdleAsync(SemanticIndexBackgroundTrigger trigger)
    {
        DateTime until = DateTime.UtcNow + Deadline;
        while (trigger.IsRunning(_root))
        {
            if (DateTime.UtcNow > until)
            {
                Assert.Fail("Background semantic index run did not clear its in-flight state before the deadline.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeIndexer(Func<string, RepoIndexStats> run) : IRepoIndexer
    {
        public int Calls { get; private set; }

        public Task<RepoIndexStats> IndexRepoAsync(
            string workspaceRoot, string[]? extensions = null, int concurrency = 4, CancellationToken ct = default)
        {
            Calls++;
            return Task.Run(() => run(workspaceRoot), ct);
        }
    }
}

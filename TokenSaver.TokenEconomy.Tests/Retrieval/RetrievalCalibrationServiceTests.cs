#pragma warning disable CA2000 // Test factories hand disposables to the code under test; each test is short-lived.
using TokenSaver.ContextHelper.Calibration;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.ContextHelper.Models.Calibration;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TokenSaver.OllamaIntegration.Tests;

using Xunit;

namespace TokenSaver.TokenEconomy.Tests;

/// <summary>
///     RetrievalCalibrationService's gap-detection/threshold-adjustment/persistence logic,
///     exercised offline via FakeOllamaHandler (same pattern as SemanticSearchServiceTests) — no
///     live Ollama, deterministic embeddings keyed off marker substrings in the seeded text.
///     Real embedding-quality behavior (does a late file actually score low because it's actually
///     irrelevant) is out of scope here, same split documented on OllamaEndpointRegistryTests —
///     see docs/context-builder-evaluation.md for the live/manual evaluation path.
/// </summary>
public sealed class RetrievalCalibrationServiceTests : IAsyncLifetime
{
    private readonly string _tempWorkspace = Path.Combine(Path.GetTempPath(), "lh-retrieval-calib-tests-" + Guid.NewGuid());
    private readonly FakeOllamaHandler _fakeHandler = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempWorkspace);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _fakeHandler.Dispose();
        if (Directory.Exists(_tempWorkspace))
        {
            try
            {
                Directory.Delete(_tempWorkspace, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup; file handles can linger briefly on Windows
            }
        }

        return Task.CompletedTask;
    }

    private RetrievalCalibrationService CreateService(ContextHelperConfig? config = null)
    {
        HttpClient httpClient = new(_fakeHandler) { BaseAddress = new Uri("http://fake-ollama.local") };
        return new RetrievalCalibrationService(
            httpClient, Options.Create(config ?? new ContextHelperConfig()), NullLogger<RetrievalCalibrationService>.Instance);
    }

    private void WriteFile(string relativePath, string content)
    {
        string abs = Path.Combine(_tempWorkspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    // Orthogonal unit vectors: MATCH aligns with the prompt vector, MISS is orthogonal to it.
    private void SeedOrthogonalEmbeddings()
    {
        _fakeHandler.EmbedResponder = input => input switch
        {
            _ when input.Contains("PROMPT", StringComparison.Ordinal) => [1f, 0f, 0f],
            _ when input.Contains("MATCH", StringComparison.Ordinal) => [1f, 0f, 0f],
            _ when input.Contains("MISS", StringComparison.Ordinal) => [0f, 1f, 0f],
            _ => [0f, 0f, 1f]
        };
    }

    [Fact]
    public void GetEffectiveThreshold_NoCalibrationFileYet_ReturnsBaseUnchanged()
    {
        RetrievalCalibrationService service = CreateService();

        double effective = service.GetEffectiveThreshold(_tempWorkspace, 0.35);

        Assert.Equal(0.35, effective);
    }

    [Fact]
    public void GetEffectiveThreshold_NullWorkspaceRoot_ReturnsBaseUnchanged()
    {
        RetrievalCalibrationService service = CreateService();

        double effective = service.GetEffectiveThreshold(null, 0.35);

        Assert.Equal(0.35, effective);
    }

    [Fact]
    public async Task CheckLateRequestAsync_NoPreviousPass_ReturnsNull()
    {
        RetrievalCalibrationService service = CreateService();

        RetrievalCalibrationCheckResult? result = await service.CheckLateRequestAsync(
            "session-1", _tempWorkspace, "PROMPT text", ["src/New.cs"]);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckLateRequestAsync_FileAlreadyInPreviousKeptSet_ReturnsNull()
    {
        RetrievalCalibrationService service = CreateService();
        service.RecordPass("session-1", "PROMPT text", ["src/Already.cs"], minKeptRelevance: 0.6);

        RetrievalCalibrationCheckResult? result = await service.CheckLateRequestAsync(
            "session-1", _tempWorkspace, "PROMPT text", ["src/Already.cs"]);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckLateRequestAsync_LateFileScoresFarBelowPreviousBar_FlagsFailure()
    {
        SeedOrthogonalEmbeddings();
        WriteFile("src/Missed.cs", "MISS content — unrelated to the prompt");

        RetrievalCalibrationService service = CreateService();
        service.RecordPass("session-1", "PROMPT text", ["src/Already.cs"], minKeptRelevance: 0.6);

        RetrievalCalibrationCheckResult? result = await service.CheckLateRequestAsync(
            "session-1", _tempWorkspace, "PROMPT text", ["src/Missed.cs"]);

        Assert.NotNull(result);
        RetrievalCalibrationOutcome outcome = Assert.Single(result.Outcomes);
        Assert.Equal("src/Missed.cs", outcome.LateFile);
        Assert.Equal(0.0, outcome.LateFileRelevance, precision: 3);
        Assert.True(outcome.WasFailure);
        Assert.True(result.AnyFailure);
    }

    [Fact]
    public async Task CheckLateRequestAsync_LateFileScoresCloseToPreviousBar_DoesNotFlagFailure()
    {
        SeedOrthogonalEmbeddings();
        WriteFile("src/Fine.cs", "MATCH content — closely related to the prompt");

        RetrievalCalibrationService service = CreateService();
        service.RecordPass("session-1", "PROMPT text", ["src/Already.cs"], minKeptRelevance: 0.6);

        RetrievalCalibrationCheckResult? result = await service.CheckLateRequestAsync(
            "session-1", _tempWorkspace, "PROMPT text", ["src/Fine.cs"]);

        Assert.NotNull(result);
        Assert.False(result.AnyFailure);
    }

    [Fact]
    public async Task CheckLateRequestAsync_MissingFileOnDisk_SkipsItAndReturnsNull()
    {
        RetrievalCalibrationService service = CreateService();
        service.RecordPass("session-1", "PROMPT text", ["src/Already.cs"], minKeptRelevance: 0.6);

        RetrievalCalibrationCheckResult? result = await service.CheckLateRequestAsync(
            "session-1", _tempWorkspace, "PROMPT text", ["src/DoesNotExist.cs"]);

        Assert.Null(result);
    }

    [Fact]
    public async Task RepeatedFailures_ReachingMinSamples_AdjustsThresholdDownAndPersists()
    {
        SeedOrthogonalEmbeddings();
        ContextHelperConfig config = new() { CalibrationMinSamplesBeforeAdjust = 3, CalibrationLearningRate = 0.02, CalibrationMaxAdjustment = 0.15 };
        RetrievalCalibrationService service = CreateService(config);

        for (int i = 0; i < 3; i++)
        {
            string file = $"src/Missed{i}.cs";
            WriteFile(file, "MISS content — unrelated to the prompt");
            service.RecordPass("session-1", "PROMPT text", ["src/Already.cs"], minKeptRelevance: 0.6);
            RetrievalCalibrationCheckResult? result = await service.CheckLateRequestAsync(
                "session-1", _tempWorkspace, "PROMPT text", [file]);
            Assert.NotNull(result);
        }

        RetrievalCalibrationState state = service.GetStats(_tempWorkspace);
        Assert.Equal(-0.02, state.ThresholdAdjustment, precision: 3);
        Assert.Equal(3, state.TotalSamples);
        Assert.Equal(3, state.TotalFailures);

        double effective = service.GetEffectiveThreshold(_tempWorkspace, 0.35);
        Assert.Equal(0.33, effective, precision: 3);
    }

    [Fact]
    public async Task ThresholdAdjustment_NeverExceedsConfiguredMax()
    {
        SeedOrthogonalEmbeddings();
        ContextHelperConfig config = new() { CalibrationMinSamplesBeforeAdjust = 1, CalibrationLearningRate = 0.1, CalibrationMaxAdjustment = 0.15 };
        RetrievalCalibrationService service = CreateService(config);

        for (int i = 0; i < 10; i++)
        {
            string file = $"src/Missed{i}.cs";
            WriteFile(file, "MISS content — unrelated to the prompt");
            service.RecordPass("session-1", "PROMPT text", ["src/Already.cs"], minKeptRelevance: 0.6);
            _ = await service.CheckLateRequestAsync("session-1", _tempWorkspace, "PROMPT text", [file]);
        }

        RetrievalCalibrationState state = service.GetStats(_tempWorkspace);
        Assert.Equal(-0.15, state.ThresholdAdjustment, precision: 3);
    }

    [Fact]
    public async Task ConsecutivePasses_ReachingMinSamples_RecoversThresholdTowardBaseline()
    {
        SeedOrthogonalEmbeddings();
        ContextHelperConfig config = new() { CalibrationMinSamplesBeforeAdjust = 1, CalibrationLearningRate = 0.1, CalibrationMaxAdjustment = 0.15 };
        RetrievalCalibrationService service = CreateService(config);

        // One failure drives the adjustment negative.
        WriteFile("src/Missed.cs", "MISS content — unrelated to the prompt");
        service.RecordPass("session-1", "PROMPT text", ["src/Already.cs"], minKeptRelevance: 0.6);
        _ = await service.CheckLateRequestAsync("session-1", _tempWorkspace, "PROMPT text", ["src/Missed.cs"]);
        Assert.Equal(-0.1, service.GetStats(_tempWorkspace).ThresholdAdjustment, precision: 3);

        // A clean turn (late file scores fine) should start recovering it back toward 0.
        WriteFile("src/Fine.cs", "MATCH content — closely related to the prompt");
        service.RecordPass("session-1", "PROMPT text", ["src/Already.cs"], minKeptRelevance: 0.6);
        _ = await service.CheckLateRequestAsync("session-1", _tempWorkspace, "PROMPT text", ["src/Fine.cs"]);

        RetrievalCalibrationState state = service.GetStats(_tempWorkspace);
        Assert.Equal(-0.05, state.ThresholdAdjustment, precision: 3);
    }

    [Fact]
    public async Task AppendsOutcomeToJsonlFile()
    {
        SeedOrthogonalEmbeddings();
        RetrievalCalibrationService service = CreateService();
        WriteFile("src/Missed.cs", "MISS content — unrelated to the prompt");
        service.RecordPass("session-1", "PROMPT text", ["src/Already.cs"], minKeptRelevance: 0.6);

        _ = await service.CheckLateRequestAsync("session-1", _tempWorkspace, "PROMPT text", ["src/Missed.cs"]);

        string outcomesPath = Path.Combine(_tempWorkspace, ".tokensaver", "retrieval-calibration-outcomes.jsonl");
        Assert.True(File.Exists(outcomesPath));
        Assert.Single(await File.ReadAllLinesAsync(outcomesPath));
    }

    [Fact]
    public void RecordPass_WithoutSessionId_IsNoOp()
    {
        RetrievalCalibrationService service = CreateService();

        // Should not throw, and leaves nothing to compare against for any session.
        service.RecordPass(null, "PROMPT text", ["src/Already.cs"], minKeptRelevance: 0.6);
    }
}

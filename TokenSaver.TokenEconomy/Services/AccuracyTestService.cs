using TokenSaver.TokenEconomy.Models.Accuracy;

using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TokenSaver.TokenEconomy.Services;

/// <summary>
///     Runs <see cref="TestCase" /> objects against the local Ollama model and scores
///     the responses using pattern matching plus a lightweight model-as-judge step.
/// </summary>
public sealed partial class AccuracyTestService(OllamaService ollama, ILogger<AccuracyTestService> logger)
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Running test {Id} [{Category}] from {File}")]
    private static partial void LogRunningTest(ILogger logger, string id, string category, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model call failed for test {Id}")]
    private static partial void LogModelCallFailed(ILogger logger, Exception ex, string id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Test {Id}: score={Score:F2} passed={Passed} latency={Ms}ms")]
    private static partial void LogTestResult(ILogger logger, string id, double score, bool passed, int ms);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Judge call failed: {Msg}")]
    private static partial void LogJudgeCallFailed(ILogger logger, string msg);

    // A score ≥ this threshold counts as a pass
    private const double PassThreshold = 0.7;

    // Judge is only run for tests that fail pattern matching — keeps costs low
    private const bool UseJudge = true;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Runs a single test case and returns the scored result.
    /// </summary>
    /// <param name="test">The test case to run.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The result of the test.</returns>
    public async Task<TestResult> RunTestAsync(TestCase test, CancellationToken ct = default)
    {

        LogRunningTest(logger, test.Id, test.Category, test.SourceFile);

        Stopwatch sw = Stopwatch.StartNew();
        string response;
        try
        {
            response = await ollama.CallModelAsync(test.Prompt, ct);
        }
        catch (Exception ex)
        {
            LogModelCallFailed(logger, ex, test.Id);
            return new TestResult
            {
                TestCase = test,
                Response = "",
                Score = 0,
                Passed = false,
                Notes = $"Model error: {ex.Message}",
                Latency = sw.Elapsed
            };
        }

        sw.Stop();

        TestResult result = Score(test, response, sw.Elapsed);

        // Run model-as-judge only for borderline / failing tests
        if (UseJudge && !result.Passed)
        {
            string? verdict = await JudgeAsync(test, response, ct);
            result = result with { JudgeVerdict = verdict };

            // If judge says YES but patterns failed, bump score slightly and note it
            if (verdict == "YES")
            {
                double bumped = Math.Min(1.0, result.Score + 0.2);
                result = result with
                {
                    Score = bumped,
                    Passed = bumped >= PassThreshold,
                    Notes = (result.Notes ?? "") + " [judge: YES]"
                };
            }
        }

        LogTestResult(logger, test.Id, result.Score, result.Passed, (int)result.Latency.TotalMilliseconds);

        return result;
    }

    /// <summary>
    ///     Runs all test cases and aggregates results into a <see cref="TestReport" />.
    /// </summary>
    public async Task<TestReport> RunAllAsync(
        IEnumerable<TestCase> cases,
        string workspaceRoot,
        CancellationToken ct = default)
    {
        List<TestResult> results = [];
        foreach (TestCase tc in cases)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunTestAsync(tc, ct));
        }

        return BuildReport(results, workspaceRoot);
    }

    // ── Scoring ────────────────────────────────────────────────────────────────

    private static TestResult Score(TestCase test, string response, TimeSpan latency)
    {
        List<string> passed = [];
        List<string> failed = [];
        List<string> forbidden = [];

        foreach (string pattern in test.ExpectedPatterns)
        {
            if (Regex.IsMatch(response, pattern, RegexOptions.IgnoreCase))
            {
                passed.Add(pattern);
            }
            else
            {
                failed.Add(pattern);
            }
        }

        foreach (string pattern in test.ForbiddenPatterns)
        {
            if (Regex.IsMatch(response, pattern, RegexOptions.IgnoreCase))
            {
                forbidden.Add(pattern);
            }
        }

        int requiredCount = test.ExpectedPatterns.Count;
        double patternScore = requiredCount == 0 ? 1.0 : (double)passed.Count / requiredCount;
        double forbiddenRatio = test.ForbiddenPatterns.Count == 0
            ? 0.0
            : (double)forbidden.Count / test.ForbiddenPatterns.Count;

        double score = patternScore * (1.0 - forbiddenRatio);
        bool didPass = score >= PassThreshold && forbidden.Count == 0;

        string? notes = failed.Count > 0
            ? $"Missing patterns: {string.Join(", ", failed)}"
            : null;
        if (forbidden.Count > 0)
        {
            notes = (notes ?? "") + $" Forbidden triggered: {string.Join(", ", forbidden)}";
        }

        return new TestResult
        {
            TestCase = test,
            Response = response,
            Score = score,
            Passed = didPass,
            PassedPatterns = [.. passed],
            FailedPatterns = [.. failed],
            TriggeredForbidden = [.. forbidden],
            Latency = latency,
            Notes = notes?.Trim()
        };
    }

    // ── Judge ──────────────────────────────────────────────────────────────────

    private async Task<string?> JudgeAsync(TestCase test, string response, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return "NO";
        }

        string judgePrompt =
            $"You are a code review judge. Answer only YES or NO.\n\n" +
            $"Task:\n{test.Prompt}\n\n" +
            $"Response:\n{response}\n\n" +
            $"Does the response correctly and completely address the task? Reply YES or NO only.";

        try
        {
            string verdict = (await ollama.CallModelAsync(judgePrompt, ct)).Trim().ToUpperInvariant();
            return verdict.StartsWith("YES", StringComparison.Ordinal) ? "YES" : verdict.StartsWith("NO", StringComparison.Ordinal) ? "NO" : null;
        }
        catch (Exception ex)
        {
            LogJudgeCallFailed(logger, ex.Message);
            return null;
        }
    }

    // ── Report building ────────────────────────────────────────────────────────

    private static TestReport BuildReport(List<TestResult> results, string workspaceRoot)
    {
        if (results.Count == 0)
        {
            return new TestReport { WorkspaceRoot = workspaceRoot };
        }

        int passed = results.Count(r => r.Passed);
        double avgScore = results.Average(r => r.Score);
        TimeSpan avgLatency = TimeSpan.FromMilliseconds(results.Average(r => r.Latency.TotalMilliseconds));

        // Per-category stats
        Dictionary<string, CategoryStats> byCategory = results
            .GroupBy(r => r.TestCase.Category)
            .ToDictionary(
                g => g.Key,
                g => new CategoryStats(
                    g.Count(),
                    g.Count(r => r.Passed),
                    g.Average(r => r.Score)));

        // Patterns that most commonly failed
        Dictionary<string, int> failureCounts = results
            .SelectMany(r => r.FailedPatterns)
            .GroupBy(p => p)
            .ToDictionary(g => g.Key, g => g.Count());

        return new TestReport
        {
            WorkspaceRoot = workspaceRoot,
            TotalTests = results.Count,
            PassedTests = passed,
            AverageScore = avgScore,
            AverageLatency = avgLatency,
            Results = [.. results],
            ByCategory = byCategory,
            CommonFailures = failureCounts
        };
    }
}

namespace TokenSaver.Common.Core.Contracts;

/// <summary>
///     Contract for running tests in IDEs and via command-line.
///     Supports running individual tests, all tests, by namespace, or re-running failed tests.
///     See docs/test-runner-contract.md.
/// </summary>
public interface ITestRunner
{
    /// <summary>
    ///     Run tests with flexible filtering options.
    ///     Can run a specific test, all tests, tests in a namespace, tests in a project, or re-run failed tests.
    /// </summary>
    /// <param name="ideType">IDE type: "VsCode" or "VisualStudio2026"</param>
    /// <param name="testName">Optional: specific test to run (e.g., "MyProject.Tests.MyTestClass.MyTestMethod")</param>
    /// <param name="testNamespace">Optional: run all tests in a namespace (e.g., "MyProject.Tests.ConfigTests")</param>
    /// <param name="projectPath">Optional: run tests in a specific project; if null and no other filters, runs all tests</param>
    /// <param name="failedOnly">If true, re-run only tests that failed in the last test run</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Test execution result</returns>
    Task<TestExecutionResult> RunTestsAsync(
        string ideType,
        string? testName = null,
        string? testNamespace = null,
        string? projectPath = null,
        bool failedOnly = false,
        CancellationToken ct = default);

    /// <summary>
    ///     Get the last test run results.
    /// </summary>
    /// <param name="ideType">IDE type: "VsCode" or "VisualStudio2026"</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Last test execution result or null if no tests have run</returns>
    Task<TestExecutionResult?> GetLastTestResultAsync(
        string ideType,
        CancellationToken ct = default);
}

public sealed record TestExecutionResult(
    bool Success,
    int TotalTests,
    int PassedTests,
    int FailedTests,
    int SkippedTests,
    long ElapsedMilliseconds,
    IReadOnlyList<TestFailure> Failures,
    string Message);

public sealed record TestFailure(
    string TestName,
    string FailureMessage,
    string StackTrace,
    string? ExpectedValue = null,
    string? ActualValue = null);

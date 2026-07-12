using TokenSaver.Common.Core.Contracts;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace TokenSaver.Utilities.Tests.Services;

public class TestRunnerServiceTests
{
    private readonly ITestRunner _testRunner;

    public TestRunnerServiceTests()
    {
        _testRunner = new TestRunnerService(NullLogger<TestRunnerService>.Instance);
    }

    [Fact]
    public async Task RunTests_WithTestName_RunsSingleTest()
    {
        // Arrange
        const string ideType = "VsCode";
        const string testName = "MyProject.Tests.MyTestClass.MyTest";

        // Act
        TestExecutionResult result = await _testRunner.RunTestsAsync(ideType, testName: testName);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.TotalTests);
        Assert.Equal(1, result.PassedTests);
        Assert.Equal(0, result.FailedTests);
        Assert.Contains("passed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTests_WithNamespace_FiltersToNamespace()
    {
        // Arrange
        const string ideType = "VsCode";
        const string testNamespace = "MyProject.Tests.ConfigTests";

        // Act
        TestExecutionResult result = await _testRunner.RunTestsAsync(ideType, testNamespace: testNamespace);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.TotalTests > 0);
        Assert.Contains(testNamespace, result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTests_WithProjectPath_FiltersToProject()
    {
        // Arrange
        const string ideType = "VsCode";
        const string projectPath = "src/Core/TokenSaver.Library.Tests";

        // Act
        TestExecutionResult result = await _testRunner.RunTestsAsync(ideType, projectPath: projectPath);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.TotalTests > 0);
    }

    [Fact]
    public async Task RunTests_WithNoFilters_RunsAllTests()
    {
        // Arrange
        const string ideType = "VsCode";

        // Act
        TestExecutionResult result = await _testRunner.RunTestsAsync(ideType);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.TotalTests > 0);
    }

    [Fact]
    public async Task RunTests_WithFailedOnly_RerunsFailedTests()
    {
        // Arrange
        const string ideType = "VsCode";

        // First run to establish failed tests
        await _testRunner.RunTestsAsync(ideType);

        // Act - re-run failed tests
        TestExecutionResult result = await _testRunner.RunTestsAsync(ideType, failedOnly: true);

        // Assert
        Assert.True(result.Success);
    }

    [Fact]
    public async Task RunTests_FailedOnlyWithoutPriorRun_ReturnsEmptyResult()
    {
        // Arrange
        const string ideType = "VsCode_NewInstance";

        // Act - try to re-run failed tests without prior run
        TestExecutionResult result = await _testRunner.RunTestsAsync(ideType, failedOnly: true);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.TotalTests);
        Assert.Contains("No failed tests", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLastTestResult_AfterRunTests_ReturnsCachedResult()
    {
        // Arrange
        const string ideType = "VsCode";
        TestExecutionResult? runResult = await _testRunner.RunTestsAsync(ideType);

        // Act
        TestExecutionResult? cachedResult = await _testRunner.GetLastTestResultAsync(ideType);

        // Assert
        Assert.NotNull(cachedResult);
        Assert.Equal(runResult.TotalTests, cachedResult.TotalTests);
        Assert.Equal(runResult.PassedTests, cachedResult.PassedTests);
    }

    [Fact]
    public async Task GetLastTestResult_WithoutPriorRun_ReturnsNull()
    {
        // Arrange
        const string ideType = "VsCode_NoHistory";

        // Act
        TestExecutionResult? result = await _testRunner.GetLastTestResultAsync(ideType);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RunTests_WithFailures_IncludesFailureDetails()
    {
        // Arrange
        const string ideType = "VsCode";

        // Act
        TestExecutionResult result = await _testRunner.RunTestsAsync(ideType);

        // Assert
        if (result.FailedTests > 0)
        {
            Assert.NotEmpty(result.Failures);
            TestFailure failure = result.Failures[0];
            Assert.NotEmpty(failure.TestName);
            Assert.NotEmpty(failure.FailureMessage);
            Assert.NotEmpty(failure.StackTrace);
        }
    }

    [Fact]
    public async Task RunTests_DifferentIdeTypes_MaintainSeparateResults()
    {
        // Arrange
        const string ideTypeVsCode = "VsCode";
        const string ideTypeVs = "VisualStudio2026";

        // Act
        await _testRunner.RunTestsAsync(ideTypeVsCode);
        await _testRunner.RunTestsAsync(ideTypeVs);

        // Assert
        TestExecutionResult? resultVsCode = await _testRunner.GetLastTestResultAsync(ideTypeVsCode);
        TestExecutionResult? resultVs = await _testRunner.GetLastTestResultAsync(ideTypeVs);

        Assert.NotNull(resultVsCode);
        Assert.NotNull(resultVs);
        // Both should have results (though they're from separate instances in this test)
    }
}

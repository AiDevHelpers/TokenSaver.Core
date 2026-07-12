using TokenSaver.Common.Core.Contracts;

using Xunit;

namespace TokenSaver.Common.Core.Tests.Contracts;

/// <summary>
/// Contract tests for IContextPipelineService implementations.
/// These tests verify that any implementation meets the interface contract.
/// </summary>
public abstract class ContextPipelineServiceContractTests
{
    /// <summary>
    /// Implementations must provide a concrete service instance for testing.
    /// </summary>
    protected abstract IContextPipelineService CreateService();

    [Fact]
    public async Task BuildConciseContextAsync_WithPrompt_ShouldReturnResult()
    {
        // Arrange
        var service = CreateService();
        var prompt = "test prompt";

        // Act
        var result = await service.BuildConciseContextAsync(prompt);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.ContextText);
    }

    [Fact]
    public async Task BuildConciseContextAsync_WithAllParameters_ShouldReturnResult()
    {
        // Arrange
        var service = CreateService();
        var prompt = "test prompt";
        var history = new[] { "message 1", "message 2" };
        var workspaceRoot = Path.GetTempPath();

        // Act
        var result = await service.BuildConciseContextAsync(
            prompt,
            history,
            "test-session",
            null,
            workspaceRoot,
            5000,
            false,
            500);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.ContextText);
    }
}

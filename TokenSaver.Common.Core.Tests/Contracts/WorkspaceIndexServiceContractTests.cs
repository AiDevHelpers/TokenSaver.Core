using TokenSaver.Common.Core.Contracts;

using Xunit;

namespace TokenSaver.Common.Core.Tests.Contracts;

/// <summary>
/// Contract tests for IWorkspaceIndexService implementations.
/// These tests verify that any implementation meets the interface contract.
/// </summary>
public abstract class WorkspaceIndexServiceContractTests
{
    /// <summary>
    /// Implementations must provide a concrete service instance for testing.
    /// </summary>
    protected abstract IWorkspaceIndexService CreateService();

    [Fact]
    public async Task IndexWorkspaceAsync_ShouldReturnIndexStats()
    {
        // Arrange
        var service = CreateService();
        var testRoot = Path.GetTempPath();

        // Act
        var stats = await service.IndexWorkspaceAsync(testRoot);

        // Assert
        Assert.NotNull(stats);
        Assert.True(stats.FilesIndexed >= 0, "FilesIndexed should be non-negative");
        Assert.True(stats.SymbolsIndexed >= 0, "SymbolsIndexed should be non-negative");
        Assert.True(stats.Elapsed >= TimeSpan.Zero, "Elapsed should be non-negative");
    }

    [Fact]
    public async Task IndexFileAsync_ShouldReturnIndexStats()
    {
        // Arrange
        var service = CreateService();
        var testRoot = Path.GetTempPath();
        var relPath = "test.cs";

        // Act
        var stats = await service.IndexFileAsync(testRoot, relPath);

        // Assert
        Assert.NotNull(stats);
        Assert.True(stats.FilesIndexed >= 0, "FilesIndexed should be non-negative");
    }
}

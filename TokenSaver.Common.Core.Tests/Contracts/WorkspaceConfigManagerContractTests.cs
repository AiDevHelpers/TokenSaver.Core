using TokenSaver.Common.Core.Contracts;

using Xunit;

namespace TokenSaver.Common.Core.Tests.Contracts;

/// <summary>
/// Contract tests for IWorkspaceConfigManager implementations.
/// These tests verify that any implementation meets the interface contract.
/// </summary>
public abstract class WorkspaceConfigManagerContractTests
{
    /// <summary>
    /// Implementations must provide a concrete service instance for testing.
    /// </summary>
    protected abstract IWorkspaceConfigManager CreateService();

    [Fact]
    public void GetWorkspaceRoot_ShouldReturnStringOrNull()
    {
        // Arrange
        var service = CreateService();

        // Act
        var root = service.GetWorkspaceRoot();

        // Assert - should return a string or null, not throw
        _ = root; // Just verify it doesn't throw
    }

    [Fact]
    public void SetWorkspaceRoot_ShouldAcceptValidPath()
    {
        // Arrange
        var service = CreateService();
        var testPath = Path.GetTempPath();

        // Act & Assert - should not throw
        service.SetWorkspaceRoot(testPath);
    }
}

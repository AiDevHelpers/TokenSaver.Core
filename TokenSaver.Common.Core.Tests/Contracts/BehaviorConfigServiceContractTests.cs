using TokenSaver.Common.Core.Contracts;

using Xunit;

namespace TokenSaver.Common.Core.Tests.Contracts;

/// <summary>
/// Contract tests for IBehaviorConfigService implementations.
/// These tests verify that any implementation meets the interface contract.
/// </summary>
public abstract class BehaviorConfigServiceContractTests
{
    /// <summary>
    /// Implementations must provide a concrete service instance for testing.
    /// </summary>
    protected abstract IBehaviorConfigService CreateService();

    [Fact]
    public void GetConfig_ShouldReturnConfigForWorkspaceRoot()
    {
        // Arrange
        var service = CreateService();
        var testRoot = Path.GetTempPath();

        // Act
        var config = service.GetConfig(testRoot);

        // Assert
        Assert.NotNull(config);
    }

    [Fact]
    public void Set_ShouldUpdateConfigViaMutate()
    {
        // Arrange
        var service = CreateService();
        var testRoot = Path.GetTempPath();
        bool mutateWasCalled = false;

        // Act
        service.Set(testRoot, ConfigScope.Local, cfg =>
        {
            mutateWasCalled = true;
            return cfg;
        });

        // Assert
        Assert.True(mutateWasCalled, "Mutate function should have been called");
    }
}

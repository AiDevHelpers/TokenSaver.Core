using TokenSaver.Config;

using Xunit;

namespace TokenSaver.Config.Tests;

public sealed class TokenSaverBehaviorConfigTests
{
    [Fact]
    public void Merge_LocalValueWinsOverGlobal()
    {
        TokenSaverBehaviorConfig local = new() { AddTokenSaverToGitignore = true };
        TokenSaverBehaviorConfig global = new() { AddTokenSaverToGitignore = false };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        Assert.True(merged.AddTokenSaverToGitignore);
    }

    [Fact]
    public void Merge_FallsBackToGlobalWhenLocalUnset()
    {
        TokenSaverBehaviorConfig local = new(); // unset
        TokenSaverBehaviorConfig global = new() { AddTokenSaverToGitignore = true };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        Assert.True(merged.AddTokenSaverToGitignore);
    }

    [Fact]
    public void Merge_NullBothSides_ReturnsUnset()
    {
        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(null, null);

        Assert.Null(merged.AddTokenSaverToGitignore);
    }

    [Fact]
    public void Merge_LocalFalseWinsOverGlobalTrue()
    {
        TokenSaverBehaviorConfig local = new() { AddTokenSaverToGitignore = false };
        TokenSaverBehaviorConfig global = new() { AddTokenSaverToGitignore = true };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        Assert.False(merged.AddTokenSaverToGitignore);
    }
}

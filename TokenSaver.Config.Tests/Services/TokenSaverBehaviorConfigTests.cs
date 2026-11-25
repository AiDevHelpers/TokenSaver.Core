using AiDevTools.TokenSaver.Config;

using Xunit;

namespace AiDevTools.TokenSaver.Config.Tests;

public sealed class TokenSaverBehaviorConfigTests
{
    [Fact]
    public void Merge_RepoHelper_LocalValueWinsOverGlobal()
    {
        TokenSaverBehaviorConfig local = new() { RepoHelper = new RepoHelperBehaviorConfig { AddTokenSaverToGitignore = true } };
        TokenSaverBehaviorConfig global = new() { RepoHelper = new RepoHelperBehaviorConfig { AddTokenSaverToGitignore = false } };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        Assert.True(merged.RepoHelper?.AddTokenSaverToGitignore);
    }

    [Fact]
    public void Merge_RepoHelper_FallsBackToGlobalWhenLocalUnset()
    {
        TokenSaverBehaviorConfig local = new(); // unset
        TokenSaverBehaviorConfig global = new() { RepoHelper = new RepoHelperBehaviorConfig { AddTokenSaverToGitignore = true } };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        Assert.True(merged.RepoHelper?.AddTokenSaverToGitignore);
    }

    [Fact]
    public void Merge_RepoHelper_NullBothSides_ReturnsNull()
    {
        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(null, null);

        Assert.Null(merged.RepoHelper);
    }

    [Fact]
    public void Merge_RepoHelper_LocalFalseWinsOverGlobalTrue()
    {
        TokenSaverBehaviorConfig local = new() { RepoHelper = new RepoHelperBehaviorConfig { AddTokenSaverToGitignore = false } };
        TokenSaverBehaviorConfig global = new() { RepoHelper = new RepoHelperBehaviorConfig { AddTokenSaverToGitignore = true } };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        Assert.False(merged.RepoHelper?.AddTokenSaverToGitignore);
    }

    [Fact]
    public void Merge_RepoHelper_OptimizeInstructions_LocalWinsOverGlobalPerProperty()
    {
        TokenSaverBehaviorConfig local = new()
        {
            RepoHelper = new RepoHelperBehaviorConfig { OptimizeInstructions = false },
        };
        TokenSaverBehaviorConfig global = new()
        {
            RepoHelper = new RepoHelperBehaviorConfig { AddTokenSaverToGitignore = true, OptimizeInstructions = true },
        };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        Assert.False(merged.RepoHelper?.OptimizeInstructions);
        // AddTokenSaverToGitignore is unset on the local side — falls back to global per-property.
        Assert.True(merged.RepoHelper?.AddTokenSaverToGitignore);
    }

    [Fact]
    public void Defaults_RepoHelper_OptimizeInstructionsTrue_AddTokenSaverToGitignoreUnset()
    {
        TokenSaverBehaviorConfig defaults = TokenSaverBehaviorConfig.Defaults();

        Assert.NotNull(defaults.RepoHelper);
        Assert.True(defaults.RepoHelper!.OptimizeInstructions);
        Assert.True(defaults.RepoHelper.AutoSplitOversizedInstructions);
        Assert.Null(defaults.RepoHelper.AddTokenSaverToGitignore);
    }

    [Fact]
    public void Merge_AgentConfig_PerFamilyOverrideWinsOverGlobalAgentConfigDefault()
    {
        TokenSaverBehaviorConfig local = new()
        {
            AgentConfig = new AgentBehaviorConfig
            {
                EnablePromptCaching = true,
                Anthropic = new AnthropicAgentConfig { EnablePromptCaching = false },
            },
        };
        TokenSaverBehaviorConfig global = new()
        {
            AgentConfig = new AgentBehaviorConfig { EnablePromptCaching = true },
        };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        // Top-level agentConfig.enablePromptCaching stays true (both sides agree),
        // but the Anthropic-specific override (false) is preserved distinctly.
        Assert.True(merged.AgentConfig?.EnablePromptCaching);
        Assert.False(merged.AgentConfig?.Anthropic?.EnablePromptCaching);
    }

    [Fact]
    public void Defaults_AgentConfig_PopulatesAllFamilySections()
    {
        TokenSaverBehaviorConfig defaults = TokenSaverBehaviorConfig.Defaults();

        Assert.NotNull(defaults.AgentConfig);
        Assert.True(defaults.AgentConfig!.EnablePromptCaching);
        Assert.NotNull(defaults.AgentConfig.Anthropic);
        Assert.NotNull(defaults.AgentConfig.DeepSeek);
        Assert.NotNull(defaults.AgentConfig.Qwen);
        Assert.NotNull(defaults.AgentConfig.OpenSource);
        Assert.Equal("q8_0", defaults.AgentConfig.OpenSource!.KvCacheQuantization);
    }

    [Fact]
    public void Defaults_LazyContext_SeedsClaudeCodeOverrideTrue()
    {
        TokenSaverBehaviorConfig defaults = TokenSaverBehaviorConfig.Defaults();

        Assert.NotNull(defaults.LazyContext);
        Assert.False(defaults.LazyContext!.DefaultWillAskForMissingContext);
        Assert.NotNull(defaults.LazyContext.Overrides);
        Assert.True(defaults.LazyContext.Overrides!["claude-code"]);
    }

    [Fact]
    public void Merge_LazyContext_LocalDefaultWinsOverGlobalDefault()
    {
        TokenSaverBehaviorConfig local = new()
        {
            LazyContext = new LazyContextBehaviorConfig { DefaultWillAskForMissingContext = true },
        };
        TokenSaverBehaviorConfig global = new()
        {
            LazyContext = new LazyContextBehaviorConfig { DefaultWillAskForMissingContext = false },
        };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        Assert.True(merged.LazyContext?.DefaultWillAskForMissingContext);
    }

    [Fact]
    public void Merge_LazyContext_OverridesDictionary_LocalKeyWinsPerEntry_GlobalKeysStillPresent()
    {
        TokenSaverBehaviorConfig local = new()
        {
            LazyContext = new LazyContextBehaviorConfig
            {
                Overrides = new Dictionary<string, bool> { ["claude-code"] = false },
            },
        };
        TokenSaverBehaviorConfig global = new()
        {
            LazyContext = new LazyContextBehaviorConfig
            {
                Overrides = new Dictionary<string, bool>
                {
                    ["claude-code"] = true,
                    ["my-custom-agent"] = true,
                },
            },
        };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        // local's entry for a key present on both sides wins...
        Assert.False(merged.LazyContext?.Overrides?["claude-code"]);
        // ...but a key only present in global still survives the merge (not a whole-dict replace).
        Assert.True(merged.LazyContext?.Overrides?["my-custom-agent"]);
    }

    [Fact]
    public void Merge_LazyContext_OverridesKeyLookup_IsCaseInsensitive()
    {
        TokenSaverBehaviorConfig local = new()
        {
            LazyContext = new LazyContextBehaviorConfig
            {
                Overrides = new Dictionary<string, bool> { ["Claude-Code"] = true },
            },
        };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, null);

        Assert.True(merged.LazyContext?.Overrides?["claude-code"]);
        Assert.True(merged.LazyContext?.Overrides?["CLAUDE-CODE"]);
    }

    [Fact]
    public void Merge_LazyContext_NullBothSides_ReturnsNull()
    {
        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(new TokenSaverBehaviorConfig(), new TokenSaverBehaviorConfig());

        Assert.Null(merged.LazyContext);
    }

    [Fact]
    public void Defaults_Context_DebugIsFalse()
    {
        TokenSaverBehaviorConfig defaults = TokenSaverBehaviorConfig.Defaults();

        Assert.NotNull(defaults.Context);
        Assert.False(defaults.Context!.Debug);
    }

    [Fact]
    public void Merge_Context_DebugLocalTrueWinsOverGlobalFalse()
    {
        TokenSaverBehaviorConfig local = new()
        {
            Context = new ContextBehaviorConfig { Debug = true },
        };
        TokenSaverBehaviorConfig global = new()
        {
            Context = new ContextBehaviorConfig { Debug = false },
        };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        Assert.True(merged.Context?.Debug);
    }

    [Fact]
    public void Merge_Context_DebugFallsBackToGlobalWhenLocalUnset()
    {
        TokenSaverBehaviorConfig local = new() { Context = new ContextBehaviorConfig() };
        TokenSaverBehaviorConfig global = new() { Context = new ContextBehaviorConfig { Debug = true } };

        TokenSaverBehaviorConfig merged = TokenSaverBehaviorConfig.Merge(local, global);

        Assert.True(merged.Context?.Debug);
    }
}

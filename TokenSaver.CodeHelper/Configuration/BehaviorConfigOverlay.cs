using Microsoft.Extensions.Options;

using TokenSaver.Config;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.ContextHelper.Configuration;

namespace TokenSaver.CodeHelper.Configuration;

/// <summary>
///     Overlays <c>.tokensaver/config.json</c> (local, merged over <c>%ProgramData%\TokenSaver\config.json</c>
///     global — see <see cref="BehaviorConfigService" />) onto the appsettings-bound
///     <see cref="CodeHelperConfig" />/<see cref="TokenEconomyConfig" />/
///     <see cref="ContextHelperConfig" /> singletons, mutating them in place. Every consumer of
///     these config classes holds a direct reference to the same <c>IOptions&lt;T&gt;.Value</c>
///     instance, so an in-place mutation is visible everywhere immediately — no need to switch
///     call sites to <c>IOptionsMonitor&lt;T&gt;</c>.
///     Baselines are captured once at construction (before any overlay is applied) so that
///     removing a behavior-config override reverts to the original appsettings-derived default
///     rather than getting stuck on the last-applied override.
/// </summary>
public sealed class BehaviorConfigOverlay
{
    private readonly BehaviorConfigService _behaviorConfig;
    private readonly CodeHelperConfig _codeHelperConfig;
    private readonly TokenEconomyConfig _tokenEconomyConfig;
    private readonly ContextHelperConfig _contextHelperConfig;
    private readonly IOptions<TokenSaverConfig> _tokenSaverOptions;
    private readonly CodeHelperBaseline _codeHelperBaseline;
    private readonly TokenEconomyBaseline _tokenEconomyBaseline;
    private readonly ContextHelperBaseline _contextHelperBaseline;

    public BehaviorConfigOverlay(
        BehaviorConfigService behaviorConfig,
        IOptions<CodeHelperConfig> codeHelperOptions,
        IOptions<TokenSaverConfig> tokenSaverOptions,
        IOptions<ContextHelperConfig> contextHelperOptions)
    {
        _behaviorConfig = behaviorConfig;
        _codeHelperConfig = codeHelperOptions.Value;
        _tokenEconomyConfig = tokenSaverOptions.Value.TokenEconomy;
        _contextHelperConfig = contextHelperOptions.Value;
        // Kept as IOptions<T> rather than copying .Value.WorkspaceRoot once here: WorkspaceRoot
        // is commonly still empty at construction time (see WorkspaceRootSessionResolver — it's
        // resolved lazily on a session's first tool call, not at startup) and is mutated in place
        // on this same TokenSaverConfig instance once resolved. Reading tokenSaverOptions.Value
        // fresh on every Apply() call picks that up; a captured string snapshot would have stayed
        // permanently empty and made every Apply() call (both the startup one and every later
        // ConfigChanged-triggered one) a silent no-op for the process's entire lifetime.
        _tokenSaverOptions = tokenSaverOptions;
        _codeHelperBaseline = CodeHelperBaseline.Capture(_codeHelperConfig);
        _tokenEconomyBaseline = TokenEconomyBaseline.Capture(_tokenEconomyConfig);
        _contextHelperBaseline = ContextHelperBaseline.Capture(_contextHelperConfig);

        _behaviorConfig.ConfigChanged += (_, _) => Apply();
    }

    /// <summary>Applies the current merged behavior config onto the target instances. Safe to call repeatedly.</summary>
    public void Apply()
    {
        string workspaceRoot = _tokenSaverOptions.Value.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return;
        }

        TokenSaverBehaviorConfig b = _behaviorConfig.GetConfig(workspaceRoot);
        CodeHelperBehaviorConfig? ch = b.CodeHelper;
        TokenEconomyBehaviorConfig? te = b.TokenEconomy;
        ContextBehaviorConfig? ctx = b.Context;

        _codeHelperConfig.AutoEnabled = ch?.AutoEnabled ?? _codeHelperBaseline.AutoEnabled;
        _codeHelperConfig.RequireAnchorMatch = ch?.RequireAnchorMatch ?? _codeHelperBaseline.RequireAnchorMatch;
        _codeHelperConfig.CanDoThreshold = ch?.Ownership?.CanDoThreshold ?? _codeHelperBaseline.CanDoThreshold;
        _codeHelperConfig.DeferThreshold = ch?.Ownership?.DeferThreshold ?? _codeHelperBaseline.DeferThreshold;
        _codeHelperConfig.ConfidenceThreshold = ch?.Planner?.ConfidenceThreshold ?? _codeHelperBaseline.ConfidenceThreshold;
        _codeHelperConfig.ModelCallThreshold = ch?.Planner?.ModelCallThreshold ?? _codeHelperBaseline.ModelCallThreshold;
        _codeHelperConfig.WasteThreshold = ch?.Calibration?.WasteThreshold ?? _codeHelperBaseline.WasteThreshold;
        _codeHelperConfig.LearningRate = ch?.Calibration?.LearningRate ?? _codeHelperBaseline.LearningRate;

        _tokenEconomyConfig.EnableDistill = te?.EnableDistill ?? _tokenEconomyBaseline.EnableDistill;
        _tokenEconomyConfig.EnableTerse = te?.AllowTerse ?? _tokenEconomyBaseline.EnableTerse;
        _tokenEconomyConfig.EnableRawRetention = te?.EnableRawRetention ?? _tokenEconomyBaseline.EnableRawRetention;
        _tokenEconomyConfig.EnableSavingsTracking = te?.EnableSavingsTracking ?? _tokenEconomyBaseline.EnableSavingsTracking;
        _tokenEconomyConfig.EnableSummaryEmbedding = te?.EnableSummaryEmbedding ?? _tokenEconomyBaseline.EnableSummaryEmbedding;
        _tokenEconomyConfig.Terse = te?.TerseByDefault ?? _tokenEconomyBaseline.Terse;

        _contextHelperConfig.AutoWarmOnStartup = ctx?.AutoWarmOnStartup ?? _contextHelperBaseline.AutoWarmOnStartup;
        _contextHelperConfig.AutoIntercept = ctx?.AutoIntercept ?? _contextHelperBaseline.AutoIntercept;
        _contextHelperConfig.ContextSizeLimit = ctx?.ContextSizeLimit ?? _contextHelperBaseline.ContextSizeLimit;
        _contextHelperConfig.TokenBudgetMargin = ctx?.TokenBudgetMargin ?? _contextHelperBaseline.TokenBudgetMargin;
        _contextHelperConfig.SemanticThreshold = ctx?.SemanticThreshold ?? _contextHelperBaseline.SemanticThreshold;
        _contextHelperConfig.Debug = ctx?.Debug ?? _contextHelperBaseline.Debug;
    }

    private readonly record struct CodeHelperBaseline(
        bool AutoEnabled,
        bool RequireAnchorMatch,
        double CanDoThreshold,
        double DeferThreshold,
        double ConfidenceThreshold,
        double ModelCallThreshold,
        double WasteThreshold,
        double LearningRate)
    {
        public static CodeHelperBaseline Capture(CodeHelperConfig c) => new(
            c.AutoEnabled, c.RequireAnchorMatch, c.CanDoThreshold, c.DeferThreshold,
            c.ConfidenceThreshold, c.ModelCallThreshold, c.WasteThreshold, c.LearningRate);
    }

    private readonly record struct TokenEconomyBaseline(
        bool EnableDistill,
        bool EnableTerse,
        bool EnableRawRetention,
        bool EnableSavingsTracking,
        bool EnableSummaryEmbedding,
        bool Terse)
    {
        public static TokenEconomyBaseline Capture(TokenEconomyConfig c) => new(
            c.EnableDistill, c.EnableTerse, c.EnableRawRetention, c.EnableSavingsTracking,
            c.EnableSummaryEmbedding, c.Terse);
    }

    private readonly record struct ContextHelperBaseline(
        bool AutoWarmOnStartup,
        bool AutoIntercept,
        int ContextSizeLimit,
        double TokenBudgetMargin,
        double SemanticThreshold,
        bool Debug)
    {
        public static ContextHelperBaseline Capture(ContextHelperConfig c) => new(
            c.AutoWarmOnStartup, c.AutoIntercept, c.ContextSizeLimit, c.TokenBudgetMargin, c.SemanticThreshold, c.Debug);
    }
}

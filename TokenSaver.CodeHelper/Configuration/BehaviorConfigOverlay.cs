using Microsoft.Extensions.Options;

using TokenSaver.Config;
using TokenSaver.Library.Configuration;

namespace TokenSaver.CodeHelper.Configuration;

/// <summary>
///     Overlays <c>.tokensaver/config.json</c> (local, merged over <c>%ProgramData%\TokenSaver\config.json</c>
///     global — see <see cref="BehaviorConfigService" />) onto the appsettings-bound
///     <see cref="CodeHelperConfig" />/<see cref="TokenEconomyConfig" /> singletons, mutating them
///     in place. Every consumer of these two config classes holds a direct reference to the same
///     <c>IOptions&lt;T&gt;.Value</c> instance, so an in-place mutation is visible everywhere
///     immediately — no need to switch call sites to <c>IOptionsMonitor&lt;T&gt;</c>.
///     Baselines are captured once at construction (before any overlay is applied) so that
///     removing a behavior-config override reverts to the original appsettings-derived default
///     rather than getting stuck on the last-applied override.
/// </summary>
public sealed class BehaviorConfigOverlay
{
    private readonly BehaviorConfigService _behaviorConfig;
    private readonly CodeHelperConfig _codeHelperConfig;
    private readonly TokenEconomyConfig _tokenEconomyConfig;
    private readonly string _workspaceRoot;
    private readonly CodeHelperBaseline _codeHelperBaseline;
    private readonly TokenEconomyBaseline _tokenEconomyBaseline;

    public BehaviorConfigOverlay(
        BehaviorConfigService behaviorConfig,
        IOptions<CodeHelperConfig> codeHelperOptions,
        IOptions<TokenSaverConfig> tokenSaverOptions)
    {
        _behaviorConfig = behaviorConfig;
        _codeHelperConfig = codeHelperOptions.Value;
        _tokenEconomyConfig = tokenSaverOptions.Value.TokenEconomy;
        _workspaceRoot = tokenSaverOptions.Value.WorkspaceRoot;
        _codeHelperBaseline = CodeHelperBaseline.Capture(_codeHelperConfig);
        _tokenEconomyBaseline = TokenEconomyBaseline.Capture(_tokenEconomyConfig);

        _behaviorConfig.ConfigChanged += (_, _) => Apply();
    }

    /// <summary>Applies the current merged behavior config onto the target instances. Safe to call repeatedly.</summary>
    public void Apply()
    {
        if (string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            return;
        }

        TokenSaverBehaviorConfig b = _behaviorConfig.GetConfig(_workspaceRoot);

        _codeHelperConfig.AutoEnabled = b.AutoCodeHelperEnabled ?? _codeHelperBaseline.AutoEnabled;
        _codeHelperConfig.RequireAnchorMatch = b.RequireAnchorMatch ?? _codeHelperBaseline.RequireAnchorMatch;
        _codeHelperConfig.CanDoThreshold = b.CanDoThreshold ?? _codeHelperBaseline.CanDoThreshold;
        _codeHelperConfig.DeferThreshold = b.DeferThreshold ?? _codeHelperBaseline.DeferThreshold;
        _codeHelperConfig.ConfidenceThreshold = b.ConfidenceThreshold ?? _codeHelperBaseline.ConfidenceThreshold;
        _codeHelperConfig.ModelCallThreshold = b.ModelCallThreshold ?? _codeHelperBaseline.ModelCallThreshold;
        _codeHelperConfig.WasteThreshold = b.WasteThreshold ?? _codeHelperBaseline.WasteThreshold;
        _codeHelperConfig.LearningRate = b.LearningRate ?? _codeHelperBaseline.LearningRate;

        _tokenEconomyConfig.EnableDistill = b.EnableDistill ?? _tokenEconomyBaseline.EnableDistill;
        _tokenEconomyConfig.EnableTerse = b.EnableTerse ?? _tokenEconomyBaseline.EnableTerse;
        _tokenEconomyConfig.EnableRawRetention = b.EnableRawRetention ?? _tokenEconomyBaseline.EnableRawRetention;
        _tokenEconomyConfig.EnableSavingsTracking = b.EnableSavingsTracking ?? _tokenEconomyBaseline.EnableSavingsTracking;
        _tokenEconomyConfig.EnableSummaryEmbedding = b.EnableSummaryEmbedding ?? _tokenEconomyBaseline.EnableSummaryEmbedding;
        _tokenEconomyConfig.Terse = b.Terse ?? _tokenEconomyBaseline.Terse;
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
}

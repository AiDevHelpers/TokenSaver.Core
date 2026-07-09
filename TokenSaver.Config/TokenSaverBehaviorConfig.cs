using System.Text.Json.Serialization;

namespace TokenSaver.Config;

/// <summary>
///     Behavior configuration persisted in <c>.tokensaver/config.json</c> (per-workspace) and,
///     optionally, <c>%ProgramData%\TokenSaver\config.json</c> (machine-global). Every property is
///     nullable so that "unset" is distinguishable from an explicit value — this drives both the
///     local-over-global merge (a non-null local value always wins) and the "ask if not set"
///     behaviors (a null value means the user has never answered).
///     A <see langword="record" /> so callers can read-modify-write with <c>with</c> expressions.
/// </summary>
public sealed record TokenSaverBehaviorConfig
{
    /// <summary>
    ///     Whether <c>.tokensaver/</c> should be added to the workspace <c>.gitignore</c>.
    ///     <c>null</c> means the user has never been asked — tools surface a pending prompt in
    ///     that case. <c>true</c>/<c>false</c> records their answer and suppresses the prompt.
    /// </summary>
    [JsonPropertyName("addTokenSaverToGitignore")]
    public bool? AddTokenSaverToGitignore { get; init; }

    // ── CodeHelper auto-workflow overrides ──────────────────────────────────────
    // Overlaid onto CodeHelperConfig at startup and on every config-file change — see
    // TokenSaver.CodeHelper.Configuration.BehaviorConfigOverlay. Null = keep the
    // appsettings-bound default.

    /// <summary>Overrides <c>CodeHelperConfig.AutoEnabled</c>.</summary>
    [JsonPropertyName("autoCodeHelperEnabled")]
    public bool? AutoCodeHelperEnabled { get; init; }

    /// <summary>Overrides <c>CodeHelperConfig.RequireAnchorMatch</c>.</summary>
    [JsonPropertyName("requireAnchorMatch")]
    public bool? RequireAnchorMatch { get; init; }

    /// <summary>Overrides <c>CodeHelperConfig.CanDoThreshold</c>.</summary>
    [JsonPropertyName("canDoThreshold")]
    public double? CanDoThreshold { get; init; }

    /// <summary>Overrides <c>CodeHelperConfig.DeferThreshold</c>.</summary>
    [JsonPropertyName("deferThreshold")]
    public double? DeferThreshold { get; init; }

    /// <summary>Overrides <c>CodeHelperConfig.ConfidenceThreshold</c>.</summary>
    [JsonPropertyName("confidenceThreshold")]
    public double? ConfidenceThreshold { get; init; }

    /// <summary>Overrides <c>CodeHelperConfig.ModelCallThreshold</c>.</summary>
    [JsonPropertyName("modelCallThreshold")]
    public double? ModelCallThreshold { get; init; }

    /// <summary>Overrides <c>CodeHelperConfig.WasteThreshold</c>.</summary>
    [JsonPropertyName("wasteThreshold")]
    public double? WasteThreshold { get; init; }

    /// <summary>Overrides <c>CodeHelperConfig.LearningRate</c>.</summary>
    [JsonPropertyName("learningRate")]
    public double? LearningRate { get; init; }

    // ── TokenEconomy overrides ──────────────────────────────────────────────────
    // Overlaid onto TokenSaverConfig.TokenEconomy the same way.

    /// <summary>Overrides <c>TokenEconomyConfig.EnableDistill</c>.</summary>
    [JsonPropertyName("enableDistill")]
    public bool? EnableDistill { get; init; }

    /// <summary>Overrides <c>TokenEconomyConfig.EnableTerse</c>.</summary>
    [JsonPropertyName("enableTerse")]
    public bool? EnableTerse { get; init; }

    /// <summary>Overrides <c>TokenEconomyConfig.EnableRawRetention</c>.</summary>
    [JsonPropertyName("enableRawRetention")]
    public bool? EnableRawRetention { get; init; }

    /// <summary>Overrides <c>TokenEconomyConfig.EnableSavingsTracking</c>.</summary>
    [JsonPropertyName("enableSavingsTracking")]
    public bool? EnableSavingsTracking { get; init; }

    /// <summary>Overrides <c>TokenEconomyConfig.EnableSummaryEmbedding</c>.</summary>
    [JsonPropertyName("enableSummaryEmbedding")]
    public bool? EnableSummaryEmbedding { get; init; }

    /// <summary>Overrides <c>TokenEconomyConfig.Terse</c>.</summary>
    [JsonPropertyName("terse")]
    public bool? Terse { get; init; }

    /// <summary>
    ///     Produces a merged config where each property is taken from <paramref name="local" />
    ///     when that property is set (non-null), otherwise from <paramref name="global" />.
    ///     Either side may be null (treated as an all-unset config).
    /// </summary>
    public static TokenSaverBehaviorConfig Merge(TokenSaverBehaviorConfig? local, TokenSaverBehaviorConfig? global)
    {
        return new TokenSaverBehaviorConfig
        {
            AddTokenSaverToGitignore = local?.AddTokenSaverToGitignore ?? global?.AddTokenSaverToGitignore,
            AutoCodeHelperEnabled = local?.AutoCodeHelperEnabled ?? global?.AutoCodeHelperEnabled,
            RequireAnchorMatch = local?.RequireAnchorMatch ?? global?.RequireAnchorMatch,
            CanDoThreshold = local?.CanDoThreshold ?? global?.CanDoThreshold,
            DeferThreshold = local?.DeferThreshold ?? global?.DeferThreshold,
            ConfidenceThreshold = local?.ConfidenceThreshold ?? global?.ConfidenceThreshold,
            ModelCallThreshold = local?.ModelCallThreshold ?? global?.ModelCallThreshold,
            WasteThreshold = local?.WasteThreshold ?? global?.WasteThreshold,
            LearningRate = local?.LearningRate ?? global?.LearningRate,
            EnableDistill = local?.EnableDistill ?? global?.EnableDistill,
            EnableTerse = local?.EnableTerse ?? global?.EnableTerse,
            EnableRawRetention = local?.EnableRawRetention ?? global?.EnableRawRetention,
            EnableSavingsTracking = local?.EnableSavingsTracking ?? global?.EnableSavingsTracking,
            EnableSummaryEmbedding = local?.EnableSummaryEmbedding ?? global?.EnableSummaryEmbedding,
            Terse = local?.Terse ?? global?.Terse,
        };
    }
}

/// <summary>Scope a behavior value is written to.</summary>
public enum ConfigScope
{
    /// <summary><c>.tokensaver/config.json</c> under the workspace root.</summary>
    Local,

    /// <summary><c>%ProgramData%\TokenSaver\config.json</c> — machine-global, shared across workspaces.</summary>
    Global,
}

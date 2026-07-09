using System.Text.Json.Serialization;

namespace TokenSaver.Config;

/// <summary>
///     Behavior configuration persisted in <c>.tokensaver/config.json</c> (per-workspace) and,
///     optionally, <c>%ProgramData%\TokenSaver\config.json</c> (machine-global). Every leaf
///     property is nullable so that "unset" is distinguishable from an explicit value — this
///     drives both the local-over-global merge (a non-null local value always wins) and the
///     "ask if not set" behaviors (a null value means the user has never answered). Grouped into
///     nested sections (<see cref="CodeHelper" />/<see cref="TokenEconomy" />) so the on-disk file
///     reads as documentation of what each setting belongs to, instead of one flat bag of ~15
///     same-looking booleans/doubles.
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

    /// <summary>CodeHelper auto-workflow overrides — see <see cref="CodeHelperBehaviorConfig" />.</summary>
    [JsonPropertyName("codeHelper")]
    public CodeHelperBehaviorConfig? CodeHelper { get; init; }

    /// <summary>TokenEconomy overrides — see <see cref="TokenEconomyBehaviorConfig" />.</summary>
    [JsonPropertyName("tokenEconomy")]
    public TokenEconomyBehaviorConfig? TokenEconomy { get; init; }

    /// <summary>Context-pipeline overrides — see <see cref="ContextBehaviorConfig" />.</summary>
    [JsonPropertyName("context")]
    public ContextBehaviorConfig? Context { get; init; }

    /// <summary>
    ///     Per-provider context-reduction overrides (prompt/context caching, context editing,
    ///     KV-cache tuning) — see <see cref="AgentBehaviorConfig" />.
    /// </summary>
    [JsonPropertyName("agentConfig")]
    public AgentBehaviorConfig? AgentConfig { get; init; }

    /// <summary>
    ///     Per-client lazy-context overrides — whether a calling client can be trusted to re-ask
    ///     for content TokenSaver didn't send up front. See <see cref="LazyContextBehaviorConfig" />.
    /// </summary>
    [JsonPropertyName("lazyContext")]
    public LazyContextBehaviorConfig? LazyContext { get; init; }

    /// <summary>
    ///     Produces a merged config where each leaf property is taken from
    ///     <paramref name="local" /> when set (non-null), otherwise from
    ///     <paramref name="global" />. Either side, or either nested section, may be null (treated
    ///     as an all-unset config/section).
    /// </summary>
    public static TokenSaverBehaviorConfig Merge(TokenSaverBehaviorConfig? local, TokenSaverBehaviorConfig? global)
    {
        return new TokenSaverBehaviorConfig
        {
            AddTokenSaverToGitignore = local?.AddTokenSaverToGitignore ?? global?.AddTokenSaverToGitignore,
            CodeHelper = CodeHelperBehaviorConfig.Merge(local?.CodeHelper, global?.CodeHelper),
            TokenEconomy = TokenEconomyBehaviorConfig.Merge(local?.TokenEconomy, global?.TokenEconomy),
            Context = ContextBehaviorConfig.Merge(local?.Context, global?.Context),
            AgentConfig = AgentBehaviorConfig.Merge(local?.AgentConfig, global?.AgentConfig),
            LazyContext = LazyContextBehaviorConfig.Merge(local?.LazyContext, global?.LazyContext),
        };
    }

    /// <summary>
    ///     Every CodeHelper/TokenEconomy/Context/AgentConfig/LazyContext field populated with its
    ///     real appsettings-bound default (mirrors <c>CodeHelperConfig</c>/<c>TokenEconomyConfig</c>/
    ///     <c>ContextHelperConfig</c>'s own <c>= ...</c> defaults), so a freshly-created config
    ///     file on disk is self-documenting instead of an empty <c>{}</c>.
    ///     <see cref="AddTokenSaverToGitignore" /> is left <see langword="null" /> — it's a
    ///     stateful sentinel ("never asked"), not a tunable default.
    /// </summary>
    public static TokenSaverBehaviorConfig Defaults() => new()
    {
        CodeHelper = CodeHelperBehaviorConfig.Defaults(),
        TokenEconomy = TokenEconomyBehaviorConfig.Defaults(),
        Context = ContextBehaviorConfig.Defaults(),
        AgentConfig = AgentBehaviorConfig.Defaults(),
        LazyContext = LazyContextBehaviorConfig.Defaults(),
    };
}

/// <summary>
///     Controls whether TokenSaver treats a calling client as able to re-ask for content it
///     didn't send up front (<see cref="ContextFormat.WillAskForMissingContext" />) — i.e.
///     whether it's safe to send summaries/partial files and let the client re-fetch full
///     content on demand, versus needing to send everything eagerly because nothing not sent in
///     the response is ever seen again.
///     <para>
///     This is NOT the same thing as MCP tool-schema deferral (lazy-loading which tools are
///     visible to an agent) — TokenSaver has no control over that; it only controls the shape and
///     completeness of the context blob it hands back in one MCP response. "Lazy" here means
///     "safe to summarize/omit, because the caller will come back for more."
///     </para>
///     <para>
///     Three of the four built-in <c>TokenSaver.AgentHelpers.OpenSource</c> client adapters
///     already hardcode this per measured behavior: Cursor writes tool output to temp files and
///     re-reads selectively (<see langword="true" /> — genuinely lazy-safe); Copilot silently
///     drops content above ~8K tokens per response and never asks again; Continue hard-errors
///     above ~12K. Neither Copilot nor Continue can be made lazy no matter what this config says —
///     <see cref="LazyContextResolver" /> ignores overrides for names matching those adapters'
///     <c>KnownClientNames</c> and always returns their adapter's hardcoded value, so a
///     misconfigured override can't cause silent data loss for a client that provably won't
///     re-ask. For every other client name (no adapter exists yet — e.g. Claude Code, a custom
///     in-house agent) TokenSaver has no measured data, so the safe default is <c>false</c>
///     (eager/complete) until a user records here that they've verified their client re-fetches
///     missing content, matching <c>ContextFormat.WillAskForMissingContext</c>'s own documented
///     default of "off unless proven safe."
///     </para>
/// </summary>
public sealed record LazyContextBehaviorConfig
{
    /// <summary>
    ///     Default <see cref="ContextFormat.WillAskForMissingContext" /> applied to any client
    ///     name with no adapter and no entry in <see cref="Overrides" />. <see langword="false" />
    ///     (eager/complete context) unless a user has verified otherwise for their setup.
    /// </summary>
    [JsonPropertyName("defaultWillAskForMissingContext")]
    public bool? DefaultWillAskForMissingContext { get; init; }

    /// <summary>
    ///     Per-client-name overrides, keyed by the same <c>ClientName</c> value
    ///     <see cref="TokenSaver.AgentHelpers.Core.Models.CallerInfo" /> reports (from the MCP
    ///     <c>initialize</c> handshake — e.g. <c>"claude-code"</c>, <c>"cursor"</c>,
    ///     <c>"my-custom-agent"</c>), case-insensitive. A key present here always wins over
    ///     <see cref="DefaultWillAskForMissingContext" /> for that client — <i>except</i> for
    ///     client names already claimed by a hardcoded <c>OpenSource</c> client adapter (Cursor,
    ///     Copilot, Continue), where the adapter's measured value always wins instead; see the
    ///     type-level remarks for why that guard exists.
    /// </summary>
    [JsonPropertyName("overrides")]
    public IReadOnlyDictionary<string, bool>? Overrides { get; init; }

    public static LazyContextBehaviorConfig? Merge(LazyContextBehaviorConfig? local, LazyContextBehaviorConfig? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        // Overrides dictionary: local entries win per-key, falling back to global entries for
        // any client name only present in the global file — not a whole-dictionary replace.
        Dictionary<string, bool>? mergedOverrides = null;
        if (global?.Overrides is { Count: > 0 } g)
        {
            mergedOverrides = new Dictionary<string, bool>(g, StringComparer.OrdinalIgnoreCase);
        }

        if (local?.Overrides is { Count: > 0 } l)
        {
            mergedOverrides ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, bool> kv in l)
            {
                mergedOverrides[kv.Key] = kv.Value;
            }
        }

        return new LazyContextBehaviorConfig
        {
            DefaultWillAskForMissingContext = local?.DefaultWillAskForMissingContext ?? global?.DefaultWillAskForMissingContext,
            Overrides = mergedOverrides,
        };
    }

    public static LazyContextBehaviorConfig Defaults() => new()
    {
        DefaultWillAskForMissingContext = false,
        Overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            // Seeded documentation of the one client TokenSaver has no hardcoded adapter for yet
            // but that's known from real-world use to re-read files on demand, same as Cursor.
            ["claude-code"] = true,
        },
    };
}

/// <summary>
///     Overlaid onto <c>ContextHelperConfig</c> at startup and on every config-file change — see
///     <c>TokenSaver.CodeHelper.Configuration.BehaviorConfigOverlay</c>. Null = keep the
///     appsettings-bound default. Covers BuildConciseContext/ContextHygiene tuning, chiefly
///     whether the server warms its context cache automatically at startup.
/// </summary>
public sealed record ContextBehaviorConfig
{
    /// <summary>
    ///     Whether the server builds an initial concise-context cache automatically at startup.
    ///     On by default. Overrides <c>ContextHelperConfig.AutoWarmOnStartup</c>.
    /// </summary>
    [JsonPropertyName("autoWarmOnStartup")]
    public bool? AutoWarmOnStartup { get; init; }

    /// <summary>
    ///     Whether every prompt-bearing MCP tool call gets auto-cleaned (dedup/log-strip/
    ///     code-block-collapse) by <c>ContextInterceptMiddleware</c> before forwarding. On by
    ///     default — independent of <c>codeHelper.autoEnabled</c>, which gates that same
    ///     middleware's separate draft-model interception step (off by default). Overrides
    ///     <c>ContextHelperConfig.AutoIntercept</c>.
    /// </summary>
    [JsonPropertyName("autoIntercept")]
    public bool? AutoIntercept { get; init; }

    /// <summary>Token budget a built context is capped at. Overrides <c>ContextHelperConfig.ContextSizeLimit</c>.</summary>
    [JsonPropertyName("contextSizeLimit")]
    public int? ContextSizeLimit { get; init; }

    /// <summary>Fraction of <see cref="ContextSizeLimit" /> the pipeline targets before trimming further. Overrides <c>ContextHelperConfig.TokenBudgetMargin</c>.</summary>
    [JsonPropertyName("tokenBudgetMargin")]
    public double? TokenBudgetMargin { get; init; }

    /// <summary>Minimum embedding-similarity score for a chunk to survive the semantic relevance filter. Overrides <c>ContextHelperConfig.SemanticThreshold</c>.</summary>
    [JsonPropertyName("semanticThreshold")]
    public double? SemanticThreshold { get; init; }

    /// <summary>
    ///     When true, the cleaning pipeline saves the raw (pre-pipeline) and cleaned
    ///     (post-pipeline) context text to <c>.tokensaver/debug/</c> for every run and logs a
    ///     warning if the cleaned text did not shrink. Off by default. Overrides
    ///     <c>ContextHelperConfig.Debug</c>.
    /// </summary>
    [JsonPropertyName("debug")]
    public bool? Debug { get; init; }

    public static ContextBehaviorConfig? Merge(ContextBehaviorConfig? local, ContextBehaviorConfig? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new ContextBehaviorConfig
        {
            AutoWarmOnStartup = local?.AutoWarmOnStartup ?? global?.AutoWarmOnStartup,
            AutoIntercept = local?.AutoIntercept ?? global?.AutoIntercept,
            ContextSizeLimit = local?.ContextSizeLimit ?? global?.ContextSizeLimit,
            TokenBudgetMargin = local?.TokenBudgetMargin ?? global?.TokenBudgetMargin,
            SemanticThreshold = local?.SemanticThreshold ?? global?.SemanticThreshold,
            Debug = local?.Debug ?? global?.Debug,
        };
    }

    public static ContextBehaviorConfig Defaults() => new()
    {
        AutoWarmOnStartup = true,
        AutoIntercept = true,
        ContextSizeLimit = 120_000,
        TokenBudgetMargin = 0.85,
        SemanticThreshold = 0.35,
        Debug = false,
    };
}

/// <summary>
///     Overlaid onto <c>CodeHelperConfig</c> at startup and on every config-file change — see
///     <c>TokenSaver.CodeHelper.Configuration.BehaviorConfigOverlay</c>. Null = keep the
///     appsettings-bound default. Grouped by what the setting decides, not just its C# type:
///     <see cref="Ownership" /> = whether CodeHelper handles a step at all, <see cref="Planner" />
///     = whether the local model gets consulted while planning, <see cref="Calibration" /> = how
///     past outcomes adjust those weights over time.
/// </summary>
public sealed record CodeHelperBehaviorConfig
{
    /// <summary>Overrides <c>CodeHelperConfig.AutoEnabled</c>.</summary>
    [JsonPropertyName("autoEnabled")]
    public bool? AutoEnabled { get; init; }

    /// <summary>Overrides <c>CodeHelperConfig.RequireAnchorMatch</c>.</summary>
    [JsonPropertyName("requireAnchorMatch")]
    public bool? RequireAnchorMatch { get; init; }

    /// <summary>Composite-score thresholds that decide whether CodeHelper owns a step outright, defers it entirely, or asks the calling AI.</summary>
    [JsonPropertyName("ownership")]
    public OwnershipThresholds? Ownership { get; init; }

    /// <summary>Thresholds the agentic planner uses while building a plan for a step it owns.</summary>
    [JsonPropertyName("planner")]
    public PlannerThresholds? Planner { get; init; }

    /// <summary>Tuning for how calibration weights adjust after each recorded outcome.</summary>
    [JsonPropertyName("calibration")]
    public CalibrationTuning? Calibration { get; init; }

    public static CodeHelperBehaviorConfig? Merge(CodeHelperBehaviorConfig? local, CodeHelperBehaviorConfig? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new CodeHelperBehaviorConfig
        {
            AutoEnabled = local?.AutoEnabled ?? global?.AutoEnabled,
            RequireAnchorMatch = local?.RequireAnchorMatch ?? global?.RequireAnchorMatch,
            Ownership = OwnershipThresholds.Merge(local?.Ownership, global?.Ownership),
            Planner = PlannerThresholds.Merge(local?.Planner, global?.Planner),
            Calibration = CalibrationTuning.Merge(local?.Calibration, global?.Calibration),
        };
    }

    public static CodeHelperBehaviorConfig Defaults() => new()
    {
        AutoEnabled = false,
        RequireAnchorMatch = true,
        Ownership = new OwnershipThresholds { CanDoThreshold = 0.70, DeferThreshold = 0.40 },
        Planner = new PlannerThresholds { ConfidenceThreshold = 0.65, ModelCallThreshold = 0.55 },
        Calibration = new CalibrationTuning { WasteThreshold = 0.40, LearningRate = 0.10 },
    };
}

/// <summary>Decides whether CodeHelper claims a step outright or defers it entirely to the calling AI.</summary>
public sealed record OwnershipThresholds
{
    /// <summary>Composite score at or above which CodeHelper claims full ownership of a step. Overrides <c>CodeHelperConfig.CanDoThreshold</c>.</summary>
    [JsonPropertyName("canDoThreshold")]
    public double? CanDoThreshold { get; init; }

    /// <summary>Composite score below which CodeHelper defers the step entirely to the AI. Overrides <c>CodeHelperConfig.DeferThreshold</c>.</summary>
    [JsonPropertyName("deferThreshold")]
    public double? DeferThreshold { get; init; }

    public static OwnershipThresholds? Merge(OwnershipThresholds? local, OwnershipThresholds? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new OwnershipThresholds
        {
            CanDoThreshold = local?.CanDoThreshold ?? global?.CanDoThreshold,
            DeferThreshold = local?.DeferThreshold ?? global?.DeferThreshold,
        };
    }
}

/// <summary>Decides how confident the agentic planner needs to be before acting alone vs. consulting the local model.</summary>
public sealed record PlannerThresholds
{
    /// <summary>Below this, hand the step off to the calling AI instead of planning it locally. Overrides <c>CodeHelperConfig.ConfidenceThreshold</c>.</summary>
    [JsonPropertyName("confidenceThreshold")]
    public double? ConfidenceThreshold { get; init; }

    /// <summary>Below this, invoke Ollama for the plan rather than using the built-in planner alone. Overrides <c>CodeHelperConfig.ModelCallThreshold</c>.</summary>
    [JsonPropertyName("modelCallThreshold")]
    public double? ModelCallThreshold { get; init; }

    public static PlannerThresholds? Merge(PlannerThresholds? local, PlannerThresholds? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new PlannerThresholds
        {
            ConfidenceThreshold = local?.ConfidenceThreshold ?? global?.ConfidenceThreshold,
            ModelCallThreshold = local?.ModelCallThreshold ?? global?.ModelCallThreshold,
        };
    }
}

/// <summary>Tuning for how calibration weights shift after each recorded outcome (see <c>CalibrationService</c>).</summary>
public sealed record CalibrationTuning
{
    /// <summary>Fraction of scaffolded work the AI had to redo before it counts as "not worth it" and weights get decremented. Overrides <c>CodeHelperConfig.WasteThreshold</c>.</summary>
    [JsonPropertyName("wasteThreshold")]
    public double? WasteThreshold { get; init; }

    /// <summary>Step size for weight updates each time an outcome is recorded (0.0–1.0). Overrides <c>CodeHelperConfig.LearningRate</c>.</summary>
    [JsonPropertyName("learningRate")]
    public double? LearningRate { get; init; }

    public static CalibrationTuning? Merge(CalibrationTuning? local, CalibrationTuning? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new CalibrationTuning
        {
            WasteThreshold = local?.WasteThreshold ?? global?.WasteThreshold,
            LearningRate = local?.LearningRate ?? global?.LearningRate,
        };
    }
}

/// <summary>
///     Overlaid onto <c>TokenSaverConfig.TokenEconomy</c> the same way as
///     <see cref="CodeHelperBehaviorConfig" />. Each <c>Enable*</c>/<see cref="AllowTerse" /> flag
///     is a kill switch (all default on) — flip one off to rule out a feature during debugging
///     without touching the others.
/// </summary>
public sealed record TokenEconomyBehaviorConfig
{
    /// <summary>Master switch for distillation itself. False = every distilling tool returns raw text instead of calling Ollama. Overrides <c>TokenEconomyConfig.EnableDistill</c>.</summary>
    [JsonPropertyName("enableDistill")]
    public bool? EnableDistill { get; init; }

    /// <summary>
    ///     Hard kill switch for terse mode: false means no caller can turn terse on, not even
    ///     with an explicit per-call <c>terse:true</c> — <see cref="TerseByDefault" /> and any
    ///     per-call override are both ignored while this is false. Overrides
    ///     <c>TokenEconomyConfig.EnableTerse</c> (renamed here for clarity: this gates whether
    ///     terse mode is <i>allowed</i>, not whether it's on).
    /// </summary>
    [JsonPropertyName("allowTerse")]
    public bool? AllowTerse { get; init; }

    /// <summary>
    ///     Server-wide default for terse mode when a tool call doesn't pass its own
    ///     <c>terse:</c> argument. False (default) = normal prompts/maxTokens. Only takes effect
    ///     while <see cref="AllowTerse" /> is true. Overrides <c>TokenEconomyConfig.Terse</c>
    ///     (renamed here for clarity: this is the *default*, not the switch — a caller can still
    ///     ask for terse per-call even when this is false).
    /// </summary>
    [JsonPropertyName("terseByDefault")]
    public bool? TerseByDefault { get; init; }

    /// <summary>Whether distilling tools write their raw output to disk before distilling (the escape hatch back to raw text). Overrides <c>TokenEconomyConfig.EnableRawRetention</c>.</summary>
    [JsonPropertyName("enableRawRetention")]
    public bool? EnableRawRetention { get; init; }

    /// <summary>Whether every distill() call records a row to the TokenSavings table. Overrides <c>TokenEconomyConfig.EnableSavingsTracking</c>.</summary>
    [JsonPropertyName("enableSavingsTracking")]
    public bool? EnableSavingsTracking { get; init; }

    /// <summary>Whether IndexRepo embeds each chunk's one-line summary rather than the raw chunk text. Overrides <c>TokenEconomyConfig.EnableSummaryEmbedding</c>.</summary>
    [JsonPropertyName("enableSummaryEmbedding")]
    public bool? EnableSummaryEmbedding { get; init; }

    public static TokenEconomyBehaviorConfig? Merge(TokenEconomyBehaviorConfig? local, TokenEconomyBehaviorConfig? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new TokenEconomyBehaviorConfig
        {
            EnableDistill = local?.EnableDistill ?? global?.EnableDistill,
            AllowTerse = local?.AllowTerse ?? global?.AllowTerse,
            TerseByDefault = local?.TerseByDefault ?? global?.TerseByDefault,
            EnableRawRetention = local?.EnableRawRetention ?? global?.EnableRawRetention,
            EnableSavingsTracking = local?.EnableSavingsTracking ?? global?.EnableSavingsTracking,
            EnableSummaryEmbedding = local?.EnableSummaryEmbedding ?? global?.EnableSummaryEmbedding,
        };
    }

    public static TokenEconomyBehaviorConfig Defaults() => new()
    {
        EnableDistill = true,
        AllowTerse = true,
        TerseByDefault = false,
        EnableRawRetention = true,
        EnableSavingsTracking = true,
        EnableSummaryEmbedding = true,
    };
}

/// <summary>
///     Global defaults for the provider-native context-reduction features surfaced by
///     <c>TokenSaver.AgentHelpers.*</c> (see <c>docs/config-behavior-contract.md</c> for the
///     research backing these fields). Every AI-model provider TokenSaver talks to
///     (Anthropic/Claude, DeepSeek, Qwen/DashScope, and the Ollama-hosted OpenSource family —
///     Llama/Mistral/Gemma) ships some built-in mechanism for shrinking the tokens it actually
///     bills/processes on repeated or growing context, even though the mechanisms themselves
///     differ (explicit cache breakpoints vs. always-on disk caching vs. KV-cache quantization).
///     <see cref="EnablePromptCaching" />/<see cref="CacheTtlSeconds" />/
///     <see cref="MinCacheableTokens" /> are the three knobs common to every provider that
///     supports caching at all (Anthropic, Qwen; DeepSeek's is automatic and ignores these but
///     still benefits from stable prompt ordering). These three act as the fallback for any
///     per-family section below that leaves the same-named property unset — each per-family
///     section can override any of them, plus add fields with no cross-provider equivalent
///     (e.g. Anthropic's context-editing trigger, Ollama's KV-cache quantization type).
/// </summary>
public sealed record AgentBehaviorConfig
{
    /// <summary>
    ///     Master switch for provider-native prompt/context caching. Overridable per family.
    ///     Anthropic/Qwen: adds <c>cache_control</c> breakpoints to outgoing requests. DeepSeek:
    ///     caching is always on server-side regardless of this flag — kept here so a family
    ///     section can still be authored uniformly. Ollama: no server cache; this flag instead
    ///     gates whether <see cref="OpenSourceAgentConfig.UseFlashAttention" />/KV quantization
    ///     are applied.
    /// </summary>
    [JsonPropertyName("enablePromptCaching")]
    public bool? EnablePromptCaching { get; init; }

    /// <summary>
    ///     How long a cached prefix stays valid, in seconds, for providers with a configurable
    ///     TTL (Anthropic: 300 or 3600; Qwen: fixed ~300, resets on hit). Ignored by DeepSeek
    ///     (not configurable) and Ollama (no server-side cache).
    /// </summary>
    [JsonPropertyName("cacheTtlSeconds")]
    public int? CacheTtlSeconds { get; init; }

    /// <summary>
    ///     Minimum prefix size, in tokens, before a provider will bother caching it. Below this,
    ///     caching is skipped silently (no error) on every provider that enforces one.
    /// </summary>
    [JsonPropertyName("minCacheableTokens")]
    public int? MinCacheableTokens { get; init; }

    /// <summary>Anthropic/Claude-specific overrides — see <see cref="AnthropicAgentConfig" />.</summary>
    [JsonPropertyName("anthropic")]
    public AnthropicAgentConfig? Anthropic { get; init; }

    /// <summary>DeepSeek-specific overrides — see <see cref="DeepSeekAgentConfig" />.</summary>
    [JsonPropertyName("deepSeek")]
    public DeepSeekAgentConfig? DeepSeek { get; init; }

    /// <summary>Qwen/DashScope-specific overrides — see <see cref="QwenAgentConfig" />.</summary>
    [JsonPropertyName("qwen")]
    public QwenAgentConfig? Qwen { get; init; }

    /// <summary>
    ///     Ollama-hosted open-source family (Llama/Mistral/Gemma) overrides — see
    ///     <see cref="OpenSourceAgentConfig" />.
    /// </summary>
    [JsonPropertyName("openSource")]
    public OpenSourceAgentConfig? OpenSource { get; init; }

    public static AgentBehaviorConfig? Merge(AgentBehaviorConfig? local, AgentBehaviorConfig? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new AgentBehaviorConfig
        {
            EnablePromptCaching = local?.EnablePromptCaching ?? global?.EnablePromptCaching,
            CacheTtlSeconds = local?.CacheTtlSeconds ?? global?.CacheTtlSeconds,
            MinCacheableTokens = local?.MinCacheableTokens ?? global?.MinCacheableTokens,
            Anthropic = AnthropicAgentConfig.Merge(local?.Anthropic, global?.Anthropic),
            DeepSeek = DeepSeekAgentConfig.Merge(local?.DeepSeek, global?.DeepSeek),
            Qwen = QwenAgentConfig.Merge(local?.Qwen, global?.Qwen),
            OpenSource = OpenSourceAgentConfig.Merge(local?.OpenSource, global?.OpenSource),
        };
    }

    public static AgentBehaviorConfig Defaults() => new()
    {
        EnablePromptCaching = true,
        CacheTtlSeconds = 300,
        MinCacheableTokens = 1024,
        Anthropic = AnthropicAgentConfig.Defaults(),
        DeepSeek = DeepSeekAgentConfig.Defaults(),
        Qwen = QwenAgentConfig.Defaults(),
        OpenSource = OpenSourceAgentConfig.Defaults(),
    };
}

/// <summary>
///     Anthropic/Claude overrides. Any property left <see langword="null" /> falls back to the
///     matching property on <see cref="AgentBehaviorConfig" />. Claude is also the only family
///     here with server-side "context editing" (auto-clearing old tool results/thinking blocks
///     mid-conversation) — see
///     <see href="https://platform.claude.com/docs/en/build-with-claude/context-editing" />.
/// </summary>
public sealed record AnthropicAgentConfig
{
    /// <summary>Overrides <see cref="AgentBehaviorConfig.EnablePromptCaching" /> for Anthropic calls.</summary>
    [JsonPropertyName("enablePromptCaching")]
    public bool? EnablePromptCaching { get; init; }

    /// <summary>Overrides <see cref="AgentBehaviorConfig.CacheTtlSeconds" />. Anthropic accepts only 300 ("5m") or 3600 ("1h").</summary>
    [JsonPropertyName("cacheTtlSeconds")]
    public int? CacheTtlSeconds { get; init; }

    /// <summary>Overrides <see cref="AgentBehaviorConfig.MinCacheableTokens" />. Model-dependent floor (512–4096); values below this are cached as if unset.</summary>
    [JsonPropertyName("minCacheableTokens")]
    public int? MinCacheableTokens { get; init; }

    /// <summary>
    ///     Enables server-side context editing (<c>clear_tool_uses_20250919</c>): old tool
    ///     results are auto-cleared once the conversation crosses
    ///     <see cref="ContextEditingTriggerTokens" />.
    /// </summary>
    [JsonPropertyName("enableContextEditing")]
    public bool? EnableContextEditing { get; init; }

    /// <summary>Input-token threshold that activates context editing. Overrides Anthropic's own 100,000-token default.</summary>
    [JsonPropertyName("contextEditingTriggerTokens")]
    public int? ContextEditingTriggerTokens { get; init; }

    /// <summary>How many of the most recent tool-use/result pairs context editing preserves when it clears.</summary>
    [JsonPropertyName("contextEditingKeepToolUses")]
    public int? ContextEditingKeepToolUses { get; init; }

    public static AnthropicAgentConfig? Merge(AnthropicAgentConfig? local, AnthropicAgentConfig? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new AnthropicAgentConfig
        {
            EnablePromptCaching = local?.EnablePromptCaching ?? global?.EnablePromptCaching,
            CacheTtlSeconds = local?.CacheTtlSeconds ?? global?.CacheTtlSeconds,
            MinCacheableTokens = local?.MinCacheableTokens ?? global?.MinCacheableTokens,
            EnableContextEditing = local?.EnableContextEditing ?? global?.EnableContextEditing,
            ContextEditingTriggerTokens = local?.ContextEditingTriggerTokens ?? global?.ContextEditingTriggerTokens,
            ContextEditingKeepToolUses = local?.ContextEditingKeepToolUses ?? global?.ContextEditingKeepToolUses,
        };
    }

    public static AnthropicAgentConfig Defaults() => new()
    {
        EnableContextEditing = false,
        ContextEditingTriggerTokens = 100_000,
        ContextEditingKeepToolUses = 3,
    };
}

/// <summary>
///     DeepSeek overrides. DeepSeek's disk-based context caching is always on server-side and
///     not configurable (no TTL/threshold knobs to send) — see
///     <see href="https://api-docs.deepseek.com/guides/kv_cache" />. Only present here for
///     symmetry and so a future DeepSeek-specific knob has somewhere to live; today it carries
///     no fields that diverge from the global defaults.
/// </summary>
public sealed record DeepSeekAgentConfig
{
    /// <summary>
    ///     Purely advisory — DeepSeek caches automatically regardless of this value. Kept so
    ///     downstream code can still branch on "does the user want caching considered" uniformly
    ///     across families instead of special-casing DeepSeek.
    /// </summary>
    [JsonPropertyName("enablePromptCaching")]
    public bool? EnablePromptCaching { get; init; }

    public static DeepSeekAgentConfig? Merge(DeepSeekAgentConfig? local, DeepSeekAgentConfig? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new DeepSeekAgentConfig
        {
            EnablePromptCaching = local?.EnablePromptCaching ?? global?.EnablePromptCaching,
        };
    }

    public static DeepSeekAgentConfig Defaults() => new()
    {
        EnablePromptCaching = true,
    };
}

/// <summary>
///     Qwen/DashScope overrides. Qwen supports both an explicit cache (developer-marked
///     breakpoints, same shape as Anthropic's) and an implicit cache (automatic, cannot be
///     disabled) — see
///     <see href="https://www.alibabacloud.com/help/en/model-studio/context-cache" />.
///     <see cref="EnablePromptCaching" /> here controls only the explicit mode.
/// </summary>
public sealed record QwenAgentConfig
{
    /// <summary>Overrides <see cref="AgentBehaviorConfig.EnablePromptCaching" /> for Qwen's explicit cache markers. The implicit cache is always on regardless.</summary>
    [JsonPropertyName("enablePromptCaching")]
    public bool? EnablePromptCaching { get; init; }

    /// <summary>Overrides <see cref="AgentBehaviorConfig.CacheTtlSeconds" />. Qwen's explicit cache is fixed at ~300s and resets on each hit.</summary>
    [JsonPropertyName("cacheTtlSeconds")]
    public int? CacheTtlSeconds { get; init; }

    /// <summary>Overrides <see cref="AgentBehaviorConfig.MinCacheableTokens" /> for the explicit cache (1024). The implicit cache's own floor (256) is not user-configurable.</summary>
    [JsonPropertyName("minCacheableTokens")]
    public int? MinCacheableTokens { get; init; }

    public static QwenAgentConfig? Merge(QwenAgentConfig? local, QwenAgentConfig? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new QwenAgentConfig
        {
            EnablePromptCaching = local?.EnablePromptCaching ?? global?.EnablePromptCaching,
            CacheTtlSeconds = local?.CacheTtlSeconds ?? global?.CacheTtlSeconds,
            MinCacheableTokens = local?.MinCacheableTokens ?? global?.MinCacheableTokens,
        };
    }

    public static QwenAgentConfig Defaults() => new()
    {
        EnablePromptCaching = true,
        CacheTtlSeconds = 300,
        MinCacheableTokens = 1024,
    };
}

/// <summary>
///     Ollama-hosted open-source family (Llama/Mistral/Gemma) overrides. These models have no
///     server-side prompt cache; the closest analogs to caching are Flash Attention (shrinks the
///     KV-cache memory footprint as context grows) and KV-cache quantization (trades precision
///     for a smaller resident cache) — see
///     <see href="https://docs.ollama.com/faq" />. <see cref="AgentBehaviorConfig.CacheTtlSeconds" />/
///     <see cref="AgentBehaviorConfig.MinCacheableTokens" /> have no Ollama equivalent and are
///     not exposed here.
/// </summary>
public sealed record OpenSourceAgentConfig
{
    /// <summary>
    ///     Overrides <see cref="AgentBehaviorConfig.EnablePromptCaching" />; here it gates whether
    ///     <see cref="UseFlashAttention" />/<see cref="KvCacheQuantization" /> are applied at all,
    ///     since there is no real request-level cache to toggle.
    /// </summary>
    [JsonPropertyName("enablePromptCaching")]
    public bool? EnablePromptCaching { get; init; }

    /// <summary>Enables Flash Attention (<c>OLLAMA_FLASH_ATTENTION=1</c>), which reduces memory growth as context size increases. Required for <see cref="KvCacheQuantization" /> to take effect.</summary>
    [JsonPropertyName("useFlashAttention")]
    public bool? UseFlashAttention { get; init; }

    /// <summary>
    ///     KV-cache quantization type (<c>OLLAMA_KV_CACHE_TYPE</c>): <c>"f16"</c> (default, full
    ///     precision), <c>"q8_0"</c> (~half the memory, minimal quality loss), or <c>"q4_0"</c>
    ///     (~quarter the memory, small-to-medium quality loss).
    /// </summary>
    [JsonPropertyName("kvCacheQuantization")]
    public string? KvCacheQuantization { get; init; }

    /// <summary>Context window size in tokens (<c>num_ctx</c> / <c>OLLAMA_CONTEXT_LENGTH</c>). Overrides the model's built-in default (commonly 4096).</summary>
    [JsonPropertyName("numCtx")]
    public int? NumCtx { get; init; }

    public static OpenSourceAgentConfig? Merge(OpenSourceAgentConfig? local, OpenSourceAgentConfig? global)
    {
        if (local is null && global is null)
        {
            return null;
        }

        return new OpenSourceAgentConfig
        {
            EnablePromptCaching = local?.EnablePromptCaching ?? global?.EnablePromptCaching,
            UseFlashAttention = local?.UseFlashAttention ?? global?.UseFlashAttention,
            KvCacheQuantization = local?.KvCacheQuantization ?? global?.KvCacheQuantization,
            NumCtx = local?.NumCtx ?? global?.NumCtx,
        };
    }

    public static OpenSourceAgentConfig Defaults() => new()
    {
        EnablePromptCaching = true,
        UseFlashAttention = true,
        KvCacheQuantization = "q8_0",
        NumCtx = 8192,
    };
}

/// <summary>Scope a behavior value is written to.</summary>
public enum ConfigScope
{
    /// <summary><c>.tokensaver/config.json</c> under the workspace root.</summary>
    Local,

    /// <summary><c>%ProgramData%\TokenSaver\config.json</c> — machine-global, shared across workspaces.</summary>
    Global,
}

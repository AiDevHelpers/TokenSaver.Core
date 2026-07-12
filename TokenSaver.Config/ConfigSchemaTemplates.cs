using System.Linq;

namespace TokenSaver.Config;

/// <summary>
///     A single node in the <see cref="TokenSaverBehaviorConfig" /> schema tree — either a leaf
///     setting (<see cref="Children" /> is <see langword="null" />) or a nested object
///     (<c>ownership</c>, <c>anthropic</c>, etc., <see cref="Children" /> populated). Each node
///     carries its own fully self-contained template text in both forms:
///     <see cref="CommentedBlock" /> (commented-out, used in the local
///     <c>.tokensaver/config.json</c> template) and <see cref="UncommentedBlock" /> (active, used
///     in the machine-global template). Both <see cref="WorkspaceConfigDefaults" /> (fresh-file
///     templates) and <see cref="ConfigUpgradeService" /> (patching an existing file that's
///     missing some of these nodes) render from this single tree, so the two can never drift out
///     of sync with each other or with the wording a user sees.
///     <see cref="SentinelNeverActive" /> marks a leaf whose <see cref="CommentedBlock" /> and
///     <see cref="UncommentedBlock" /> are identical (always commented out, even in the "active"
///     global template) because it has no real default to activate — e.g. a "never asked yet"
///     sentinel, or an override that intentionally falls back to a parent value. Such a leaf never
///     appears as live JSON in a freshly-generated file, so <see cref="ConfigUpgradeService" />
///     must not treat its permanent absence as a schema-version gap to repair — doing so would
///     re-insert it on every single call.
/// </summary>
internal sealed record ConfigElementTemplate(
    string Key,
    string CommentedBlock,
    string UncommentedBlock,
    IReadOnlyList<ConfigElementTemplate>? Children = null,
    bool SentinelNeverActive = false);

/// <summary>
///     The full <see cref="TokenSaverBehaviorConfig" /> schema as a tree of
///     <see cref="ConfigElementTemplate" /> nodes, down to individual leaf settings — see that
///     type's remarks for why this exists as a shared tree instead of separate flat templates.
/// </summary>
public static class ConfigSchemaTemplates
{
    // ── repoHelper ──────────────────────────────────────────────────────────────

    // Stays commented in BOTH forms: a stateful "never asked yet" sentinel has no real default,
    // so activating it in the global template would fabricate an answer nobody gave.
    private const string AddTokenSaverToGitignoreBlock =
        "    // Whether .tokensaver/ should be added to the workspace .gitignore. Unset = you haven't been asked yet.\n" +
        "    // \"addTokenSaverToGitignore\": true,";

    private const string OptimizeInstructionsCommented =
        "    // On every session connect, copy the machine-global Claude instructions\n" +
        "    // (%USERPROFILE%\\.claude\\CLAUDE.md) into a workspace instruction file chosen for\n" +
        "    // whatever tool is connecting, using that tool's most token-efficient supported\n" +
        "    // mechanism. Default: true.\n" +
        "    // \"optimizeInstructions\": true,";

    private const string OptimizeInstructionsUncommented =
        "    // On every session connect, copy the machine-global Claude instructions\n" +
        "    // (%USERPROFILE%\\.claude\\CLAUDE.md) into a workspace instruction file chosen for\n" +
        "    // whatever tool is connecting, using that tool's most token-efficient supported\n" +
        "    // mechanism. Default: true.\n" +
        "    \"optimizeInstructions\": true,";

    private const string AutoSplitOversizedInstructionsCommented =
        "    // When a discovered CLAUDE.md is oversized, automatically split large sections out into\n" +
        "    // synthetic sub-docs — purely in the cached/synced representation sent to AI clients,\n" +
        "    // never rewriting your real CLAUDE.md or .claude/rules/ files. Default: true.\n" +
        "    // \"autoSplitOversizedInstructions\": true,";

    private const string AutoSplitOversizedInstructionsUncommented =
        "    // When a discovered CLAUDE.md is oversized, automatically split large sections out into\n" +
        "    // synthetic sub-docs — purely in the cached/synced representation sent to AI clients,\n" +
        "    // never rewriting your real CLAUDE.md or .claude/rules/ files. Default: true.\n" +
        "    \"autoSplitOversizedInstructions\": true,";

    // Stays commented in BOTH forms, same rationale as AddTokenSaverToGitignoreBlock — this
    // rewrites the user's real CLAUDE.md and a settings file, so it has no "on by default" form
    // to activate in the global template; enabling it is always an explicit, hand-made choice.
    private const string CombineClaudeMdForClaudeCodeBlock =
        "    // Folds the whole discovered CLAUDE.md chain (global + every ancestor repo + this\n" +
        "    // workspace's own) into one combined, deduped, auto-split file that becomes this\n" +
        "    // workspace's real CLAUDE.md — your hand-authored original is preserved as\n" +
        "    // CLAUDE.human.md — and configures Claude Code's claudeMdExcludes setting so it stops\n" +
        "    // loading the global/ancestor files a second time natively. Unlike optimizeInstructions\n" +
        "    // and autoSplitOversizedInstructions above, this rewrites real files. Default: false.\n" +
        "    // \"combineClaudeMdForClaudeCode\": true,";

    private static readonly ConfigElementTemplate AddTokenSaverToGitignore = new(
        "addTokenSaverToGitignore", AddTokenSaverToGitignoreBlock, AddTokenSaverToGitignoreBlock, SentinelNeverActive: true);

    private static readonly ConfigElementTemplate OptimizeInstructions = new(
        "optimizeInstructions", OptimizeInstructionsCommented, OptimizeInstructionsUncommented);

    private static readonly ConfigElementTemplate AutoSplitOversizedInstructions = new(
        "autoSplitOversizedInstructions", AutoSplitOversizedInstructionsCommented, AutoSplitOversizedInstructionsUncommented);

    private static readonly ConfigElementTemplate CombineClaudeMdForClaudeCode = new(
        "combineClaudeMdForClaudeCode", CombineClaudeMdForClaudeCodeBlock, CombineClaudeMdForClaudeCodeBlock, SentinelNeverActive: true);

    private static readonly ConfigElementTemplate RepoHelper = new(
        "repoHelper",
        "  // Repo-helper: workspace-hygiene behaviors that act on the repo itself.\n" +
        "  \"repoHelper\": {\n" +
        AddTokenSaverToGitignoreBlock + "\n\n" + OptimizeInstructionsCommented + "\n\n" + AutoSplitOversizedInstructionsCommented + "\n\n" + CombineClaudeMdForClaudeCodeBlock + "\n" +
        "  },",
        "  // Repo-helper: workspace-hygiene behaviors that act on the repo itself.\n" +
        "  \"repoHelper\": {\n" +
        AddTokenSaverToGitignoreBlock + "\n\n" + OptimizeInstructionsUncommented + "\n\n" + AutoSplitOversizedInstructionsUncommented + "\n\n" + CombineClaudeMdForClaudeCodeBlock + "\n" +
        "  },",
        [AddTokenSaverToGitignore, OptimizeInstructions, AutoSplitOversizedInstructions, CombineClaudeMdForClaudeCode]);

    // ── codeHelper ──────────────────────────────────────────────────────────────

    private const string AutoEnabledCommented =
        "    // Master switch for CodeHelper auto-workflow. Default: true.\n" +
        "    // \"autoEnabled\": true,";

    private const string AutoEnabledUncommented =
        "    // Master switch for CodeHelper auto-workflow. Default: true.\n" +
        "    \"autoEnabled\": true,";

    private const string RequireAnchorMatchCommented =
        "    // Require an exact anchor-text match before applying a patch. Default: true.\n" +
        "    // \"requireAnchorMatch\": true,";

    private const string RequireAnchorMatchUncommented =
        "    // Require an exact anchor-text match before applying a patch. Default: true.\n" +
        "    \"requireAnchorMatch\": true,";

    private const string CanDoThresholdCommented =
        "      // Score at/above which CodeHelper claims full ownership of a step. Default: 0.70.\n" +
        "      // \"canDoThreshold\": 0.70,";

    private const string CanDoThresholdUncommented =
        "      // Score at/above which CodeHelper claims full ownership of a step. Default: 0.70.\n" +
        "      \"canDoThreshold\": 0.70,";

    private const string DeferThresholdCommented =
        "      // Score below which CodeHelper defers the step entirely to the calling AI. Default: 0.40.\n" +
        "      // \"deferThreshold\": 0.40,";

    private const string DeferThresholdUncommented =
        "      // Score below which CodeHelper defers the step entirely to the calling AI. Default: 0.40.\n" +
        "      \"deferThreshold\": 0.40,";

    private const string ConfidenceThresholdCommented =
        "      // Below this confidence, hand the step to the calling AI instead of planning locally. Default: 0.65.\n" +
        "      // \"confidenceThreshold\": 0.65,";

    private const string ConfidenceThresholdUncommented =
        "      // Below this confidence, hand the step to the calling AI instead of planning locally. Default: 0.65.\n" +
        "      \"confidenceThreshold\": 0.65,";

    private const string ModelCallThresholdCommented =
        "      // Below this, invoke Ollama for the plan rather than the built-in planner alone. Default: 0.55.\n" +
        "      // \"modelCallThreshold\": 0.55,";

    private const string ModelCallThresholdUncommented =
        "      // Below this, invoke Ollama for the plan rather than the built-in planner alone. Default: 0.55.\n" +
        "      \"modelCallThreshold\": 0.55,";

    private const string WasteThresholdCommented =
        "      // Fraction of scaffolded work redone before a weight counts as \"not worth it\". Default: 0.40.\n" +
        "      // \"wasteThreshold\": 0.40,";

    private const string WasteThresholdUncommented =
        "      // Fraction of scaffolded work redone before a weight counts as \"not worth it\". Default: 0.40.\n" +
        "      \"wasteThreshold\": 0.40,";

    private const string LearningRateCommented =
        "      // Step size for weight updates each time an outcome is recorded (0.0-1.0). Default: 0.10.\n" +
        "      // \"learningRate\": 0.10,";

    private const string LearningRateUncommented =
        "      // Step size for weight updates each time an outcome is recorded (0.0-1.0). Default: 0.10.\n" +
        "      \"learningRate\": 0.10,";

    private static readonly ConfigElementTemplate AutoEnabled = new("autoEnabled", AutoEnabledCommented, AutoEnabledUncommented);
    private static readonly ConfigElementTemplate RequireAnchorMatch = new("requireAnchorMatch", RequireAnchorMatchCommented, RequireAnchorMatchUncommented);
    private static readonly ConfigElementTemplate CanDoThreshold = new("canDoThreshold", CanDoThresholdCommented, CanDoThresholdUncommented);
    private static readonly ConfigElementTemplate DeferThreshold = new("deferThreshold", DeferThresholdCommented, DeferThresholdUncommented);
    private static readonly ConfigElementTemplate ConfidenceThreshold = new("confidenceThreshold", ConfidenceThresholdCommented, ConfidenceThresholdUncommented);
    private static readonly ConfigElementTemplate ModelCallThreshold = new("modelCallThreshold", ModelCallThresholdCommented, ModelCallThresholdUncommented);
    private static readonly ConfigElementTemplate WasteThreshold = new("wasteThreshold", WasteThresholdCommented, WasteThresholdUncommented);
    private static readonly ConfigElementTemplate LearningRate = new("learningRate", LearningRateCommented, LearningRateUncommented);

    private static readonly ConfigElementTemplate Ownership = new(
        "ownership",
        "    // Composite-score thresholds deciding whether CodeHelper owns, defers, or asks about a step.\n" +
        "    \"ownership\": {\n" + CanDoThresholdCommented + "\n\n" + DeferThresholdCommented + "\n    },",
        "    // Composite-score thresholds deciding whether CodeHelper owns, defers, or asks about a step.\n" +
        "    \"ownership\": {\n" + CanDoThresholdUncommented + "\n\n" + DeferThresholdUncommented + "\n    },",
        [CanDoThreshold, DeferThreshold]);

    private static readonly ConfigElementTemplate Planner = new(
        "planner",
        "    // Thresholds the agentic planner uses while building a plan for a step it owns.\n" +
        "    \"planner\": {\n" + ConfidenceThresholdCommented + "\n\n" + ModelCallThresholdCommented + "\n    },",
        "    // Thresholds the agentic planner uses while building a plan for a step it owns.\n" +
        "    \"planner\": {\n" + ConfidenceThresholdUncommented + "\n\n" + ModelCallThresholdUncommented + "\n    },",
        [ConfidenceThreshold, ModelCallThreshold]);

    private static readonly ConfigElementTemplate Calibration = new(
        "calibration",
        "    // Tuning for how calibration weights shift after each recorded outcome.\n" +
        "    \"calibration\": {\n" + WasteThresholdCommented + "\n\n" + LearningRateCommented + "\n    },",
        "    // Tuning for how calibration weights shift after each recorded outcome.\n" +
        "    \"calibration\": {\n" + WasteThresholdUncommented + "\n\n" + LearningRateUncommented + "\n    },",
        [WasteThreshold, LearningRate]);

    private static readonly ConfigElementTemplate CodeHelper = new(
        "codeHelper",
        "  // CodeHelper: auto-workflow behavior — whether/how the local model assists with code steps.\n" +
        "  \"codeHelper\": {\n" +
        AutoEnabledCommented + "\n\n" + RequireAnchorMatchCommented + "\n\n" +
        Ownership.CommentedBlock + "\n\n" + Planner.CommentedBlock + "\n\n" + Calibration.CommentedBlock + "\n" +
        "  },",
        "  // CodeHelper: auto-workflow behavior — whether/how the local model assists with code steps.\n" +
        "  \"codeHelper\": {\n" +
        AutoEnabledUncommented + "\n\n" + RequireAnchorMatchUncommented + "\n\n" +
        Ownership.UncommentedBlock + "\n\n" + Planner.UncommentedBlock + "\n\n" + Calibration.UncommentedBlock + "\n" +
        "  },",
        [AutoEnabled, RequireAnchorMatch, Ownership, Planner, Calibration]);

    // ── tokenEconomy ────────────────────────────────────────────────────────────

    private static readonly ConfigElementTemplate EnableDistill = new(
        "enableDistill",
        "    // Master switch for distillation. false = every distilling tool returns raw text. Default: true.\n    // \"enableDistill\": true,",
        "    // Master switch for distillation. false = every distilling tool returns raw text. Default: true.\n    \"enableDistill\": true,");

    private static readonly ConfigElementTemplate AllowTerse = new(
        "allowTerse",
        "    // Hard kill switch for terse mode; false blocks even an explicit per-call terse:true. Default: true.\n    // \"allowTerse\": true,",
        "    // Hard kill switch for terse mode; false blocks even an explicit per-call terse:true. Default: true.\n    \"allowTerse\": true,");

    private static readonly ConfigElementTemplate TerseByDefault = new(
        "terseByDefault",
        "    // Server-wide default for terse mode when a call doesn't pass its own terse: argument. Default: false.\n    // \"terseByDefault\": false,",
        "    // Server-wide default for terse mode when a call doesn't pass its own terse: argument. Default: false.\n    \"terseByDefault\": false,");

    private static readonly ConfigElementTemplate EnableRawRetention = new(
        "enableRawRetention",
        "    // Whether distilling tools write raw output to disk before distilling. Default: true.\n    // \"enableRawRetention\": true,",
        "    // Whether distilling tools write raw output to disk before distilling. Default: true.\n    \"enableRawRetention\": true,");

    private static readonly ConfigElementTemplate EnableSavingsTracking = new(
        "enableSavingsTracking",
        "    // Whether every distill() call records a row to the TokenSavings table. Default: true.\n    // \"enableSavingsTracking\": true,",
        "    // Whether every distill() call records a row to the TokenSavings table. Default: true.\n    \"enableSavingsTracking\": true,");

    private static readonly ConfigElementTemplate EnableSummaryEmbedding = new(
        "enableSummaryEmbedding",
        "    // Whether IndexRepo embeds each chunk's one-line summary instead of the raw chunk text. Default: true.\n    // \"enableSummaryEmbedding\": true,",
        "    // Whether IndexRepo embeds each chunk's one-line summary instead of the raw chunk text. Default: true.\n    \"enableSummaryEmbedding\": true,");

    private static readonly ConfigElementTemplate TokenEconomy = new(
        "tokenEconomy",
        "  // TokenEconomy: distillation/terse-mode kill switches (all default on unless noted).\n" +
        "  \"tokenEconomy\": {\n" +
        string.Join("\n\n", EnableDistill.CommentedBlock, AllowTerse.CommentedBlock, TerseByDefault.CommentedBlock,
            EnableRawRetention.CommentedBlock, EnableSavingsTracking.CommentedBlock, EnableSummaryEmbedding.CommentedBlock) +
        "\n  },",
        "  // TokenEconomy: distillation/terse-mode kill switches (all default on unless noted).\n" +
        "  \"tokenEconomy\": {\n" +
        string.Join("\n\n", EnableDistill.UncommentedBlock, AllowTerse.UncommentedBlock, TerseByDefault.UncommentedBlock,
            EnableRawRetention.UncommentedBlock, EnableSavingsTracking.UncommentedBlock, EnableSummaryEmbedding.UncommentedBlock) +
        "\n  },",
        [EnableDistill, AllowTerse, TerseByDefault, EnableRawRetention, EnableSavingsTracking, EnableSummaryEmbedding]);

    // ── context ─────────────────────────────────────────────────────────────────

    private static readonly ConfigElementTemplate AutoWarmOnStartup = new(
        "autoWarmOnStartup",
        "    // Whether the server builds an initial concise-context cache automatically at startup. Default: true.\n    // \"autoWarmOnStartup\": true,",
        "    // Whether the server builds an initial concise-context cache automatically at startup. Default: true.\n    \"autoWarmOnStartup\": true,");

    private static readonly ConfigElementTemplate AutoIntercept = new(
        "autoIntercept",
        "    // Whether every prompt-bearing MCP tool call gets auto-cleaned before forwarding. Default: true.\n    // \"autoIntercept\": true,",
        "    // Whether every prompt-bearing MCP tool call gets auto-cleaned before forwarding. Default: true.\n    \"autoIntercept\": true,");

    private static readonly ConfigElementTemplate ContextSizeLimit = new(
        "contextSizeLimit",
        "    // Token budget a built context is capped at. Default: 120000.\n    // \"contextSizeLimit\": 120000,",
        "    // Token budget a built context is capped at. Default: 120000.\n    \"contextSizeLimit\": 120000,");

    private static readonly ConfigElementTemplate TokenBudgetMargin = new(
        "tokenBudgetMargin",
        "    // Fraction of contextSizeLimit the pipeline targets before trimming further. Default: 0.85.\n    // \"tokenBudgetMargin\": 0.85,",
        "    // Fraction of contextSizeLimit the pipeline targets before trimming further. Default: 0.85.\n    \"tokenBudgetMargin\": 0.85,");

    private static readonly ConfigElementTemplate SemanticThreshold = new(
        "semanticThreshold",
        "    // Minimum embedding-similarity score for a chunk to survive the semantic relevance filter. Default: 0.35.\n    // \"semanticThreshold\": 0.35,",
        "    // Minimum embedding-similarity score for a chunk to survive the semantic relevance filter. Default: 0.35.\n    \"semanticThreshold\": 0.35,");

    private static readonly ConfigElementTemplate Debug = new(
        "debug",
        "    // Save raw/cleaned context text to .tokensaver/debug/ for every run and warn if it didn't shrink. Default: false.\n    // \"debug\": false,",
        "    // Save raw/cleaned context text to .tokensaver/debug/ for every run and warn if it didn't shrink. Default: false.\n    \"debug\": false,");

    private static readonly ConfigElementTemplate DetectBuildArtifacts = new(
        "detectBuildArtifacts",
        "    // Automatically detect and exclude build/cache artifacts (bin/, obj/, node_modules/, etc.) from context. Default: true.\n    // \"detectBuildArtifacts\": true,",
        "    // Automatically detect and exclude build/cache artifacts (bin/, obj/, node_modules/, etc.) from context. Default: true.\n    \"detectBuildArtifacts\": true,");

    private static readonly ConfigElementTemplate Context = new(
        "context",
        "  // Context-pipeline overrides for the concise-context cache and auto-cleaning middleware.\n" +
        "  \"context\": {\n" +
        string.Join("\n\n", AutoWarmOnStartup.CommentedBlock, AutoIntercept.CommentedBlock, ContextSizeLimit.CommentedBlock,
            TokenBudgetMargin.CommentedBlock, SemanticThreshold.CommentedBlock, Debug.CommentedBlock, DetectBuildArtifacts.CommentedBlock) +
        "\n  },",
        "  // Context-pipeline overrides for the concise-context cache and auto-cleaning middleware.\n" +
        "  \"context\": {\n" +
        string.Join("\n\n", AutoWarmOnStartup.UncommentedBlock, AutoIntercept.UncommentedBlock, ContextSizeLimit.UncommentedBlock,
            TokenBudgetMargin.UncommentedBlock, SemanticThreshold.UncommentedBlock, Debug.UncommentedBlock, DetectBuildArtifacts.UncommentedBlock) +
        "\n  },",
        [AutoWarmOnStartup, AutoIntercept, ContextSizeLimit, TokenBudgetMargin, SemanticThreshold, Debug, DetectBuildArtifacts]);

    // ── agentConfig ─────────────────────────────────────────────────────────────

    private static readonly ConfigElementTemplate AgentEnablePromptCaching = new(
        "enablePromptCaching",
        "    // Master switch for provider-native prompt/context caching (overridable per family below). Default: true.\n    // \"enablePromptCaching\": true,",
        "    // Master switch for provider-native prompt/context caching (overridable per family below). Default: true.\n    \"enablePromptCaching\": true,");

    private static readonly ConfigElementTemplate AgentCacheTtlSeconds = new(
        "cacheTtlSeconds",
        "    // How long a cached prefix stays valid, in seconds, for providers with a configurable TTL. Default: 300.\n    // \"cacheTtlSeconds\": 300,",
        "    // How long a cached prefix stays valid, in seconds, for providers with a configurable TTL. Default: 300.\n    \"cacheTtlSeconds\": 300,");

    private static readonly ConfigElementTemplate AgentMinCacheableTokens = new(
        "minCacheableTokens",
        "    // Minimum prefix size, in tokens, before a provider bothers caching it. Default: 1024.\n    // \"minCacheableTokens\": 1024,",
        "    // Minimum prefix size, in tokens, before a provider bothers caching it. Default: 1024.\n    \"minCacheableTokens\": 1024,");

    // Falls back to agentConfig's own values, so Defaults() leaves these unset — stays commented in both forms.
    private const string AnthropicEnablePromptCachingBlock =
        "      // Overrides enablePromptCaching for Anthropic calls. Default: unset (falls back to agentConfig).\n      // \"enablePromptCaching\": true,";

    private const string AnthropicCacheTtlSecondsBlock =
        "      // Overrides cacheTtlSeconds for Anthropic. Accepts only 300 (\"5m\") or 3600 (\"1h\"). Default: unset.\n      // \"cacheTtlSeconds\": 300,";

    private const string AnthropicMinCacheableTokensBlock =
        "      // Overrides minCacheableTokens for Anthropic. Default: unset.\n      // \"minCacheableTokens\": 1024,";

    private static readonly ConfigElementTemplate AnthropicEnablePromptCaching = new(
        "enablePromptCaching", AnthropicEnablePromptCachingBlock, AnthropicEnablePromptCachingBlock, SentinelNeverActive: true);

    private static readonly ConfigElementTemplate AnthropicCacheTtlSeconds = new(
        "cacheTtlSeconds", AnthropicCacheTtlSecondsBlock, AnthropicCacheTtlSecondsBlock, SentinelNeverActive: true);

    private static readonly ConfigElementTemplate AnthropicMinCacheableTokens = new(
        "minCacheableTokens", AnthropicMinCacheableTokensBlock, AnthropicMinCacheableTokensBlock, SentinelNeverActive: true);

    private static readonly ConfigElementTemplate EnableContextEditing = new(
        "enableContextEditing",
        "      // Enables server-side context editing (auto-clears old tool results past the trigger). Default: false.\n      // \"enableContextEditing\": false,",
        "      // Enables server-side context editing (auto-clears old tool results past the trigger). Default: false.\n      \"enableContextEditing\": false,");

    private static readonly ConfigElementTemplate ContextEditingTriggerTokens = new(
        "contextEditingTriggerTokens",
        "      // Input-token threshold that activates context editing. Default: 100000.\n      // \"contextEditingTriggerTokens\": 100000,",
        "      // Input-token threshold that activates context editing. Default: 100000.\n      \"contextEditingTriggerTokens\": 100000,");

    private static readonly ConfigElementTemplate ContextEditingKeepToolUses = new(
        "contextEditingKeepToolUses",
        "      // How many recent tool-use/result pairs context editing preserves when it clears. Default: 3.\n      // \"contextEditingKeepToolUses\": 3,",
        "      // How many recent tool-use/result pairs context editing preserves when it clears. Default: 3.\n      \"contextEditingKeepToolUses\": 3,");

    private static readonly ConfigElementTemplate Anthropic = new(
        "anthropic",
        "    // Anthropic/Claude-specific overrides.\n" +
        "    \"anthropic\": {\n" +
        string.Join("\n\n", AnthropicEnablePromptCachingBlock, AnthropicCacheTtlSecondsBlock, AnthropicMinCacheableTokensBlock,
            EnableContextEditing.CommentedBlock, ContextEditingTriggerTokens.CommentedBlock, ContextEditingKeepToolUses.CommentedBlock) +
        "\n    },",
        "    // Anthropic/Claude-specific overrides.\n" +
        "    \"anthropic\": {\n" +
        string.Join("\n\n", AnthropicEnablePromptCachingBlock, AnthropicCacheTtlSecondsBlock, AnthropicMinCacheableTokensBlock,
            EnableContextEditing.UncommentedBlock, ContextEditingTriggerTokens.UncommentedBlock, ContextEditingKeepToolUses.UncommentedBlock) +
        "\n    },",
        [AnthropicEnablePromptCaching, AnthropicCacheTtlSeconds, AnthropicMinCacheableTokens,
            EnableContextEditing, ContextEditingTriggerTokens, ContextEditingKeepToolUses]);

    private static readonly ConfigElementTemplate DeepSeekEnablePromptCaching = new(
        "enablePromptCaching",
        "      // Purely advisory; DeepSeek caches automatically regardless of this value. Default: true.\n      // \"enablePromptCaching\": true,",
        "      // Purely advisory; DeepSeek caches automatically regardless of this value. Default: true.\n      \"enablePromptCaching\": true,");

    private static readonly ConfigElementTemplate DeepSeek = new(
        "deepSeek",
        "    // DeepSeek-specific overrides. DeepSeek's disk cache is always on server-side regardless of this.\n" +
        "    \"deepSeek\": {\n" + DeepSeekEnablePromptCaching.CommentedBlock + "\n    },",
        "    // DeepSeek-specific overrides. DeepSeek's disk cache is always on server-side regardless of this.\n" +
        "    \"deepSeek\": {\n" + DeepSeekEnablePromptCaching.UncommentedBlock + "\n    },",
        [DeepSeekEnablePromptCaching]);

    private static readonly ConfigElementTemplate QwenEnablePromptCaching = new(
        "enablePromptCaching",
        "      // Overrides enablePromptCaching for Qwen's explicit cache markers. Default: true.\n      // \"enablePromptCaching\": true,",
        "      // Overrides enablePromptCaching for Qwen's explicit cache markers. Default: true.\n      \"enablePromptCaching\": true,");

    private static readonly ConfigElementTemplate QwenCacheTtlSeconds = new(
        "cacheTtlSeconds",
        "      // Overrides cacheTtlSeconds for Qwen (fixed ~300s, resets on hit). Default: 300.\n      // \"cacheTtlSeconds\": 300,",
        "      // Overrides cacheTtlSeconds for Qwen (fixed ~300s, resets on hit). Default: 300.\n      \"cacheTtlSeconds\": 300,");

    private static readonly ConfigElementTemplate QwenMinCacheableTokens = new(
        "minCacheableTokens",
        "      // Overrides minCacheableTokens for Qwen's explicit cache. Default: 1024.\n      // \"minCacheableTokens\": 1024,",
        "      // Overrides minCacheableTokens for Qwen's explicit cache. Default: 1024.\n      \"minCacheableTokens\": 1024,");

    private static readonly ConfigElementTemplate Qwen = new(
        "qwen",
        "    // Qwen/DashScope-specific overrides. Controls only Qwen's explicit cache; the implicit cache is always on.\n" +
        "    \"qwen\": {\n" +
        string.Join("\n\n", QwenEnablePromptCaching.CommentedBlock, QwenCacheTtlSeconds.CommentedBlock, QwenMinCacheableTokens.CommentedBlock) +
        "\n    },",
        "    // Qwen/DashScope-specific overrides. Controls only Qwen's explicit cache; the implicit cache is always on.\n" +
        "    \"qwen\": {\n" +
        string.Join("\n\n", QwenEnablePromptCaching.UncommentedBlock, QwenCacheTtlSeconds.UncommentedBlock, QwenMinCacheableTokens.UncommentedBlock) +
        "\n    },",
        [QwenEnablePromptCaching, QwenCacheTtlSeconds, QwenMinCacheableTokens]);

    private static readonly ConfigElementTemplate OpenSourceEnablePromptCaching = new(
        "enablePromptCaching",
        "      // Gates whether useFlashAttention/kvCacheQuantization are applied. Default: true.\n      // \"enablePromptCaching\": true,",
        "      // Gates whether useFlashAttention/kvCacheQuantization are applied. Default: true.\n      \"enablePromptCaching\": true,");

    private static readonly ConfigElementTemplate UseFlashAttention = new(
        "useFlashAttention",
        "      // Enables Flash Attention (reduces memory growth as context grows). Default: true.\n      // \"useFlashAttention\": true,",
        "      // Enables Flash Attention (reduces memory growth as context grows). Default: true.\n      \"useFlashAttention\": true,");

    private static readonly ConfigElementTemplate KvCacheQuantization = new(
        "kvCacheQuantization",
        "      // KV-cache quantization type: \"f16\" (full precision), \"q8_0\", or \"q4_0\". Default: \"q8_0\".\n      // \"kvCacheQuantization\": \"q8_0\",",
        "      // KV-cache quantization type: \"f16\" (full precision), \"q8_0\", or \"q4_0\". Default: \"q8_0\".\n      \"kvCacheQuantization\": \"q8_0\",");

    private static readonly ConfigElementTemplate NumCtx = new(
        "numCtx",
        "      // Context window size in tokens (num_ctx). Default: 8192.\n      // \"numCtx\": 8192,",
        "      // Context window size in tokens (num_ctx). Default: 8192.\n      \"numCtx\": 8192,");

    private static readonly ConfigElementTemplate OpenSource = new(
        "openSource",
        "    // Ollama-hosted open-source family (Llama/Mistral/Gemma) overrides — no server-side prompt cache.\n" +
        "    \"openSource\": {\n" +
        string.Join("\n\n", OpenSourceEnablePromptCaching.CommentedBlock, UseFlashAttention.CommentedBlock,
            KvCacheQuantization.CommentedBlock, NumCtx.CommentedBlock) +
        "\n    },",
        "    // Ollama-hosted open-source family (Llama/Mistral/Gemma) overrides — no server-side prompt cache.\n" +
        "    \"openSource\": {\n" +
        string.Join("\n\n", OpenSourceEnablePromptCaching.UncommentedBlock, UseFlashAttention.UncommentedBlock,
            KvCacheQuantization.UncommentedBlock, NumCtx.UncommentedBlock) +
        "\n    },",
        [OpenSourceEnablePromptCaching, UseFlashAttention, KvCacheQuantization, NumCtx]);

    private static readonly ConfigElementTemplate AgentConfig = new(
        "agentConfig",
        "  // Per-provider context-reduction overrides (prompt/context caching, context editing, KV-cache tuning).\n" +
        "  \"agentConfig\": {\n" +
        AgentEnablePromptCaching.CommentedBlock + "\n\n" + AgentCacheTtlSeconds.CommentedBlock + "\n\n" + AgentMinCacheableTokens.CommentedBlock + "\n\n" +
        Anthropic.CommentedBlock + "\n\n" + DeepSeek.CommentedBlock + "\n\n" + Qwen.CommentedBlock + "\n\n" + OpenSource.CommentedBlock + "\n" +
        "  },",
        "  // Per-provider context-reduction overrides (prompt/context caching, context editing, KV-cache tuning).\n" +
        "  \"agentConfig\": {\n" +
        AgentEnablePromptCaching.UncommentedBlock + "\n\n" + AgentCacheTtlSeconds.UncommentedBlock + "\n\n" + AgentMinCacheableTokens.UncommentedBlock + "\n\n" +
        Anthropic.UncommentedBlock + "\n\n" + DeepSeek.UncommentedBlock + "\n\n" + Qwen.UncommentedBlock + "\n\n" + OpenSource.UncommentedBlock + "\n" +
        "  },",
        [AgentEnablePromptCaching, AgentCacheTtlSeconds, AgentMinCacheableTokens, Anthropic, DeepSeek, Qwen, OpenSource]);

    // ── lazyContext ─────────────────────────────────────────────────────────────

    private static readonly ConfigElementTemplate DefaultWillAskForMissingContext = new(
        "defaultWillAskForMissingContext",
        "    // Default WillAskForMissingContext for any client name with no adapter and no override entry. Default: false.\n    // \"defaultWillAskForMissingContext\": false,",
        "    // Default WillAskForMissingContext for any client name with no adapter and no override entry. Default: false.\n    \"defaultWillAskForMissingContext\": false,");

    // A free-form per-client-name dictionary, not a fixed schema shape — treated as one opaque
    // leaf rather than recursed into, so arbitrary user-added client-name keys are never touched.
    private static readonly ConfigElementTemplate Overrides = new(
        "overrides",
        "    // Per-client-name overrides, keyed by the MCP-reported client name (case-insensitive).\n" +
        "    \"overrides\": {\n      // \"claude-code\": true\n    },",
        "    // Per-client-name overrides, keyed by the MCP-reported client name (case-insensitive).\n" +
        "    \"overrides\": {\n      \"claude-code\": true\n    },");

    private static readonly ConfigElementTemplate LazyContext = new(
        "lazyContext",
        "  // Per-client lazy-context overrides — whether a calling client can be trusted to re-ask for omitted content.\n" +
        "  \"lazyContext\": {\n" + DefaultWillAskForMissingContext.CommentedBlock + "\n\n" + Overrides.CommentedBlock + "\n  },",
        "  // Per-client lazy-context overrides — whether a calling client can be trusted to re-ask for omitted content.\n" +
        "  \"lazyContext\": {\n" + DefaultWillAskForMissingContext.UncommentedBlock + "\n\n" + Overrides.UncommentedBlock + "\n  },",
        [DefaultWillAskForMissingContext, Overrides]);

    /// <summary>
    ///     Every top-level <see cref="TokenSaverBehaviorConfig" /> section, in template render
    ///     order. <see cref="ConfigUpgradeService" /> walks this tree recursively against an
    ///     existing config.json's actual structure to find every missing node — section, nested
    ///     object, or individual leaf setting — at any depth.
    /// </summary>
    internal static readonly IReadOnlyList<ConfigElementTemplate> RootElements =
        [RepoHelper, CodeHelper, TokenEconomy, Context, AgentConfig, LazyContext];

    public static string CommentedFileTemplate =>
        "{\n" +
        "  // TokenSaver also reads a machine-global config at %ProgramData%\\TokenSaver\\config.json,\n" +
        "  // shared across every workspace on this machine. Any setting uncommented in THIS file\n" +
        "  // (local) always wins over the same setting in the global file — local overrides global,\n" +
        "  // never the other way around.\n" +
        "\n" +
        string.Join("\n\n", RootElements.Select(e => e.CommentedBlock)) +
        "\n}\n";

    public static string UncommentedFileTemplate =>
        "{\n" +
        "  // This is the machine-global TokenSaver config — every setting below is active by\n" +
        "  // default (not commented out) and applies to EVERY workspace on this machine unless a\n" +
        "  // given workspace's own .tokensaver/config.json overrides it (local always wins).\n" +
        "  //\n" +
        "  // WARNING: this file lives outside any git repo, at %ProgramData%\\TokenSaver\\config.json.\n" +
        "  // It does not travel with you to a new machine, isn't backed up by source control, and\n" +
        "  // is invisible to anyone else working in a shared repo. Prefer setting overrides in a\n" +
        "  // workspace's .tokensaver/config.json instead — it's versioned with the repo and follows\n" +
        "  // the project wherever it's cloned. Use this global file only for genuinely per-machine\n" +
        "  // preferences you want to apply everywhere without repeating them in every workspace.\n" +
        "\n" +
        string.Join("\n\n", RootElements.Select(e => e.UncommentedBlock)) +
        "\n}\n";
}

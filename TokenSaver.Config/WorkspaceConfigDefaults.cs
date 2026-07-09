using Microsoft.Extensions.Logging;

namespace TokenSaver.Config;

/// <summary>
///     Creates the <c>.tokensaver/</c> folder and its default config files for a workspace —
///     both the first time the MCP server loads a workspace, and again on every later MCP
///     session connect if a file has since been deleted — so the files exist on disk
///     (discoverable/editable) instead of only ever being implied by "file not found = defaults".
///     Idempotent: an existing file is never overwritten, only ever re-created when missing.
/// </summary>
public static partial class WorkspaceConfigDefaults
{
    private const string LocalFolder = ".tokensaver";

    /// <summary>
    ///     Ensures <c>.tokensaver/config.json</c>, <c>.ignore-additional</c>, and
    ///     <c>.ignore-bypass</c> exist under <paramref name="workspaceRoot" />, (re)creating each
    ///     with its documented default content if missing. Best-effort: I/O failures are logged
    ///     and swallowed so a locked/read-only workspace never blocks server startup or a session
    ///     connect.
    ///     <para>
    ///     <c>config.json</c> is written as a JSON-with-comments template: every known setting is
    ///     present, commented out, each preceded by a one-line <c>//</c> comment describing what
    ///     it does and what its default value is. This makes the file self-documenting and
    ///     directly editable — uncomment a line and change the value to override it. The reader
    ///     (<see cref="BehaviorConfigService" />) uses <c>JsonCommentHandling.Skip</c>, so the
    ///     comments and commented-out lines are safely ignored until uncommented.
    ///     </para>
    ///     Does not create <c>context-exclusions.json</c> — that JSON-format exclusion list was
    ///     retired in favor of the gitignore-syntax <c>.ignore-additional</c> file, which already
    ///     supports everything it could express (plus real glob patterns). A pre-existing
    ///     <c>context-exclusions.json</c> from an older workspace is left on disk untouched but is
    ///     no longer read by anything; move any entries into <c>.ignore-additional</c> by hand.
    /// </summary>
    public static void EnsureDefaults(string workspaceRoot, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string localDir = Path.Combine(workspaceRoot, LocalFolder);

        try
        {
            _ = Directory.CreateDirectory(localDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogEnsureDefaultsFailed(logger, localDir, ex);
            return;
        }

        EnsureFile(logger, Path.Combine(localDir, BehaviorConfigService.ConfigFileName), CommentedConfigTemplate);

        EnsureFile(
            logger,
            Path.Combine(localDir, ".ignore-additional"),
            "# TokenSaver: extra gitignore-syntax patterns to exclude from indexing/context," + Environment.NewLine +
            "# in addition to .gitignore. One pattern per line; same syntax as .gitignore." + Environment.NewLine);

        EnsureFile(
            logger,
            Path.Combine(localDir, ".ignore-bypass"),
            "# TokenSaver: gitignore-syntax patterns that force-include paths otherwise ignored" + Environment.NewLine +
            "# by .gitignore/.ignore-additional. One pattern per line; same syntax as .gitignore." + Environment.NewLine);
    }

    /// <summary>
    ///     Full <c>config.json</c> template: every <see cref="TokenSaverBehaviorConfig" /> leaf
    ///     property, commented out, preceded by a one-line description of what it controls and
    ///     its default value. Kept as a hand-written literal (rather than reflected from the
    ///     record types) so the wording can stay short and user-facing instead of echoing the
    ///     longer XML-doc remarks verbatim.
    /// </summary>
    private static string CommentedConfigTemplate =>
        string.Join(Environment.NewLine, new[]
        {
            "{",
            "  // Whether .tokensaver/ should be added to the workspace .gitignore. Unset = you haven't been asked yet.",
            "  // \"addTokenSaverToGitignore\": true,",
            "",
            "  // CodeHelper: auto-workflow behavior — whether/how the local model assists with code steps.",
            "  \"codeHelper\": {",
            "    // Master switch for CodeHelper auto-workflow. Default: false.",
            "    // \"autoEnabled\": false,",
            "",
            "    // Require an exact anchor-text match before applying a patch. Default: true.",
            "    // \"requireAnchorMatch\": true,",
            "",
            "    // Composite-score thresholds deciding whether CodeHelper owns, defers, or asks about a step.",
            "    \"ownership\": {",
            "      // Score at/above which CodeHelper claims full ownership of a step. Default: 0.70.",
            "      // \"canDoThreshold\": 0.70,",
            "",
            "      // Score below which CodeHelper defers the step entirely to the calling AI. Default: 0.40.",
            "      // \"deferThreshold\": 0.40",
            "    },",
            "",
            "    // Thresholds the agentic planner uses while building a plan for a step it owns.",
            "    \"planner\": {",
            "      // Below this confidence, hand the step to the calling AI instead of planning locally. Default: 0.65.",
            "      // \"confidenceThreshold\": 0.65,",
            "",
            "      // Below this, invoke Ollama for the plan rather than the built-in planner alone. Default: 0.55.",
            "      // \"modelCallThreshold\": 0.55",
            "    },",
            "",
            "    // Tuning for how calibration weights shift after each recorded outcome.",
            "    \"calibration\": {",
            "      // Fraction of scaffolded work redone before a weight counts as \"not worth it\". Default: 0.40.",
            "      // \"wasteThreshold\": 0.40,",
            "",
            "      // Step size for weight updates each time an outcome is recorded (0.0-1.0). Default: 0.10.",
            "      // \"learningRate\": 0.10",
            "    }",
            "  },",
            "",
            "  // TokenEconomy: distillation/terse-mode kill switches (all default on unless noted).",
            "  \"tokenEconomy\": {",
            "    // Master switch for distillation. false = every distilling tool returns raw text. Default: true.",
            "    // \"enableDistill\": true,",
            "",
            "    // Hard kill switch for terse mode; false blocks even an explicit per-call terse:true. Default: true.",
            "    // \"allowTerse\": true,",
            "",
            "    // Server-wide default for terse mode when a call doesn't pass its own terse: argument. Default: false.",
            "    // \"terseByDefault\": false,",
            "",
            "    // Whether distilling tools write raw output to disk before distilling. Default: true.",
            "    // \"enableRawRetention\": true,",
            "",
            "    // Whether every distill() call records a row to the TokenSavings table. Default: true.",
            "    // \"enableSavingsTracking\": true,",
            "",
            "    // Whether IndexRepo embeds each chunk's one-line summary instead of the raw chunk text. Default: true.",
            "    // \"enableSummaryEmbedding\": true",
            "  },",
            "",
            "  // Context-pipeline overrides for the concise-context cache and auto-cleaning middleware.",
            "  \"context\": {",
            "    // Whether the server builds an initial concise-context cache automatically at startup. Default: true.",
            "    // \"autoWarmOnStartup\": true,",
            "",
            "    // Whether every prompt-bearing MCP tool call gets auto-cleaned before forwarding. Default: true.",
            "    // \"autoIntercept\": true,",
            "",
            "    // Token budget a built context is capped at. Default: 120000.",
            "    // \"contextSizeLimit\": 120000,",
            "",
            "    // Fraction of contextSizeLimit the pipeline targets before trimming further. Default: 0.85.",
            "    // \"tokenBudgetMargin\": 0.85,",
            "",
            "    // Minimum embedding-similarity score for a chunk to survive the semantic relevance filter. Default: 0.35.",
            "    // \"semanticThreshold\": 0.35,",
            "",
            "    // Save raw/cleaned context text to .tokensaver/debug/ for every run and warn if it didn't shrink. Default: false.",
            "    // \"debug\": false",
            "  },",
            "",
            "  // Per-provider context-reduction overrides (prompt/context caching, context editing, KV-cache tuning).",
            "  \"agentConfig\": {",
            "    // Master switch for provider-native prompt/context caching (overridable per family below). Default: true.",
            "    // \"enablePromptCaching\": true,",
            "",
            "    // How long a cached prefix stays valid, in seconds, for providers with a configurable TTL. Default: 300.",
            "    // \"cacheTtlSeconds\": 300,",
            "",
            "    // Minimum prefix size, in tokens, before a provider bothers caching it. Default: 1024.",
            "    // \"minCacheableTokens\": 1024,",
            "",
            "    // Anthropic/Claude-specific overrides.",
            "    \"anthropic\": {",
            "      // Overrides enablePromptCaching for Anthropic calls. Default: unset (falls back to agentConfig).",
            "      // \"enablePromptCaching\": true,",
            "",
            "      // Overrides cacheTtlSeconds for Anthropic. Accepts only 300 (\"5m\") or 3600 (\"1h\"). Default: unset.",
            "      // \"cacheTtlSeconds\": 300,",
            "",
            "      // Overrides minCacheableTokens for Anthropic. Default: unset.",
            "      // \"minCacheableTokens\": 1024,",
            "",
            "      // Enables server-side context editing (auto-clears old tool results past the trigger). Default: false.",
            "      // \"enableContextEditing\": false,",
            "",
            "      // Input-token threshold that activates context editing. Default: 100000.",
            "      // \"contextEditingTriggerTokens\": 100000,",
            "",
            "      // How many recent tool-use/result pairs context editing preserves when it clears. Default: 3.",
            "      // \"contextEditingKeepToolUses\": 3",
            "    },",
            "",
            "    // DeepSeek-specific overrides. DeepSeek's disk cache is always on server-side regardless of this.",
            "    \"deepSeek\": {",
            "      // Purely advisory; DeepSeek caches automatically regardless of this value. Default: true.",
            "      // \"enablePromptCaching\": true",
            "    },",
            "",
            "    // Qwen/DashScope-specific overrides. Controls only Qwen's explicit cache; the implicit cache is always on.",
            "    \"qwen\": {",
            "      // Overrides enablePromptCaching for Qwen's explicit cache markers. Default: true.",
            "      // \"enablePromptCaching\": true,",
            "",
            "      // Overrides cacheTtlSeconds for Qwen (fixed ~300s, resets on hit). Default: 300.",
            "      // \"cacheTtlSeconds\": 300,",
            "",
            "      // Overrides minCacheableTokens for Qwen's explicit cache. Default: 1024.",
            "      // \"minCacheableTokens\": 1024",
            "    },",
            "",
            "    // Ollama-hosted open-source family (Llama/Mistral/Gemma) overrides — no server-side prompt cache.",
            "    \"openSource\": {",
            "      // Gates whether useFlashAttention/kvCacheQuantization are applied. Default: true.",
            "      // \"enablePromptCaching\": true,",
            "",
            "      // Enables Flash Attention (reduces memory growth as context grows). Default: true.",
            "      // \"useFlashAttention\": true,",
            "",
            "      // KV-cache quantization type: \"f16\" (full precision), \"q8_0\", or \"q4_0\". Default: \"q8_0\".",
            "      // \"kvCacheQuantization\": \"q8_0\",",
            "",
            "      // Context window size in tokens (num_ctx). Default: 8192.",
            "      // \"numCtx\": 8192",
            "    }",
            "  },",
            "",
            "  // Per-client lazy-context overrides — whether a calling client can be trusted to re-ask for omitted content.",
            "  \"lazyContext\": {",
            "    // Default WillAskForMissingContext for any client name with no adapter and no override entry. Default: false.",
            "    // \"defaultWillAskForMissingContext\": false,",
            "",
            "    // Per-client-name overrides, keyed by the MCP-reported client name (case-insensitive).",
            "    \"overrides\": {",
            "      // \"claude-code\": true",
            "    }",
            "  }",
            "}",
            "",
        });

    private static void EnsureFile(ILogger logger, string path, string defaultContent)
    {
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, defaultContent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogEnsureDefaultsFailed(logger, path, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create default TokenSaver config at {Path}; continuing without it")]
    private static partial void LogEnsureDefaultsFailed(ILogger logger, string path, Exception ex);
}

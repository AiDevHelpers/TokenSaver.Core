namespace TokenSaver.Library.Configuration;

/// <summary>
///     Configuration for the token-economy tool surface (distillation, terse-mode, chunked
///     repo indexing/semantic search, and savings tracking) — see
///     TokenSaver/docs/token-economy-tools-contract.md for the full behavioral spec.
///     Bound under <c>TokenSaver:TokenEconomy</c> in appsettings.json, or overridden via
///     <c>TOKENSAVER_TokenSaver__TokenEconomy__&lt;PropertyName&gt;</c> env vars, following the
///     same binding convention as the rest of <see cref="TokenSaverConfig" />.
/// </summary>
public class TokenEconomyConfig
{
    // ---- Per-feature kill switches --------------------------------------------------------
    // Every independent token-saving mechanism gets its own on/off switch so one can be turned
    // off (e.g. distillation for a role that turns out not worth it, per WorthKeeping) without
    // touching the others. All default to true — this is a kill-switch list, not opt-in.

    /// <summary>
    ///     Master switch for distillation itself. False = every distilling tool returns raw text
    ///     (still capped at <see cref="RawTextCapChars" /> chars) instead of calling Ollama.
    /// </summary>
    public bool EnableDistill { get; set; } = true;

    /// <summary>
    ///     Allows <see cref="Terse" /> (and any per-call <c>terse:true</c>) to take effect at all.
    ///     False = terse mode is ignored everywhere, even if explicitly requested per-call.
    /// </summary>
    public bool EnableTerse { get; set; } = true;

    /// <summary>
    ///     Whether distilling tools write their raw output to
    ///     <c>&lt;WorkspaceRoot&gt;/.tokensaver/raw/&lt;tool&gt;/&lt;timestamp&gt;.txt</c> before
    ///     distilling. False = skip the write; summary-only, no escape hatch back to raw output.
    /// </summary>
    public bool EnableRawRetention { get; set; } = true;

    /// <summary>
    ///     Whether every distill() call records a row to the TokenSavings table. False = distill()
    ///     still runs and still compresses output, it just isn't tracked — TokenSavingsReport and
    ///     WorthKeeping will have nothing to report while this is off.
    /// </summary>
    public bool EnableSavingsTracking { get; set; } = true;

    /// <summary>
    ///     Whether IndexRepo embeds each chunk's one-line summary (cheap, what SemanticGrep
    ///     actually shows Claude anyway) rather than the raw chunk text (more accurate recall,
    ///     heavier disk/compute, no token-savings angle since embeddings never reach Claude either
    ///     way).
    /// </summary>
    public bool EnableSummaryEmbedding { get; set; } = true;

    // ---- Mode toggle + tuning (only meaningful while their ENABLE_* flag above is on) --------

    /// <summary>
    ///     Server-wide default for terse mode. False (default) = normal role prompts/maxTokens.
    ///     True = terse prompts + reduced maxTokens ceiling everywhere, unless overridden per-call.
    ///     Ignored entirely when <see cref="EnableTerse" /> is false.
    /// </summary>
    public bool Terse { get; set; }

    /// <summary>Below this percent saved, WorthKeeping/TokenSavingsReport flag a tool/role as not worth it, regardless of latency.</summary>
    public int WorthItMinPct { get; set; } = 30;

    /// <summary>Above this average latency (ms), WorthKeeping/TokenSavingsReport flag a tool/role as not worth it, regardless of savings.</summary>
    public int WorthItMaxMs { get; set; } = 4000;

    /// <summary>Minimum number of recorded calls before WorthKeeping/TokenSavingsReport will render a verdict instead of "not enough data yet".</summary>
    public int WorthItMinCalls { get; set; } = 5;

    /// <summary>
    ///     task_type -&gt; Ollama model name, used by the ModelRoute tool and internally by
    ///     chunk summarization. Keys match the plan's original routing table: code, chat,
    ///     summarize, embed, rerank. Missing keys fall back to <see cref="OllamaModel"/>... actually
    ///     falls back to the "chat" entry (see ModelRouteResolver).
    /// </summary>
    public Dictionary<string, string> ModelRoutes { get; set; } = new()
    {
        ["code"] = "qwen2.5-coder:7b",
        ["chat"] = "llama3.2:3b",
        ["summarize"] = "qwen2.5-coder:7b",
        ["embed"] = "nomic-embed-text",
        ["rerank"] = "qwen2.5-coder:7b"
    };

    /// <summary>Hard cap on raw input text (chars) fed to any distillation/summarization prompt, to keep local inference fast.</summary>
    public int RawTextCapChars { get; set; } = 16_000;

    /// <summary>Terse mode multiplies the caller's requested maxTokens by this ratio.</summary>
    public double TerseMaxTokensRatio { get; set; } = 0.5;
}

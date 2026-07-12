namespace TokenSaver.Common.Core.Configuration;

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
        // qwen2.5-coder:7b isn't pulled in this dev environment (only 3b/0.5b variants are) —
        // pointed at 3b 2026-07-10 so the conditional-rerank pass in ConditionalRetrievalService
        // actually fires instead of silently falling back to embed-only every call. Its judged
        // scores ran conservative in a smoke test (~0.4 for a clearly-correct candidate) — revisit
        // SemanticGrepAbstainThreshold if that turns out to cause false abstains post-rerank.
        ["rerank"] = "qwen2.5-coder:3b"
    };

    /// <summary>Hard cap on raw input text (chars) fed to any distillation/summarization prompt, to keep local inference fast.</summary>
    public int RawTextCapChars { get; set; } = 16_000;

    /// <summary>Terse mode multiplies the caller's requested maxTokens by this ratio.</summary>
    public double TerseMaxTokensRatio { get; set; } = 0.5;

    /// <summary>
    ///     Cosine-similarity floor below which <c>SemanticGrep</c> returns
    ///     <c>{"match":"none", best_candidate, confidence}</c> instead of handing back its
    ///     top-scoring hit as if it were relevant (see <c>docs/retrieval-design.md</c> §3, the
    ///     abstain path). Calibrated 2026-07-10 against the 332-query golden set
    ///     (<c>docs/retrieval-benchmark/queries.jsonl</c>) by sweeping thresholds over
    ///     <c>production-eval-results.json</c> to minimize (negative-query false positives) +
    ///     (correct top-1 hits wrongly suppressed). At 0.66 that's 6/15 negative queries still
    ///     slipping through (embed-only similarity can't fully separate the "muddy middle" — see
    ///     retrieval-design.md §2/#4) against 5/246 correct hits suppressed. Closing the remaining
    ///     gap needs the conditional-rerank pass, not further threshold tuning. Re-run
    ///     <c>run_eval.py</c> and recalibrate if the embedding model or corpus changes materially.
    /// </summary>
    public double SemanticGrepAbstainThreshold { get; set; } = 0.66;

    /// <summary>
    ///     Master switch for the conditional-rerank pass (<c>docs/retrieval-design.md</c> §2).
    ///     False = <c>SemanticGrep</c> always uses embed-only ranking, matching pre-rerank
    ///     behavior — useful if the "rerank" model route isn't a capable-enough model to trust for
    ///     judged scoring.
    /// </summary>
    public bool EnableConditionalRerank { get; set; } = true;

    /// <summary>
    ///     Embed top-1 score at or above which, combined with <see cref="SemanticSearchClearMargin" />,
    ///     a query counts as a "clear winner" and skips reranking (retrieval-design.md §2's
    ///     HIGH_CONF). Calibrated 2026-07-10 against the golden set together with
    ///     <see cref="SemanticSearchClearMargin" />: at 0.79/0.05 every adversarial/conceptual
    ///     ("hard") query in the 332-query set has a top-1 score below this floor or too small a
    ///     margin, so 100% of them correctly fall through to reranking; 70.5% of doc_derived/
    ///     symbol_lookup ("easy") queries clear both bars and skip it. The design doc's aspirational
    ///     target was 95% easy-skip/90% hard-rerank — this data doesn't support hitting both at
    ///     once (hard- and easy-query top-1 scores overlap too much on embed-only similarity alone),
    ///     so the calibration favors never missing a hard query over minimizing rerank latency on
    ///     easy ones. Re-run the sweep in <c>docs/retrieval-design.md</c> §2 if the embedding model
    ///     or corpus changes.
    /// </summary>
    public double SemanticSearchHighConfidence { get; set; } = 0.79;

    /// <summary>Minimum gap between top-1 and top-2 embed scores required alongside <see cref="SemanticSearchHighConfidence" /> to count as a clear winner (retrieval-design.md §2's CLEAR_MARGIN).</summary>
    public double SemanticSearchClearMargin { get; set; } = 0.05;

    /// <summary>
    ///     How many embed-ranked candidates to pull before the conditional-rerank decision
    ///     (retrieval-design.md §2's candidate pool feeding RERANK_K). The K=4/8-vs-12 latency
    ///     sweep from the design doc was benchmarked against the candidate Qwen3-Reranker-0.6B
    ///     cross-encoder pair specifically and doesn't transfer directly to this LLM-judged rerank
    ///     (see <c>RerankService</c> doc comment) — its cost driver is reliability, not latency: a
    ///     small chat model asked to score a JSON array unreliably drops/adds entries as the array
    ///     gets longer. Live-measured 2026-07-10 against the 332-query golden set with
    ///     <c>qwen2.5-coder:3b</c> on the "rerank" route (<c>docs/retrieval-benchmark/rerank_eval.py</c>):
    ///     at pool size 12 only 37% of muddy-middle queries got a parseable score array (63%
    ///     silently fell back to embed-only); shrinking to 6 raised that to 92% and dropped
    ///     negative-query false-positive rate from 33.3% to 20.0% with recall held flat — see
    ///     <c>docs/retrieval-design.md</c> §2 for the full comparison. Re-run that sweep before
    ///     changing this further, or if the rerank model changes (a more capable model may tolerate
    ///     a larger pool).
    /// </summary>
    public int SemanticSearchRerankCandidatePoolSize { get; set; } = 6;
}

# Conditional-Rerank Live Eval

Model: `qwen2.5-coder:3b` on the "rerank" route. Pool size 6. Thresholds: floor=0.66, high_conf=0.79, margin=0.05.

Bucket counts: clear_winner=198, below_floor=37, muddy_middle=97 (rerank_fired=89, rerank_fallback=8).

| Category | N | Recall@1 | Recall@5 |
|---|---|---|---|
| doc_derived | 236 | 91.95% | 99.58% |
| symbol_lookup | 45 | 17.78% | 44.44% |
| conceptual | 25 | 29.41% | 58.82% |
| adversarial | 11 | 30.00% | 70.00% |
| negative | 15 | 0.00% | 0.00% |

**Negative-query false-positive rate (with rerank live)**: 80.0% above 0.5, 20.0% above the actual production abstain floor (0.66) — compare against the embed-only baseline in PRODUCTION-EVAL.md (100% at both thresholds).

doc_derived+symbol_lookup recall@5 (with rerank live): 90.7% — compare against PRODUCTION-EVAL.md's embed-only baseline (91.1%).
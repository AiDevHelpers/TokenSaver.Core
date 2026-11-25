# Retrieval Benchmark Results

Embed-only (no rerank), top-10, 332 queries.

| Category | N | Recall@1 | Recall@5 | Recall@10 | File Recall@K | MRR | nDCG@10 |
|---|---|---|---|---|---|---|---|
| doc_derived | 236 | 96.61% | 99.58% | 100.00% | 100.00% | 0.980 | 0.985 |
| symbol_lookup | 45 | 20.00% | 46.67% | 68.89% | 82.22% | 0.343 | 0.417 |
| conceptual | 25 | 29.41% | 58.82% | 58.82% | 66.67% | 0.397 | 0.391 |
| adversarial | 11 | 40.00% | 70.00% | 70.00% | 81.82% | 0.498 | 0.445 |
| negative | 15 | 0.00% | 0.00% | 0.00% | 0.00% | 0.000 | 0.000 |

**Too-wide check — negative-query false-positive rate**: 100.0% of 15 negative queries returned a top-1 hit above 0.5 (should be near 0; a high rate means the context builder will pull in irrelevant files for queries about functionality that doesn't exist).

**Discrimination check**: 54.5% of 11 adversarial queries ranked the correct chunk above its lexically-similar distractor (embed-only, no rerank — this is the number retrieval-design.md's conditional-rerank proposal exists to improve).

**Verdict: FAIL** — doc_derived+symbol_lookup recall@5 91.1% (floor 85%), negative FP 100.0% (ceiling 20%), adversarial discrimination 54.5% (floor 60%).

- Low recall on doc_derived/symbol_lookup -> **too narrow**: the context builder's semantic filter is dropping or never surfacing files a query should have found.
- High negative-query FP rate -> **too wide**: irrelevant chunks are scoring high enough to look relevant for queries about nonexistent functionality.
- Low adversarial discrimination -> embed-only similarity can't tell semantically-different same-named symbols apart; see retrieval-design.md's conditional-rerank plan.
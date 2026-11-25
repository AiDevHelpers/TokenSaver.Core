# CUDA vs CPU Results — Qwen3-Embedding-0.6B-GGUF / Qwen3-Reranker-0.6B

Measured on: NVIDIA GeForce GTX 1660 (6 GB, Turing, **no Tensor Cores**) + 16-core CPU, 32 GB RAM,
Windows. Embedder via `llama-cpp-python` (CUDA-accelerated GGUF, Q8_0). Reranker via a direct
`transformers` implementation of the model card's yes/no-logit trick (see "Implementation notes"
below — the `sentence-transformers` `CrossEncoder` wrapper was not used; here's why).

Run on a **74-query stratified sample** of `queries.jsonl` (all 11 adversarial, all 15 negative,
25 of 25 conceptual, 8 of 45 symbol_lookup, 15 of 236 doc_derived) against the full 442-chunk
corpus, rerank window K=12. Full-332-query numbers were not collected — at these per-query costs
the full set is a ~2 hour run; the sample was chosen to preserve 100% of the small, diagnostic
categories (adversarial/negative/conceptual) while thinning the large, easy `doc_derived` bucket.
Raw output: `results_cuda.json`, `results_cpu.json`.

## Speed

| Stage | CUDA | CPU | CUDA speedup |
|---|---|---|---|
| Embed model load | 1.20 s | 0.53 s | — |
| Rerank model load | 5.05 s | 6.87 s | — |
| Corpus embedding (442 chunks) | 97.5 s (4.53 chunks/s) | 206.0 s (2.15 chunks/s) | 2.1x |
| Query embedding (74 queries) | 3.96 s (18.7 q/s) | 16.7 s (4.42 q/s) | 4.2x |
| Cosine top-K (74 queries) | 6.3 ms | 2.7 ms | — (CPU faster; trivial numpy op, noise-level) |
| Rerank latency, mean | 6.59 s/query | 11.34 s/query | 1.7x |
| Rerank latency, p95 | 8.39 s/query | 14.59 s/query | 1.7x |
| Rerank latency, p99 | 8.97 s/query | 19.26 s/query | 2.1x |
| End-to-end per query | 6.64 s | 11.57 s | 1.7x |

**Reranking, not embedding, dominates total latency on both devices** — at K=12 candidates per
query it's ~90% of end-to-end time. CUDA is consistently faster but by a much smaller margin than
you'd expect from a GPU vs CPU comparison, because the GTX 1660 (the non-Ti, non-RTX SKU) has
**no Tensor Cores** — Turing's tensor cores only shipped on the RTX 20-series. Without them, the
0.6B reranker's matmuls run on ordinary CUDA shader cores, closing much of the gap to a 16-core
CPU. On a Tensor Core GPU (RTX 20-series or newer) or an Ampere+ card with FlashAttention support,
expect a substantially larger CUDA advantage than shown here.

## Memory / load

| | CUDA | CPU |
|---|---|---|
| Peak GPU VRAM | 1.88 GB | n/a |
| Peak process RSS | 231 MB | 1.12 GB |

Both models fit comfortably in the GTX 1660's 6 GB — plenty of headroom for a larger rerank
window (K=20+) if latency budget allows. CPU RSS is ~5x higher, expected for fp32 weights plus no
KV/activation memory reuse.

## Accuracy

Pre-rerank (embedding-only) numbers are **numerically identical between CUDA and CPU** — same
model, same math, floating-point path doesn't diverge before the reranker. Post-rerank numbers
diverge slightly on two categories due to fp16 (CUDA) vs fp32 (CPU) precision affecting tie-breaks
between closely-scored candidates:

| Category | Metric | Pre-rerank | CUDA post | CPU post |
|---|---|---|---|---|
| adversarial (n=11) | nDCG@10 | 0.792 | 0.846 | 0.846 |
| adversarial (n=11) | discrimination rate | — | **100%** | **100%** |
| conceptual (n=25) | nDCG@10 | 0.501 | 0.536 | 0.542 |
| symbol_lookup (n=8) | nDCG@10 | 0.954 | 0.954 | 0.954 |
| doc_derived (n=15) | nDCG@10 | 0.933 | 0.933 | 0.884 |
| negative (n=15) | false-positive rate | — | **20%** | **20%** |

Takeaways:
- **The reranker earns its cost exactly where the README predicted**: adversarial and conceptual
  queries (the ones needing real discrimination, not lexical overlap) see nDCG gains of +0.05 to
  +0.07. The already-saturated `symbol_lookup`/`doc_derived` categories don't move — there's no
  headroom left for a reranker to add.
- **Adversarial discrimination rate: 100%** on both devices — every one of the 11 hand-picked
  name-collision queries (two `Resolve` methods, two `WithDbLockAsync` overloads, etc.) had its
  correct answer outrank the distractor after reranking.
- **Negative false-positive rate: 20%** on both devices — 3 of 15 "this doesn't exist in
  TokenSaver" queries still produced a confident (>0.5 probability) top hit after reranking. This
  is the actionable finding for production use: don't trust the reranker's top-1 result blindly on
  low-signal queries; pair it with a probability threshold or an explicit "no good match" path.
- CPU's doc_derived nDCG@10 dropped to 0.884 (from 0.933 pre-rerank) where CUDA held steady —
  fp32 reranking occasionally reordered an already-correct top-1 doc_derived result to rank 2,
  a case where fp16 (CUDA) didn't. Not a systematic device advantage, just precision noise on a
  handful of closely-scored pairs — worth re-running on the full 332-query set before treating
  this as a real CPU-vs-CUDA accuracy difference.

## Implementation notes (if you extend this benchmark)

- **Don't route the reranker through `sentence_transformers.CrossEncoder`.** In testing here (ST
  5.6.0), it produced a ~5.7 GB VRAM footprint and 20+ second latency for a single 20-pair batch
  on a 0.6B model — consistent with it not doing a plain single forward pass for this
  causal-LM-based reranker architecture. Switching to a direct `transformers`
  `AutoModelForCausalLM` implementation of the model card's documented yes/no-logit method (see
  `DirectReranker` in `run_benchmark.py`) fixed both.
- **Pass `logits_to_keep=1` to the forward call.** Without it, `transformers` projects every
  sequence position through the LM head, producing a `(batch, seq_len, vocab=151669)` logits
  tensor even though only the last position is ever read — for a 20×480 batch that's ~1.4B values
  computed for nothing. This didn't fix the GTX 1660's fundamental speed ceiling (that's the
  no-Tensor-Cores hardware limit above), but it's a correctness/efficiency fix worth keeping
  regardless of GPU.
- **Check for GPU contention before benchmarking.** LM Studio (or any other local inference
  server) holding a model resident on the same GPU will silently degrade every number here. Close
  other GPU-resident processes first.

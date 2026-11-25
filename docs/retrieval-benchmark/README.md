# TokenSaver Code Retrieval Benchmark — Qwen3-Embedding-0.6B / Qwen3-Reranker-0.6B

A labeled test set for benchmarking an embed→rerank pair on the task TokenSaver actually does:
finding relevant code chunks in a real codebase given a natural-language query. Built from
TokenSaver's own source (`F:\mcp\TokenSaver`), so results reflect the corpus you'll really index,
not a generic public benchmark.

Target models:

- **Embedder**: [Qwen/Qwen3-Embedding-0.6B-GGUF](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF)
  — 28 layers, 32K context, embedding dim up to 1024 (MRL, truncatable 32–1024), instruction-aware.
  Runs via `llama.cpp` (`llama-embedding` / `llama-server --embedding --pooling last`).
- **Reranker**: [Qwen/Qwen3-Reranker-0.6B](https://huggingface.co/Qwen/Qwen3-Reranker-0.6B)
  — 28 layers, 32K context, cross-encoder scored via the `yes`/`no` logit trick (see model card),
  instruction-aware. Runs via `transformers`, `sentence-transformers` `CrossEncoder`, or `vllm`.

Both are Apache-2.0, part of the same Qwen3 Embedding series (paper: arXiv:2506.05176).

## Files

- `corpus.jsonl` — 442 code chunks extracted from TokenSaver's `.cs` source, one JSON object per
  line: `chunk_id`, `file`, `kind` (class/interface/record/enum/method/property), `name`,
  `start_line`, `end_line`, `summary` (cleaned XML `<summary>` text), `code` (the snippet,
  capped at 140 lines).
- `queries.jsonl` — 332 queries, one JSON object per line: `query_id`, `query`, `category`,
  `difficulty`, `relevant` (list of `{chunk_id, grade}`, empty for negative queries), `notes`.
- `extract_corpus.py` — the extraction script, in case the corpus needs to be regenerated after
  the repo changes (e.g. to re-run against a newer commit for a fresh benchmark).

## How the corpus was built

Every public/internal C# class, interface, record, struct, enum, method, and property in `src/`
that carries an XML `/// <summary>` doc comment became one chunk (regex + brace-matching
extraction, not a full Roslyn parse — good enough for benchmark corpus, not for production
indexing). 442 chunks resulted from 210 `.cs` files. `<see cref>`/`<paramref>` tags in summaries
were stripped to plain text.

This mirrors how a real "find relevant code" query would actually hit TokenSaver's own index —
documented, meaningfully-named symbols, not every brace-delimited block in the repo.

## How the queries were built (332 total)

| Category | Count | How | Relevance |
|---|---|---|---|
| `doc_derived` | 236 | Auto-generated from the chunk's own doc summary, rotated through 4 phrasing templates (verbatim statement, "how does the code…?", "I need to…", "find the code responsible for…") so the set isn't 100% literal string overlap. | 1 chunk, grade 3 |
| `symbol_lookup` | 45 | Exact-name lookups ("Where is X defined?", "Show me the X class") over classes/interfaces/records. | 1 chunk, grade 3 |
| `conceptual` | 25 | Hand-written, requiring domain/cross-file understanding TokenSaver's own docs give you (e.g. "how does TokenSaver parse symbols across languages" → 3 parser classes). Several are intentionally multi-relevant or graded 2/3 to test whether the pair surfaces *all* correct chunks, not just one. | 1–4 chunks, graded 1–3 |
| `adversarial` | 11 | Hand-picked from real name collisions in the corpus (two `Resolve` methods, two `WithDbLockAsync` overloads across DB accessors, `ContextFormat` vs `ResearchContextFormat`, `CodeChunk` vs `ChunkEntity`, etc). Designed to see whether the reranker actually discriminates on meaning, not just lexical similarity to the symbol name. | 1 target (grade 3) + 1–4 distractors (grade 1) |
| `negative` | 15 | Hand-written queries about functionality that does not exist anywhere in TokenSaver (OAuth2 PKCE, GraphQL, Kubernetes HPA, Kafka, Redis, WebGL, etc). Verified against the corpus. Expected result: no chunk should score highly — tests whether the pipeline correctly returns "nothing relevant" instead of forcing a top-k match. | none |

Relevance grades: **3** = directly answers the query (the chunk *is* the answer), **2** =
strongly related but not the single best answer (e.g. one of several co-relevant chunks), **1**
= a plausible distractor that shares a name/topic but isn't the right answer, **0**/absent = not
relevant.

`extract_corpus.py`'s chunk IDs are stable across a re-run on the same commit; every
`relevant.chunk_id` in `queries.jsonl` was validated to resolve to a real row in `corpus.jsonl`
(332/332 clean).

## Suggested evaluation protocol

You didn't ask for a runnable harness, so this is the spec to wire into whatever calls
`llama.cpp` for embeddings and the reranker for scoring — matching how `SemanticSearchService`
already does embed → cosine-similarity → (add reranker step) in TokenSaver's own code.

### Accuracy

For each query, embed the query, cosine-rank all 442 corpus chunks, take top-K (K=20 is a
reasonable rerank window), then rerank those K with Qwen3-Reranker-0.6B, and report:

- **Recall@1 / Recall@5 / Recall@10** — did a grade-3 chunk land in the top N, before and after
  reranking (isolates how much the reranker actually helps).
- **MRR** (mean reciprocal rank of the first grade-3 hit).
- **nDCG@10** — uses the full 0–3 grade scale, so it rewards surfacing grade-2 co-relevant chunks
  in the `conceptual` category, not just the single best match.
- **Negative-query false-positive rate** — for the 15 `negative` queries, what fraction return
  any chunk above your intended relevance threshold. Should be near 0; a high rate means the
  reranker (or your score cutoff) is too permissive.
- **Adversarial discrimination rate** — for the 11 `adversarial` queries, does the grade-3 chunk
  outrank the grade-1 distractor after reranking. This is the single best signal for whether the
  reranker is pulling its weight over pure embedding similarity.

Break all of the above out by `category` and `difficulty` — an aggregate score across 332 mixed
queries will hide whether the pair is only good at the easy 236 `doc_derived` lookups and falls
over on the 36 adversarial/negative ones that actually matter for a coding assistant.

### Speed

Measure separately, since the embed and rerank stages have very different cost profiles:

- **Embedding throughput**: chunks/sec to embed all 442 corpus chunks once (one-time indexing
  cost), and queries/sec for the 332 query embeddings (steady-state query cost). Use
  `llama-embedding -m model.gguf --pooling last` batched, or `llama-server --embedding` with
  concurrent requests.
- **Rerank latency**: ms per (query, chunk) pair at the rerank window size you pick (K=20 →
  6,640 pair-scores across the full query set). Report p50/p95/p99, not just mean — reranker
  cross-encoder cost scales with K and this is where tail latency usually hides.
- **End-to-end query latency**: embed query → cosine top-K → rerank top-K → final ranked list,
  measured wall-clock per query across all 332.

### Load

- **Concurrent query throughput**: fire N concurrent queries (N = 1, 4, 8, 16) at the embed and
  rerank stages independently and combined; watch for GPU/CPU saturation and queueing behavior.
  `llama-server` and `vllm` both support batching — this benchmark's 442-chunk corpus and 332
  queries are sized to make batch-size sweeps (8/16/32/64) actually show a throughput curve
  rather than being dominated by fixed overhead.
- **Memory/VRAM footprint**: peak RSS/VRAM at rest, during a 442-chunk indexing pass, and during
  a burst of concurrent reranking (the reranker is the more expensive stage since it runs a full
  forward pass per query–chunk pair, unlike the embedder which encodes each chunk once).
- **Cold-start latency**: model load time for both GGUF (llama.cpp) and safetensors (transformers
  or vllm) — relevant if TokenSaver's own `DraftModel.Loader` needs to load/unload these models
  on demand rather than keeping them resident.

### Notes on instruction formatting

Both models are instruction-aware and the model cards report a 1–5% quality drop without a task
instruction on the query side. For a code-search benchmark, use something like `"Given a code
search query, retrieve relevant source code that implements or documents it"` as the instruction
for both the embedder and the reranker's `<Instruct>` field, applied consistently across all 332
queries — don't vary it per-category, or you'll conflate instruction-tuning effects with the
category differences this test set is designed to isolate.

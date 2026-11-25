#!/usr/bin/env python3
"""
Live end-to-end measurement of the conditional-rerank pass (ConditionalRetrievalService /
RerankService, TokenSaver.Library) against the golden set — extends run_eval.py's embed-only
baseline rather than replacing it (see that script's module docstring for why it stays
embed-only). This script:

  1. Runs the same embed-only ranking run_eval.py does (reusing its functions/embed cache).
  2. Classifies each query as clear-winner / below-floor / muddy-middle using the exact
     production thresholds (SemanticGrepAbstainThreshold=0.66, SemanticSearchHighConfidence=0.79,
     SemanticSearchClearMargin=0.05 — TokenEconomyConfig.cs).
  3. For muddy-middle queries only, calls the real "rerank" model route (default qwen2.5-coder:3b
     as of 2026-07-10, see TokenEconomyConfig.ModelRoutes) with the exact prompt RerankService
     builds, parses the response the same tolerant way, and re-sorts the candidate pool.
  4. Recomputes negative-FP / recall metrics with rerank live and reports the delta against the
     embed-only baseline.

Usage:
    python rerank_eval.py                                   # full 332-query run
    python rerank_eval.py --generate-url http://localhost:11434/api/generate --rerank-model qwen2.5-coder:3b
    python rerank_eval.py --limit 20                         # smoke test

Writes rerank-eval-results.json and RERANK-EVAL.md into this directory.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
import urllib.error
import urllib.request
from collections import defaultdict
from pathlib import Path

import run_eval

HERE = Path(__file__).parent

# Production defaults as of 2026-07-10 (TokenEconomyConfig.cs)
ABSTAIN_FLOOR = 0.66
HIGH_CONF = 0.79
CLEAR_MARGIN = 0.05
POOL_SIZE = 12


def build_rerank_prompt(query: str, candidate_summaries: list[str]) -> str:
    lines = [
        "Score how relevant each numbered candidate is to the query, on a 0.0-1.0 scale "
        "(1.0 = directly answers the query, 0.0 = unrelated). Reply with ONLY a JSON array of "
        "numbers, one per candidate, in the same order as listed — no other text.",
        "",
        f"QUERY: {query}",
        "",
    ]
    for i, s in enumerate(candidate_summaries, 1):
        lines.append(f"{i}. {s}")
    return "\n".join(lines) + "\n"


def call_rerank_model(url: str, model: str, prompt: str, timeout: float) -> str:
    payload = json.dumps({"model": model, "prompt": prompt, "stream": False}).encode("utf-8")
    req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        body = json.loads(resp.read().decode("utf-8"))
    return body.get("response", "")


def parse_rerank_scores(response: str, expected_count: int) -> list[float] | None:
    start = response.find("[")
    end = response.rfind("]")
    if start < 0 or end <= start:
        return None
    try:
        scores = json.loads(response[start:end + 1])
    except json.JSONDecodeError:
        return None
    if not isinstance(scores, list) or len(scores) != expected_count:
        return None
    try:
        return [max(0.0, min(1.0, float(s))) for s in scores]
    except (TypeError, ValueError):
        return None


def classify(top1: float, top2: float) -> str:
    margin = top1 - top2
    if top1 < ABSTAIN_FLOOR:
        return "below_floor"
    if top1 >= HIGH_CONF and margin >= CLEAR_MARGIN:
        return "clear_winner"
    return "muddy_middle"


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--queries", default=str(HERE / "queries.jsonl"))
    ap.add_argument("--corpus", default=str(HERE / "corpus.jsonl"))
    ap.add_argument("--embed-url", default="http://localhost:11434/api/embed")
    ap.add_argument("--embed-model", default="nomic-embed-text")
    ap.add_argument("--embed-field", choices=["summary", "code"], default="summary")
    ap.add_argument("--generate-url", default="http://localhost:11434/api/generate")
    ap.add_argument("--rerank-model", default="qwen2.5-coder:3b")
    ap.add_argument("--pool-size", type=int, default=POOL_SIZE)
    ap.add_argument("--negative-fp-threshold", type=float, default=0.5)
    ap.add_argument("--limit", type=int, default=None)
    ap.add_argument("--timeout", type=float, default=60.0)
    ap.add_argument("--no-cache", action="store_true")
    args = ap.parse_args()

    queries = run_eval.load_jsonl(Path(args.queries))
    corpus = run_eval.load_jsonl(Path(args.corpus))
    corpus_by_id = {c["chunk_id"]: c for c in corpus}

    if args.limit:
        by_cat: dict[str, list[dict]] = defaultdict(list)
        for q in queries:
            by_cat[q["category"]].append(q)
        queries = [q for cat in by_cat.values() for q in cat[: args.limit]]

    cache = run_eval.EmbedCache(HERE / ".embed-cache.json", disabled=args.no_cache)

    print(f"Embedding {len(corpus)} corpus chunks against {args.embed_url} ({args.embed_model})...")
    corpus_vectors: dict[str, list[float]] = {}
    for i, c in enumerate(corpus, 1):
        text = c.get(args.embed_field) or c.get("summary") or c.get("code") or ""
        corpus_vectors[c["chunk_id"]] = cache.get_or_embed(args.embed_url, args.embed_model, text, args.timeout)
        if i % 100 == 0 or i == len(corpus):
            print(f"  {i}/{len(corpus)}")
    cache.save()

    print(f"\nScoring {len(queries)} queries, reranking muddy-middle via {args.rerank_model}...")
    t0 = time.time()
    results = []
    counts = {"clear_winner": 0, "below_floor": 0, "muddy_middle": 0, "rerank_fired": 0, "rerank_fallback": 0}

    for qi, q in enumerate(queries, 1):
        qvec = cache.get_or_embed(args.embed_url, args.embed_model, q["query"], args.timeout)
        scored_all = sorted(
            ((cid, run_eval.cosine(qvec, vec)) for cid, vec in corpus_vectors.items()),
            key=lambda x: x[1], reverse=True,
        )
        pool = scored_all[: args.pool_size]

        top1 = pool[0][1] if pool else 0.0
        top2 = pool[1][1] if len(pool) > 1 else 0.0
        bucket = classify(top1, top2) if pool else "below_floor"
        counts[bucket] += 1

        final_pool = pool
        reranked = False
        if bucket == "muddy_middle":
            summaries = [corpus_by_id[cid]["summary"] for cid, _ in pool]
            prompt = build_rerank_prompt(q["query"], summaries)
            try:
                response = call_rerank_model(args.generate_url, args.rerank_model, prompt, args.timeout)
                scores = parse_rerank_scores(response, len(pool))
            except (urllib.error.URLError, TimeoutError) as e:
                print(f"  [{qi}/{len(queries)}] rerank call failed for {q['query_id']}: {e}", file=sys.stderr)
                scores = None

            if scores is not None:
                final_pool = sorted(zip((cid for cid, _ in pool), scores), key=lambda x: x[1], reverse=True)
                reranked = True
                counts["rerank_fired"] += 1
            else:
                counts["rerank_fallback"] += 1

        relevant = {r["chunk_id"]: r["grade"] for r in q.get("relevant", [])}
        grade3_ids = {cid for cid, g in relevant.items() if g >= 3}
        final_ids = [cid for cid, _ in final_pool]

        def recall_at(n: int) -> float | None:
            if not grade3_ids:
                return None
            return 1.0 if grade3_ids & set(final_ids[:n]) else 0.0

        results.append({
            "query_id": q["query_id"],
            "category": q["category"],
            "bucket": bucket,
            "reranked": reranked,
            "top1_score": final_pool[0][1] if final_pool else 0.0,
            "top1_chunk": final_pool[0][0] if final_pool else None,
            "recall@1": recall_at(1),
            "recall@5": recall_at(5),
        })

        if qi % 25 == 0 or qi == len(queries):
            print(f"  {qi}/{len(queries)} ({counts['rerank_fired']} reranked, {counts['rerank_fallback']} fallback)")

    print(f"\nDone in {time.time() - t0:.1f}s. Bucket counts: {counts}\n")

    report = build_report(results, args, counts)
    (HERE / "rerank-eval-results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    (HERE / "RERANK-EVAL.md").write_text(report, encoding="utf-8")
    print(report)
    print(f"\nWrote {HERE / 'rerank-eval-results.json'} and {HERE / 'RERANK-EVAL.md'}")
    return 0


def build_report(results: list[dict], args: argparse.Namespace, counts: dict) -> str:
    by_cat: dict[str, list[dict]] = defaultdict(list)
    for r in results:
        by_cat[r["category"]].append(r)

    def avg(vals: list[float | None]) -> float:
        v = [x for x in vals if x is not None]
        return sum(v) / len(v) if v else 0.0

    lines = ["# Conditional-Rerank Live Eval", "",
              f"Model: `{args.rerank_model}` on the \"rerank\" route. Pool size {args.pool_size}. "
              f"Thresholds: floor={ABSTAIN_FLOOR}, high_conf={HIGH_CONF}, margin={CLEAR_MARGIN}.",
              "",
              f"Bucket counts: clear_winner={counts['clear_winner']}, below_floor={counts['below_floor']}, "
              f"muddy_middle={counts['muddy_middle']} (rerank_fired={counts['rerank_fired']}, "
              f"rerank_fallback={counts['rerank_fallback']}).",
              ""]

    lines.append("| Category | N | Recall@1 | Recall@5 |")
    lines.append("|---|---|---|---|")
    narrow_recall5 = []
    for cat in ["doc_derived", "symbol_lookup", "conceptual", "adversarial", "negative"]:
        rows = by_cat.get(cat, [])
        if not rows:
            continue
        r1, r5 = avg([r["recall@1"] for r in rows]), avg([r["recall@5"] for r in rows])
        lines.append(f"| {cat} | {len(rows)} | {r1:.2%} | {r5:.2%} |")
        if cat in ("doc_derived", "symbol_lookup"):
            narrow_recall5.extend(r["recall@5"] for r in rows if r["recall@5"] is not None)
    lines.append("")

    negative_rows = by_cat.get("negative", [])
    neg_fp_05 = sum(1 for r in negative_rows if r["top1_score"] > 0.5) / len(negative_rows) if negative_rows else 0.0
    neg_fp_floor = sum(1 for r in negative_rows if r["top1_score"] >= ABSTAIN_FLOOR) / len(negative_rows) if negative_rows else 0.0
    lines.append(f"**Negative-query false-positive rate (with rerank live)**: {neg_fp_05:.1%} above 0.5, "
                 f"{neg_fp_floor:.1%} above the actual production abstain floor ({ABSTAIN_FLOOR}) — compare "
                 f"against the embed-only baseline in PRODUCTION-EVAL.md (100% at both thresholds).")
    lines.append("")
    lines.append(f"doc_derived+symbol_lookup recall@5 (with rerank live): {avg(narrow_recall5):.1%} — "
                 f"compare against PRODUCTION-EVAL.md's embed-only baseline (91.1%).")

    return "\n".join(lines)


if __name__ == "__main__":
    sys.exit(main())

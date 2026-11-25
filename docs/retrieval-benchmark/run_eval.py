#!/usr/bin/env python3
"""
Runnable evaluation harness for the golden query set (queries.jsonl / corpus.jsonl) —
implements the "Suggested evaluation protocol" from README.md's Accuracy section.

This is deliberately a different tool from run_benchmark.py in this same directory:
run_benchmark.py loads the candidate Qwen3-Embedding/Reranker pair directly via llama-cpp-python
(models/*.gguf) to evaluate whether that pair is worth adopting — see RESULTS.md for its findings.
This script instead calls the endpoint TokenSaver actually has wired up today: Ollama's
/api/embed with the configured EmbeddingModel (nomic-embed-text by default), the exact call
SemanticRelevanceFilterStage and SemanticSearchService make in production. Use run_benchmark.py
to evaluate a candidate model; use this script to check whether the context builder, as deployed
right now, is too narrow or too wide. Pass --model/--embed-url to point it at a different
endpoint (e.g. a llama.cpp server serving Qwen3-Embedding-0.6B) if you want this script's report
format against that pair instead.

No reranker step — this is embed-only recall/precision, which is what the context builder
actually uses today (SemanticRelevanceFilterStage has no rerank stage). If/when the conditional
rerank design in retrieval-design.md ships, extend this script rather than writing a second one.

Usage:
    python run_eval.py                                  # full 332-query run against localhost:11434
    python run_eval.py --embed-url http://localhost:11435/api/embed --model nomic-embed-text
    python run_eval.py --limit 20                        # fast smoke test, 20 queries/category max
    python run_eval.py --embed-field code                # embed raw code instead of the chunk summary

Writes production-eval-results.json (raw per-query data) and PRODUCTION-EVAL.md (the report) into this directory.
Exits 1 if any category falls below its floor (see --*-floor args) — usable as a regression gate.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
import time
import urllib.error
import urllib.request
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).parent


def embed(url: str, model: str, text: str, timeout: float) -> list[float]:
    payload = json.dumps({"model": model, "input": text}).encode("utf-8")
    req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        body = json.loads(resp.read().decode("utf-8"))
    embeddings = body.get("embeddings") or body.get("embedding")
    if embeddings and isinstance(embeddings[0], list):
        return embeddings[0]
    if embeddings:
        return embeddings
    raise ValueError(f"Unexpected embed response shape: {list(body.keys())}")


def cosine(a: list[float], b: list[float]) -> float:
    if not a or not b or len(a) != len(b):
        return 0.0
    dot = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    return 0.0 if na == 0 or nb == 0 else dot / (na * nb)


def cache_key(model: str, text: str) -> str:
    return hashlib.sha256(f"{model}\x00{text}".encode("utf-8")).hexdigest()


class EmbedCache:
    """Persists embeddings across runs to a local JSON file, keyed by sha256(model+text).
    Gitignored — see .gitignore entry added alongside this script. disabled=True skips both
    reading and writing (used for --no-cache) without needing a platform-specific null path."""

    def __init__(self, path: Path, disabled: bool = False):
        self.path = path
        self.disabled = disabled
        self.data: dict[str, list[float]] = {}
        if not disabled and path.exists():
            try:
                self.data = json.loads(path.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                self.data = {}
        self._dirty = False

    def get_or_embed(self, url: str, model: str, text: str, timeout: float) -> list[float]:
        if self.disabled:
            return embed(url, model, text, timeout)
        key = cache_key(model, text)
        if key in self.data:
            return self.data[key]
        vec = embed(url, model, text, timeout)
        self.data[key] = vec
        self._dirty = True
        return vec

    def save(self) -> None:
        if not self.disabled and self._dirty:
            self.path.write_text(json.dumps(self.data), encoding="utf-8")


def load_jsonl(path: Path) -> list[dict]:
    rows = []
    with path.open(encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def ndcg_at_k(ranked_grades: list[int], k: int) -> float:
    dcg = sum(g / math.log2(i + 2) for i, g in enumerate(ranked_grades[:k]) if g > 0)
    ideal = sorted(ranked_grades, reverse=True)
    idcg = sum(g / math.log2(i + 2) for i, g in enumerate(ideal[:k]) if g > 0)
    return 0.0 if idcg == 0 else dcg / idcg


def main() -> int:
    # Windows consoles default to a legacy codepage (cp1252) that can't encode the em-dashes/arrows
    # in the report text below and would crash on print() — force UTF-8 with graceful fallback
    # instead of stripping punctuation. File writes already specify encoding="utf-8" explicitly.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--queries", default=str(HERE / "queries.jsonl"))
    ap.add_argument("--corpus", default=str(HERE / "corpus.jsonl"))
    ap.add_argument("--embed-url", default="http://localhost:11434/api/embed")
    ap.add_argument("--model", default="nomic-embed-text")
    ap.add_argument("--embed-field", choices=["summary", "code"], default="summary",
                     help="corpus text embedded — 'summary' matches production default (EnableSummaryEmbedding=true)")
    ap.add_argument("--top-k", type=int, default=10)
    ap.add_argument("--negative-fp-threshold", type=float, default=0.5,
                     help="a negative query's top-1 score above this counts as a false positive")
    ap.add_argument("--limit", type=int, default=None, help="cap queries per category (smoke-test runs)")
    ap.add_argument("--timeout", type=float, default=30.0)
    ap.add_argument("--no-cache", action="store_true", help="ignore/overwrite the local embed cache")
    ap.add_argument("--recall5-floor", type=float, default=0.85, help="min doc_derived+symbol_lookup recall@5 to exit 0")
    ap.add_argument("--negative-fp-floor", type=float, default=0.20, help="max acceptable negative-query FP rate to exit 0")
    ap.add_argument("--adversarial-floor", type=float, default=0.60, help="min adversarial discrimination rate to exit 0")
    args = ap.parse_args()

    queries = load_jsonl(Path(args.queries))
    corpus = load_jsonl(Path(args.corpus))
    corpus_by_id = {c["chunk_id"]: c for c in corpus}

    if args.limit:
        by_cat: dict[str, list[dict]] = defaultdict(list)
        for q in queries:
            by_cat[q["category"]].append(q)
        queries = [q for cat in by_cat.values() for q in cat[: args.limit]]

    cache = EmbedCache(HERE / ".embed-cache.json", disabled=args.no_cache)

    print(f"Embedding {len(corpus)} corpus chunks ({args.embed_field}) against {args.embed_url} ({args.model})...")
    t0 = time.time()
    corpus_vectors: dict[str, list[float]] = {}
    for i, c in enumerate(corpus, 1):
        text = c.get(args.embed_field) or c.get("summary") or c.get("code") or ""
        try:
            corpus_vectors[c["chunk_id"]] = cache.get_or_embed(args.embed_url, args.model, text, args.timeout)
        except (urllib.error.URLError, TimeoutError, ValueError) as e:
            print(f"FATAL: could not embed corpus chunk {c['chunk_id']}: {e}", file=sys.stderr)
            print("Is the embedding endpoint reachable? (dev server / Ollama / DraftModel router)", file=sys.stderr)
            return 2
        if i % 50 == 0 or i == len(corpus):
            print(f"  {i}/{len(corpus)}")
    cache.save()
    print(f"Corpus embedded in {time.time() - t0:.1f}s")

    print(f"\nRunning {len(queries)} queries (top-{args.top_k})...")
    t0 = time.time()
    per_query_results = []
    for q in queries:
        qvec = cache.get_or_embed(args.embed_url, args.model, q["query"], args.timeout)
        scored = sorted(
            ((cid, cosine(qvec, vec)) for cid, vec in corpus_vectors.items()),
            key=lambda x: x[1], reverse=True,
        )
        top_k = scored[: args.top_k]

        relevant = {r["chunk_id"]: r["grade"] for r in q.get("relevant", [])}
        relevant_files = {corpus_by_id[cid]["file"] for cid in relevant if cid in corpus_by_id}
        grade3_ids = {cid for cid, g in relevant.items() if g >= 3}

        ranked_ids = [cid for cid, _ in scored]
        ranked_grades = [relevant.get(cid, 0) for cid, _ in scored]
        top_files = {corpus_by_id[cid]["file"] for cid, _ in top_k if cid in corpus_by_id}

        def recall_at(n: int) -> float:
            if not grade3_ids:
                return None  # negative queries have no recall notion
            return 1.0 if grade3_ids & set(ranked_ids[:n]) else 0.0

        rank_of_first_grade3 = next((i + 1 for i, cid in enumerate(ranked_ids) if cid in grade3_ids), None)
        mrr = 1.0 / rank_of_first_grade3 if rank_of_first_grade3 else 0.0

        file_recall_at_k = None
        if relevant_files:
            file_recall_at_k = 1.0 if relevant_files & top_files else 0.0

        result = {
            "query_id": q["query_id"],
            "category": q["category"],
            "recall@1": recall_at(1),
            "recall@5": recall_at(5),
            "recall@10": recall_at(10),
            "file_recall@k": file_recall_at_k,
            "mrr": mrr if grade3_ids else None,
            "ndcg@10": ndcg_at_k(ranked_grades, 10) if relevant else None,
            "top1_score": scored[0][1] if scored else 0.0,
            "top1_chunk": scored[0][0] if scored else None,
            "top2_score": scored[1][1] if len(scored) > 1 else 0.0,
        }

        if q["category"] == "adversarial":
            grade1_ids = {cid for cid, g in relevant.items() if g == 1}
            best_grade3 = max((s for cid, s in scored if cid in grade3_ids), default=None)
            best_grade1 = max((s for cid, s in scored if cid in grade1_ids), default=None)
            result["adversarial_discriminated"] = (
                best_grade3 is not None and best_grade1 is not None and best_grade3 > best_grade1
            )

        per_query_results.append(result)

    print(f"Queries scored in {time.time() - t0:.1f}s\n")

    report = build_report(per_query_results, args)
    (HERE / "production-eval-results.json").write_text(json.dumps(per_query_results, indent=2), encoding="utf-8")
    (HERE / "PRODUCTION-EVAL.md").write_text(report["markdown"], encoding="utf-8")
    print(report["markdown"])
    print(f"\nWrote {HERE / 'production-eval-results.json'} and {HERE / 'PRODUCTION-EVAL.md'}")

    return 0 if report["passed"] else 1


def build_report(results: list[dict], args: argparse.Namespace) -> dict:
    by_cat: dict[str, list[dict]] = defaultdict(list)
    for r in results:
        by_cat[r["category"]].append(r)

    def avg(vals: list[float | None]) -> float:
        v = [x for x in vals if x is not None]
        return sum(v) / len(v) if v else 0.0

    lines = ["# Retrieval Benchmark Results", ""]
    lines.append(f"Embed-only (no rerank), top-{args.top_k}, {sum(len(v) for v in by_cat.values())} queries.")
    lines.append("")
    lines.append("| Category | N | Recall@1 | Recall@5 | Recall@10 | File Recall@K | MRR | nDCG@10 |")
    lines.append("|---|---|---|---|---|---|---|---|")

    narrow_recall5_vals = []
    for cat in ["doc_derived", "symbol_lookup", "conceptual", "adversarial", "negative"]:
        rows = by_cat.get(cat, [])
        if not rows:
            continue
        r1, r5, r10 = avg([r["recall@1"] for r in rows]), avg([r["recall@5"] for r in rows]), avg([r["recall@10"] for r in rows])
        fr = avg([r["file_recall@k"] for r in rows])
        mrr = avg([r["mrr"] for r in rows])
        ndcg = avg([r["ndcg@10"] for r in rows])
        lines.append(f"| {cat} | {len(rows)} | {r1:.2%} | {r5:.2%} | {r10:.2%} | {fr:.2%} | {mrr:.3f} | {ndcg:.3f} |")
        if cat in ("doc_derived", "symbol_lookup"):
            narrow_recall5_vals.extend(r["recall@5"] for r in rows if r["recall@5"] is not None)

    lines.append("")

    negative_rows = by_cat.get("negative", [])
    neg_fp_rate = (
        sum(1 for r in negative_rows if r["top1_score"] > args.negative_fp_threshold) / len(negative_rows)
        if negative_rows else 0.0
    )
    lines.append(f"**Too-wide check — negative-query false-positive rate**: {neg_fp_rate:.1%} "
                 f"of {len(negative_rows)} negative queries returned a top-1 hit above {args.negative_fp_threshold} "
                 f"(should be near 0; a high rate means the context builder will pull in irrelevant files for "
                 f"queries about functionality that doesn't exist).")
    lines.append("")

    adversarial_rows = [r for r in by_cat.get("adversarial", []) if "adversarial_discriminated" in r]
    adv_rate = (
        sum(1 for r in adversarial_rows if r["adversarial_discriminated"]) / len(adversarial_rows)
        if adversarial_rows else 0.0
    )
    lines.append(f"**Discrimination check**: {adv_rate:.1%} of {len(adversarial_rows)} adversarial queries ranked "
                 f"the correct chunk above its lexically-similar distractor (embed-only, no rerank — this is the "
                 f"number retrieval-design.md's conditional-rerank proposal exists to improve).")
    lines.append("")

    narrow_recall5 = avg(narrow_recall5_vals)
    passed = (
        narrow_recall5 >= args.recall5_floor
        and neg_fp_rate <= args.negative_fp_floor
        and adv_rate >= args.adversarial_floor
    )
    verdict = "PASS" if passed else "FAIL"
    lines.append(f"**Verdict: {verdict}** — doc_derived+symbol_lookup recall@5 {narrow_recall5:.1%} "
                 f"(floor {args.recall5_floor:.0%}), negative FP {neg_fp_rate:.1%} (ceiling {args.negative_fp_floor:.0%}), "
                 f"adversarial discrimination {adv_rate:.1%} (floor {args.adversarial_floor:.0%}).")
    lines.append("")
    lines.append("- Low recall on doc_derived/symbol_lookup -> **too narrow**: the context builder's semantic "
                 "filter is dropping or never surfacing files a query should have found.")
    lines.append("- High negative-query FP rate -> **too wide**: irrelevant chunks are scoring high enough to "
                 "look relevant for queries about nonexistent functionality.")
    lines.append("- Low adversarial discrimination -> embed-only similarity can't tell semantically-different "
                 "same-named symbols apart; see retrieval-design.md's conditional-rerank plan.")

    return {"markdown": "\n".join(lines), "passed": passed}


if __name__ == "__main__":
    sys.exit(main())

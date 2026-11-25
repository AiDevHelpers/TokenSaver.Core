#!/usr/bin/env python3
"""
CPU vs CUDA benchmark for the Qwen3-Embedding-0.6B-GGUF + Qwen3-Reranker-0.6B
pair on TokenSaver's own code-retrieval test set (corpus.jsonl + queries.jsonl).

Usage:
    .venv\\Scripts\\python.exe run_benchmark.py --device cpu  --out results_cpu.json
    .venv\\Scripts\\python.exe run_benchmark.py --device cuda --out results_cuda.json

Measures, per device:
  - corpus embedding throughput (chunks/sec)
  - query embedding throughput (queries/sec)
  - cosine top-K retrieval latency
  - reranker latency (p50/p95/p99 per query, over top-K candidates)
  - end-to-end per-query latency
  - accuracy: recall@1/5/10, MRR, nDCG@10, negative false-positive rate,
    adversarial discrimination rate -- all broken out by query category,
    both pre-rerank (embedding-only) and post-rerank
  - peak memory (GPU VRAM via torch, or process RSS via psutil on CPU)
"""
import argparse
import json
import math
import os
import statistics
import time
from collections import defaultdict

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
CORPUS_PATH = os.path.join(HERE, "corpus.jsonl")
QUERIES_PATH = os.path.join(HERE, "queries.jsonl")
EMBED_MODEL_PATH = os.path.join(HERE, "models", "Qwen3-Embedding-0.6B-Q8_0.gguf")
RERANK_MODEL_DIR = os.path.join(HERE, "models", "Qwen3-Reranker-0.6B")

EMBED_INSTRUCTION = "Given a code search query, retrieve relevant source code that implements or documents it"
RERANK_INSTRUCTION = EMBED_INSTRUCTION
TOP_K = 12


def load_jsonl(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def chunk_text(c, max_chars=1800):
    code = c["code"]
    if len(code) > max_chars:
        code = code[:max_chars] + "\n... [truncated]"
    return f"{c['kind']} {c['name']} ({c['file']})\n{c['summary']}\n{code}"


def percentile(values, p):
    if not values:
        return 0.0
    s = sorted(values)
    k = (len(s) - 1) * (p / 100)
    f = math.floor(k)
    c = math.ceil(k)
    if f == c:
        return s[int(k)]
    return s[f] + (s[c] - s[f]) * (k - f)


def cosine_sim_matrix(query_vecs, corpus_vecs):
    q = query_vecs / (np.linalg.norm(query_vecs, axis=1, keepdims=True) + 1e-12)
    c = corpus_vecs / (np.linalg.norm(corpus_vecs, axis=1, keepdims=True) + 1e-12)
    return q @ c.T


def dcg(grades):
    return sum(g / math.log2(i + 2) for i, g in enumerate(grades))


def ndcg_at_k(ranked_grades, ideal_grades, k):
    d = dcg(ranked_grades[:k])
    ideal = dcg(sorted(ideal_grades, reverse=True)[:k])
    return d / ideal if ideal > 0 else (1.0 if d == 0 else 0.0)


def load_embedder(device):
    from llama_cpp import Llama
    n_gpu_layers = -1 if device == "cuda" else 0
    t0 = time.perf_counter()
    llm = Llama(
        model_path=EMBED_MODEL_PATH,
        embedding=True,
        n_gpu_layers=n_gpu_layers,
        n_ctx=4096,
        pooling_type=3,  # LLAMA_POOLING_TYPE_LAST
        verbose=False,
    )
    load_s = time.perf_counter() - t0
    return llm, load_s


def embed_texts(llm, texts):
    vecs = []
    times = []
    for t in texts:
        t0 = time.perf_counter()
        r = llm.create_embedding(t)
        times.append(time.perf_counter() - t0)
        emb = r["data"][0]["embedding"]
        if isinstance(emb[0], list):  # some pooling modes return per-token vectors
            emb = emb[-1]
        vecs.append(emb)
    return np.array(vecs, dtype=np.float32), times


class DirectReranker:
    """Direct transformers implementation of the Qwen3-Reranker yes/no-logit
    trick from the model card -- a single forward pass per batch, reading the
    'yes' vs 'no' token logits at the last position. Avoids routing through
    sentence-transformers' CrossEncoder, which (as of ST 5.6.0) appears to
    fall back to a much slower/heavier generation-style path for this
    causal-LM-based reranker architecture on this hardware."""

    def __init__(self, model_dir, device):
        import torch
        from transformers import AutoModelForCausalLM, AutoTokenizer
        self.torch = torch
        self.device = device
        self.tokenizer = AutoTokenizer.from_pretrained(model_dir, padding_side="left")
        dtype = torch.float16 if device == "cuda" else torch.float32
        self.model = AutoModelForCausalLM.from_pretrained(
            model_dir, dtype=dtype, attn_implementation="sdpa"
        ).to(device).eval()
        self.token_false_id = self.tokenizer.convert_tokens_to_ids("no")
        self.token_true_id = self.tokenizer.convert_tokens_to_ids("yes")
        self.max_length = 2048
        self.prefix = (
            "<|im_start|>system\nJudge whether the Document meets the requirements based on "
            "the Query and the Instruct provided. Note that the answer can only be \"yes\" or \"no\"."
            "<|im_end|>\n<|im_start|>user\n"
        )
        self.suffix = "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n"
        self.prefix_tokens = self.tokenizer.encode(self.prefix, add_special_tokens=False)
        self.suffix_tokens = self.tokenizer.encode(self.suffix, add_special_tokens=False)

    def _format(self, instruction, query, doc):
        return f"<Instruct>: {instruction}\n<Query>: {query}\n<Document>: {doc}"

    def predict(self, pairs, instruction=None):
        instruction = instruction or RERANK_INSTRUCTION
        torch = self.torch
        texts = [self._format(instruction, q, d) for q, d in pairs]
        inputs = self.tokenizer(
            texts, padding=False, truncation="longest_first",
            return_attention_mask=False,
            max_length=self.max_length - len(self.prefix_tokens) - len(self.suffix_tokens),
        )
        for i, ids in enumerate(inputs["input_ids"]):
            inputs["input_ids"][i] = self.prefix_tokens + ids + self.suffix_tokens
        inputs = self.tokenizer.pad(inputs, padding=True, return_tensors="pt", max_length=self.max_length)
        inputs = {k: v.to(self.device) for k, v in inputs.items()}
        with torch.no_grad():
            # logits_to_keep=1: only project the last position through the LM head.
            # Without this, transformers computes the full (batch, seq_len, vocab=151669)
            # logits tensor -- for a 20x480 batch that's ~1.4B values, which dominates
            # runtime and VRAM on a memory-limited GPU even though only the last
            # position's yes/no logits are ever used.
            logits = self.model(**inputs, logits_to_keep=1).logits[:, -1, :]
        true_vec = logits[:, self.token_true_id]
        false_vec = logits[:, self.token_false_id]
        stacked = torch.stack([false_vec, true_vec], dim=1)
        probs = torch.nn.functional.log_softmax(stacked, dim=1)[:, 1].exp()
        return probs.float().cpu().tolist()


def load_reranker(device):
    t0 = time.perf_counter()
    model = DirectReranker(RERANK_MODEL_DIR, device)
    load_s = time.perf_counter() - t0
    return model, load_s


def run(device, out_path, queries_path=None):
    print(f"=== Running benchmark on device={device} ===")
    corpus = load_jsonl(CORPUS_PATH)
    queries = load_jsonl(queries_path or QUERIES_PATH)
    by_id = {c["chunk_id"]: c for c in corpus}
    corpus_texts = [chunk_text(c) for c in corpus]

    mem_before_gpu = None
    if device == "cuda":
        import torch
        torch.cuda.reset_peak_memory_stats()
        mem_before_gpu = torch.cuda.memory_allocated()

    # ---- Embedder ----
    llm, embed_load_s = load_embedder(device)

    t0 = time.perf_counter()
    corpus_vecs, corpus_embed_times = embed_texts(llm, corpus_texts)
    corpus_embed_total_s = time.perf_counter() - t0

    query_texts = [f"Instruct: {EMBED_INSTRUCTION}\nQuery: {q['query']}" for q in queries]
    t0 = time.perf_counter()
    query_vecs, query_embed_times = embed_texts(llm, query_texts)
    query_embed_total_s = time.perf_counter() - t0

    # ---- Cosine top-K ----
    t0 = time.perf_counter()
    sims = cosine_sim_matrix(query_vecs, corpus_vecs)
    cosine_total_s = time.perf_counter() - t0
    topk_idx = np.argsort(-sims, axis=1)[:, :TOP_K]

    # ---- Reranker ----
    reranker, rerank_load_s = load_reranker(device)

    rerank_latencies = []
    per_query_results = []
    for qi, q in enumerate(queries):
        cand_idx = topk_idx[qi]
        cand_chunks = [corpus[i] for i in cand_idx]
        pairs = [(q["query"], chunk_text(c, max_chars=500)) for c in cand_chunks]
        t0 = time.perf_counter()
        scores = reranker.predict(pairs)
        rerank_latencies.append(time.perf_counter() - t0)
        order = np.argsort(-np.array(scores))
        reranked_ids = [cand_chunks[j]["chunk_id"] for j in order]
        reranked_scores = [float(scores[j]) for j in order]
        pre_rerank_ids = [corpus[i]["chunk_id"] for i in cand_idx]
        per_query_results.append({
            "query_id": q["query_id"],
            "category": q["category"],
            "pre_rerank_ranked_ids": pre_rerank_ids,
            "post_rerank_ranked_ids": reranked_ids,
            "post_rerank_scores": reranked_scores,
        })

    peak_gpu_mb = None
    if device == "cuda":
        import torch
        peak_gpu_mb = torch.cuda.max_memory_allocated() / (1024 * 1024)

    peak_rss_mb = None
    try:
        import psutil
        peak_rss_mb = psutil.Process(os.getpid()).memory_info().rss / (1024 * 1024)
    except ImportError:
        pass

    # ---- Accuracy metrics ----
    metrics_by_cat = defaultdict(lambda: {
        "n": 0, "recall1_pre": 0, "recall5_pre": 0, "recall10_pre": 0,
        "recall1_post": 0, "recall5_post": 0, "recall10_post": 0,
        "mrr_pre": [], "mrr_post": [], "ndcg10_pre": [], "ndcg10_post": [],
    })
    negative_fp = 0
    negative_n = 0
    adversarial_correct = 0
    adversarial_total = 0

    grade_lookup = {}
    for q in queries:
        grade_lookup[q["query_id"]] = {r["chunk_id"]: r["grade"] for r in q["relevant"]}

    for q, res in zip(queries, per_query_results):
        cat = q["category"]
        grades = grade_lookup[q["query_id"]]
        m = metrics_by_cat[cat]
        m["n"] += 1

        def recall_at_k(ranked_ids, k):
            return int(any(grades.get(cid, 0) >= 3 for cid in ranked_ids[:k])) if grades else None

        def mrr(ranked_ids):
            for i, cid in enumerate(ranked_ids):
                if grades.get(cid, 0) >= 3:
                    return 1.0 / (i + 1)
            return 0.0

        def ndcg10(ranked_ids):
            ranked_grades = [grades.get(cid, 0) for cid in ranked_ids[:10]]
            ideal = list(grades.values())
            return ndcg_at_k(ranked_grades, ideal, 10)

        if grades:  # skip negative queries for recall/mrr/ndcg (no positive answer exists)
            for k, key in [(1, "recall1"), (5, "recall5"), (10, "recall10")]:
                m[f"{key}_pre"] += recall_at_k(res["pre_rerank_ranked_ids"], k) or 0
                m[f"{key}_post"] += recall_at_k(res["post_rerank_ranked_ids"], k) or 0
            m["mrr_pre"].append(mrr(res["pre_rerank_ranked_ids"]))
            m["mrr_post"].append(mrr(res["post_rerank_ranked_ids"]))
            m["ndcg10_pre"].append(ndcg10(res["pre_rerank_ranked_ids"]))
            m["ndcg10_post"].append(ndcg10(res["post_rerank_ranked_ids"]))

        if cat == "negative":
            negative_n += 1
            top_score = res["post_rerank_scores"][0] if res["post_rerank_scores"] else 0
            if top_score > 0.5:
                negative_fp += 1

        if cat == "adversarial":
            target_ids = [cid for cid, g in grades.items() if g == 3]
            distractor_ids = [cid for cid, g in grades.items() if g < 3]
            if target_ids and distractor_ids:
                adversarial_total += 1
                ranked = res["post_rerank_ranked_ids"]
                try:
                    target_rank = min(ranked.index(t) for t in target_ids if t in ranked)
                except ValueError:
                    target_rank = 999
                distractor_ranks = [ranked.index(d) for d in distractor_ids if d in ranked]
                min_distractor_rank = min(distractor_ranks) if distractor_ranks else 999
                if target_rank < min_distractor_rank:
                    adversarial_correct += 1

    summary_by_cat = {}
    for cat, m in metrics_by_cat.items():
        n = m["n"]
        denom = len(m["mrr_pre"]) or 1
        summary_by_cat[cat] = {
            "n": n,
            "recall@1_pre": m["recall1_pre"] / denom, "recall@1_post": m["recall1_post"] / denom,
            "recall@5_pre": m["recall5_pre"] / denom, "recall@5_post": m["recall5_post"] / denom,
            "recall@10_pre": m["recall10_pre"] / denom, "recall@10_post": m["recall10_post"] / denom,
            "mrr_pre": statistics.mean(m["mrr_pre"]) if m["mrr_pre"] else None,
            "mrr_post": statistics.mean(m["mrr_post"]) if m["mrr_post"] else None,
            "ndcg@10_pre": statistics.mean(m["ndcg10_pre"]) if m["ndcg10_pre"] else None,
            "ndcg@10_post": statistics.mean(m["ndcg10_post"]) if m["ndcg10_post"] else None,
        }

    results = {
        "device": device,
        "corpus_size": len(corpus),
        "query_count": len(queries),
        "top_k": TOP_K,
        "timing": {
            "embed_model_load_s": embed_load_s,
            "rerank_model_load_s": rerank_load_s,
            "corpus_embed_total_s": corpus_embed_total_s,
            "corpus_embed_chunks_per_sec": len(corpus) / corpus_embed_total_s if corpus_embed_total_s else None,
            "query_embed_total_s": query_embed_total_s,
            "query_embed_queries_per_sec": len(queries) / query_embed_total_s if query_embed_total_s else None,
            "cosine_topk_total_s": cosine_total_s,
            "rerank_latency_p50_s": percentile(rerank_latencies, 50),
            "rerank_latency_p95_s": percentile(rerank_latencies, 95),
            "rerank_latency_p99_s": percentile(rerank_latencies, 99),
            "rerank_latency_mean_s": statistics.mean(rerank_latencies) if rerank_latencies else None,
            "end_to_end_query_mean_s": (
                (query_embed_total_s / len(queries)) + (cosine_total_s / len(queries)) +
                (statistics.mean(rerank_latencies) if rerank_latencies else 0)
            ),
        },
        "memory": {
            "peak_gpu_mb": peak_gpu_mb,
            "peak_process_rss_mb": peak_rss_mb,
        },
        "accuracy_by_category": summary_by_cat,
        "negative_false_positive_rate": (negative_fp / negative_n) if negative_n else None,
        "adversarial_discrimination_rate": (adversarial_correct / adversarial_total) if adversarial_total else None,
    }

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)
    print(json.dumps(results, indent=2))
    print(f"\nWrote {out_path}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--device", choices=["cpu", "cuda"], required=True)
    ap.add_argument("--out", default=None)
    ap.add_argument("--queries", default=None, help="Optional path to a queries.jsonl subset")
    args = ap.parse_args()
    out = args.out or os.path.join(HERE, f"results_{args.device}.json")
    run(args.device, out, queries_path=args.queries)

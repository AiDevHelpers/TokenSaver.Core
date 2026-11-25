# TokenSaver.MCP — Token-Economy Tools Contract

This document is the source of truth for the token-economy MCP tool surface: `TokenEconomyTools`,
`SessionTools`, `DevLoopTools`, and `SemanticIndexTools` (all under
`TokenSaver.MCP/Tools/*.cs`), plus the shared services backing them (`DistillationService`,
`SavingsTracker`, `RepoIndexer`, `SemanticSearchService` — all `TokenSaver.Library/Services/*.cs`).
It exists so `TokenSaver.MCP.Tests`/`TokenSaver.Library.Tests` have a fixed spec to assert
against, and so anyone calling these tools knows exactly what to expect, per CLAUDE.md's "Public
contracts must be testable" rule.

## Design: no tool returns raw bulk text

Every tool here that can produce a lot of text (test output, logs, diffs, lint results) routes it
through `IDistillationService.DistillAsync` first — a local Ollama call with a role-specific
prompt and a hard output ceiling — and returns the compressed summary plus token counts, not the
raw text. Raw output isn't discarded: tools that support it write it to
`<WorkspaceRoot>/.tokensaver/raw/<tool>/<timestamp>.txt` (`RawArtifactWriter`) and mention the
path in their response, so a compressed summary is never a dead end.

## Config: `TokenSaver:TokenEconomy` (`TokenEconomyConfig`)

Bound the same way as the rest of `TokenSaverConfig` — `appsettings.json` under
`TokenSaver:TokenEconomy:*`, or `TOKENSAVER_TokenSaver__TokenEconomy__<Property>` env vars.

| Property | Default | Effect when false/changed |
|---|---|---|
| `EnableDistill` | `true` | `DistillAsync` never calls Ollama; returns raw text capped at `RawTextCapChars`, `DistillResult.Distilled = false`. |
| `EnableTerse` | `true` | Terse mode (see below) can never activate, even via per-call `terse:true`. |
| `EnableRawRetention` | `true` | `RawArtifactWriter.WriteAsync` returns `null` and writes nothing; tools omit the "full: `<path>`" reference. |
| `EnableSavingsTracking` | `true` | `SavingsTracker.RecordAsync` returns immediately without opening the DB; `TokenSavingsReport`/`WorthKeeping` have nothing to report. |
| `EnableSummaryEmbedding` | `true` | `RepoIndexer`/`SemanticSearchService` embed the raw chunk text instead of its one-line summary (heavier compute, no token-savings angle since embeddings never reach Claude either way). |
| `Terse` | `false` | Server-wide default for terse mode; per-call `terse` param overrides it unless `EnableTerse` is false. |
| `WorthItMinPct` / `WorthItMaxMs` / `WorthItMinCalls` | `30` / `4000` / `5` | Thresholds for the `worth_it` verdict on `TokenSavingsReport`/`WorthKeeping` rows. |
| `ModelRoutes` | code/chat/summarize/embed/rerank → specific models | `ModelRoute` tool and internal summarization/embedding calls resolve through this table; an unknown `task_type` falls back to the `chat` entry. |
| `RawTextCapChars` | `16000` | Input cap fed to any distillation/summarization prompt. |
| `TerseMaxTokensRatio` | `0.5` | Terse mode multiplies the caller's `maxTokens` by this. |

## `TokenEconomyTools`

### `TokenBudget(content, threshold=8000)`
Pure calculation (`IDistillationService.EstimateTokens` — `ceil(chars/4)`), no Ollama call, no
degraded path. Returns `{tokens, threshold, over_budget, pct_of_threshold}`.
`pct_of_threshold` is `0` (not a divide-by-zero exception) when `threshold=0`.

### `ModelRoute(taskType, prompt, maxTokens=400)`
Resolves `taskType` via `TokenEconomyConfig.ModelRoutes` (`ModelRouteResolver.Resolve`) and calls
that model directly — no distillation, this is a raw passthrough call. `taskType="embed"` returns
a JSON float array (`OllamaService.EmbedAsync`) instead of a chat completion; every other
`taskType` returns the model's text response as-is. Requires a live Ollama endpoint reachable at
`TokenSaver:OllamaUrl` — there's no degraded/offline path here, an unreachable endpoint throws
`HttpRequestException` up to the MCP error response.

### `TokenSavingsReport(sinceDays=7, groupBy="tool")`
Queries `SavingsTracker.GetReportAsync`, ranked by `saved_tokens` descending, each row carrying a
`worth_it`/`reason` verdict (`SavingsTracker.Verdict`). Returns an empty `results` array (not an
error) when `EnableSavingsTracking` has been off for the whole window, or no matching tool/role
was ever recorded. Each row also includes `avg_conversation_turns`/`avg_conversation_tokens` —
averages of `RecordAsync`'s `conversationTurns`/`conversationTokens` params across the group's
calls, `null` when the group has zero rows. Both are `0` for non-conversational tools
(`RunTests`, `ContainerLogs`, etc. — anything that goes through `DistillationService.DistillAsync`
rather than `BuildConciseContext`), since only `ContextPipelineService.BuildConciseContextAsync`
has a `history` array to measure; they're only meaningfully nonzero for the `buildConciseContext`
tool/role rows.

### `WorthKeeping(target, groupBy="tool", sinceDays=30)`
Single-target version of the above. Returns `{target, worth_it: null, reason: "no data for this
target"}` when there's no data — this is the one place `worth_it` being `null` is a normal,
expected response shape rather than "not enough calls yet" (which is `SavingsTracker.Verdict`'s
own null case once there IS at least one row, just fewer than `WorthItMinCalls`). Same
`avg_conversation_turns`/`avg_conversation_tokens` fields as `TokenSavingsReport`.

## `SessionTools`

### `SessionSnapshot(sessionId, contextBlob, outputPath=null)`
Writes `<dest>.json` (summary + pointer) and `<dest>.full.txt` (the untouched original blob).
Blobs over 4000 chars are distilled (`DistillRole.GenericCondense`) before being stored as the
summary; shorter blobs are stored as-is. The full blob is **always** written regardless of size —
distillation only affects what's in the `.json`'s `summary` field.

### `SessionRestore(snapshotPath, full=false)`
Reads the summary by default; `full:true` reads `full_blob_path` from the snapshot and returns
that file's contents instead. Throws `InvalidOperationException` if a snapshot file lacks
`full_blob_path` (should not happen for a snapshot actually written by `SessionSnapshot`) and lets
`FileNotFoundException`/`JsonException` propagate as-is for a missing/malformed snapshot path —
no special degraded-response handling for those two cases.

## `DevLoopTools`

All shell-out tools here share one exit-code convention: a non-zero exit is treated as "there's
something to report" (test failure, lint issues), not as a tool-level error — the distilled
summary of stdout/stderr IS the successful response. A tool-level error (returned as plain text,
not thrown) is reserved for the *command itself* being unavailable (`docker`/`git` not on PATH, or
the target not existing) — callers should not need to distinguish "no docker" from "docker logs
were empty" by exception type, both come back as a text response.

### `ContainerLogs(containerName, tailLines=500, verbose=false)`
Shells out to the `docker` CLI (no Docker SDK package dependency) in four steps, returning a
plain-text message rather than throwing when a step fails:
1. `docker --version`: missing CLI (`Win32Exception`/`InvalidOperationException`) → "Docker is not installed or not on PATH".
2. `docker info`: non-zero exit → "Docker is installed but the daemon is not running".
3. `docker inspect`: non-zero exit → "Container '<name>' not found".
4. `docker logs --tail <n> <container>`, prefixed with a status header (`status`, `health`,
   `exitCode`, `finished`). Logs are fetched even when the container is stopped.

`verbose:true` skips distillation and returns the header plus raw log text (still written to the
raw-retention path first if enabled).

Container names must match `^[A-Za-z0-9][A-Za-z0-9_.-]*$`; anything else returns an "Invalid
container name" message without running docker.

### `ContainerStart` / `ContainerStop` / `ContainerRestart(containerName)`
Run the same preflight as `ContainerLogs`, then `docker start|stop|restart <container>`. Success
returns the container's status header after the action. A non-zero docker exit returns the stderr
text. These tools do not send input to a container.

### `ContainerExec(containerName, command, timeoutSeconds=60)`
Runs `docker exec <container> /bin/sh -c <command>` with the arguments passed as a list, so the
caller's text is not shell-quoted by TokenSaver. Returns `exit=<code>` followed by the captured
stdout and stderr, each capped at 20,000 characters. A timeout kills the docker client
(`Process.Kill(entireProcessTree)`); the command inside the container may keep running. One-shot
only: no interactive console or stdin.

### `DiffSummarize(repoPath, stagedOnly=false)`
Shells out to `git diff` / `git diff --staged` in `repoPath`. Returns `"No changes."` for an empty
diff (not an error). Same CLI-unavailable degraded text-response pattern as `ContainerLogs`.

### `RunTests(projectPath, testCommand="dotnet test", verbose=false)`
Exit code `0` → `"PASS (all tests)"`, no distillation call at all (nothing to compress). Non-zero
→ raw output retained, then distilled with `DistillRole.TestFailure` unless `verbose:true`.
`testCommand`'s first space-delimited token is the process filename, the rest is passed as
`Arguments` verbatim (no shell involved — `UseShellExecute=false` — so shell operators like `&&`
or `|` in a custom `testCommand` will NOT work; pass a single command, not a pipeline).

### `LintFix(projectPath, lintCommand="dotnet format --verify-no-changes")`
Same command-splitting caveat as `RunTests`. `"Clean — no remaining lint issues."` when exit code
is `0` **and** there's no output at all; otherwise distilled with `DistillRole.LintSummary`
regardless of exit code (a linter that exits 0 but still prints informational output still gets
distilled, unlike `RunTests`' all-or-nothing PASS short-circuit).

### `BuildWatch(logPath, sinceLine=0)`
Reads `logPath`, returns lines after `sinceLine` (`0` = whole file). Returns
`{new_errors: [], last_line}` (not a distillation call) when there's nothing new. When there IS
something new, output under 800 chars is returned as-is (not worth an Ollama round trip); 800+
chars gets `DistillRole.LogErrors`. Lets `FileNotFoundException` propagate for a missing
`logPath` — no degraded-response handling for that case.

## `SemanticIndexTools`

Deliberately separate from `CodeHelperTools.CodeIndex` (exact/substring symbol-name search via
`WorkspaceIndexService`/EF `Symbols` table) — this is chunk-level, embedding-based, natural-
language search, backed by its own `Chunks` table in the same `.tokensaver/index.db` file. The
two indexes have no dependency on each other; `IndexRepo` can run with or without `CodeIndex(scan)`
ever having been called for the same workspace.

### `IndexRepo(rootPath=null, extensions=null, concurrency=4)`
Defaults: `rootPath` → configured `WorkspaceRoot`; `extensions` → `.cs`/`.js`/`.ts`/`.py`/`.md`.
Walks the tree (skipping `node_modules`, `.git`, `dist`, `build`, `venv`, `__pycache__`, `bin`,
`obj`, `.tokensaver`, `.vs`), splits each file into chunks (`IChunkExtractor` — regex boundary
heuristics per extension, single "module"/"md_section" fallback chunk for files with no detected
boundaries), and for each chunk whose content hash changed: summarizes it (one line, Ollama),
embeds it (`EnableSummaryEmbedding` controls summary-vs-raw-text as the embed input), and upserts
into `Chunks`. Prunes chunks whose span disappeared from a still-existing file, and all chunks for
files that no longer exist on disk. Returns
`{files, chunks_indexed, chunks_skipped, chunks_deleted, elapsed_seconds}`. Ollama calls are bounded
by `concurrency` (file-level sequential scan, chunk-level bounded fan-out within each file) — file
I/O errors on an individual file (e.g. locked file) are skipped for that pass, not fatal to the run.

### `SemanticGrep(query, pathFilter=null, topK=5)`
Embeds `query`, cosine-similarity-ranks against every row in `Chunks` (optionally filtered by a
`RelativePath` substring), returns the top `topK` as
`[{file, lines, type, name, summary, score}]` — summary and score, **not** raw chunk text; the
caller reads the actual file for whichever 1-2 hits look relevant. Returns an empty array (not an
error) if `IndexRepo` has never been run for this workspace — an empty `Chunks` table is a valid,
unexceptional state.

**Conditional rerank + abstain path** (retrieval-design.md §2/§3, implemented via
`IConditionalRetrievalService`): `SemanticGrep` pulls an embed-ranked candidate pool (size
`TokenEconomyConfig.SemanticSearchRerankCandidatePoolSize`, default `12`), then:

1. **Clear winner** — top-1 score ≥ `SemanticSearchHighConfidence` (`0.79`) *and* the gap to
   top-2 ≥ `SemanticSearchClearMargin` (`0.05`): return embed ranking as-is, no rerank call.
2. **Below floor** — top-1 score < `SemanticGrepAbstainThreshold` (`0.66`): abstain immediately,
   no rerank call (no point spending a model call on noise).
3. **Muddy middle** — everything else: call `IRerankService.ScoreAsync` (the "rerank" model
   route judges relevance via a chat prompt, not a dedicated cross-encoder — see
   `RerankService`'s doc comment for why) and re-sort by its scores. If the call fails or its
   response doesn't parse, silently fall back to the embed ranking (`EnableConditionalRerank`
   also lets you turn this off entirely).

After all of that, if the final top-1 score is still below `SemanticGrepAbstainThreshold`, the
tool returns `{"match":"none", "best_candidate": {...}, "confidence": <score>}` instead of the hit
array — callers must treat `match:"none"` as "nothing relevant is indexed," never fall back to
`best_candidate` as if it were a real hit. `IndexRepo` never having been run (empty `Chunks`
table, zero candidates) still returns `[]`, not the abstain shape — different state.

This is the fix for retrieval-design.md §3/#4's "doesn't exist" problem: uncalibrated, production
embed-only retrieval returned a confident top-1 hit for 100% of the golden set's negative queries
(see `docs/retrieval-benchmark/PRODUCTION-EVAL.md`). Live-measured end-to-end against the full
golden set (`docs/retrieval-benchmark/rerank_eval.py`, results in `RERANK-EVAL.md`) with "rerank"
repointed at `qwen2.5-coder:3b` (the only chat model actually pulled in this dev environment — the
old default `qwen2.5-coder:7b` isn't):

| | Negative-FP (0.66 floor) | doc/symbol recall@5 |
|---|---|---|
| Embed-only baseline | 100% (15/15) | 91.1% |
| Abstain-only (no rerank) | 40% (6/15) | 91.1% |
| Abstain + rerank, pool size 12 | 33.3% (5/15) | 90.0% |
| **Abstain + rerank, pool size 6 (current default)** | **20.0% (3/15)** | 90.7% |

The pool-size-12 win was small because of a real reliability problem, not a judgment-quality one:
of the 97 golden-set queries that landed in the muddy middle, `qwen2.5-coder:3b` only returned a
parseable, correctly-counted score array for 36 of them (37%) — asked to score all 12 pooled
candidates, it unreliably drops or adds an entry to the JSON array. Shrinking
`TokenEconomyConfig.SemanticSearchRerankCandidatePoolSize` to `6` raised the parseable-response
rate to 92% (89/97) and, as a direct result, brought negative-FP down to 20% — the ceiling
retrieval-design.md's §3 originally targeted — with recall held flat.

## Not covered by this contract / by `TokenSaver.MCP.Tests`/`TokenSaver.Library.Tests`

- **Anything requiring a live Ollama process**: actual distillation/summarization/embedding output
  quality, `ModelRoute`'s live passthrough, `RepoIndexer`'s/`SemanticSearchService`'s embedding
  calls. Tests cover the kill-switch/raw-fallback paths (`DistillationServiceTests`) and pure logic
  (`ChunkExtractorTests`, `SavingsTrackerTests.Verdict_*`) — the same split
  `OllamaEndpointRegistryTests` already documents for this codebase (offline-testable behavior vs.
  live-verification).
- **`RunTests`/`LintFix`/`ContainerLogs`/`DiffSummarize` against real external processes** —
  covered by manual verification, not automated tests, since they depend on `dotnet`/`docker`/`git`
  actually being installed and behaving a specific way in the test environment.
- **Exact wording of any distilled summary** — only response *shape* (fields present, degraded-path
  text vs. thrown exception) is part of this contract; prompt wording lives in
  `DistillationService`'s role-prompt tables and can change without being a contract break.

# TokenSaver MCP — agent instructions

These instructions apply to every session. Follow them automatically without being asked.

## Context — run at session start

Call `build_concise_context` at the start of every session before answering questions about the codebase. This builds a token-efficient workspace index into `.tokensaver/` and returns a folder summary. Use the workspace root from the current working directory or `F:\mcp` if not otherwise known.

If context generation hits the token limit, call `context_hygiene` first to prune stale or unrelated cached context, then retry.

## Code editing — use EditFile for all file changes

When making code changes, always use the `EditFile` tool rather than generating raw diffs or suggesting manual edits. EditFile validates the patch against the current file content before applying it, which prevents line-offset drift and partial-apply errors.

For changes spanning multiple files, call `ManagePlan` to create a plan first, then execute steps via `EditFile` one at a time.

## Code index — use CodeIndex for navigation

Before reading a file to understand how a symbol is used, call `CodeIndex(action: "find", name: "SymbolName")` to locate it without reading every file. Use `CodeIndex(action: "dependencies", filePath: "...")` to understand what a file imports and what imports it.

If the index seems stale (symbol not found that you know exists), call `CodeIndex(action: "scan")` to refresh it.

## Context hygiene — keep the cache lean

Call `context_hygiene` before any large context generation pass, and whenever the active context looks bloated or full of old unrelated summaries. This prunes `.tokensaver/` without losing valid cached entries.

## Model roles (config.json)

- `draft` — fast, lightweight model for initial generation
- `main` — larger model for refinement and review
- `deep` — deep reasoning model for complex tasks
- `large` — highest-capacity model for demanding tasks

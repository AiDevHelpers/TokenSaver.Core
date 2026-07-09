# TokenSaver MCP Context Test

This is a TokenSaver Test. It validates the `.tokensaver` context cache and the tool-based context hygiene workflow within the TokenSaver MCP.

## Goal

Ensure the local context builder and hygiene report are only invoked through explicit trigger actions, not automatically forwarded to the calling AI.

## Steps

### Step 1 — Build concise context

```text
run_step(model="draft",
  step_title="Build concise local context",
  step_content="""
Use the `build_concise_context` tool to generate a minimal local context summary
from the current workspace and requested file list.

- requestedFiles: ["src/cleaner/localContext.ts", "src/server.ts"]
- workspaceRoot: "."
""")
```

### Step 2 — Generate hygiene report

```text
run_step(model="main",
  step_title="Generate context hygiene report",
  step_content="""
Use the `context_hygiene` tool to inspect the `.tokensaver` cache and return a
hygiene summary with counts of cached files and any stale entries.
""")
```

## Criteria

- The context builder and hygiene report are only used when the TokenSaver Test trigger is invoked.
- No `.tokensaver` context artifacts are automatically included in regular prompt handling.
- The report should return cached local file counts and any hygiene alert status.

# TokenSaver Full Self-Test

Trigger phrase: "ask tokensaver to run the full self test".
IDE bridge checks are maintained in the `SperryDev.Ide-Bridge` repository at `docs/self_test.md`.

Run every TokenSaver MCP tool (`mcp__TokenSaver__*`) and report the result of each in a table.

## Setup

1. Load the schema for every TokenSaver tool with ToolSearch, in one call, using `select:` with all names. Use the schemas for arguments. Do not guess argument shapes.
2. Create a scratch folder `./.tokensaver-test/` in the workspace root. Write `./.tokensaver-test/sample.txt` with three lines of text. Use this folder for every write test. Do not touch any other file.

## Tier 1: read-only

Call these directly and record the result.

- help
- token_budget (content: "self test sample", threshold: 8000)
- token_savings_report
- context_diag (action: describe)
- list_instructions
- get_instruction (name: tdd)
- list_ollama_endpoints
- agent_config (action: get)
- model_route (only if a read-only form exists; otherwise SKIPPED)
- get_captured_problems
- get_problem_summary
- get_high_priority_issues
- get_problems_by_file
- get_problems_by_severity
- get_debug_events
- wsl_status
- wsl_health
- container_logs (containerName: "tokensaver-self-test-nonexistent")
- container_logs (containerName: "bad;name") — expect an "Invalid container name" message, no docker call
- container_start, container_stop, container_restart (containerName: "tokensaver-self-test-nonexistent") — each must return a "not found" message; the preflight stops before any docker action runs
- container_exec (containerName: "tokensaver-self-test-nonexistent", command: "echo hi") — expect "not found"; no command runs
- code_index (action: find, name: "TokenSaver")
- semantic_grep (query: "where are the MCP tool definitions registered")

## Tier 2: scratch-scoped writes

Run these only against `./.tokensaver-test/` or the `./.tokensaver/` cache. Use the smallest arguments that exercise the tool.

- build_concise_context (prompt: "self test: summarize ./.tokensaver-test only", requestedFiles: [".tokensaver-test"])
- index_repo (rootPath: ./.tokensaver-test, extensions: [".txt"])
- code_index (action: scan, root: ./.tokensaver-test)
- session_snapshot (sessionId: "tokensaver-self-test", output path in `./.tokensaver-test/`)
- session_restore (on the snapshot just written)
- context_hygiene
- manage_plan (action: create, prompt: "add a comment line at the top of .tokensaver-test/sample.txt"), then manage_plan (action: execute, step 1) if steps were generated
- edit_file (action: apply, on `.tokensaver-test/sample.txt`, "line one" to "line 1")
- check_code (operation: syntax, on `.tokensaver-test/sample.txt`)
- lint_fix (projectPath: ./.tokensaver-test, lintCommand: "echo lint-ok")
- fix_code (operation: format, on `.tokensaver-test/sample.txt`)
- run_tests (only if a test project is discoverable in `./.tokensaver-test/`; otherwise SKIPPED)
- code_work (action: start, prompt: "What is the current working directory?")
- diff_summarize (repoPath: workspace root, stagedOnly: true)

## Tier 3: confirm first

Do not run these without explicit approval from the user. Describe what each would do and what approval it needs:

- wsl_run, wsl_do, wsl_debug_setup
- register_mcp_providers
- set_agent_preference
- manage_config (write actions)
- clean_and_chat
- build_watch
- worth_keeping
- calibration
- container_start, container_stop, container_restart (on a real container; these change its running state)
- container_exec (on a real container; runs a command inside it)

## Tier 4: Windows Terminal session (not an MCP tool)

No MCP tool exposes the terminal session code, so run these test suites instead. They are not MCP calls, so record them as their own rows.


## Rules

- For each tool, record: tier, arguments used, PASS, FAIL, SKIPPED, or NOT RUN, and a one-line note with the key output or error text.
- A tool that returns an error is a FAIL. Quote the error. Do not retry more than once.
- Do not edit any file outside `./.tokensaver-test/` and `./.tokensaver/`.
- At the end, delete `./.tokensaver-test/` and any test plan created in tier 2. Confirm in one line.
- Finish with: counts of PASS, FAIL, SKIPPED, and NOT RUN (tier 3), then the list of FAILs with their causes.

# TokenSaver Full Self-Test

Trigger phrase: "ask tokensaver to run the full self test".

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
- query_ide_status (ideType: VsCode)
- get_debug_state (ideType: VsCode)
- get_debug_output (ideType: VsCode)
- list_breakpoints (ideType: VsCode)
- get_captured_problems
- get_problem_summary
- get_high_priority_issues
- get_problems_by_file
- get_problems_by_severity
- get_last_test_result (ideType: VsCode)
- get_debug_events
- search_debug_output (pattern: "error")
- wsl_status
- wsl_health
- container_logs (containerName: "tokensaver-self-test-nonexistent")
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
- set_breakpoint, then remove_breakpoint (on `.tokensaver-test/sample.txt` line 1; a missing debug session is recorded as a FAIL with its message)
- execute_step (only if a debug session is active; otherwise SKIPPED)

## Tier 3: confirm first

Do not run these without explicit approval from the user. Describe what each would do and what approval it needs:

- launch_ide, stop_ide
- start_debugger, stop_debugger, start_debug_session, stop_debug_session
- clear_debug_output, clear_all_diagnostics
- wsl_run, wsl_do, wsl_debug_setup
- register_mcp_providers
- set_agent_preference
- manage_config (write actions)
- clean_and_chat
- build_watch
- worth_keeping
- calibration

## Tier 4: Windows Terminal session (not an MCP tool)

No MCP tool exposes the terminal session code, so run these test suites instead. They are not MCP calls, so record them as their own rows.

- `dotnet test src/Core/TokenSaver.TerminalControl.Tests`: unit tests, command pipe tests, and the end-to-end test that runs the real wrapper under `pwsh`. Expect every test to pass. Record the pass and fail counts.
- Real Windows Terminal smoke test: `TOKENSAVER_WT_SMOKE=1 dotnet test src/Core/TokenSaver.TerminalControl.Tests --filter WindowsTerminalSmokeTests`. It opens a real `wt` window, so run it only with explicit approval (tier 3 rule). Otherwise record it as NOT RUN.
- Elevated smoke test: `TOKENSAVER_WT_ELEVATED_SMOKE=1 dotnet test src/Core/TokenSaver.TerminalControl.Tests --filter ElevatedSessionTabRunsElevated`. It needs a UAC approval and must run from a standard (non-elevated) process, or it proves nothing about pipes across integrity levels. Record the process elevation alongside the result. Same approval rule as above.

## Rules

- For each tool, record: tier, arguments used, PASS, FAIL, SKIPPED, or NOT RUN, and a one-line note with the key output or error text.
- A tool that returns an error is a FAIL. Quote the error. Do not retry more than once.
- Do not edit any file outside `./.tokensaver-test/` and `./.tokensaver/`.
- At the end, delete `./.tokensaver-test/` and any test plan created in tier 2. Confirm in one line.
- Finish with: counts of PASS, FAIL, SKIPPED, and NOT RUN (tier 3), then the list of FAILs with their causes.

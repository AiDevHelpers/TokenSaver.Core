# TokenSaver MCP

MCP server that exposes the DraftModel to Claude as callable tools.
Enables step-by-step plan execution with context-aware sizing, sub-step chunking, and intelligent clarification.

## Files

- `config.json` — model definitions and runtime parameters
- `agent.md` — concise trigger guidance for callers
- `SPEC.md` — full implementation spec for more detailed follow-up

## Tools

- `ollama_status` — health check + models loaded in VRAM
- `ensure_models(min_ctx?)` — build missing models and register runtime num_ctx override
- `load_model(model?, keep_alive?)` — warm model into VRAM; returns GPU and VRAM stats
- `unload_model(model?)` — evict model immediately
- `load_plan(path, max_sub_tokens?=400)` — parse plan into sub-steps and calculate context needs
- `run_step(step_title, step_content, context?, model?)` — execute one sub-step
- `answer_question(step_title, step_content, question, answer, context?, model?)` — resolve clarifications
- `save_response(plan_path, content)` — write output to `<plan>.response.md`

## Execution flow

1. `load_plan(path)` → splits steps into sub-steps and returns recommended `num_ctx`
2. `ensure_models(min_ctx=N)` → builds missing models and stores runtime `num_ctx` override
3. `load_model` → warm model and log GPU/VRAM stats
4. For each step and sub-step:
   - call `run_step(step_title, sub_step.content, context)`
   - if `type == "result"`: accumulate and continue
   - if `type == "question"`: resolve or escalate
   - if `type == "context_overflow"`: recover using the recommended `num_ctx`
5. `save_response`
6. `unload_model`

## Context management

- `load_plan` pre-calculates how context grows and returns `recommended_num_ctx`.
- `ensure_models(min_ctx=N)` accepts that recommendation and uses it without requiring a rebuild if the models already exist.
- Each step is split into sub-steps of around 400 tokens at paragraph boundaries.
- `run_step` sends a compressed system prompt to the DraftModel, removing example blocks and the `## Context` section.
- If the model reports `context_overflow`, recover by unloading, increasing `num_ctx`, reloading, and retrying.

## Question handling

- High-confidence answers are resolved internally without user input.
- Low-confidence or policy-dependent questions are escalated to the user.

## Model roles

Roles are defined in `config.json`. The DraftModel manages which underlying model handles each role:

- `draft` — fast, lightweight model for initial generation
- `main` — larger model for refinement and review
- `deep` — deep reasoning model for complex tasks
- `large` — highest-capacity model for demanding tasks

## TokenSaver Tests

- `mcp-docs/test_plan.md`
- `mcp-docs/accuracy_test.md`
- `mcp-docs/context_test.md`

## Production note

The research folder may be deleted before production. Use this `config.json` at the repo root and the docs under `mcp-docs/` as the stable repository assets.

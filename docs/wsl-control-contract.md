# WSL Control Contract

**Status**: Public MCP tool surface
**Version**: 1.0
**Last Updated**: 2026-07-12

## Overview

The WSL Control contract lets the calling AI work with the Windows Subsystem for Linux without
authoring shell syntax itself. It detects whether WSL is installed, enumerates distributions,
reports whether a distro is actually working, runs commands (including apt and build operations),
and prepares a distro as a VS Code debug destination.

The headline capability is **intent-driven execution**: the caller describes what it wants in plain
language (`"install ripgrep"`, `"update the packages"`, `"show disk usage"`) and the local model
(the SimpleModelServer/Ollama router this server already depends on) synthesizes the exact Linux command.
Mutating actions are gated behind an explicit confirmation flag so nothing that changes the system
runs without user approval.

## Architecture

```
MCP tool (WslControlTools)
   → IWslActionPlanner ── IWslCommandModel ─→ OllamaService (local model: command synthesis)
   → IWslProvider ─────── IWslCommandExecutor ─→ Community.Wsl.Sdk (detect / enumerate / run)
   → IWslDebugConfigService (backend probe + launch.json fragment)
```

- **`TokenSaver.WslControl`** (net10.0-windows) — the provider library. Windows-targeted because
  `Community.Wsl.Sdk` ships only `net6.0/net8.0-windows7.0` assemblies and WSL is Windows-only.
- **`IWslCommandModel`** is declared in the library but implemented in the host
  (`OllamaWslCommandModel`), so the library carries no dependency on the Ollama integration — it
  just asks "complete this prompt" and the host routes to the local model.
- **`IWslCommandExecutor`** is the seam over "run one program in a distro"; the default wraps the
  SDK's `Command`, and tests substitute a fake so command-building logic runs without a real WSL.

## Public Interface

Five MCP tools:

### WslStatus

Reports whether WSL is installed and lists registered distributions.

**Output (text):**
```
WSL is installed — 2 distribution(s), default: Ubuntu.
  * Ubuntu (WSL 2)
    Debian (WSL 2)
```

When WSL is absent, returns the reason plus the install guide URL.

### WslHealth

Probes whether a distribution is actually working.

**Input:** `distro` (optional — default distro when omitted)

**Output (text):** OS (`PRETTY_NAME`), kernel (`uname -sr`), default user (`id -un`), whether
`sudo` works non-interactively, and whether `apt-get` is present. A single probe collects all
fields as `KEY=VALUE` lines so parsing is order-independent.

### WslDo

Do something in WSL described in plain language. The local model turns the request into the exact
command; the caller never writes shell syntax.

**Input:**
- `request` (required) — natural-language description
- `distro` (optional)
- `confirm` (default false) — set true only after the user approves a system-changing action

**Behavior:**
- The distro's OS description is passed to the model as a hint so it picks distro-appropriate
  package names.
- Read-only requests run immediately.
- Mutating actions (apt install/update/upgrade, sudo, file writes/deletes) return the **proposed
  command** and do **not** run unless `confirm=true`. Confirmation is the union of the model's own
  `needsConfirmation` verdict, an always-confirm rule for apt categories, and a regex backstop that
  catches destructive verbs (`rm`, `dd`, `mkfs`, redirection, …) even if the model under-reports
  risk.
- Unknown/unparseable model replies degrade to a request for clarification — the tool never invents
  a command.

### WslRun

Runs an explicit bash command line (for when the caller already knows the command). Prefer `WslDo`.
Runs via `/bin/bash -c`, so pipes and redirection work.

### WslDebugSetup

Prepares a distribution as a VS Code debug destination.

**Input:** `runtime` (`dotnet` | `python` | `node`), `programPath` (as seen inside WSL),
`distro` (optional), `workspaceRoot` (required when writing), `write` (default false).

**Behavior:**
- Probes whether the runtime's debugger backend is present in the distro (`dotnet` → `vsdbg` at
  `~/.vsdbg`; `python` → `debugpy` importable; `node` → `node` on PATH). When missing, returns an
  **install hint** the caller can pass straight back to `WslDo`.
- Emits a `launch.json` configuration that runs the program inside WSL over the classic
  `pipeTransport` model (`wsl.exe -d <distro> -- …`).
- With `write=true` and a `workspaceRoot`, writes `.vscode/launch.json` **only when it does not
  already exist** — an existing (possibly JSONC) file is never rewritten; the fragment is returned
  for the caller to merge. This composes with the existing Debugger Control tools, which can then
  drive a session against the generated configuration.

## Safety Model

| Action | Gate |
| --- | --- |
| `WslStatus`, `WslHealth`, read-only `WslDo`/`WslRun` | none (safe) |
| apt install / update / upgrade, sudo, file mutation via `WslDo` | `confirm=true` required |
| Package names for apt | validated against `^[A-Za-z0-9][A-Za-z0-9+._-]*$`; injection rejected |
| Shell arguments (packages, working dir) | single-quoted via `BashQuote` |
| Writing `launch.json` | never overwrites an existing file |

The confirmation gate exists so a mis-classified destructive command cannot auto-run: the calling
AI is expected to surface the proposed command to the user and only re-call with `confirm=true`
after approval.

## Error Handling

| Case | Behavior |
| --- | --- |
| WSL not installed/supported | `WslStatus` returns reason + install URL; other tools return an execution error |
| No distro registered and none named | tools return an execution error explaining `wsl --install -d Ubuntu` |
| Command exits non-zero | reported as `exit N` with stdout/stderr — not an exception |
| Command exceeds timeout | result marked timed-out (apt: 15 min, build: 10 min, default: 2 min) |
| Local model unreachable | plan degrades to Unknown asking for an explicit command |
| Invalid apt package name | rejected before execution |

## Dependencies

- [Community.Wsl.Sdk 3.1.0](https://www.nuget.org/packages/Community.Wsl.Sdk) — WSL detection,
  distribution enumeration, and command execution. Windows-only (`net8.0-windows7.0`).
- The local model (SimpleModelServer router / Ollama) for command synthesis, reached through the
  same `OllamaService` the rest of the server uses.

## Revision History

| Date | Version | Changes |
|------|---------|---------|
| 2026-07-12 | 1.0 | Initial contract: WslStatus, WslHealth, WslDo, WslRun, WslDebugSetup |

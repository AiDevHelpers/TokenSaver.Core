# TokenSaver MCP Test Plan

## Goal

Verify the TokenSaver MCP question-handling flow: one question the DraftModel resolves autonomously, one it must escalate to the user.

These are TokenSaver Tests — run them against the TokenSaver MCP, not any other MCP server or model endpoint.

### Step 1 — Write a hello file

Create `./mcp_test_output.txt` containing the text "MCP OK" and the current timestamp.

### Step 2 — Append a padding line

Append a line of repeated characters to `./mcp_test_output.txt` to pad the file.
The line should be exactly 512 bytes long.

### Step 3 — Set a retention policy

Write a comment at the top of the file indicating how long logs should be retained before deletion.
The retention period is determined by the team ops policy — use whatever the team has decided.

### Step 4 — Verify

Read the last 3 lines of `./mcp_test_output.txt` and print them.

# TokenSaver MCP Accuracy Test

This is a TokenSaver Test. It verifies that the TokenSaver MCP performs correctly on key planning and implementation tasks without exposing the plan to the calling AI unless the test trigger is invoked.

## Goal

Validate the SimpleModelServer draft→main implementation chain on a narrowly scoped Python helper task and ensure the chain is reliable enough for real work.

## Steps

### Step 1 — Draft helper function

```text
run_step(model="draft",
  step_title="Draft _get_dict_dir()",
  step_content="""
Write a Python helper function _get_dict_dir() that returns the path
to the dict/ directory bundled with the app.

When running as a PyInstaller exe, sys.frozen is True and the bundle
is at sys._MEIPASS. When running normally, the dict dir is next to
the script file (__file__).

- Check getattr(sys, 'frozen', False)
- If frozen: return Path(sys._MEIPASS) / "dict"
- If not frozen: return Path(__file__).parent / "dict"
- Use from pathlib import Path and import sys

Also write: _DICT = _get_dict_dir() / "master.json"
""")
```

### Step 2 — Refine with main

```text
run_step(model="main",
  step_title="Refine _get_dict_dir()",
  step_content="""
Review and improve this Python code. Fix any bugs, clean imports,
add a one-line docstring. Output only the corrected code.

<paste draft output here>
""")
```

### Step 3 — Audit

```text
audit_step(
  step_title="_get_dict_dir()",
  step_content="<original task>",
  code="<main output>"
)
```

## Criteria

- `getattr(sys, 'frozen', False)` must be used
- `_DICT` must be present
- `sys.exit`, module imports and docstring handling are verified by audit
- failures should be caught by audit_step or by manual review when necessary

---
name: code-graph
description: Find symbols, callers/callees, related files, blast radius; not for single known files.
---

# Code graph (codebase-memory-mcp)

codebase-memory is an on-demand server of the `mcp_lazy` tool (server `codebase-memory`); it is off until you enable it.

- Enable: `mcp_lazy` with `{"action": "enable", "server": "codebase-memory"}`. The reply lists the tools and their input schemas.
- Call: `{"action": "call", "server": "codebase-memory", "tool": "<name>", "args": {…}}`.
- Disable: `{"action": "disable", "server": "codebase-memory"}` when the task no longer needs the graph.
- Not installed: `enable` fails with `"codebase-memory-mcp" was not found on PATH` when the binary is missing. Then say so to the user ("codebase-memory-mcp is not installed; using grep/read instead") and do the work with `grep`/`glob`/`read`.

## When not to use
Known file, tiny project (< ~10 source files), or a question `grep` answers in one call → skip the graph entirely.

## Before querying
1. Check ignore hygiene once per project: the root must have `.gitignore` or `.cbmignore` covering secrets (`.env*`, `*.pem`, `*.key`, `secrets/`) and build output. If missing, do NOT index — tell the user, suggest a `.cbmignore`, continue with `grep`/`glob`/`read`.
2. Enable the server, then before the first query of a session call `index_repository` with the absolute project path — it is incremental: it only reprocesses changed files. Run it again after files change (your edits, git operations, a long pause); do not re-run it between queries when nothing changed. Never index a home directory, parent folder or another project.

## Queries
- Symbol: `search_graph` (`name_pattern` regex, `label` Function/Class/…). Then `get_code_snippet` with the qualified name.
- Who calls / what it calls: `trace_path` (`function_name`, `direction` inbound|outbound|both, depth ≤ 3).
- What a change affects: `detect_changes` (git diff → symbols, risk).

## The graph is not the truth
- The index is a snapshot. After edits, renames or deletions run `index_repository` again before trusting results (no background watcher in this profile).
- Absence in the graph does not prove absence in the program: config keys, string-based dispatch, dynamic imports, reflection, IPC/event names, unsupported languages → confirm with `grep`.
- Cite `file:line` from `read`/`grep`, not only graph output.

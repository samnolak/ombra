# Harness bench

Compares two engine trees (baseline vs changed harness) on fixed tasks with the same model. Results and conclusions:
K5 in `docs/PRODUCT_DIRECTIONS.md`. Every script documents its arguments in its header comment.

- `run.mjs` + `model.mjs` + `tasks.mjs` + `compare.mjs` — scripted OpenAI-compatible model with fault injection
  (429, 503, stream drop, truncated tool JSON, small server window); `BENCH_MODEL=` runs the same tasks on a real model.
- `polyglot.mjs` — Aider Polyglot subset (`polyglot-subset.json`, 10), the full JS+Python set (`BENCH_SUBSET=all`,
  83) or the hard set (`polyglot-hard.json`, written by `select-hard.mjs`), no Docker.
- `swebench.mjs` — SWE-bench Verified subset (`swebench-subset.json`, 20 instances; `swebench-subset-50.json`, a
  50-instance superset), scored by the official `swebench` harness in Docker with Epoch AI's arm64 images (native
  under colima on Apple silicon).
- `codex.mjs` — OpenAI Codex CLI (`codex exec`) as a side of `polyglot.mjs` and `swebench.mjs`, for comparing the
  harness with the market's on the same model.
- `claudecode.mjs` — Anthropic Claude Code (`claude -p`) as the same kind of side, for Anthropic models.
- `isolation.mjs` — the `--config` overlay that keeps the user's own MCP servers, extensions, rules, skills and agents
  out of engine sides (see below).

## Sides: baseline, shipped, variants, Codex, Claude Code

| side | swebench.mjs side | polyglot.mjs / run.mjs |
|---|---|---|
| engine without harness layer | `<name>=<engine>=<provider/model>=<thinking>` | `BENCH_MODEL=<provider/model>` |
| as shipped (`harness/profile`) | `…=<thinking>=shipped` | `BENCH_AS_SHIPPED=1` |
| harness variant dir | `…=<thinking>=shipped:<harness-dir>` | `BENCH_AS_SHIPPED=1 BENCH_HARNESS_DIR=<harness-dir>` |
| Codex CLI | `<name>=codex=<model>=<effort>` | polyglot only: `BENCH_CODEX=1 BENCH_MODEL=<model>` (engine arg `-`) |
| Claude Code | `<name>=claude=<model>=<effort>` | polyglot only: `BENCH_CLAUDE=1 BENCH_MODEL=<model>` (engine arg `-`) |

The harness dir becomes `OMBRA_HARNESS_DIR`. Codex setup (once): `npm i @openai/codex` in `.temp/lab/codex` and
`.temp/lab/codex-home/auth.json` as a symlink to `~/.codex/auth.json`, nothing else in that folder. Each Codex run gets
a fresh `CODEX_HOME` holding only that symlink (the user's Codex config, AGENTS.md, rules, skills stay out), full access
without approvals or sandbox, no web search/browser, and `allow_login_shell=false` so the PATH wrappers stay first.
Codex rows carry `codex/<model>`, tokens from `turn.completed`, requests from the session rollout (`rollout.jsonl`),
and `toolErrors` = shell commands with a non-zero exit; cost is 0 (subscription).

Claude Code setup (once): `npm i @anthropic-ai/claude-code` in `.temp/lab/claude` (a native binary; `BENCH_CLAUDE_BIN`
overrides it). Auth: `CLAUDE_CODE_OAUTH_TOKEN` from the bench's environment if set, else the Anthropic OAuth access
token omp keeps in `<agent-dir>/agent.db` (`BENCH_AGENT_DIR`, `PI_CODING_AGENT_DIR`, `~/.omp/agent`), read read-only
at every run start because omp refreshes it. Only the access token is passed, never the refresh token (Claude Code must
not rotate the user's login), through a pipe on fd 3 (`CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR`), so it is not in the
agent's Bash environment; it is never printed or written. An expired token fails the run: an omp request refreshes it.
Each run gets a fresh `CLAUDE_CONFIG_DIR` under `.temp/lab/claude-runs` (removed after the run: no user settings,
CLAUDE.md, memory, plugins, hooks), the side's isolated `HOME`, `--dangerously-skip-permissions`,
`--strict-mcp-config` plus `ENABLE_CLAUDEAI_MCP_SERVERS=false` (no MCP, claude.ai connectors included), WebSearch and
WebFetch disallowed, `--no-session-persistence`, autoupdate/telemetry/nonessential traffic off. Not `--bare`: the side
is Claude Code as people use it. The prompt goes on stdin (`--disallowedTools` is variadic). `SHELL=/bin/bash`: the
Bash tool sources a snapshot that exports the CLI's own PATH and runs no login shell, so the PATH wrappers stay first.
Claude Code rows carry `claude-code/<model>`; from `stream-json` (`--verbose --forward-subagent-text`, subagents
included): `requests` = distinct assistant message ids, `toolCalls` = tool_use blocks, `commands`/`testRuns` = Bash
calls (matching the test pattern), `toolErrors` = tool_results with `is_error`; tokens summed over the result's
`modelUsage` (all models; input includes cache reads and writes; `reasoningTokens` = thinking, inside output);
`costUsd` = `total_cost_usd`, the API equivalent (subscription), which counts toward `BENCH_BUDGET_USD`. A run that a
signal ended (other than the bench's own timeout) gets `[harness-bench] claude ended on <signal>` in `stderr.txt`: on
the owner's Mac since 2026-10-27 an outside process sends Claude Code SIGKILL at its first tool call (endpoint
protection suspected), and such rows are not results.

```sh
BENCH_CODEX=1 BENCH_MODEL=gpt-6-luna BENCH_THINKING=low bun polyglot.mjs - <installed-runtime> <polyglot> <out> 2
BENCH_CLAUDE=1 BENCH_MODEL=claude-haiku-5-5 BENCH_THINKING=low bun polyglot.mjs - <installed-runtime> <polyglot> <out> 2
BENCH_SUBSET=all BENCH_MODEL=openai-codex/gpt-6-luna bun polyglot.mjs runtime/engine <installed-runtime> <polyglot> <out> 3
bun select-hard.mjs <out-a> <out-b>    # → polyglot-hard.json: exercises not solved in every run
```

Engine sides read their credentials from the user's agent dir (`PI_CODING_AGENT_DIR`, default `~/.omp/agent`;
`BENCH_AGENT_DIR` overrides it), and with it everything else that dir holds. Before 2026-10-26 that leaked the owner's
MCP servers into every engine request (`figma-remote`: about 3.9k tokens of tool routes and server instructions in
the first request), while a Codex side starts from an empty `CODEX_HOME`. `isolation.mjs` now gives every
real-model engine side of `run.mjs`, `polyglot.mjs` and `swebench.mjs` a `--config` overlay
(`<out>/isolation.yml`, `<out>/<side>/isolation.yml` for SWE-bench), passed after `BENCH_CONFIG` and
`swebench-offline.yml`:

- `disabledProviders: [native]`: the native discovery provider reads the agent dir's `mcp.json`, `extensions/`,
  `rules/`, `skills/`, `hooks/`, `tools/`, `prompts/`, `commands/`, `AGENTS.md` and `SYSTEM.md` (and a project `.omp/`,
  which bench work dirs do not have). Harness items come from the `ombra-harness` provider and stay on.
- `disabledExtensions: [mcp:<name>, …]`: every server named in the agent dir's `mcp.json`/`.mcp.json`, read at run time
  (names only), so the server stays off if another source declares it.
- `task.disabledAgents`: the agents in `<agent-dir>/agents/*.md`, except names the harness also defines.

The lists also take the values of the harness `config.yml` and the other overlays, because array settings replace
lower layers. The credentials, `OMBRA_HARNESS_DIR` and its extensions (`lazy-mcp`, `step-checkpoint`,
`completion-check`) are unchanged, and so are the settings in the user's `config.yml`. The scripted-model side of
`run.mjs` already uses a fresh agent dir under its temp `HOME`. Results recorded before this change carry the leak.

## run.mjs checks and edit modes

A run succeeds only if the world agrees, not the agent's report:

- the task's `check.mjs` prints OK (`checkOk`);
- every workspace file outside the task's `allowedChanges` has the same SHA-256 after the run as before
  (`untouchedChanged` lists the files that differ, including new and deleted ones; a rewritten `check.mjs` fails here);
- every stdout line of the engine's `--mode json` channel parses as a JSON object (`strayStdout` lists the others).

Failures also print a `FAIL <run dir>: …` line on stderr. The summary and `compare.mjs` show `check OK` and `stray
stdout` next to `success`.

`BENCH_EDIT_MODE=<hashline|replace|patch|apply_patch|sloppy>` pins the engine's `edit.mode` (through
`PI_EDIT_VARIANT`) for one side. The `prices` and `rename` tasks change three separate lines of one file through
`edit`; the scripted model writes the call in the variant the engine's tool schema offers (`editModeSeen`): one call
for hashline, patch, apply_patch and sloppy, one call per site for replace. With the scripted model this is a smoke
test of each variant and a measure of request count and input size; edit quality needs `BENCH_MODEL`.

```sh
BENCH_EDIT_MODE=hashline bun run.mjs runtime/engine <installed-runtime> .temp/edit-hashline 3 none prices,rename
BENCH_EDIT_MODE=replace bun run.mjs runtime/engine <installed-runtime> .temp/edit-replace 3 none prices,rename
bun compare.mjs .temp/edit-hashline .temp/edit-replace task
```

With `BENCH_MODEL=` the run strips extensions, skills and rules by default, so both sides see the same tool set.
`BENCH_AS_SHIPPED=1` keeps them on: the harness layer (`OMBRA_HARNESS_DIR=BENCH_HARNESS_DIR`, default
`harness/profile`) runs as Ombra ships it, for example to check a subscription model against the harness itself. The
user's own items stay off in both modes (see "Sides").

## SWE-bench

One-time setup (`SWEBENCH_DATA`, default `.temp/SweBench`): a Python ≥ 3.10 venv with `swebench` and `datasets` and
`verified.jsonl` (`load_dataset("SWE-bench/SWE-bench_Verified", split="test").to_json(...)`). Images live in a tar
cache (`<data>/images/<id>.tar`) pulled on the host by the `crane` binary (go-containerregistry) and `docker load`ed:
behind a TLS-intercepting proxy colima's Docker cannot verify ghcr.io (and `docker pull` can hang for hours), so
`docker pull` is only the last resort, cut after `BENCH_DOCKER_PULL_TIMEOUT_MS` (120 s). `bun swebench.mjs prefetch`
fills the cache for the subset ahead of time. Keep `NODE_EXTRA_CA_CERTS` set for the agent's provider requests when
such a proxy is on.

```sh
BENCH_BUDGET_USD=10 bun swebench.mjs run <out> <installed-runtime> gold=gold \
  haiku-base=<baseline-engine>=anthropic/claude-haiku-4-5=low haiku-k5=runtime/engine=anthropic/claude-haiku-4-5=low
bun swebench.mjs report <out> results/swebench-<date>.json
BENCH_SUBSET=swebench-subset-50.json bun swebench.mjs run <out> <installed-runtime> \
  ship=runtime/engine=openai-codex/gpt-6-luna=low=shipped var=runtime/engine=openai-codex/gpt-6-luna=low=shipped:.temp/lab/variants/<name> \
  codex=codex=gpt-6-luna=low
BENCH_SUBSET=swebench-subset-50.json bun swebench.mjs run <out> <installed-runtime> \
  ship=runtime/engine=anthropic/claude-haiku-5-5=low=shipped cc=claude=claude-haiku-5-5=low
```

`select [20|50]` rewrites a subset; the 50 is a superset of the 20 (same strata, higher counts, the 20 listed first).
The `gold` side applies the reference patch: an instance it does not resolve has a broken environment on this
machine and goes to `excluded` in the subset. The sides of one instance run concurrently, so every side sees the
same machine load. The agent works offline (`swebench-offline.yml`: no web search, URL reads or browser).
`python`/`pytest`/`pip` on its PATH run in the instance's container. Completed runs and evaluations are cached in
`<out>`.

Caveats:
- `BENCH_BUDGET_USD` is checked only before an instance starts. Set it below the real cap by the cost of the most
  expensive instance (one haiku base+k5 pair cost up to ≈ $1.9) and use `BENCH_PARALLEL=1` for the paid sides.
- Some images ship a testbed that is already modified (sphinx `setup.py`/`tox.ini`, astropy `pyproject.toml`,
  requests `build/`). Those files land in every side's patch, gold included, and inflate `linesAdded`/`extraEdits`.
  `results/swebench-2026-10-19.json` gives corrected values that exclude the gold side's non-gold files.

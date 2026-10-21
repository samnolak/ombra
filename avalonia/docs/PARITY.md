# Feature parity: pinned omp 18.12.2 vs Ombra

The original inventory covered omp 18.2.0 CLI flags, builtins and RPC, using its source (`packages/coding-agent/src`:
`cli/args.ts`, `slash-commands/builtin-*.ts`, `modes/rpc/rpc-mode.ts`, `modes/rpc/rpc-types.ts`) and running help/tests.
The current runtime pin is **omp 18.12.2**. Beta R0.2 updates the recovery and approval rows below; this does not
claim a fresh exhaustive audit of every historical row. Additional pin capabilities and remaining gaps are tracked
in [PARITY_ROADMAP](PARITY_ROADMAP.md), with executed gates in [NEXT_RELEASE_ROADMAP](../../docs/NEXT_RELEASE_ROADMAP.md).

Status:

- **Done** — the GUI does it, and where.
- **Done (typed)** — a builtin that runs over RPC: typed in the message box it runs inside omp and its output shows
  in the conversation. No dedicated control (yet).
- **Via omp terminal** — omp offers it only in its terminal UI (or its CLI). The GUI answers the command with a card
  and **Open omp terminal**, which starts omp's own TUI in the terminal panel (a separate omp, with its own session).
- **Not applicable** — makes no sense in a GUI (why).
- **Missing** — not in the GUI yet (why, and the plan).

An area in italics (*Panes*, *Files*, *Connectors*, *Workspace*) names the part of the client that does it; every row
was checked against the merged client and its tests.

## F. Compatible runtime and upgrade contract

| Gate | Contract |
|---|---|
| F1: owned runtime pin | `ombra-engine-18.12.2-bun-1.4.2-v1`: complete repository-owned engine sources based on omp **18.12.2**, not a patched installed npm tree; Bun **1.4.2**, frozen dependency lockfile and integrity-pinned downloads. |
| F2: protocol and source identity | Shared-engine protocol **1** wraps session RPC v2 framing. Installer requires the host's `--self-check` to report protocol 1 and omp 18.12.2 before activation. Manifest/source SHA256 and source identity must match this client; tests reject protocol 0/2 and another omp version. |
| F3: upgrade path | Engine upgrades ship with the client. Review the upstream source changes, update canonical `runtime/engine` and any dependency/version pins, run `python3 runtime/generate-engine-artifact.py`, then its `--check` gate, runtime installer/source tests and real-engine smoke. In particular recheck steer/settle semantics before raising the upstream base. Staging failure preserves the existing installation; older packs are retained for explicit cleanup, never silently selected as compatible fallbacks. Packages carry the installed pack (`tools/package/package.sh` → `Ombra --install-engine`); after a dependency change recheck each `RuntimePrune` rule's evidence (onnxruntime-web only in transformers' browser export, react/lucide only in omp-stats' dashboard sources, onnxruntime-node's binding path) and rerun a local-model (ONNX) smoke on the pruned pack. |

Package native isolation is enforced before signing and against the final ZIP/tar.gz:
`tools/package/native-platform.py <rid> <archive>`. It inspects ELF, Mach-O (including
universal slices), and native PE headers, not filename extensions: portable managed
DLLs stay, foreign OS/CPU binaries (including `.node` addons and extensionless Bun)
are removed from unsigned staging. Signing only sees the resulting platform tree;
the final check also covers notarization re-zips and cross-built RIDs without a smoke
runner. `test-native-platform.py` exercises all six RID combinations, managed DLLs,
universal macOS binaries, staging pruning and both archive formats.
The shared `Ombra.Core.Interop` project selects `libombra_core` by the target RID,
not the build host: host builds use Cargo's `target/release`, cross-builds use
`target/<Rust target triple>/release`. An optional host native build must never
leak into a different RID's publish directory.

For isolated local smoke builds, build `tests/Ombra.Tests` in Release with
`--artifacts-path <absolute-path>`, then pass that same path as `OMBRA_ARTIFACTS_PATH`
to `tools/ci/real-omp-smoke.sh <work-dir>`. Without that variable, existing CI build
locations remain unchanged. The smoke uses a **stand-in model**, but the installed
engine, provider transport, RPC and tools are real; it does not certify native rendering.

2026-10-18 macOS arm64 verification: `python3 runtime/generate-engine-artifact.py --check`
passed; `OMP_SMOKE_PORT=18436 OMBRA_ARTIFACTS_PATH="$PWD/.temp/ParityF/artifacts"
bash avalonia/tools/ci/real-omp-smoke.sh .temp/ParityF/smoke` passed the real installer
(1 test), installed Bun engine contracts (59 tests), and real-omp/acceptance suite
(16 tests, including steer, abort, recovery and v2 chunking). The smoke now runs its
stand-in server with the installed pinned Bun rather than requiring a separate Node,
and fails immediately if that server does not start.

The script's additional perf-lite gate **did not pass**: warm 10k history 2305.5 ms
against 1500 ms (cold 3910.5 ms), measured during concurrent team builds/tests.
Re-measure on a quiet machine in RC; the threshold is unchanged. The 30-second
headless stream completed (631 RPC frames, footprint +2.06%); this short run does
not exercise the native cadence or long-duration memory gates.

Installer/source regression command:
`dotnet test avalonia/tests/Ombra.Tests -c Release --artifacts-path .temp/ParityF/artifacts --filter 'FullyQualifiedName~RuntimeSourceArtifactTests|FullyQualifiedName~RuntimeInstallerTests'`
— **35 passed, 0 failed, 0 skipped**, including the added protocol rejection and
manifest version/entry consistency assertions.

## 1. Launch flags (`omp [flags] [messages]`)

The GUI opens a logical session in its shared engine with explicit project cwd and session arguments:
`--approval-mode <mode> [--resume <file>] [--model <m>] <extraArgs>`. It does not launch a CLI for each chat.
`extraArgs` in `ombra.local.json` applies to logical sessions; `environment` configures the shared host and
requires a coordinated restart. Explicit management/TUI commands remain separate from GUI session transport.
"Settings file only" below means there is no control for it in the window.

| Flag | What omp does | GUI |
|---|---|---|
| `--model` | Model for the session (fuzzy) | **Done** — model picker next to the message box (`set_model`); a fixed start model: `model` in the settings file |
| `--smol`, `--slow`, `--plan` | Models for the smol / slow / plan roles | **Via omp terminal** (`/agents`, the TUI's model selector) or `omp config set modelRoles`. The GUI sets the *advisor* role only (Model menu › Advisor › Choose model). Plan: a role picker in Settings |
| `--prewalk`, `--no-prewalk`, `--prewalk-into` | Hand off to a cheaper model at the first edit | **Done (typed)** for the session: `/prewalk` arms it. The launch flags: settings file only |
| `--plan-yolo`, `--plan-yolo-into` | Start in plan mode and auto-approve the plan | **Via omp terminal** (plan mode is TUI-only in 18.2.0) |
| `--provider` | Legacy provider selector | **Not applicable** (superseded by `--model`) |
| `--api-key` | API key for this run | **Not applicable** by design: keys stay in omp's configuration (Settings › Model providers signs in; `environment` in the settings file can pass a key variable) |
| `--system-prompt`, `--append-system-prompt` | Replace / extend the system prompt | **Configuration only by design** (owner principle, 2026-10-18): no separate Instructions settings editor; project rule files use the existing file editor |
| `--allow-home` | Allow starting in `~` | **Missing** (settings file only): opening your home folder as a project lets omp switch to a temp folder |
| `--profile` | Isolated profile | **Done** — Settings › Advanced › Profile |
| `--alias` | Create a shell shortcut for a profile | **Not applicable** (a shell alias) |
| `--cwd` | Start directory | **Done** — Open folder… / the project chip; the sidebar's project groups |
| `--mode` | text / json / rpc / rpc-ui | **Not applicable** (the GUI is the rpc-ui client) |
| `--config` | Extra config overlay | **Used by the app**: every omp it starts gets an overlay with `disabledProviders` (Network privacy); your own overlay: settings file only |
| `--add-dir` | Extra workspace directory | **Done** for the open session: session menu › Workspace folders (`/add-dir`, `/remove-dir`); as a launch flag: settings file only |
| `-p`, `--print`, `--print-thoughts` | Non-interactive run | **Not applicable** (one-shot CLI mode) |
| `-c`, `--continue` | Continue the previous session | **Done** — the sidebar lists the project's sessions newest first; the GUI reopens the last project |
| `-r`, `--resume` | Resume by id / path / picker | **Done** — sidebar sessions (another project restarts omp there with `--resume`); `/resume` opens the list |
| `--from-claude`, `--from-codex` | Import a Claude Code / Codex session | **Via omp terminal** (`/resume @claude`, `@codex`). A separate GUI import wizard is not planned (owner principle, 2026-10-18); this is not GUI import parity |
| `--session-dir` | Session storage directory | **Missing** (settings file only); the sessions list follows the folder omp reports |
| `--no-session` | Ephemeral session | **Not applicable** (the GUI lists and resumes sessions) |
| `--models` | Models for Ctrl+P cycling | **Not applicable** (TUI key cycling; the picker lists every available model) |
| `--no-tools`, `--tools` | Built-in tools on/off | **Missing** (settings file only; `/tools` typed shows them) |
| `--no-lsp`, `--no-pty` | LSP tools, PTY bash | **Missing** (settings file only) |
| `--thinking` | Thinking level | **Done** — thinking chip (`set_thinking_level`) |
| `--service-tier` | OpenAI service tier | **Done** for priority: Model menu › Fast mode (`set_fast_mode`); other tiers settings file only |
| `--hide-thinking` | Hide thinking blocks (display) | **Done** — thinking is folded ("Thought for 4s") |
| `--advisor` | Enable the advisor | **Done** — Model menu › Advisor (`/advisor on|off`, for the session) |
| `--external-thinking` | Private scratchpad instead of provider reasoning | **Missing** (settings file only) |
| `--hook`, `-e/--extension`, `--no-extensions`, `--plugin-dir` | Load / disable extensions | **Done** for installed ones — Plugins page › Plugins — *Connectors* area; the flags: settings file only |
| `--no-skills`, `--skills` | Skills discovery / filter | **Done** — Plugins page › Skills (skills on/off) — *Connectors* area |
| `--no-rules` | Rules discovery | **Missing** (settings file only) |
| `--export` | Export a session file to HTML | **Done** for the open session — session menu › Export as HTML |
| `--no-title` | No title generation | **Missing** (settings file only); rename in the session menu |
| `--max-time` | Stop after a duration | **Missing** (settings file only) |
| `--auto-approve` | Approve every tool | **Done** — permission mode › Bypass permissions |
| `--approval-mode` | always-ask / write / yolo | **Done** — permission mode chip (restarts omp on the session) |

## 2. Subcommands (`omp <command>`)

| Command | GUI |
|---|---|
| `acp` | **Not applicable** (another client protocol, for editors) |
| `agents` | **Via omp terminal** (`omp agents unpack`) |
| `auth-broker`, `auth-gateway`, `dry-balance` | **Not applicable** (credential infrastructure, not a desktop task) |
| `bench`, `if-bench`, `gallery`, `render`, `grep`, `read`, `search` | **Not applicable** (benchmarks and developer test tools) |
| `browser-relay` | **Via omp terminal** |
| `cleanse` | **Via omp terminal** (also `/cleanse`) |
| `collab`, `join` | **Via omp terminal** (live sharing is TUI-only) |
| `commit` | **Via omp terminal** |
| `completions` | **Not applicable** (shell completion) |
| `compress` | **Via omp terminal** |
| `config` | **Done** — the Settings pages read and write omp's settings through `omp config get/set --json` (Computer use, Git, SSH, Plugins… — *Connectors / Workspace* areas); Model menu options; share settings on the Share card |
| `gc` | **Via omp terminal** |
| `git` | **Via omp terminal** (omp's git TUI); the branch / worktree chip — *Workspace* area |
| `grievances`, `images`, `ttsr`, `tiny-models`, `token` | **Via omp terminal** |
| `install`, `plugin` | **Done** — Plugins page › Plugins and catalog — *Connectors* area |
| `models` | **Done** for listing (model picker); `omp models refresh`: via omp terminal |
| `ps` | **Done** for the session's jobs — Background tasks pane — *Panes* area; the daemon process list: via omp terminal |
| `say` | **Not applicable** (text to speech on the speakers) |
| `setup` | **Done** for first run (install omp, Open omp setup for a provider); optional dependencies: via omp terminal |
| `share` | **Done** for the open session — session menu › Share a link; another saved session: via omp terminal |
| `shell` | **Done** — the terminal panel (a shell in the project) |
| `ssh` | **Done** — Settings › SSH hosts — *Workspace* area |
| `stats` | **Done** — context popover › Usage dashboard (`/stats`) |
| `update` | **Through the client's updates**: each client version pins one omp; a client update that brings a newer omp installs it by itself after the restart (Settings › Updates) |
| `usage` | **Done** for the current provider — context popover (`/usage`); every account at once: via omp terminal |
| `worktree` | **Done** — Settings › Git and worktrees and `/wt` — *Workspace* area |

## 3. Slash commands

omp 18.2.0 has two kinds of builtins: the ones with a text-mode handler run **over RPC** (the GUI sends them as a
prompt; omp runs them and prints `command_output`), the others exist **only in its terminal UI** and, sent over RPC,
would reach the model as a plain message. The GUI never sends the second kind: typed in the message box they open
the GUI's equivalent or the "runs in omp's terminal" card (`OmpBuiltins.TuiOnly`, `TerminalOnlyCommands`), and the
slash menu lists them with where they go ("Settings", "terminal"…). Skills (`/skill:name`), prompt templates and
extension commands that omp lists itself keep going to omp.

### Session and conversation (`builtin-lifecycle.ts`, `builtin-session.ts`)

| Command | RPC | GUI |
|---|---|---|
| `/new` | TUI | **Done** — a project's **+** or *New session here*; `/new` starts one in the current project (the sidebar's New chat, Ctrl+N, starts a chat outside any project) |
| `/resume` | TUI | **Done** — the sidebar; `/resume [text]` shows it filtered |
| `/rename [title]` | yes | **Done** — session menu › Rename (`set_session_name`); `/rename` without a title (omp names it) typed |
| `/session info` | yes | **Done** — session menu › Copy session ID, Copy session file path |
| `/session delete` | yes | **Done** — session menu › Delete session… (asks first, then a new session) |
| `/session pin [account]` | yes | **Done (typed)** (pin a provider account to the session) |
| `/drop` | TUI | **Done** — opens Delete session (the same: delete, then a new session) |
| `/clear` | TUI | **Via omp terminal** (no RPC to clear the context in place). Plan: none in 18.2.0; Compact or New session instead |
| `/fresh` | yes | **Done** — session menu › Fresh provider session |
| `/compact [soft\|remote\|snapcompact] [focus]` | yes (background) | **Done** — session menu › Compact conversation… (focus text), context popover › Compact now; running card with Stop (`abort`), omp's result ("48,210 → 9,120 tokens"). The mode words: typed |
| `/shake [elide\|images\|thinking]` | yes | **Done (typed)** |
| `/handoff [focus]` | yes (background) | **Done** — session menu › Hand off… (omp writes a handoff document and goes on from it *in the same session*; it is not a new session over RPC) |
| `/retry` | yes (starts a turn) | **Done** — session menu › Retry the last turn; the retried turn shows like any run |
| `/branch`, `/rewind` | TUI (RPC `navigate_tree`, owned engine) | **Done** — session menu › Rewind…, **Rewind to here** on a message, also what typing `/branch` or `/rewind` opens (`get_branch_messages` + `navigate_tree`: in place, as omp's own Esc Esc rewind — the same session file goes on from before the chosen message, what came after stays in it as a branch, the message back in the box; no new session file, no second chat in the list). RPC `branch` (a new file holding a copy of the whole history) is no longer used |
| `/fork` | TUI (RPC `fork`) | **Done** — chat menu › Fork (also what typing `/fork` does: the whole conversation in a new session) and **Fork from here** on a reply (its hover action and right-click menu: a new session up to that reply, `get_entries` finds its entry); the original session stays; omp's `session_busy` refusal shows on the card |
| `/tree` | TUI | **Via omp terminal** (session tree navigation). The owned engine's RPC `navigate_tree` moves the leaf; a GUI tree view is not built |
| `/pin [session id]` | yes | **Done** — session menu › Pin / Unpin session; pinned sessions are listed first in the sidebar with a pin |
| `/move <path>` | yes | **Done** — session menu › Move to another folder… (the window follows omp to the new folder) |
| `/wt`, `/worktree [branch]` | yes | **Done** — *Workspace* area |
| `/dirs`, `/add-dir <path>`, `/remove-dir <path>` | yes | **Done** — session menu › Workspace folders… (list, Add folder…, remove) |
| `/memory view\|clear\|sync` | yes | **Done** — session menu › Memory (what goes into the context, Update now, Clear memory… with a confirmation, or "Memory is off") |
| `/memory stats\|diagnose\|queue\|enqueue\|rebuild` | yes | **Done (typed)**; `/memory mm …`: omp refuses it outside its TUI |
| `/todo …` | yes | **Done** — tasks card above the message box; Plan pane — *Panes* area; `/todo` subcommands typed |
| `/jobs` | yes | **Done** — Background tasks pane — *Panes* area (read over RPC `get_jobs`, not the `/jobs` text) |
| `/usage [show]` | yes | **Done** — context popover (tokens and cost, or the provider's plan limits with bars) |
| `/usage reset …` | yes | **Done** — the account's saved resets under its plan limits (popover and Settings → Model providers), **Use a saved reset** spends one after asking (`/usage reset` lists the account ids, then `/usage reset <provider>/<id>`); typing it works too |
| `/context` | yes | **Done** — the context ring and its popover (the breakdown as bars) |
| `/stats` | yes | **Done** — context popover › Usage dashboard (link to omp's local dashboard) |
| `/changelog [full]`, `/tools` | yes | **Done (typed)** |
| `/hotkeys` | TUI | **Done** — opens the keyboard shortcut sheet (also ⌘/ / Ctrl+/); omp's TUI keys: its terminal |
| `/extensions`, `/status` | TUI | **Done** — card → Plugins page — *Connectors* area; omp's dashboard: terminal |
| `/agents` | TUI | **Via omp terminal** (per-agent models, prewalk, advisor config) |
| `/git [revision]` | TUI | **Via omp terminal** (git TUI); branch chip — *Workspace* area |
| `/hub` | TUI | **Done** — opens Background tasks — *Panes* area |
| `/login [provider]` | TUI (RPC `login`) | **Done** — Settings › Model providers (RPC sign-in with the browser link / code card) |
| `/logout [provider]` | TUI (RPC `get_logout_accounts`, `logout`, omp 18.8.0) | **Done** — Settings › Model providers (the sign-out icon beside *Signed in*: pick the stored account to remove) |
| `/mcp …` | yes | **Done** — Plugins page › MCP — *Connectors* area (sign-in / sign in again / sign out, Reconnect, Reload config, server forms over RPC); the chat's connectors plug under the message box; `/mcp reauth <name>` typed opens its sign-in page in Ombra (the chat's Browser; Settings: the sign-in window; right-click Sign in: the default browser) |
| `/ssh …` | yes | **Done** — Settings › SSH hosts — *Workspace* area |
| `/btw` | TUI | **Done** over the owned engine's RPC (`btw`, `btw_cancel`, `get_btw_history`): the BTW panel above the message box, also while omp works (*Session* area); an omp without them: via omp terminal |
| `/tan`, `/omfg`, `/cleanse`, `/debug` | TUI | **Via omp terminal** |
| `/exit`, `/quit`, `/q` | TUI | **Not applicable**: the card says to close the window (omp stops with it) |
| `/restart` | TUI | **Done** — restarts omp on the session (`/restart`, Restart omp) |

### Modes, models and tools (`builtin-modes.ts`, `builtin-control.ts`)

| Command | RPC | GUI |
|---|---|---|
| `/model`, `/models` | yes | **Done** — `/model` alone opens the model picker; `/model <name>` typed goes to omp |
| `/switch [model]` | yes | **Done (typed)** (a session-only model) |
| `/fast [on\|off\|status]` | yes (+ `set_fast_mode`) | **Done** — Model menu › Fast mode (state from `get_state.fastModeEnabled`; omp's refusal for a model without a priority tier shown under it) |
| `/extended-context [on\|off\|status]` | yes | **Done** — Model menu › Extended context (saved in omp's settings) |
| `/skillful [on\|off\|status]` | yes | **Done (typed)** |
| `/computer [on\|off\|status]` | yes | **Done** — Settings › Computer use — *Workspace* area |
| `/prewalk [restart]` | yes | **Done (typed)** |
| `/security …` | yes | **Done (typed)** |
| `/force <tool> [prompt]` | yes | **Done (typed)** |
| `/settings` | TUI | **Done** — opens Settings |
| `/setup`, `/providers` | TUI | **Done** — opens Settings › Model providers |
| Custom and local providers (`models.yml`) | — (omp reads the file at start) | **Done** — Settings › Model providers › Local and custom: Ollama, LM Studio, llama.cpp, vLLM (running or not, Add / remove) and custom OpenAI- or Anthropic-compatible providers (test, add, edit, remove); the app edits `models.yml` in place and restarts omp |
| `/plan`, `/plan-review` | TUI | **Via omp terminal** (plan mode). The Plan pane (*Panes* area) shows omp's todo plan |
| `/goal` | TUI | **Done** over the owned engine's RPC (`goal`): the goal bar above the message box — objective, status, time, Pause/Resume, Edit, Clear; an omp without it: via omp terminal |
| `/vibe`, `/guided-goal`, `/loop` | TUI | **Via omp terminal** |
| `/queue <message>` | TUI | **Done** differently: while omp works, Enter queues (`follow_up`) and Alt+Enter steers (`steer`); × or ↑ takes a queued message back (`remove_queued_message`) |
| `/live`, `/pause` | TUI | **Via omp terminal** |

### Collaboration, export, plugins (`builtin-collaboration.ts`, `builtin-marketplace.ts`)

| Command | RPC | GUI |
|---|---|---|
| `/advisor [on\|off\|status]` | yes | **Done** — Model menu › Advisor; without a model in omp's "advisor" role: Choose model (writes `modelRoles` with `omp config set`, restarts omp on the session, turns it on) |
| `/advisor dump [raw]` | yes | **Done (typed)**; `/advisor configure`: via omp terminal |
| `/export [--themes] [path]` | yes | **Done** — session menu › Export as HTML (Open, Show in folder); options typed |
| `/share` | yes | **Done** — session menu › Share a link… (says what is uploaded and where from omp's `share.*` settings before uploading; the link is copied) |
| `/dump` | yes | **Done (typed)** (the transcript as text; Copy under each reply) |
| `/trace` | yes | **Done (typed)** (prints the local trace URL) |
| `/browser [headless\|visible]` | yes | **Done (typed)** |
| `/copy` | TUI | **Done** differently: select text, or the copy buttons under replies and code blocks |
| `/open` | TUI | **Done** differently: links in the conversation open on click |
| `/collab`, `/join`, `/leave` | TUI | **Via omp terminal** |
| `/marketplace …`, `/plugins …`, `/reload-plugins` | yes | **Done** — Plugins page › catalog and plugin sources — *Connectors* area |
| `/skill:<name>`, prompt templates, extension commands | yes (omp's catalog) | **Done** — the slash menu (from `available_commands_update`), sent to omp |

### Message box prefixes (omp's TUI input rules)

| Prefix | omp's TUI | GUI |
|---|---|---|
| `/name args` | builtin, skill, prompt or extension command | **Done** — as above |
| `@path` | attach a file's contents to the message | **Done** — omp expands it over RPC too (`session.prompt` → file mentions); the Files pane's **Add to message** inserts it at the caret — *Files* area |
| `!command` | run in the shell, output added to the context | **Done** — RPC `bash` (see below) |
| `!!command` | run in the shell, kept out of the context | **Via omp terminal** (RPC `bash` always adds to the context); the card offers a plain shell in the terminal panel |
| `$ code`, `$$ code` | run Python in omp's eval | **Via omp terminal** (no RPC for it in 18.2.0); in the GUI such a message goes to the model as text |

## 4. RPC features (`modes/rpc/rpc-mode.ts`)

| RPC | GUI |
|---|---|
| `negotiate_protocol`, `ready`, `rpc_chunk` (v2 framing) | **Done** — transport |
| `prompt`, `steer`, `follow_up`, `abort` | **Done** — message box, Queue / Steer, Stop / Esc |
| `set_event_filter` | **Implemented** — pinned 18.8.0, `events:null,messageUpdates:"delta"` immediately after negotiation; every event type retained, full final message preserved. Real-runtime text equivalence and client/tool/abort/large-reply scenarios verified |
| `abort_and_restore_queue` | **Implemented in beta R0.2** — Stop withdraws runtime queued text/images into editable recovery; local originals survive limited payloads, nothing is automatically resent |
| `abort_and_prompt` | **Done** — "Stop and send" (Alt+Shift+Enter) while omp works; a refused prompt returns its text and images to the message box. `promote_queued_message`: "Send now" on a queued follow-up |
| `new_session`, `switch_session` | **Done** — sidebar |
| `navigate_tree`, `get_branch_messages` | **Done** — session menu › Rewind…, a message's Rewind to here (in place). Text and image bytes return from the engine into the composer, including messages outside the loaded history page |
| `fork`, `get_entries` | **Done** — chat menu › Fork, a reply's Fork from here (`get_entries` maps the reply to its message entry) |
| `get_state` | **Done** — model, thinking, session name, todos, context ring, model options, queue settle |
| `set_fast_mode` | **Done** — Model menu › Fast mode |
| `get_available_commands`, `available_commands_update` | **Done** — slash menu |
| `remove_queued_message` | **Done** — × on a queued message; ↑ in the empty box puts the last one back to edit (with its images) |
| `set_todos` | **Done** — Plan pane: the box marks a task done / to do; ⋯ edits its text in place, sets To do / In progress / Done / Dropped, moves it up or down, deletes it; "Add a task" appends (a new plan when there is none). Each change sends the whole list (details and notes kept) and the pane shows omp's answer, then get_state; changes are in the plan's History. omp keeps the edit in memory only: its next todo tool call builds on it, a reload of the session restores the last list the todo tool saved |
| `set_host_tools`, `host_tool_*` | **Done** — `terminal_read`: the agent reads a terminal tab of the window (selected tab or by number, last 200 lines by default, at most 2000 lines / 64 K characters), registered on every connection; omp's approval gate applies; `host_tool_cancel` stops it |
| `set_host_uri_schemes`, `host_uri_*` | **Not applicable** (the GUI offers no URI schemes of its own to the agent) |
| `set_subagent_subscription`, `get_subagents`, `get_subagent_messages`, `subagent_*` | **Done** — Background tasks pane — *Panes* area |
| `get_jobs`, `cancel_job`, `get_job_output` (Ombra engine) | **Done** — Background tasks pane: each job's status (incl. «Waiting for input»), **Stop**, **Show output** (byte-cursor tail, last 64 K characters kept) |
| `set_model`, `get_available_models` | **Done** — model picker |
| `cycle_model`, `cycle_thinking_level` | **Not applicable** (TUI key cycling; the pickers choose directly) |
| `set_thinking_level` | **Done** — thinking chip; a level picked mid-run is kept and set right before the next queued message (omp's `prompt`/`follow_up` take no level) |
| `set_steering_mode`, `set_follow_up_mode`, `set_interrupt_mode` | **Missing** (omp's defaults: one-at-a-time, immediate). Plan: Settings › General "Queued messages: one at a time / all" |
| `compact` | **Not used on purpose**: it blocks omp's command queue (no `abort` meanwhile); the GUI uses `/compact`, which omp runs in the background |
| `set_auto_compaction` | **Done** — Model menu › Auto-compact (state: `get_state.autoCompactionEnabled`) |
| `set_auto_retry` | **Done** — Model menu › Auto-retry (state: `omp config get retry.enabled`, get_state has none) |
| `abort_retry` | **Missing**: the retry notice shows; Stop aborts the run. Plan: "Cancel retry" on the notice |
| `bash`, `abort_bash` | **Done** — `!command` in the message box runs in the project (the output joins the context, a "shell" row in the conversation, Stop on its card); `!!command` (kept out of the context) exists only in omp's terminal: its card offers a shell in the terminal panel |
| `get_session_stats` | **Done** — the context ring's popover: this session's input/output/cache tokens and cost. Plan limits come from `omp usage --json` (CLI side process; RPC has no usage command and `/usage` over RPC prints either the tallies or the limits as text) |
| `export_html` | **Not used**: `/export` does the same and reports the path |
| `get_last_assistant_text` | **Not applicable** (the GUI has the text) |
| `set_session_name`, `session_info_update` | **Done** — rename |
| `handoff` | **Not used on purpose**: blocks the queue like `compact`; `/handoff` runs in the background |
| `get_messages`, `get_messages_page` | **Partial** — initial history still uses `get_messages`; paginated loading remains in the beta roadmap |
| `get_login_providers`, `login`, `get_logout_accounts`, `logout` | **Done** — Settings › Model providers |
| `extension_ui_request` select / confirm / input / editor / notify / cancel | **Done** — question/approval cards and notices; beta R0.2 preserves non-auth typed replies on observed cancel/revoke with quoted context (remote acceptance races still need runtime acknowledgement) |
| `set_ask_dialog`, `extension_ui_request` ask | **Done** — opted in on every connection: all questions of one `ask` call come as one card with a tab per question (the tool's header), one choice or any-that-apply (multi-select), the user's own words on every question, the recommended option marked (never preselected), digits pick options; Submit sends every answer in question order, Cancel answers "cancelled". An omp without the command keeps the per-question select/input cards |
| `extension_ui_request` setStatus / setWidget / set_editor_text / open_url | **Done** — status in the activity tooltip, widgets above the message box, text into the message box, the sign-in link card |
| `extension_ui_request` setTitle | **Missing** (an extension setting the window title is ignored) |
| `config_update`, `thinking_level_changed`, `model_changed` | **Done** — model and thinking chips follow |
| `auto_compaction_*`, `auto_retry_*`, `retry_fallback_*`, `ttsr_triggered`, `todo_reminder`, `notice` | **Done** — notices in the conversation |
| `goal_updated`, `btw_delta`, `btw_record` | **Done** — the goal bar and the BTW panel |
| `irc_message` | **Done** — a message card between agents (sender → recipient, folded to two lines, Markdown when open), also from the history (`irc:incoming`); a tool result omp skipped for a message that came first (`interrupt_skipped`) is a neutral *Skipped* step, not a failure |
| `command_output` | **Done** — typed builtins: in the conversation; the GUI's own: on the session card / popover / settings pages |
| `prompt_result`, `session_settled`, non-terminal `agent_end` | **Done** — run end detection |

## 5. What omp 18.2.0 does not offer over RPC (so the GUI cannot, except through the terminal)

Plan / goal / vibe / loop modes, `/clear`, `/tree`, `/logout` (over RPC since 18.8.0; plan, goal and `/btw` are in the
GUI over the owned engine's RPC), live collaboration (`/collab`, `/join`), realtime voice (`/live`), `/pause`, `/tan`, `/omfg`, `/cleanse`, the agents hub and
`/advisor configure`, omp's git TUI. (MCP OAuth sign-in and MCP forms are over RPC in the owned runtime:
`get_mcp_servers`, `mcp_login`, `mcp_logout`, `mcp_reconnect`, `elicit` — Plugins page › MCP, the chat's connectors plug and the form card.) The GUI never sends these to omp: they would
reach the model as a message. Each has the "runs in omp's terminal" card; the terminal panel's **+ omp TUI** starts
omp's own interface for them.

## 6. What the client adds (omp has no counterpart)

- **Micro-UX (M)** — shared checked copy feedback; contextual image/tool/error/event/queue/paste actions; code insertion
  and selected-text quoting; tab close menus, browser shortcuts, safe terminal right-click; persisted explicit unread
  state and window/panel layout; keyboard source-list/resize/focus-return behavior; reduced-motion-aware interaction
  transitions and new-activity indicator; Russian cardinal plural forms. The row-by-row implementation, exclusions,
  headless recorder evidence and native OS gates are in [PARITY_ROADMAP §6.9](PARITY_ROADMAP.md#69-аудит-m-2026-10-18-итоговая-таблица-и-доказательства).
- **Files pane** — the project tree with git marks and "changed in this session", Go to file, a code viewer, open in an
  editor or the file manager (*Files* area).
- **Browser preview and page comments** — a local web app beside the conversation; comments pinned to page elements
  go with the next message. omp's own browser tool drives the preview: the app serves the protocol of omp's cmux
  backend (`CMUX_SOCKET_PATH`, see `AgentBrowserBridge`), so pages omp opens show there instead of in headless Chromium
  (each `browser.open_split` is a pane tab with its own web view, `ViewModels/PreviewTab`, valid until
  `surface.close` or the user closes it, so tabs of different omp processes never evict each other; `browser.wait`
  answers ~1 s before `timeout_ms`, ahead of omp's socket timer; while the page shows a JavaScript dialog requests
  answer `dialog_open` with its kind and text and `browser.press` Enter/Escape answers it (an alert omp caused is
  accepted at once; a confirm/prompt omp caused goes to the user's card after 10 s unanswered); on macOS the helper
  library runs in the app's own `WKContentWorld` (`Platform/Mac/IsolatedWorld`, without a user gesture) and element
  actions are native input (`Platform/Mac/NativeInput`: NSEvents to the WKWebView's responder methods, `insertText:`,
  trusted with user activation; paced by `ClientCore/Browser/NativeInputActions`, settle on DOM/network quiet), with
  `"input": "synthetic"` + reason where that is not possible (no native input on WebView2/WebKitGTK yet, `<select>` and
  date/colour fields, hover in an inactive window); elsewhere the library lives in the page world under a random
  per-run global, non-writable, non-configurable and frozen with a seal; screenshots of the visible area through the
  web view's own snapshot call — `Platform/WebViewSnapshot` — not full-page or element clips, as on a real cmux surface).
  It is the agent's default for every omp the app starts (chats, the terminal's omp), by omp's official mechanisms
  only (`ClientCore/Browser/AgentBrowserDefaults`): `PI_BROWSER_CMUX=1` and `PI_BROWSER_RELAY=0` per process, an
  app-owned overlay appended to `PI_CONFIG_FILES` (`browser.enabled: true`, `cdpUrl: ""`, `cmux: true`,
  `idleCloseSec: 0`) and an `--extension` whose `before_agent_start` appends a note that the user sees this browser
  (not `--append-system-prompt`, which would replace the user's `APPEND_SYSTEM.md`); Settings › General › *Agent uses
  the Ombra browser* (default on) turns all of it off. The user's omp files are never written.
  Native windows a page opens (sign-in for HTTP auth, `alert` / `confirm` / `prompt`, *Leave this page?*, file
  chooser, download, permission, popup) are cards in the preview, one at a time over a frozen picture of the page
  (`ClientCore/Browser/DialogBroker`, `ViewModels/BrowserDialogsViewModel`), as cmux routes them: what omp's browser
  tool caused waits for the agent as modal state, what the user caused is the user's, sign-in is always the user's.
  HTTP auth (Basic, Digest, NTLM, Negotiate, proxies; Avalonia 12.1's WKWebView delegate answers none, so the server's
  401 page showed): `webView:didReceiveAuthenticationChallenge:completionHandler:` added to Avalonia's navigation
  delegate class (`Platform/Mac/WebKitHooks`), WebView2 `BasicAuthenticationRequested` (`Platform/Windows/WebView2Hooks`),
  WebKitGTK `authenticate` (`Platform/Linux/WebKitGtkHooks`); credentials for the session only, server trust and client
  certificates keep the engine's default handling.
  macOS (BROWSER_PLAN B2, `Platform/Mac/WebKitPage*`, `WebKitDownloads`, `PopupWebViewHost`; per tab through
  `ClientCore/Browser/BrowserPageHooks`): a `WKUIDelegate` of the app's own for alert/confirm/prompt, *Leave this page?*,
  file inputs and camera/microphone (*Always allow on this site* kept in `browser-sites.json` next to the client
  settings); `window.open` / `target=_blank` open as pane tabs built from WebKit's configuration, so `window.opener`
  works for OAuth pop-ups and `window.close()` closes the tab; failed navigations show an error page with the engine's
  reason and *Try again*, a crashed web process a crash page with *Reload* / *Open in browser* (its questions cancelled);
  downloads go to the folder the user chose (the save panel until then; never a default folder) and are listed under
  the page; the user agent is Safari's (`Version/… Safari/605.1.15`, Safari's version only when its WebKit is the one
  loaded). Logins persist across restarts in WebKit's default persistent data store (Avalonia's choice when no store
  is set). Not on macOS: passkeys (Apple-gated entitlement), camera (no usage description: WebKit refuses before
  asking), Storage Access (WebKit's own alert), element fullscreen.
- **Corporate network certificates** — Settings › Advanced › Network: on request only, every omp the app starts
  (chats, siblings, restarts, the omp terminal tab, `omp` CLI calls) trusts the certificate authorities macOS trusts
  (`NODE_USE_SYSTEM_CA=1`, Bun 1.4.2) or an app-validated copy of a CA file (`NODE_EXTRA_CA_CERTS`); the user's own
  variables always win (`ClientCore/Network/CorporateTrust`). A connection check makes one keyless request with the
  engine's Bun and environment and classifies TLS / DNS / refused / proxy / timeout. omp has no setting for this.
- **Network privacy** — Settings › Advanced › Network privacy: every omp the app starts (chats, siblings, restarts,
  the omp terminal tab, the self-test) gets the providers the user has not added in `disabledProviders` through a
  `--config` overlay, found by a script on omp's own runtime (`ClientCore/Network/ProviderGate`); first-run setup runs
  `omp login`, since in-session `/login` hides disabled providers. Opt-in strict mode points omp's `PI_PROXY` and the
  usual proxy variables at an allowlist proxy in the app (`ClientCore/Network/PrivacyProxy`, `NetworkPrivacy`).
  omp has no setting for either; proposal in [`upstream/omp-discovery-opt-in.md`](upstream/omp-discovery-opt-in.md).
- **Dictation** — speech to text on this computer, into the message box.
- **Pets** — ONI, an animated companion on the message box that follows what omp does (thinking, working, a question,
  a finished or stopped run, a failed tool, omp stopping, sleep); carried off it, it floats anywhere on the desktop over
  other apps, like Codex's pet (Settings › Pets).
- **Plan history** — earlier plans and every change to the todo list, kept for the session (*Panes* area).
- **Don't ask again / Deny and say why** (Claude Code's approval options) — omp 18.8's approval is a plain
  Approve/Deny select and its own `tools.approval.<tool>: allow` can only allow a whole tool, so the client keeps the
  rules (command prefix like `bash(npm test:*)`, exact command, or whole tool; this session / this project / always,
  Settings › Permissions) and answers covered requests itself, noting *Allowed automatically* in the conversation.
  A deny cannot carry a reason over RPC: the reason goes as a steering message, read right after the denied tool.
- **Durable input recovery (beta R0.2)** — per-config atomic draft journal, 500 ms debounce and lifecycle flushes;
  composer text/images/pastes/comments, queued input and uncertain submissions reopen for review, never auto-send.
  Successful session deletion removes its old journal records; active unsent composer content moves to the new chat.
  Owned draft-only chat folders survive cleanup and can be recreated on recovery; missing external projects are not created.
  Local plaintext and multi-instance/runtime-ack limits are documented in [USER_GUIDE](USER_GUIDE.md).
- **Approval safety (beta R0.2)** — hidden/control characters are visible without rewriting executed source;
  prefix rules reject shell syntax and ambiguous Git invocations, and exact persistence cannot widen a grant.
  Keyboard approval is ordered behind a release/appearance/activation guard, including localized access keys.
  Explicit exact grants and mouse buttons remain deliberate supported actions; this is not a shell sandbox.

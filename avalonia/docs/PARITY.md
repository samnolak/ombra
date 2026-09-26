# Feature parity: omp 18.2.0 vs OMP GUI

Every omp CLI flag and subcommand, every builtin slash command and every RPC feature of **omp 18.2.0**, and what the
GUI offers for it. Source of truth: omp's own code (`packages/coding-agent/src`: `cli/args.ts`, `slash-commands/
builtin-*.ts`, `modes/rpc/rpc-mode.ts`, `modes/rpc/rpc-types.ts`) and its help (`omp --help`, `omp <cmd> --help`),
checked against a running omp 18.2.0 (`tests/OmpGui.Tests/RealSessionAreaTests.cs`, `RealOmpCommandTests`).

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

## 1. Launch flags (`omp [flags] [messages]`)

The GUI starts omp as `omp --mode rpc-ui --approval-mode <mode> [--resume <file>] [--model <m>] <extraArgs>` in the
project folder. `extraArgs` (and `environment`) in the GUI's settings file (`omp-gui.local.json`) pass any other flag;
"settings file only" below means there is no control for it in the window.

| Flag | What omp does | GUI |
|---|---|---|
| `--model` | Model for the session (fuzzy) | **Done** — model picker next to the message box (`set_model`); a fixed start model: `model` in the settings file |
| `--smol`, `--slow`, `--plan` | Models for the smol / slow / plan roles | **Via omp terminal** (`/agents`, the TUI's model selector) or `omp config set modelRoles`. The GUI sets the *advisor* role only (Model menu › Advisor › Choose model). Plan: a role picker in Settings |
| `--prewalk`, `--no-prewalk`, `--prewalk-into` | Hand off to a cheaper model at the first edit | **Done (typed)** for the session: `/prewalk` arms it. The launch flags: settings file only |
| `--plan-yolo`, `--plan-yolo-into` | Start in plan mode and auto-approve the plan | **Via omp terminal** (plan mode is TUI-only in 18.2.0) |
| `--provider` | Legacy provider selector | **Not applicable** (superseded by `--model`) |
| `--api-key` | API key for this run | **Not applicable** by design: keys stay in omp's configuration (Settings › Model providers signs in; `environment` in the settings file can pass a key variable) |
| `--system-prompt`, `--append-system-prompt` | Replace / extend the system prompt | **Missing** (settings file only). Plan: an "Instructions" field in Settings › Advanced |
| `--allow-home` | Allow starting in `~` | **Missing** (settings file only): opening your home folder as a project lets omp switch to a temp folder |
| `--profile` | Isolated profile | **Done** — Settings › Advanced › Profile |
| `--alias` | Create a shell shortcut for a profile | **Not applicable** (a shell alias) |
| `--cwd` | Start directory | **Done** — Open folder… / the project chip; the sidebar's project groups |
| `--mode` | text / json / rpc / rpc-ui | **Not applicable** (the GUI is the rpc-ui client) |
| `--config` | Extra config overlay | **Missing** (settings file only) |
| `--add-dir` | Extra workspace directory | **Done** for the open session: session menu › Workspace folders (`/add-dir`, `/remove-dir`); as a launch flag: settings file only |
| `-p`, `--print`, `--print-thoughts` | Non-interactive run | **Not applicable** (one-shot CLI mode) |
| `-c`, `--continue` | Continue the previous session | **Done** — the sidebar lists the project's sessions newest first; the GUI reopens the last project |
| `-r`, `--resume` | Resume by id / path / picker | **Done** — sidebar sessions (another project restarts omp there with `--resume`); `/resume` opens the list |
| `--from-claude`, `--from-codex` | Import a Claude Code / Codex session | **Via omp terminal** (`/resume @claude`, `@codex`). Plan: an "Import…" entry in the sidebar |
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
| `--hook`, `-e/--extension`, `--no-extensions`, `--plugin-dir` | Load / disable extensions | **Done** for installed ones — Settings › Plugins and skills — *Connectors* area; the flags: settings file only |
| `--no-skills`, `--skills` | Skills discovery / filter | **Done** — Settings › Plugins and skills (skills on/off) — *Connectors* area |
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
| `install`, `plugin` | **Done** — Settings › Plugins and skills — *Connectors* area |
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
| `/new` | TUI | **Done** — New session (sidebar, Ctrl+N); `/new` starts one |
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
| `/branch`, `/rewind` | TUI (RPC `branch`) | **Done** — session menu › Rewind…, also what typing `/branch` or `/rewind` opens (`get_branch_messages` + `branch`: a new session from before the chosen message, the message back in the box, the old session kept) |
| `/fork` | TUI | **Via omp terminal** (copies the whole session; no RPC for `fork`). Rewind covers "start over from a message" |
| `/tree` | TUI | **Via omp terminal** (session tree navigation). Plan: needs an RPC for `navigateTree` |
| `/pin [session id]` | yes | **Done** — session menu › Pin / Unpin session; pinned sessions are listed first in the sidebar with a pin |
| `/move <path>` | yes | **Done** — session menu › Move to another folder… (the window follows omp to the new folder) |
| `/wt`, `/worktree [branch]` | yes | **Done** — *Workspace* area |
| `/dirs`, `/add-dir <path>`, `/remove-dir <path>` | yes | **Done** — session menu › Workspace folders… (list, Add folder…, remove) |
| `/memory view\|clear\|sync` | yes | **Done** — session menu › Memory (what goes into the context, Update now, Clear memory… with a confirmation, or "Memory is off") |
| `/memory stats\|diagnose\|queue\|enqueue\|rebuild` | yes | **Done (typed)**; `/memory mm …`: omp refuses it outside its TUI |
| `/todo …` | yes | **Done** — tasks card above the message box; Plan pane — *Panes* area; `/todo` subcommands typed |
| `/jobs` | yes | **Done** — Background tasks pane — *Panes* area |
| `/usage [show]` | yes | **Done** — context popover (tokens and cost, or the provider's plan limits with bars) |
| `/usage reset …` | yes | **Done (typed)** (spend a saved Codex rate-limit reset) |
| `/context` | yes | **Done** — the context ring and its popover (the breakdown as bars) |
| `/stats` | yes | **Done** — context popover › Usage dashboard (link to omp's local dashboard) |
| `/changelog [full]`, `/tools` | yes | **Done (typed)** |
| `/hotkeys` | TUI | **Done** — the card lists the GUI's shortcuts; omp's TUI keys: its terminal |
| `/extensions`, `/status` | TUI | **Done** — card → Settings › Plugins and skills — *Connectors* area; omp's dashboard: terminal |
| `/agents` | TUI | **Via omp terminal** (per-agent models, prewalk, advisor config) |
| `/git [revision]` | TUI | **Via omp terminal** (git TUI); branch chip — *Workspace* area |
| `/hub` | TUI | **Done** — opens Background tasks — *Panes* area |
| `/login [provider]` | TUI (RPC `login`) | **Done** — Settings › Model providers (RPC sign-in with the browser link / code card) |
| `/logout [provider]` | TUI | **Via omp terminal**. Plan: none over RPC in 18.2.0 |
| `/mcp …` | yes | **Done** — Settings › Connectors — *Connectors* area |
| `/ssh …` | yes | **Done** — Settings › SSH hosts — *Workspace* area |
| `/btw`, `/tan`, `/omfg`, `/cleanse`, `/debug` | TUI | **Via omp terminal** |
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
| `/plan`, `/plan-review` | TUI | **Via omp terminal** (plan mode). The Plan pane (*Panes* area) shows omp's todo plan |
| `/vibe`, `/goal`, `/guided-goal`, `/loop` | TUI | **Via omp terminal** (omp's goal updates still show in the conversation) |
| `/queue <message>` | TUI | **Done** differently: while omp works, Enter queues (`follow_up`) and Alt+Enter steers (`steer`) |
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
| `/marketplace …`, `/plugins …`, `/reload-plugins` | yes | **Done** — Settings › Plugins and skills — *Connectors* area |
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
| `abort_and_prompt` | **Missing** (Stop, then send). Plan: "Stop and send" when a message is typed during a run |
| `new_session`, `switch_session` | **Done** — sidebar |
| `branch`, `get_branch_messages` | **Done** — session menu › Rewind… |
| `get_state` | **Done** — model, thinking, session name, todos, context ring, model options, queue settle |
| `set_fast_mode` | **Done** — Model menu › Fast mode |
| `get_available_commands`, `available_commands_update` | **Done** — slash menu |
| `set_todos` | **Missing** (the GUI shows todos; editing them: `/todo` typed) |
| `set_host_tools`, `host_tool_*`, `set_host_uri_schemes`, `host_uri_*` | **Not applicable** (the GUI offers no tools of its own to the agent) |
| `set_subagent_subscription`, `get_subagents`, `get_subagent_messages`, `subagent_*` | **Done** — Background tasks pane — *Panes* area |
| `set_model`, `get_available_models` | **Done** — model picker |
| `cycle_model`, `cycle_thinking_level` | **Not applicable** (TUI key cycling; the pickers choose directly) |
| `set_thinking_level` | **Done** — thinking chip |
| `set_steering_mode`, `set_follow_up_mode`, `set_interrupt_mode` | **Missing** (omp's defaults: one-at-a-time, immediate). Plan: Settings › General "Queued messages: one at a time / all" |
| `compact` | **Not used on purpose**: it blocks omp's command queue (no `abort` meanwhile); the GUI uses `/compact`, which omp runs in the background |
| `set_auto_compaction` | **Done** — Model menu › Auto-compact (state: `get_state.autoCompactionEnabled`) |
| `set_auto_retry` | **Done** — Model menu › Auto-retry (state: `omp config get retry.enabled`, get_state has none) |
| `abort_retry` | **Missing**: the retry notice shows; Stop aborts the run. Plan: "Cancel retry" on the notice |
| `bash`, `abort_bash` | **Done** — `!command` in the message box runs in the project (the output joins the context, a "shell" row in the conversation, Stop on its card); `!!command` (kept out of the context) exists only in omp's terminal: its card offers a shell in the terminal panel |
| `get_session_stats` | **Not used**: `/usage` and `/context` give the same numbers as omp prints them |
| `export_html` | **Not used**: `/export` does the same and reports the path |
| `get_last_assistant_text` | **Not applicable** (the GUI has the text) |
| `set_session_name`, `session_info_update` | **Done** — rename |
| `handoff` | **Not used on purpose**: blocks the queue like `compact`; `/handoff` runs in the background |
| `get_messages`, `get_messages_page` | **Done** — history when a session opens |
| `get_login_providers`, `login` | **Done** — Settings › Model providers |
| `extension_ui_request` select / confirm / input / editor / notify / cancel | **Done** — question and approval cards, notices |
| `extension_ui_request` setStatus / setWidget / set_editor_text / open_url | **Done** — status in the activity tooltip, widgets above the message box, text into the message box, the sign-in link card |
| `extension_ui_request` setTitle | **Missing** (an extension setting the window title is ignored) |
| `config_update`, `thinking_level_changed`, `model_changed` | **Done** — model and thinking chips follow |
| `auto_compaction_*`, `auto_retry_*`, `retry_fallback_*`, `ttsr_triggered`, `todo_reminder`, `irc_message`, `goal_updated`, `notice` | **Done** — notices in the conversation |
| `command_output` | **Done** — typed builtins: in the conversation; the GUI's own: on the session card / popover / settings pages |
| `prompt_result`, `session_settled`, non-terminal `agent_end` | **Done** — run end detection |

## 5. What omp 18.2.0 does not offer over RPC (so the GUI cannot, except through the terminal)

Plan / goal / vibe / loop modes, `/clear`, `/fork` (whole-session copy), `/tree`, `/logout`, live collaboration
(`/collab`, `/join`), realtime voice (`/live`), `/pause`, `/btw`, `/tan`, `/omfg`, `/cleanse`, the agents hub and
`/advisor configure`, omp's git TUI, MCP OAuth sign-in (TUI-only). The GUI never sends these to omp: they would
reach the model as a message. Each has the "runs in omp's terminal" card; the terminal panel's **+ omp TUI** starts
omp's own interface for them.

## 6. What the client adds (omp has no counterpart)

- **Files pane** — the project tree with git marks and "changed in this session", Go to file, a code viewer, open in an
  editor or the file manager (*Files* area).
- **Browser preview and page comments** — a local web app beside the conversation; comments pinned to page elements
  go with the next message.
- **Dictation** — speech to text on this computer, into the message box.
- **Pets** — a pixel companion on the message box that follows what omp does (Settings › Pets).
- **Plan history** — earlier plans and every change to the todo list, kept for the session (*Panes* area).

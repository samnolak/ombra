# Roadmap

Living document: current plan only, no history. Update after every significant change.
State, decisions and what is verified where: [`PROJECT_STATE.md`](PROJECT_STATE.md).

Next release planning: [`NEXT_RELEASE_ROADMAP.md`](../../docs/NEXT_RELEASE_ROADMAP.md) integrates the
product directions and parity work, with decision gates and acceptance criteria. D2 was accepted on 2026-10-15:
an Ombra-maintained omp fork is allowed. D3 now means improving our harness, not adding ACP backends;
existing authentication stays unchanged. D4 selects one Nightly/Gecko runtime for standalone browsing and ALL Ombra
browser/preview surfaces, with automatic connection and GPU surface sharing. Replace the current WebKit/WebView2
backend rather than retaining two engines; platform integration and performance require proof.
K7 adds a native code editor; D1's server trust model remains open.

## Ground rules

- **Agent runtime:** upstream omp (`can1357/oh-my-pi`) is the base for an Ombra-maintained fork, approved on
  2026-10-15. The owned source runs logical chat sessions in one app-managed host, not one CLI process per chat.
  Keep its versioned multiplexed RPC contract; fix missing contracts at their source rather than accumulating
  client workarounds. Review upstream changes in integration branches,
  run contract and end-to-end gates, then pin an immutable runtime artifact. No automatic upstream cutover and
  no patching installed user runtimes. Upstream PRs are useful, not a prerequisite for shipping our changes.
  The beta uses `ombra-engine-18.12.2-bun-1.4.2-v1`; published release readiness remains gated separately.
- Our layers: **Client Integration Layer** (`Ombra.Rpc`: process, stdio, framing, correlation) →
  **Application Core** (`Ombra.ClientCore`: state, reducer, session control) → **ViewModels** → **Avalonia UI**,
  plus a **Platform Layer** (OS adapters) created only when the first real OS-specific implementation appears.
- Harness and model/provider are separate layers on top of omp; the client uses them in place and never
  re-implements what omp or the harness already does.
- Targets: **Windows, macOS, Linux**. One portable Application Core; no OS checks outside the composition
  root and platform adapters. UX parity, not pixel parity.
- A milestone closes only when its verification gates pass. Unavailable platforms are reported as
  `NOT VERIFIED — PLATFORM UNAVAILABLE`, never as PASS.
- Feature cycle: discover → upstream omp → portable behaviour → platform differences →
  Application Core → platform adapters (if needed) → unit → real omp → Linux → macOS → Windows → GUI → regression → done.

Milestone statuses: NOT STARTED · IN PROGRESS · BLOCKED · VERIFY · DONE.
Feature statuses (never mixed): IMPLEMENTED (code + tests on this machine) · VERIFIED (all applicable gates, incl. CI
on 3 OSes) · BLOCKED_EXTERNAL (needs something we cannot get: hardware, certificates, a real model) · FAILED ·
NOT_APPLICABLE · DONE. BLOCKED_EXTERNAL is not DONE; a build is not GUI VERIFIED; a PASS on one OS is not carried
to another. Model evidence, never mixed: STAND-IN (scripted mock) · REAL MODEL GENERIC (a small real model, SmolLM2) ·
TARGET MODEL / PROVIDER (a larger model or a hosted provider) · PROVIDER SIGN-IN.

---

## Milestones

| Milestone | Status | Exit criterion |
|---|---|---|
| **M1 — First vertical slice** (15 features below, through the official RPC of the unmodified omp 18.2.0) | **IMPLEMENTATION COMPLETE ON LINUX** | all 15 features implemented and verified on the first platform — met on Linux |
| **M2 — Cross-platform RC validation** | **IN PROGRESS** — CROSS-PLATFORM RELEASE READINESS: INCOMPLETE | the macOS and Windows gates are PASS or documented external platform gates needing no architectural change |
| **M3 — Wave 2** (features beyond the slice) | IN PROGRESS — much of it is on `main` before M2's exit; not release-verified off Linux | M2 exit for a release |
| Release | BLOCKED (external) | M2 gates PASS on each OS, signing, a published release |

### M1 — First vertical slice · IMPLEMENTATION COMPLETE ON LINUX

Built in eight stages (recovery / research · Client Integration Layer + Application Core · desktop shell · daily
workflow · agent UX · platform features · distribution · UX / performance), all done. Linux is the first platform
confirmed end to end (build, tests, real omp, generic real
model, packaged GUI with real keyboard / mouse, notifications, updater, 20-minute background run). Windows / macOS:
earlier commits passed build, headless tests, real omp and package smoke on hosted runners; the current code is not
verified there (M2).

### M2 — Cross-platform RC validation · IN PROGRESS

Make the client demonstrably portable: Linux 20-min background gate; macOS and Windows build, package and runtime
gates (one command each: `tools/verify/macos-gate.sh`, `tools/verify/windows-package-gate.ps1`, or the manual workflow
`avalonia-platform-gates.yml`); a configurable TARGET model / provider gate (`tools/verify/target-model-gate.sh`).
External inputs it waits for: macOS / Windows runners or machines, a larger model server, CI runners.

### M3 — Wave 2 · IN PROGRESS

Started before M2's exit: side panes (Plan, Background tasks, Files), settings pages
(Connectors / MCP, Plugins and skills, Computer use, Git and worktrees, SSH hosts, Pets), the session menu, context
ring and model options, page comments, dictation and self-update are on `main` (parity in [`PARITY.md`](PARITY.md)),
checked on Linux only ([`PROJECT_STATE.md`](PROJECT_STATE.md)). A release still needs M2's exit.

In 0.0.6: file changes as one diff card per file with New file / Deleted / Renamed / Moved badges, old and new line
numbers, colour bars and word-level marks (the Files pane's Changes alike). Tool steps read as verbs with clear status
marks, commands as console blocks with the exit code, runs of reads and searches fold into *Explored*, and a run's end
lists the files it changed. New chat (⌘N) starts outside any project in a folder of its own, at once, from an omp
started ahead of time; the sidebar lists such chats under *Chats*. New chats and sessions start at thinking *Medium*
instead of omp's *High*. macOS releases are signed with the project's own long-lived certificate instead of ad-hoc,
so permissions given to the app (Documents, microphone…) stay across updates. Quitting the app ends everything it
started, omp's detached daemon broker and its background jobs included, and a start ends what a crashed or earlier
version left (macOS and Linux; Windows not verified). The harness template (1.3.0) gains
on-demand MCP servers: the agent enables Blender or Figma (Desktop) through the `mcp_lazy` tool only when a task needs
them and disables them afterwards ([`docs/harness.md`](../../docs/harness.md)). Settings › Model providers gains
*Local and custom*: Ollama, LM Studio, llama.cpp and vLLM shown as running or not and added in one click, and custom
OpenAI- or Anthropic-compatible providers tested, added, edited and removed (omp's `models.yml`, edited in place);
signing out is a compact icon beside *Signed in*. Preview comments stay on the page they were left on, single-page
app routes included. Updates are checked at every start and once a day and, by default, downloaded and made ready by
themselves: the new version goes in at the next restart or quit.

In 0.0.7: the preview's tab strip ends with **+** (alone as *+ New tab* while there is one tab) to open tabs of your
own, closable like omp's; the first tab, while empty, is no longer listed as *New tab* before omp's. The model menu
folds models under their provider (an accordion; the current model's provider open, search still flat). Sidebar:
projects can be pinned (first, in the order pinned) and renamed (a name of the sidebar's; the folder stays), and their
right-click menu shows the folder in Finder / Explorer; a pinned chat stays in its project, at its top (no separate
*Pinned* group). Settings › General › **Language**: English or Russian built in, and languages of one's own (JSON files in the
settings folder's `languages/`, started from `template.json`). Expanded tool output, consoles, diffs and thoughts have a
header bar that stays at the top while the block is on screen and folds it; a running tool's mark turns instead of
blinking; a chat waiting for an answer shows an amber raised hand in the sidebar; the message box's caret no longer
vanishes after a pause; hovers and clipped areas are rounded everywhere; tool tooltips read as plain text.

In 0.0.8 (Codex-style reskin, after the ChatGPT desktop app's Codex section): a neutral
grey palette with the logo's yellow as the one accent (Send, switches, checks; your messages tinted with it), pill buttons, 12-radius menus with a shortcut column.
An icon rail left of the sidebar opens Plugins, MCP and Skills, with Settings at its foot (the sidebar's and header's
Settings buttons are gone). The right side is one panel with tabs (Files, Changes, Background tasks, Plan and the
browser's pages); **+** opens a page of tools and the files this chat changed; ⌘/Ctrl+Shift+G opens Changes. The
terminal is a bottom panel. The header has the folder, the title, a **⋯** menu with every session action and the
panels, and a summary popover (changes, plan, background work). Sidebar search sits behind an icon; a chat row's hover
shows pin and delete and a card with its project and age. In the conversation a finished turn folds under
*Worked for 1m 18s* above its answer, tool calls group into one grey line per run (a command opens into a Shell card
with its exit code), user messages are bubbles tinted with the yellow, with time, copy and rewind on hover, a date line marks new days or
long gaps, the change card lists files with +/− and *Review changes*, and notices sit centred between hairlines. On
macOS the window has no separate title strip: the traffic lights sit in the app's own top row, whose empty parts move
the window (double-click zooms; a double-click on the chat title no longer renames there — Rename is in **⋯**).
The frame follows Codex too: a 44 px title band and the icon rail are one chrome surface, the sidebar, the
conversation and the right panel sit on rounded sheets under it, the panel's tabs are rounded tabs in the band, its
toolbars are grouped pills, and the new-tab tools, settings sections and recent files are rounded cards. Icons are
Lucide at Codex's sizes; the window has macOS 26's own rounder corners and its traffic lights sit in the middle of the
band; a new session's greeting, message box and the row under it stay in the middle of the frame, the terminal open
or not. Unpin shows a struck-through pin.

Also in 0.0.8: renamed OMP GUI → **Ombra** (projects and namespaces `Ombra.*`, executable `Ombra`, bundle id
`io.github.samnolak.ombra`, `OMBRA_*` variables, update feed `samnolak/ombra`). The app now uses the owner's
silver/red swirl artwork (2026-10-20); the ONI pet remains separate. New sessions greet: *Oh? You're approaching ‹project›?* The first
start moves the old data folders and links the old paths (omp's sessions keep them); packages carry the launcher a
second time as `OmpGui` so installed OMP GUI updaters accept them (checked: 0.0.7's updater installs a 0.0.8 package
on macOS); an updated *OMP GUI.app* renames itself to *Ombra.app*. Once no OMP GUI install is left, the `OmpGui`
launcher copy can go.

App icon assets are generated from `tools/icon/ombra-icon-sheet.png` with `python3 tools/icon/make_icon.py`
(Pillow, numpy, macOS `iconutil`; Xcode 26 for the macOS 26 catalog). The sheet's large tile is a mock-up with its
own corners, rim and glow, so the generator lifts the swirl off it onto a full-bleed black square.
`packaging/macos/Ombra.icon` (Icon Composer) is compiled into `Assets.car` (`CFBundleIconName`): macOS 26 draws
its own shape and its dark, tinted and clear looks, macOS 14–15 actool's flat renditions — the self-shaped icon
before this sat shrunk on a grey plate in the macOS 26 Dock. `ombra.icns` (`CFBundleIconFile`) has Apple's
continuous-corner shape on the 1024/824 grid. Window, greeting, Linux and Windows use that shaped tile;
circular sheet variants supply the tray and small ICO frames. Details: [`PLATFORM_NOTES.md`](PLATFORM_NOTES.md).

## M1 scope: the 15 features (fixed 2026-09-25)

"Finished" = a daily-usable OMP desktop client on Windows, macOS and Linux. The list below is the release
scope; anything not in it goes to the backlog (end of this file) and does not block a release. Every omp
feature and what the client offers for it: [`PARITY.md`](PARITY.md).
The historical M1 slice used Original OMP Core's official RPC; missing 18.2.0 paths were reached through
documented files, omp CLI or its TUI. This describes that slice, not a restriction on the next release:
new integration work follows the fork policy above and removes superseded workarounds on cutover.

| # | Feature | Stage | Mechanism |
|---|---|---|---|
| 1 | Approvals + extension dialogs (select / confirm / input / editor / cancel / notify / status / widget / set_editor_text / open_url), ask tool | workflow | RPC extension UI sub-protocol |
| 2 | Approval mode choice (ask for exec / ask always / auto-approve = explicit user opt-in, never automatic) | workflow | `--approval-mode` on restart of the same session |
| 3 | Sessions: list, new, open/resume, rename; project (working directory) choice | workflow | RPC `new_session` / `switch_session` / `set_session_name`; list = read-only scan of omp's session folder (`UNDOCUMENTED / VERSION-PINNED` file layout, no RPC in 18.2.0) |
| 4 | Model / provider: picker, thinking level, sign-in for OAuth providers | workflow | RPC `get_available_models`, `set_model`, `set_thinking_level`, `get_login_providers`, `login` + `open_url` |
| 5 | First-run setup: find or install the pinned omp runtime, profile choice, settings persisted by the client (never in omp's files) | workflow / distribution | client settings file; pinned runtime pack with integrity check |
| 6 | Composer: steer / follow-up while running, image attachments (picker, paste, drop), file references | workflow | RPC `prompt` `images`, `steer`, `follow_up` |
| 7 | Rich transcript: Markdown, code blocks with copy, tool cards (bash, read, edit with diff, write), thinking collapsed, copy message | workflow / agent UX | client rendering of RPC events |
| 8 | Error recovery: restart omp on the same session after a crash, clear error states, no orphan processes | workflow | client policy + `--session`/`switch_session` |
| 9 | Slash commands: catalog, autocomplete, command output; compaction | agent UX | RPC `get_available_commands`, `prompt`, `command_output`, `compact` |
| 10 | Skills / MCP / memory as far as omp exposes them (skill commands, `/mcp` text verbs, compaction/retry notices, todo progress) | agent UX | RPC commands + events |
| 11 | Terminal panel: shell in the project folder, and the omp TUI for TUI-only features (plan/goal/vibe, `/mcp` auth…) | platform | `ITerminalSession` (ConPTY / PTY) + VT emulator |
| 12 | Voice dictation into the composer (on-device) | platform | platform audio capture + local speech model |
| 13 | Clipboard, drag & drop, file dialogs, notifications on run end, app menu (macOS), tray | platform | Avalonia + Platform Layer adapters |
| 14 | Packages per OS/arch, runtime provisioning pinned with integrity checks, safe update check, diagnostics bundle (redacted), user docs | distribution | CI-built artifacts |
| 15 | Design system (light/dark), keyboard/focus, accessibility names, long-session performance | polish | client |

## Backlog (Wave 2 candidates, not release-blocking)

Several omp processes at once (background sessions, pool/LRU) · GUI flows for plan / goal / vibe / btw / tan
(via an omp extension or future upstream RPC) · skills manager page · MCP OAuth manager · models.yml editor · git panel
(stage/commit) · subagent hub · CLI operations panel · collab / live voice · harness updater · Claude/Codex
session import · session tree navigation / branch · controlled omp upgrade to a release with `session_settled`
to remove the settle probe (`set_event_filter` is already enabled on pinned 18.8.0) · opt-in model discovery and remote
catalog upstream ([`upstream/omp-discovery-opt-in.md`](upstream/omp-discovery-opt-in.md)): once omp ships it, its
settings replace the provider-gate script and close the `catalog.stencil.so` limitation.

---

## Next

1. M2 gates: the macOS and Windows gates need runners or machines; the target model gate needs a larger model server.
2. A release candidate from one commit once those gates pass on each OS, signed.
3. The rest of Wave 2 (backlog above) after M2's exit.
4. Parity with Codex / Claude Code, the bug classes they fixed and optimizations from other pi/omp clients, in
   waves: [`PARITY_ROADMAP.md`](PARITY_ROADMAP.md) (sources and evidence in [`parity-research/`](parity-research/)).

# Roadmap

Living document: current plan only, no history. Update after every significant change.
State, decisions and what is verified where: [`PROJECT_STATE.md`](PROJECT_STATE.md).

## Ground rules

- **Original OMP Core** is upstream omp (`can1357/oh-my-pi`) and stays outside this project. We do not
  rewrite, fork or patch it. Its contract to us is the official RPC over stdio. Incompatibilities are solved
  in our Client Integration Layer; real upstream bugs are recorded as `UPSTREAM OMP ISSUE` with a minimal
  external workaround. Dependencies on undocumented behaviour are marked `UNDOCUMENTED / VERSION-PINNED`.
- Our layers: **Client Integration Layer** (`OmpGui.Rpc`: process, stdio, framing, correlation) →
  **Application Core** (`OmpGui.ClientCore`: state, reducer, session control) → **ViewModels** → **Avalonia UI**,
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

On the [`next`](https://github.com/samnolak/omp-gui/tree/next) branch for the next version (not released): network
privacy (omp contacts only added providers; opt-in strict network privacy), sign-out per stored account, and in the
preview device sizes, area comments and a shrinking, scrolling tab strip.

## M1 scope: the 15 features (fixed 2026-09-25)

"Finished" = a daily-usable OMP desktop client on Windows, macOS and Linux. The list below is the release
scope; anything not in it goes to the backlog (end of this file) and does not block a release. Every omp
feature and what the client offers for it: [`PARITY.md`](PARITY.md).
Everything goes through Original OMP Core's official RPC; where 18.2.0 has no RPC path the feature either uses
a minimal external mechanism (files omp documents, `omp` CLI) or is reached through the omp TUI in the
terminal panel, never through a patched or re-implemented omp.

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
and `set_event_filter` (removes the settle probe and the quadratic streaming cost) · opt-in model discovery and remote
catalog upstream ([`upstream/omp-discovery-opt-in.md`](upstream/omp-discovery-opt-in.md)): once omp ships it, its
settings replace the provider-gate script and close the `catalog.stencil.so` limitation.

---

## Next

1. M2 gates: the macOS and Windows gates need runners or machines; the target model gate needs a larger model server.
2. A release candidate from one commit once those gates pass on each OS, signed.
3. The rest of Wave 2 (backlog above) after M2's exit.

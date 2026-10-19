# Project state — Ombra (Avalonia, cross-platform)

Living document; keep it short. Plan: [`ROADMAP.md`](ROADMAP.md) · features: [`PARITY.md`](PARITY.md) · design:
[`design/DESIGN_SYSTEM.md`](design/DESIGN_SYSTEM.md).

## Goal

A native desktop client on **Windows, macOS and Linux** with an owned coding-agent source tree derived from
omp 18.12.2. The beta architecture uses one app-owned Bun host for independent logical `AgentSession` objects,
not an omp CLI process per chat. Versioned multiplexed JSONL RPC connects the host to the .NET client.
Upstream changes are reviewed and verified, not adopted automatically.

## Terminology

| Term | Meaning | Code |
|---|---|---|
| Agent engine | owned coding-agent sources and shared host; private logical sessions in one VM | `runtime/engine` + pinned immutable pack |
| Client Integration Layer | shared process ownership, logical connections, JSONL/v2 framing, request correlation | `src/Ombra.Rpc` |
| Application Core / Client Core | portable state, reducer, session control, runtime options | `src/Ombra.ClientCore` |
| ViewModels + Avalonia UI | presentation | `src/Ombra.App` |
| Platform Layer | OS-specific adapters; the only place that asks which OS this is | `src/Ombra.App/Platform` (notifications, runtime platform key) |
| Harness, Model/Provider | configuration/behaviour layers on top of omp; not ours, not omp core | user profile |

## Where things are

The client lives in `avalonia/`. It builds together with the complete owned engine sources (`runtime/engine`)
and generated pack resources (`runtime/packs`); extracting only the client subtree does not include those inputs.
The harness profile template (`harness/profile`) is used by the stand-in environment.

## Current state

- **Beta baseline, 2026-10-15:** `beta` already contains published alpha `v0.0.9` (`4ebcbd1`).
  On macOS arm64: Release build, 1105 tests passed / 34 gated tests skipped / 0 failed;
  clean pinned-runtime install and 15 real-omp smoke scenarios passed with a stand-in model.
  The daily-work acceptance test initially waited on the previous chat's controller; it now checks the active
  session and passed separately against real omp. This is headless UI evidence, not packaged GUI or signing approval.
  Details and remaining stage-0 work: [beta baseline](../../docs/NEXT_RELEASE_ROADMAP.md#старт-beta-от-опубликованной-альфы--2026-10-15).
  The platform limitations below describe the earlier milestone, not a claim that this baseline exercised every gate.
- **Beta R0.2:** durable drafts/queue/revoked-input recovery without auto-send; fail-closed shell-prefix
  approval persistence, visible hidden-character markers, localized release-before-press keyboard guard.
  On macOS arm64: 51 targeted regressions and 16 real-omp/acceptance scenarios passed; native production-window
  smoke covered SIGKILL/recovery, Stop/queue restoration and deletion/journal cleanup. Independent security
  and correctness/UX reviews have no remaining R0.2 blockers. First full run: 1211 passed / 34 skipped /
  2 failed (wording assertion and review-directory setup); corrected. Integrated R0.2+R0.3 suite: 1267 passed /
  34 skipped / 0 failed. See the release roadmap for platform and signing limitations.
  Plaintext journal, debounce, uncertain delivery and non-atomic deletion limits are documented in
  [USER_GUIDE](USER_GUIDE.md); platform and incomplete safety requirements remain explicit in
  [PARITY_ROADMAP](PARITY_ROADMAP.md). This is not completion of the beta roadmap.
- **Deleted-folder recovery:** explicit current-project/folder-picker resume or a separate read-only transcript.
  `engine_read_session` reads the canonical persisted branch through the shared root host without creating
  a logical session, loading project extensions, claiming drafts or acquiring a writer lease. Existing backward
  page contracts apply; source identity changes reject old cursors and require explicit reload.
  macOS acceptance: 109 targeted .NET tests, 20 Bun tests, actual source-host paging/reload and native-window
  choice/read-only/explicit writable resume passed. No provider request; Windows/Linux native checks remain open.
  Independent read-only security review found no blocking mutation escape in runtime, controller, draft or UI paths.
- **Wake/status safety:** window-owned NSWorkspace / Windows power / Linux logind subscriptions reconcile
  all hosted live chats without prompt replay or automatic abort/restart; read-only readers are excluded.
  Stale-response guards cover session/run lifetimes; a post-wake quiet watchdog reports an unresponsive engine.
  Live run/thinking/tool/shell/subagent durations are monotonic. Extension widgets and activity/status text
  use visible control markers without changing source data.
  macOS native smoke: posted NSWorkspace sleep/wake notifications triggered one real-host `get_state` per
  foreground/background chat, restored context 87 → 0, left read-only unchanged and stopped callbacks on disposal.
  The visible duration stayed `1m 05s` across controlled UTC jumps; widget markers were visually inspected.
  Physical system sleep and Windows/Linux native delivery remain unverified.
  Independent wake review found two defects, now fixed: an idle engine with retained input stays Ready;
  dropped D-Bus connections report unavailable. The native window with a synthetic backend and the actual
  Linux adapter against a disposable local D-Bus socket peer passed; this is not Linux/logind OS acceptance.
  Targeted integration: 210 passed, 1 real-omp scenario skipped.
  Full-suite follow-up fixed the small-window vertical budget: cards scroll and composer input shrinks before
  the jump-to-latest button is clipped. 26 layout/scroll/render tests and native 900×600 / 640×600 smoke passed.
  The obsolete unbounded-stream display test was removed; native clipboard smoke copied the full updated
  210k-character original, including its omitted final tail.
  Complete client regression after all safety, layout, permission and file-send corrections:
  **1716 passed, 40 skipped, 0 failed** in 15 min 41 s. Skipped real-provider/native scenarios are not acceptance.

- **Browser permission freshness / oversized input:** permission decisions reload the backing store and fail closed;
  failed revocations deny locally until persisted with a later successful mutation. Actual revoke subprocess and
  independent security review passed. Explicit **Send text as file** retains complete private UTF-8 input beside
  the draft journal and sends a bounded reference with images; files are intentionally user-retained.
  85 targeted tests passed. Native macOS button delivery, source-byte SHA-256, canonical runtime fileMention
  parsing, invalid quote-path rejection and single-source queued recovery passed, using a synthetic transport
  backend without provider requests. Independent review found two defects; both were fixed and re-reviewed.
  The complete client regression above passed; Windows/Linux native acceptance remains open.

- **Catalog cache / RPC deadlines:** profile/root-scoped private summary sidecar, metadata invalidation and
  window-focus reconciliation are implemented. Actual macOS activation reflected external edit/add/delete
  without changing the draft. A new process reused cached summaries while journal content was exclusively locked.
  256-journal warm scans took 4.7/1.9 ms versus the uncached 18.3/17.8 ms baseline; cold fill took 45.8 ms.
  RPC ordinary requests allow 40 s and history reads 120 s; explicit request/config overrides remain authoritative.
  Startup and readonly history have separate bounds. Actual pipe smoke accepted 32 s ordinary / 45 s history
  responses and preserved short overrides, cancellation and subsequent connection use.
  Final targeted integration: 162 passed, 0 failed. Independent review's stale-instance cache republishing and
  blocking read-cancel cleanup findings were reproduced, fixed and re-reviewed. Actual directory-outage and
  held-root-writer smoke passed; the live chat remained usable. The full-suite result above predates this batch.

- **Live subagent / account usage:** opt-in visible expanded cards poll complete-line byte deltas once a second,
  retaining the latest 40 preview entries. Collapse, viewport exit, pane closure, chat replacement and disposal
  cancel reads; stale replies cannot update a replacement card. Canonical runtime reader smoke covered partial
  UTF-8/newline, empty polls and truncation/reset; native macOS covered append, stop/reopen and reset.
  Composer usage stages now come from RPC state; full account reports and saved resets remain in Settings.
  Native badge/popover and explicit Settings reads passed with a synthetic host, without provider requests.
  Review found three usage lifecycle defects: `/fresh` identity adoption, credentials changing under an old
  state read, and a post-reset report refresh lost on navigation. Each was reproduced before correction and
  passed native smoke afterward; independent re-review closed all three. The prior full suite predates this batch.
  Final targeted integration: **134 passed, 3 skipped, 0 failed**. The skipped real-omp scenarios are not acceptance;
  Windows/Linux native checks remain open.

- **M1 first vertical slice: implemented and verified on Linux** — build, tests, real omp, a small real model
  (SmolLM2), the packaged GUI with real keyboard / mouse, notifications, updater, a 20-minute background run.
- **Beyond the slice, on `main`:** side panes (Plan with history, Background tasks, Files), settings pages
  (Connectors / MCP, Plugins and skills, Computer use, Git and worktrees, SSH hosts, Pets), the session menu, context
  ring and model options, terminal-only commands handled, page comments, dictation, self-update; parity with omp's
  CLI in [`PARITY.md`](PARITY.md). Checked on Linux: the suite with real omp, the stand-in model and SmolLM2, the
  layout audit (all screens, light and dark, 1180 / 900 / 640), `tools/verify/package-e2e.sh` (20 steps in a real
  X11 window) and `tools/verify/annotate-e2e/run.sh` (page comments, WebKitGTK).
- **Not verified:** Windows and macOS at run time (the gate scripts `tools/verify/macos-gate.sh` and
  `tools/verify/windows-package-gate.ps1` are ready; no runners — workflows run by hand only), arm64 at run time, the
  real speech model (`avalonia-speech.yml`), larger models and hosted providers (`tools/verify/target-model-gate.sh`),
  code signing (no certificates).
- **Self-update:** the client updates itself from signed GitHub releases, and omp moves with it.
  - `Services/Updates.cs` trusts `update.json` only with a valid `update.json.sig`. The signature is ECDSA P-256
    over SHA-256, checked against `packaging/update-signing.pub`, which is built into the client. Schema 2 adds the
    runtime pack a version brings.
  - `Services/UpdateInstaller.cs` unpacks the verified package next to the app and runs its `--version`.
    Unix uses a shell script; Windows uses `UpdateApplyHelper` via a private copied app/runtime and strict
    `--apply-update` dispatch, without PowerShell. Both wait for exit and retain `.previous`.
  - Historical pack directories remain discoverable, but incompatible per-session CLI packs are not GUI launch fallbacks.
  - `--version` and `--update [--yes] [--restart]` work without a window.
  - Release tooling is in `tools/release` (`make-manifest.py`, `sign-manifest.sh`, `keygen.sh`). The package
    workflow signs with the `UPDATE_SIGNING_KEY` secret and makes a release only when started with `release` set
    to draft or published.
  - Windows-native helper locks/job escape and the macOS signed-bundle/Gatekeeper swap remain **NOT VERIFIED**.
    The real helper subprocess was exercised on macOS with disposable payloads, including parent-exit ordering.
  - Linux checks: `UpdateInstallTests`, `UpdateUiTests` (window flow, layout audit) and
    `tools/verify/update-e2e.sh`. The E2E installs 0.9.0, serves a signed feed for 0.9.1 over 127.0.0.1 and updates:
    tampered and foreign-key manifests are refused, the folder is swapped, 0.9.0 is kept, and the new window
    restarts.
- The UI is styled on the client's own design system ([`design/DESIGN_SYSTEM.md`](design/DESIGN_SYSTEM.md)).

## Architecture

```
Ombra-owned engine (omp 18.12.2 source, Bun 1.4.2): one host, independent AgentSession objects
        ⇅ stdin/stdout (multiplexed session keys, JSONL protocol v2 rpc_chunk), engine-labelled stderr
Client Integration Layer   OmpEngine → OmpSession → RpcConnection (bounded channel, deadlines) → OmpCommands
        ↓ ChannelReader<RpcFrame> (4096, backpressure, never drops)
Application Core           SessionController (pump, settle probe, stop) + ConversationState (pure reducer)
        ↓ capacity-1 "changed" signal + snapshot pull
ViewModels                 MainViewModel: ≤30 applies/s, paused while minimized, newest snapshot on restore
        ↓
Avalonia UI                MainWindow (sidebar, header, transcript, cards, composer, terminal panel, settings)
Platform Layer             notifications, runtime platform key; PTY via Porta.Pty; audio via PortAudio / system recorders
```

Rules: the UI never owns the process, reads stdout, decodes frames, holds pending requests or writes stdin.
Nothing critical depends on UI timers, focus or rendering. No OS checks outside the composition root.

## Decisions

| Decision | Why |
|---|---|
| Ombra-owned shared engine | maintain the complete coding-agent source in this repository, share loaded code across chats, isolate logical lifecycle/tools/settings, review upstream changes; no per-chat CLI or spare process |
| .NET 10 LTS + Avalonia 12.1.3 | current LTS / stable; builds and tests on all three OSes (CI) |
| Platform Layer as a folder of the app, not a project | few adapters (notifications, platform key); OS checks only there and in the composition root |
| UI-capable logical session and explicit `--approval-mode` (default write) | upstream's permissive default is not silently inherited; the client requires explicit confirmation before enabling it |
| Always negotiate v2 when limits match | v1 truncates large frames; official client rule |
| Deltas build text, `message_end` settles it | omp 18.2.0 serializes the partial lazily (`message_start` may contain the first delta) |
| Settle probe after non-terminal `agent_end` | `UPSTREAM OMP ISSUE`: 18.2.0 can go idle without a terminal `agent_end`; `session_settled` honoured for newer omp |
| UI delivery paused while minimized + young-gen GC bound | Avalonia queues dirty visuals until the next frame (none while minimized); the workstation GC's gen0 budget is large |
| Caret blinks only in the active window and stops after 10 s without input | a blinking caret repaints: 2–5% CPU idle under software rendering; some window systems do not report deactivation |
| Client-managed internal engine pack | pinned Bun 1.4.2/npm dependencies plus complete owned sources, manifest hashes, staged installation and real host self-check; no separate omp application required |
| Own updater, not Velopack or Sparkle / Squirrel | the client ships as a plain folder / `.app` in an archive; an in-place swap of that folder keeps its path and needs no installer format per OS. The manifest is signed with ECDSA P-256, because .NET verifies it without extra libraries and Ed25519 is not built into .NET 10. Installing waits for the user |
| Engine updates only with the client | each client pins a compatible pack; activation does not silently fall back to an incompatible older CLI |
| First-run provider setup through omp's own TUI | omp exits before its RPC is ready when no provider is set up (18.2.0: "No models available", 18.8.0: "No default model selected"), so RPC login cannot be used then |
| Local settings in the OS config folder (`ombra.local.json`, or `--config`, `OMBRA_CONFIG`) | no machine paths or secrets in source; atomic writes, `.bak` of hand edits |
| Mock model only for deterministic tests | Original OMP Core, RPC, provider path, tools stay real; reports separate mock-model results |
| omp starts with `--config <overlay>` naming every provider the user has not added in `disabledProviders` (ClientCore/Network/ProviderGate) | `UPSTREAM OMP ISSUE`: 18.8.0 fetches public model lists from every provider it knows and probes local model servers, signed in or not (draft: `docs/upstream/omp-discovery-opt-in.md`). The overlay is omp's documented per-run settings layer, so config.yml is never written. Which providers exist and which are added is asked of omp's own modules by a script on its Bun runtime (`UNDOCUMENTED / VERSION-PINNED`: omp 18.8.0 module paths and catalog rules; RealProviderGateTests). A failed check stops the start. The model catalog download (catalog.stencil.so) has no off switch in 18.8.0: documented as a known limitation |
| Strict network privacy = a loopback proxy in the app, opt-in (ClientCore/Network/PrivacyProxy, NetworkPrivacy) | omp routes every fetch through `PI_PROXY` (18.8.0), so a host allowlist there covers provider streams, token refresh, usage, discovery and the catalog without touching omp; tools inherit the proxy variables. Not an OS firewall: programs that ignore proxy variables, and loopback / private-range targets (omp keeps those direct), are not covered, and the page says so |

## Baseline facts

omp 18.12.2 npm sources (upgraded from 18.8.0 on 2026-10-19; upstream base `1d5aff90b057ce7dbfe32ea557a527c1b3144b72` from npm SLSA provenance, signatures not independently verified), Bun `1.4.2+744846f84`; harness template
1.2.0; the tests' model route is the stand-in model.

## How to run

```
dotnet build Ombra.sln                         # warnings are errors
dotnet test tests/Ombra.Tests                  # real-omp + acceptance tests run when OMBRA_TEST_CONFIG is set
dotnet run --project src/Ombra.App             # no bundled engine here: first run offers engine setup; or --config <ombra.local.json>
Ombra --self-test report.json [--install-runtime]   # packaged-app check without a window
Ombra --install-engine <root> [--engine-platform darwin-x64]   # package time: install + prune + self-check the pinned pack

# Package (Linux; the same for the other RIDs). The package carries the engine (<dist>/<package>.engine.txt);
# OMBRA_ENGINE_BUILD_ROOT=<dir> keeps the installed pack between local runs (re-checked when reused).
dotnet publish src/Ombra.App -c Release -r linux-x64 --self-contained -o <pub> -p:Version=0.1.0 -p:InformationalVersion=<version>
tools/package/package.sh linux-x64 <pub> <dist> <version>
tools/package/smoke.sh linux-x64 <dist> <work> <version>   # unpacked, clean HOME, network blocked: bundled engine used

# Mock environment: owned engine + harness profile + stand-in model (a scripted server, not a model)
tools/ci/real-omp-smoke.sh <work-dir>           # installs the pinned runtime, builds the harness home, runs the real-omp tests
#   or by hand: node tools/mock-model/server.mjs 18080 &  tools/stand-in-env/setup.sh <home> 18080 <work>
#   then OMBRA_TEST_CONFIG=<ombra.local.json with profile "harness", HOME=<home>> dotnet test …
#   Every real-omp config (OMBRA_TEST_CONFIG, OMBRA_WORKSPACE_TEST_CONFIG, OMBRA_REAL_MODEL_CONFIG) and the feature smoke
#   (tools/verify/feature-smoke/run.sh) must give omp a HOME and agent folder of their own and the app its own chats and
#   teams folders: tools/verify/HarnessIsolation.cs fails the test, or stops the smoke before anything starts, otherwise
#   (an unreadable smoke settings file once ran on the defaults and left a chat in the user's ~/.omp/agent/sessions).

# Real-model environment: SmolLM2-135M on llama.cpp, both from PyPI with checked hashes, on 127.0.0.1:18090
tools/real-model/setup.sh <work> <omp-runtime-dir> 18090    # writes <work>/realmodel.json
OMBRA_REAL_MODEL_CONFIG=<work>/realmodel.json OMBRA_REAL_MODEL_ROUTE=local-llamacpp/smollm2-135m-instruct-q4_1 dotnet test … --filter RealModelTests
REAL_MODEL_URL=http://127.0.0.1:18090/v1 REAL_MODEL_ID=smollm2-135m-instruct-q4_1 tools/verify/package-e2e.sh <package> <work>

# Updates: E2E on two local packages (a throwaway key, a feed on 127.0.0.1); a release
tools/verify/update-e2e.sh <work>                # builds 0.9.0 and 0.9.1, or pass <old.tar.gz> <new.tar.gz>
# Actions → Avalonia packages → Run workflow with version 0.6.0 and release draft|published (needs UPDATE_SIGNING_KEY)
tools/release/keygen.sh <dir>                    # a new release key pair (see the script before replacing the key)

# Before a merge (Actions run by hand only): build, the suite, and for UI changes the layout audit
# (OMBRA_REVIEW_DIR=<dir> dotnet test … --filter LayoutAudit) and package-e2e.sh; check that the model servers
# answer GET /v1/models first.
```

Packages: `.github/workflows/avalonia-package.yml` (artifacts; `tools/package/package.sh`, `smoke.sh`). User guide:
[`USER_GUIDE.md`](USER_GUIDE.md). Verification tools in `tools/verify`: `background-test.sh` (20 min minimized),
`linux-benchmark.sh`, `linux-notification-check.sh`, `scale-shots.sh` (the package at 100 / 150 / 200 %),
`secret-scan.sh` (run before committing logs or screenshots), `windows-*.ps1` (not executed: no Windows machine),
`webview-harness/run.sh <scenario|all>` (macOS: the built-in browser's WKWebView hooks in a windowless process — no
window on screen, no permission prompt; JSON per scenario, exit 0 = all checks passed; `HARNESS_ISOLATION_CHECK=1`
also checks the unified log for TCC requests; runs in the macOS job of `avalonia-platform-gates.yml`).

## Known issues / limits

- Open in the roadmap: M2 exit (macOS and Windows gates, a larger model / provider, signing certificates, CI
  runners) and the backlog in [`ROADMAP.md`](ROADMAP.md); from the UX reviews: grouping tool calls, a one-place
  session diff, non-image files as chips, a text question's own send, pin placement, default mode/model for new
  sessions, terminal colours in the light theme; the "Missing" rows of [`PARITY.md`](PARITY.md).
- Windows and macOS interactive desktops, DPI/scaling, microphone and notification delivery: not verified (no
  machines or devices; CI is headless).
- Real model: verified with SmolLM2-135M-Instruct only; larger models, hosted providers and real OAuth sign-in are
  not verified. Every other model output in tests is the stand-in.
- Self-update on Windows (app-owned helper, file locks/Job Object/GPO/EDR) and macOS (the signed `.app` swap,
  Gatekeeper on the new bundle): not verified natively. A copy the user cannot change (Program Files) falls back
  to downloading the package. The release key's
  private half lives only in the `UPDATE_SIGNING_KEY` secret; replacing it takes one last release
  signed with the old key (`tools/release/keygen.sh`).
- Shared-engine macOS validation: full Release suite **1411 passed, 34 skipped, 0 failed**; actual-engine scenarios
  **15 passed, 2 skipped**, owned Bun contracts **33 passed**. Native two-project runs share one PID, preserve peer
  work on Stop/close, and recover unsent input after a shared crash without resending. Native paging loads
  100/360 messages initially, prepends with **0 px** anchor drift, and retains the original title.
  See NEXT_RELEASE_ROADMAP's R0.3 history/queue + R0.8 evidence. This is not Windows/Linux or hosted-provider proof.
  Transient provider-privacy preflight still imports the SDK in a short-lived helper per logical launch;
  no claim that all repeated initialization or measured end-to-end latency has been eliminated.
- Pinned omp 18.8.0 now receives `set_event_filter {events:null,messageUpdates:"delta"}` after negotiation,
  before history/Ready. All event types and full `message_end` remain enabled. Actual runtime smoke:
  identical assistant text, 26 updates, 29,932 bytes full vs 7,222 delta; not an end-to-end latency benchmark.
  Targeted client/RPC/real-omp verification: 68 passed, 0 skipped, 0 failed (local stand-in model).
- The installed omp runtime is ~1.2 GB on disk (omp's optional ML dependencies).

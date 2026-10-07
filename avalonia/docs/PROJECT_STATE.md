# Project state — OMP GUI (Avalonia, cross-platform)

Living document; keep it short. Plan: [`ROADMAP.md`](ROADMAP.md) · features: [`PARITY.md`](PARITY.md) · design:
[`design/DESIGN_SYSTEM.md`](design/DESIGN_SYSTEM.md).

## Goal

A native desktop client for omp on **Windows, macOS and Linux**. It talks
to the **Original OMP Core** (upstream `can1357/oh-my-pi`, unmodified, never forked or patched) only through
its official RPC over stdio, and uses the harness profile and model/provider configuration in place.

## Terminology

| Term | Meaning | Code |
|---|---|---|
| Original OMP Core | upstream omp; a separate runtime/process | — (outside this project) |
| Client Integration Layer | process, stdio, JSONL/v2 framing, request correlation | `src/OmpGui.Rpc` |
| Application Core / Client Core | portable state, reducer, session control, runtime options | `src/OmpGui.ClientCore` |
| ViewModels + Avalonia UI | presentation | `src/OmpGui.App` |
| Platform Layer | OS-specific adapters; the only place that asks which OS this is | `src/OmpGui.App/Platform` (notifications, runtime platform key) |
| Harness, Model/Provider | configuration/behaviour layers on top of omp; not ours, not omp core | user profile |

## Where things are

The client lives in `avalonia/` (laid out as a standalone project root; extract with
`git subtree split --prefix avalonia`). Next to it: the pinned runtime pack (`runtime/packs`, built into the client)
and the harness profile template (`harness/profile`, used by the stand-in test environment).

## Current state

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
  - `Services/UpdateInstaller.cs` unpacks the verified package next to the app and runs its `--version`. A script
    (sh, or PowerShell on Windows) swaps the folder or `.app` in once the app has quit and keeps `.previous`.
  - `RuntimeInstaller.FindPrevious` keeps omp running from the earlier pack while the new one installs.
  - `--version` and `--update [--yes] [--restart]` work without a window.
  - Release tooling is in `tools/release` (`make-manifest.py`, `sign-manifest.sh`, `keygen.sh`). The package
    workflow signs with the `UPDATE_SIGNING_KEY` secret and makes a release only when started with `release` set
    to draft or published.
  - The Windows script and the macOS bundle swap are **NOT VERIFIED** (no runners).
  - Linux checks: `UpdateInstallTests`, `UpdateUiTests` (window flow, layout audit) and
    `tools/verify/update-e2e.sh`. The E2E installs 0.9.0, serves a signed feed for 0.9.1 over 127.0.0.1 and updates:
    tampered and foreign-key manifests are refused, the folder is swapped, 0.9.0 is kept, and the new window
    restarts.
- The UI is styled on the client's own design system ([`design/DESIGN_SYSTEM.md`](design/DESIGN_SYSTEM.md)).

## Architecture

```
Original OMP Core (omp 18.8.0, Bun 1.4.2)  --mode rpc-ui --approval-mode write, OMP_PROFILE=harness
        ⇅ stdin/stdout (JSONL, protocol v2 rpc_chunk), stderr drained separately
Client Integration Layer   OmpProcess → RpcConnection (bounded event channel, deadlines) → OmpCommands
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
| .NET 10 LTS + Avalonia 12.1.3 | current LTS / stable; builds and tests on all three OSes (CI) |
| Platform Layer as a folder of the app, not a project | few adapters (notifications, platform key); OS checks only there and in the composition root |
| `rpc-ui` + `--approval-mode` always passed (default write) | omp's own default is yolo; the client never enables it without an explicit second confirmation |
| Always negotiate v2 when limits match | v1 truncates large frames; official client rule |
| Deltas build text, `message_end` settles it | omp 18.2.0 serializes the partial lazily (`message_start` may contain the first delta) |
| Settle probe after non-terminal `agent_end` | `UPSTREAM OMP ISSUE`: 18.2.0 can go idle without a terminal `agent_end`; `session_settled` honoured for newer omp |
| UI delivery paused while minimized + young-gen GC bound | Avalonia queues dirty visuals until the next frame (none while minimized); the workstation GC's gen0 budget is large |
| Caret blinks only in the active window and stops after 10 s without input | a blinking caret repaints: 2–5% CPU idle under software rendering; some window systems do not report deactivation |
| The client installs the pinned runtime itself (Bun 1.4.2 + omp 18.8.0) | a first run needs no global tools; sha512-pinned Bun, frozen lockfile, no lifecycle scripts, staged swap |
| Own updater, not Velopack or Sparkle / Squirrel | the client ships as a plain folder / `.app` in an archive; an in-place swap of that folder keeps its path and needs no installer format per OS. The manifest is signed with ECDSA P-256, because .NET verifies it without extra libraries and Ed25519 is not built into .NET 10. Installing waits for the user |
| omp updates only with the client | each client version pins one runtime pack (tested together); a new pack installs after the client update while the old omp keeps working |
| First-run provider setup through omp's own TUI | omp exits before its RPC is ready when no provider is set up (18.2.0: "No models available", 18.8.0: "No default model selected"), so RPC login cannot be used then |
| Local settings in the OS config folder (`omp-gui.local.json`, or `--config`, `OMPGUI_CONFIG`) | no machine paths or secrets in source; atomic writes, `.bak` of hand edits |
| Mock model only for deterministic tests | Original OMP Core, RPC, provider path, tools stay real; reports separate mock-model results |

## Baseline facts

omp 18.8.0 npm sources (pinned since 2026-10-07; 18.2.0 before it, RPC byte-identical to tag v18.2.0 `6f2c14b3`), Bun `1.4.2+744846f84`; harness template
1.2.0; the tests' model route is the stand-in model.

## How to run

```
dotnet build OmpGui.sln                         # warnings are errors
dotnet test tests/OmpGui.Tests                  # real-omp + acceptance tests run when OMPGUI_TEST_CONFIG is set
dotnet run --project src/OmpGui.App             # first run offers to install omp; or --config <omp-gui.local.json>
OmpGui --self-test report.json [--install-runtime]   # packaged-app check without a window

# Package (Linux; the same for the other RIDs)
dotnet publish src/OmpGui.App -c Release -r linux-x64 --self-contained -o <pub> -p:Version=0.1.0 -p:InformationalVersion=<version>
tools/package/package.sh linux-x64 <pub> <dist> <version>

# Mock environment: Original OMP + the harness profile + the stand-in model (a scripted server, not a model)
tools/ci/real-omp-smoke.sh <work-dir>           # installs the pinned runtime, builds the harness home, runs the real-omp tests
#   or by hand: node tools/mock-model/server.mjs 18080 &  tools/stand-in-env/setup.sh <home> 18080 <work>
#   then OMPGUI_TEST_CONFIG=<omp-gui.local.json with profile "harness", HOME=<home>> dotnet test …

# Real-model environment: SmolLM2-135M on llama.cpp, both from PyPI with checked hashes, on 127.0.0.1:18090
tools/real-model/setup.sh <work> <omp-runtime-dir> 18090    # writes <work>/realmodel.json
OMPGUI_REAL_MODEL_CONFIG=<work>/realmodel.json OMPGUI_REAL_MODEL_ROUTE=local-llamacpp/smollm2-135m-instruct-q4_1 dotnet test … --filter RealModelTests
REAL_MODEL_URL=http://127.0.0.1:18090/v1 REAL_MODEL_ID=smollm2-135m-instruct-q4_1 tools/verify/package-e2e.sh <package> <work>

# Updates: E2E on two local packages (a throwaway key, a feed on 127.0.0.1); a release
tools/verify/update-e2e.sh <work>                # builds 0.9.0 and 0.9.1, or pass <old.tar.gz> <new.tar.gz>
# Actions → Avalonia packages → Run workflow with version 0.6.0 and release draft|published (needs UPDATE_SIGNING_KEY)
tools/release/keygen.sh <dir>                    # a new release key pair (see the script before replacing the key)

# Before a merge (Actions run by hand only): build, the suite, and for UI changes the layout audit
# (OMPGUI_REVIEW_DIR=<dir> dotnet test … --filter LayoutAudit) and package-e2e.sh; check that the model servers
# answer GET /v1/models first.
```

Packages: `.github/workflows/avalonia-package.yml` (artifacts; `tools/package/package.sh`, `smoke.sh`). User guide:
[`USER_GUIDE.md`](USER_GUIDE.md). Verification tools in `tools/verify`: `background-test.sh` (20 min minimized),
`linux-benchmark.sh`, `linux-notification-check.sh`, `scale-shots.sh` (the package at 100 / 150 / 200 %),
`secret-scan.sh` (run before committing logs or screenshots), `windows-*.ps1` (not executed: no Windows machine).

## Known issues / limits

- Open in the roadmap: M2 exit (macOS and Windows gates, a larger model / provider, signing certificates, CI
  runners) and the backlog in [`ROADMAP.md`](ROADMAP.md); from the UX reviews: grouping tool calls, a one-place
  session diff, non-image files as chips, a text question's own send, pin placement, default mode/model for new
  sessions, terminal colours in the light theme; the "Missing" rows of [`PARITY.md`](PARITY.md).
- Windows and macOS interactive desktops, DPI/scaling, microphone and notification delivery: not verified (no
  machines or devices; CI is headless).
- Real model: verified with SmolLM2-135M-Instruct only; larger models, hosted providers and real OAuth sign-in are
  not verified. Every other model output in tests is the stand-in.
- Self-update on Windows (PowerShell apply script) and macOS (the `.app` swap, Gatekeeper on the new bundle): not
  verified. A copy the user cannot change (Program Files) falls back to downloading the package. The release key's
  private half lives only in the `UPDATE_SIGNING_KEY` secret; replacing it takes one last release
  signed with the old key (`tools/release/keygen.sh`).
- One omp process (one live session) at a time; switching sessions during a run is disabled.
- omp 18.2.0 streaming cost is quadratic in reply length (full partial in every update); upstream HEAD offers event
  filtering (backlog: controlled upgrade).
- The installed omp runtime is ~1.2 GB on disk (omp's optional ML dependencies).

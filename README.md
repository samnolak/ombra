# OMP GUI

A cross-platform desktop client for [oh-my-pi](https://github.com/can1357/oh-my-pi) (`omp`), the coding agent. The
agent itself is always the unmodified omp: the client starts it, talks to it over omp's official RPC (JSON lines over
stdio) and shows what it does. Nothing here forks, patches or re-implements omp.

The client is built with .NET 10 and Avalonia and lives in [`avalonia/`](avalonia). It is an independent project,
not an official product of the omp, Bun, Anthropic or OpenAI projects.

![OMP GUI: a conversation with the Plan pane open](avalonia/docs/design/screens/readme-window.png)

## What it does

- **Conversation** — omp's replies as they stream, thinking folded ("Thought for 4s"), every tool call as a line with
  its time, file changes as inline diffs, a summary at the end of each run ("Worked for 12s · 3 tools · 2 files
  changed"), approval and question cards, follow-ups and steering while omp works, image attachments.
- **Composer** — in the box: "+" (images, omp's slash commands and skills, connectors, plugins, page comments),
  thinking level, the model with fast mode, extended context, advisor, auto-compact and auto-retry, dictation on this
  computer, send (Queue and Steer while omp works). Under it: the project (before the first message), the permission
  mode (Ask permissions / Accept edits / Bypass permissions) and a context gauge (`/context` breakdown and `/usage`
  on click).
- **Sessions** — every project's sessions, grouped by project, with search; rename, compact, hand off, fresh
  provider session, retry, rewind, export, share, workspace folders, move, pin, memory and delete from the session
  title's menu.
- **Panes** (⋮ Views menu) — Plan (omp's todo list with checkboxes and its history), Background tasks (subagents and
  jobs), Files (the project tree with git marks, a code viewer), a terminal (a shell, or omp's own terminal UI for
  what omp offers only there) and a browser preview with comments on page elements.
- **Settings** — General (appearance, notifications), Model providers, Connectors (MCP servers, Smithery search),
  Plugins and skills, Computer use, Git and worktrees, SSH hosts, Pets, Updates, Advanced (which omp to run) and
  Diagnostics. What belongs to omp is changed through omp's own commands and settings.
- **First start without omp** — the client installs the pinned omp 18.2.0 on Bun 1.4.2 into its own folder, checked
  against the hashes built into it. Model providers and keys stay in omp's configuration. Newer omp releases (18.3
  and later) are not verified with this client yet; your own omp can be set in Settings → Advanced.

The [user guide](avalonia/docs/USER_GUIDE.md) describes all of it; [PARITY.md](avalonia/docs/PARITY.md) lists every
omp flag, command and RPC feature with what the client offers for it.

## Platforms and status

| OS | Architectures |
|---|---|
| Windows 11 24H2+ (Windows 10 only as Enterprise LTSC / IoT) | x64, arm64 |
| macOS 14 Sonoma+ | Apple Silicon (arm64), Intel (x64) |
| Linux, glibc 2.27+ (X11 or XWayland) | x64, arm64 |

The versions are the ones [.NET 10 supports](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
(ordinary Windows 10 left support in October 2025; macOS 12–13 are not supported). Packages are self-contained (no
.NET installation needed) and unsigned: macOS 15+ asks you to allow the first start in *Privacy & Security* (the
[user guide](avalonia/docs/USER_GUIDE.md#install) has the steps). **No release of this client is published yet.** Once one
is, the app finds newer releases by itself and installs one when you choose to, together with the omp it brings;
releases are signed with the project's release key.

What is verified, on Linux x64 only (details: [PROJECT_STATE.md](avalonia/docs/PROJECT_STATE.md)):
the test suite with the real omp 18.2.0, and the packaged app in a real window with a real (small, generic) model —
install, reply and streaming, stop, approvals, terminal, restart. Not verified yet: Windows and macOS at run time
(no runners), arm64 at run time, the real speech model, larger models and hosted providers, code signing.

## Build, run, test

Requires the .NET 10 SDK ([`avalonia/global.json`](avalonia/global.json)). From `avalonia/`:

```
dotnet build OmpGui.sln
dotnet run --project src/OmpGui.App             # first run offers to install omp; or --config <omp-gui.local.json>
dotnet test tests/OmpGui.Tests                  # real-omp and acceptance tests run only when OMPGUI_TEST_CONFIG is set
dotnet build OmpGui.sln -c Release && tools/ci/real-omp-smoke.sh <work-dir>
                                                # installs the pinned runtime and runs those tests against a stand-in
                                                # model server (needs Node.js, curl and network access to npm)
tools/real-model/setup.sh <work> <omp-runtime-dir>   # a real small model (SmolLM2 on llama.cpp) for RealModelTests
                                                     # and tools/verify/package-e2e.sh (the packaged app, X11)
```

A self-contained package for one platform, as CI builds it:

```
dotnet publish src/OmpGui.App -c Release -r linux-x64 --self-contained -o <publish-dir>
tools/package/package.sh linux-x64 <publish-dir> <out-dir> <version>
```

CI ([`.github/workflows`](.github/workflows)): `avalonia-ci.yml` builds and tests on Linux, Windows and macOS and runs
the real-omp smoke; `avalonia-package.yml` builds the packages for all six platforms and smoke-tests the four its
runners can start (the arm64 Linux and Windows packages are cross-built only);
`avalonia-platform-gates.yml` runs the macOS and Windows gates on demand; `avalonia-speech.yml` checks dictation
with the real speech model.

The workflows run only by hand (*Actions → Run workflow*). Before a merge, run the checks locally and post the
result in the pull request: `dotnet build`
(warnings are errors), `dotnet test tests/OmpGui.Tests` (with `OMPGUI_TEST_CONFIG` for the real-omp tests), and for
UI changes the layout audit screenshots (`OMPGUI_REVIEW_DIR`) and `tools/verify/package-e2e.sh` on a package.

## Repository

| Path | What it is |
|---|---|
| [`avalonia/`](avalonia) | The client: `src/OmpGui.Rpc` (process, stdio, framing), `src/OmpGui.ClientCore` (state, session control), `src/OmpGui.App` (view models, UI, platform adapters), `tests/`, `tools/` (packaging, verification, stand-in model), `packaging/`, `docs/` |
| [`runtime/packs/`](runtime/packs) | The pinned omp runtime pack (omp 18.2.0 on Bun 1.4.2) that the client builds in and installs |
| [`harness/`](harness) | The `harness` omp profile template, used by the client's real-omp tests ([docs/harness.md](docs/harness.md)) |

Documentation for contributors: [PROJECT_STATE.md](avalonia/docs/PROJECT_STATE.md) (state, architecture, decisions,
how to run every environment), [ROADMAP.md](avalonia/docs/ROADMAP.md), [PARITY.md](avalonia/docs/PARITY.md) and
[DESIGN_SYSTEM.md](avalonia/docs/design/DESIGN_SYSTEM.md).

## License

MIT — see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

# Release gates: Ombra + Nightly

RC QA, 2026-10-18 (agent RcQa). Machine: macOS 26.6.2, Apple silicon (arm64), .NET SDK 10.0.401, Rust 1.99.0,
Bun 1.4.2 (pinned runtime pack `ombra-engine-18.8.0-bun-1.4.2-v1`), Docker via colima (Postgres 17).

Owner decisions (2026-10-18): **the release ships on macOS only**; **no Apple Developer ID** — Ombra and Nightly are
signed ad hoc and pinned by cdhash (agent ReleaseTrust), so macOS shows a Gatekeeper warning on first open (documented
in [USER_GUIDE › Install](../avalonia/docs/USER_GUIDE.md#install)); notarization is not available without Developer ID.
Release Nightly: full release build + `mach package`, re-pinned in `runtime/nightly/nightly-runtime.json` (agent
ReleaseNightly, section at the end of this file).

Counts are passed / failed / skipped. Every command runs from the repository root unless stated; environment:
`DOTNET_ROOT=$HOME/.dotnet DOTNET_HOST_PATH=$HOME/.dotnet/dotnet PATH=$HOME/.cargo/bin:/opt/homebrew/bin:$PATH`.

## 1. Passing on macOS (this RC run)

| Suite | Command | Result |
|---|---|---|
| Ombra.Tests, English (post-freeze, after the TeamLayout restyle) | `cd avalonia && OMBRA_TEST_LANG=en dotnet test tests/Ombra.Tests --artifacts-path ../.temp/RcQa2/artifacts` | **2238 / 0 / 44**, 26 m 4 s (first post-freeze run 2221 / 17: stale engine artifact, §1.2) |
| Ombra.Tests, Russian | same with `OMBRA_TEST_LANG=ru` | **2238 / 0 / 44**, 26 m 22 s (first post-freeze run 2220 / 18: stale engine artifact + `ConnectorsParsingTests`, §1.2) |
| Ombra.Teams.Tests (real server in Docker) | `dotnet test avalonia/tests/Ombra.Teams.Tests` with `OMBRA_TEST_AGE=<age binary>`, `DOCKER_HOST` unset (the fixture points Testcontainers at colima itself) | **32 / 0 / 2** |
| Ombra.Server.Tests (Postgres 17 in Docker) | `dotnet test server/tests/Ombra.Server.Tests` (same environment) | **58 / 0 / 0** |
| Ombra.Core.Interop.Tests | `dotnet test server/tests/Ombra.Core.Interop.Tests` | **219 / 0 / 0** |
| ombra-core (Rust) | `cd server/native/ombra-core && cargo test --release --features test-attacks,failpoints` | **18 / 0 / 0** (11 + 6 + 1 across test targets) |
| Engine Bun tests (copy of `runtime/engine`, pinned `node_modules` linked) | `bun --config=../bunfig.toml test --timeout 60000 ./*.test.ts` and `./coding-agent/**/*.test.ts` | **64 / 0** (10 files) + **79 / 0** (10 files) |
| Engine artifact | `python3 runtime/generate-engine-artifact.py --check` | **OK** |
| Runtime contract tests | `OMBRA_ENGINE_ROOT=<engine copy> OMBRA_HISTORY_RPC_DIR=<engine copy>/coding-agent/src/modes/rpc bun test runtime/tests/{mcp-oauth,structured-approval,session-ownership,read-session,rpc-messages-backward}.test.ts` | **28 / 0** (5 files) |
| Real omp smoke (installer + real engine, stand-in model) | `OMBRA_ARTIFACTS_PATH=… avalonia/tools/ci/real-omp-smoke.sh <work>` | runtime install **1 / 0**; RealOmp + Acceptance **16 / 0**; perf-lite headless: warm 10k history open **1323 ms** (budget 1500), stream 30 s 113.7 fps, p95 17.99 ms, footprint drift +0.24 % |
| **First run works offline** (engine bundled; agent EngineBundle, 2026-10-19, local `0.0.99-engine` build) | `dotnet publish … -r osx-arm64 -o <pub>`; `avalonia/tools/package/package.sh osx-arm64 <pub> <dist> <version>`; `avalonia/tools/package/smoke.sh osx-arm64 <dist> <work> <version>` (unpacks with `ditto -x -k`, isolated HOME/`OMBRA_RUNTIME_DIR`, `--self-test --install-runtime` under proxy variables to a closed port **and** `sandbox-exec` denying all outbound connections but loopback) | **PASS**: `runtime` = "bundled with the app at Ombra.app/Contents/Resources/engine/ombra-engine-18.8.0-bun-1.4.2-v1", no `runtime-install`, nothing in the runtimes folder but `.transpiler-cache`; omp ready over RPC, stopped, no process left; GUI start/close Ready; `codesign --verify --deep --strict` passes before and after the engine ran (nothing written into the bundle; bun keeps Oven's Developer ID signature, the 9 npm .node/.dylib are signed with the app's identity, hardened runtime). Package 264.0 MB (without the engine 108.6 MB; with the unpruned engine ≈ 420 MB); app unpacked 778 MB (engine 515 MiB, unpruned 1017 MiB). Pruned pack, read-only (`chmod -R a-w`): host `--self-check`, a real session with the stand-in model running the `read` tool, the local ONNX title model (`falcon-h1-90m` through transformers.js → onnxruntime-node) generating a title; file list and mtimes unchanged afterwards. Required for every macOS release package. |
| Nightly mochitests (dev build, headless) | `./mach test zen/teams/tests zen/gallery/tests zen/tests/tabs zen/tests/ui zen/tests/library --headless --setpref security.sandbox.content.mac.testing_read_path1=<desktop>/src/` | gallery **165 / 0**, teams **33 / 0**, library **199 / 0**, ui **66 / 0**, tabs without `browser_drag_drop_vertical.js` **162 / 0** (auto groups, load queue, unloader); see §4 |
| Nightly mochitests (packaged release app, BuildID 20261019095634) | `./mach mochitest --appname <dist>/ombra-release/Nightly.app/Contents/MacOS/nightly --headless --setpref security.sandbox.content.mac.testing_read_path1=<desktop>/ zen/tests/ui zen/tests/library zen/teams/tests zen/gallery/tests <tabs files without drag_drop_vertical>` | gallery **165 / 0**, teams **39 / 0** (full run under load 38 / 1: `browser_team_share.js` "the Open button shows its label (0px)"; the file alone 39 / 0 twice, the manifest again 39 / 0), library **199 / 0**, tabs **162 / 0**, ui **66 / 0**; upstream exception `browser_drag_drop_vertical.js` (§4) |
| Feature smoke, all sections incl. `team` (real server), `team-share`, `shared-marks`, `progress`, `git-bell` | `avalonia/tools/verify/feature-smoke/run.sh <work> <installed runtime>` | see §1.1 |
| NightlyAcceptance (Release Ombra, pinned Nightly package) | see §1.1 | see §1.1 |
| Performance gate, native, OpenGL | `dotnet run --project avalonia/tools/verify/performance/Ombra.Performance.csproj -c Release -- --config … --model-url … --work … --seconds 60 --render opengl` | see §1.1 |

### 1.1 Window runs

Post-freeze runs, 2026-10-19, on the pinned package 1.23.2b-20261019095634 (zip sha256
`e7d3377ab5b02440aa7bce96c09335142f7bb6d4aaf37a12b0d7a870b1af14fe`, cdhash `7949beed0cb694710b8cea90dc764dc2f8768926`).
Evidence (trx, logs, screenshots): `.temp/RcQa/final-evidence/`.

| Run | Result |
|---|---|
| Feature smoke, all sections, en (`OMBRA_SMOKE_LANGUAGE=en run.sh <work> <runtime>`, incl. `title-bar`, `plugins-hub`, `team`) | **321 / 1** — `cmd-k-finds-command` typed Russian into the English window (smoke bug, fixed: §1.2); `startup,palette` rerun en **7 / 0**, ru **7 / 0** |
| Feature smoke, all sections, ru | **322 / 0**, RESULT PASS |
| Team sections after the TeamLayout restyle (`startup,team,team-share,shared-marks,progress`) | en **65 / 0**; ru first run 40 / 4 (`shared-sent`, `share-sent-to-bob`: hub items not there within 5 s, then the sections' follow-on exceptions), rerun **65 / 0** |
| `nightly-share` (`run.sh <work> <runtime> nightly-share` + `nightly-share-driver.py`, pinned package, `.temp` profile, `-no-remote`) | en **16 / 0**, ru **16 / 0**; screenshots of the share dialogs, toasts and opened link/collection checked |
| NightlyAcceptance (Release Ombra of the frozen tree before the TeamLayout restyle — Team views only, not on this path; pinned package) | en **96 / 0**; ru first run 74 / 22 (every native-input check: `device-*-tap`, `ime-*`, `cursor-*`, `context-menu*`, `scroll`, `select-choice`, `window-open-tab` — no input reached the page, as when another app holds the keyboard/mouse focus), rerun **96 / 0**; no Nightly process left |
| Performance, native, `--seconds 60 --render opengl` | warm 10k history open **1489 ms** (budget 1500; cold 3180 ms), stream 60 s **159.9 fps**, p95 **7.57 ms**, CPU 30 % of a core (unprobed 13 %, idle 5.5 %), footprint drift +2.6 %; PASS |

### 1.2 Fixed during this run

- `GitBellTests` (3 tests) asserted English text and failed under `OMBRA_TEST_LANG=ru`. The assertions now build the
  expected text through `Loc.Plural` / `Loc.F` like the view model; failing first in ru, passing in en and ru
  (`GitBellTests|FilesReviewTests`: 27 / 0 each language), full ru run green.
- `FilesReviewTests.Long_changes_count_every_hunk_and_huge_ones_are_not_read_whole` timed out once in the first full
  ru run ("never reached: too large") and passed in the targeted rerun and the full rerun: a timing flake under load,
  not changed.
- Engine artifact stale after the `coding-agent/CHANGELOG.md` update: 18 runtime tests failed in en and ru
  ("Runtime source size mismatch: coding-agent/CHANGELOG.md"; `RuntimeInstallerTests`, `RuntimeSourceArtifactTests`,
  `FirstRunUiTests`). Regenerated with `python3 runtime/generate-engine-artifact.py` (`--check` OK).
- `ConnectorsParsingTests.Catalog_is_grouped_by_the_marketplaces_categories` expected the English "Popular"/"Other"
  under `OMBRA_TEST_LANG=ru`; now `Loc.T(...)`.
- Feature smoke `palette` typed "чат" whatever the window's language; it now types the last word of `Loc.T("New chat")`.
- After the TeamLayout restyle, `SharedMarksTests.The_comments_card_shows_received_and_sent_marks` (status moved into
  the recipient line) and a `Loc` race from the new en+ru `TeamUiTests` theory (it flipped the language under
  `SessionAreaTests`) failed; fixed by TeamLayout, full en/ru green afterwards.

## 2. Needs the owner

- **Signing.** Ad hoc signature + cdhash pinning for Ombra and Nightly (ReleaseTrust); no Developer ID, no
  notarization: Gatekeeper warns on first open (USER_GUIDE › Install). A Developer ID would also allow
  `zen.embed.peer-requirement` with a team requirement instead of the cdhash pin and remove the library-validation
  exception (PARITY_ROADMAP §0 #5).
- **Release Nightly build** — done by ReleaseNightly (section at the end).
- **CI policy.** `.github/workflows/avalonia-ci.yml` / `avalonia-package.yml` are manual-only (cost policy); running
  them for the release tag is the owner's call.
- **Real model provider.** All runs above use the stand-in model (`tools/mock-model`); a short session against a real
  provider before publishing is a manual check.
- **Geolocation in the unsigned Nightly** never answers the page (macOS Location Services) — open, needs a signed
  Nightly to verify.
- **Touch ID dialog of the encrypted gallery (B3)** — manual check on the owner's Mac.

## 3. Needs another OS or real hardware (not in this release: macOS only)

- Windows and Linux native runs of Ombra (UI, runtime, package gates `tools/verify/windows-package-gate.ps1`,
  `linux-benchmark.sh`), Nightly B1/B3/B4 on Windows/Linux. Cross-builds in `avalonia-package.yml` are not runtime
  PASS.
- Team key stores: DPAPI (Windows) and libsecret (Linux) are implemented and unit-tested only on macOS — acceptance
  on the real OS is open.
- Job Object / process-tree ownership on Windows (`OwnedProcessesWindowsTests` skipped here).
- IME on real input methods (Japanese, Chinese, Korean, dead keys) in Ombra's composer, the editor and the Nightly
  pane: covered with synthetic NSEvents/marked text on macOS, not with physical input sources.
- Physical sleep/wake: OS notifications are wired and verified with synthetic NSWorkspace events; a lid-close cycle
  on hardware is open.
- Native 30-minute memory soak (`--seconds 1800`) on Windows/Linux; the macOS 30-minute run was done for O12
  (NEXT_RELEASE_ROADMAP).
- Bundled engine on Windows/Linux (`package.sh` puts it in `engine/` beside the executable; CI's linux-x64/win-x64
  smoke checks it offline). Windows: the longest file path in the pack is ~175 characters after pruning, so the
  package folder adds about 60 (`ombra-<version>-win-x64\engine\ombra-engine-…\`) — Explorer's zip extraction into a
  deep folder can hit the 260-character limit; untested. linux-arm64/win-arm64 ship without it (cross-built).
- osx-x64 bundled engine on a real Intel Mac (CI installs and self-checks it under Rosetta 2; the smoke there uses
  the mismatch fallback, the arm64 engine).

## 4. Known non-blocking failures

- `zen/tests/tabs/browser_drag_drop_vertical.js` (upstream Zen test, dev build): `IndexSizeError` in
  `ZenDragAndDrop.js:919` (`dataTransfer.mozGetDataAt(…, 0)` on the synthesized drop) and then a timeout, which also
  stops the rest of the tabs manifest. `src/zen/drag-and-drop` is not changed by this release; the remaining tabs
  tests pass when run without it (162 / 0). Not fixed.
- Nightly mochitests need `security.sandbox.content.mac.testing_read_path1`: `<desktop>/src/` for the dev build
  (its actor files are symlinks into `src/`, outside the directories the content sandbox opens to unpackaged builds;
  desktop `docs/contribute.md`), the desktop root for the packaged app (Nightly release build, step 4). Without it
  `browser_gallery_capture.js` times out because the content actor cannot load. This is a test-harness setting; the
  shipped app loads its actors from its own bundle.
- `zen/tests/ui/browser_tab_loading_indicator.js` › `test_only_the_selected_tab_shows_it` (packaged release app):
  intermittent — "Selecting the loading tab shows its indicator" timed out in 2 of 13 runs, both while the machine
  was loaded by other agents' builds/tests; 10 runs on a quieter machine passed. The test and `ZenProgressBar.sys.mjs`
  were not changed in this release; B4's new load queue/unloader touch background-tab loading, so the cause is not
  proven. Not fixed.

## Nightly release build

macOS arm64 only, unsigned by Apple (no Developer ID, no notarization); `pack-nightly.py` signs the runtime copy ad
hoc and pins its cdhash.

**Configuration.** The desktop repo's `engine/mozconfig` (from `configs/common/mozconfig` + `configs/macos/mozconfig`)
is used as is, without `ZEN_RELEASE`: objdir `engine/obj-aarch64-apple-darwin`, `--disable-debug`, `MOZ_OPTIMIZE=1`
(`-O3`), release branding and update channel, tests enabled. The `ZEN_RELEASE` path (LTO, PGO from
`~/artifact/merged.profdata`, `--enable-bootstrap`, clang plugin) is not used: there is no PGO profile on this machine
and it sets `--disable-tests`, which would leave the package without the mochitest gate below.

**Steps** (desktop repo, `source ~/Developer/arclike/env.sh`):

1. Sources in the engine: every `src/` file is linked into `engine/` and every changed `src/**/*.patch` must already
   be applied (`cd engine && git apply -R --check ../<patch>`); then `npm run ffprefs` regenerates
   `zen-static-prefs.inc` / `zen.js` from `prefs/`. (`npm run import` on an already patched engine fails on the
   upstream PiP patches; it is not needed when the checks above hold.)
2. `cd engine && nice -n 10 ./mach build` — 48 min for the full build over the existing objdir (18 cores), 4.5 min
   incremental.
3. `MOZ_PKG_DIR=ombra-release ./mach package` (14 s). `MOZ_PKG_DIR` keeps the staging app out of
   `dist/nightly/Nightly.app` (the owner's daily app, never overwritten); output:
   `dist/ombra-release/Nightly.app` and `dist/nightly-<version>.en-US.mac.dmg`, copied to
   `omp/.temp/ReleaseNightly/`.
4. Gate, against the packaged binary:
   `./mach mochitest --appname $PWD/obj-aarch64-apple-darwin/dist/ombra-release/Nightly.app/Contents/MacOS/nightly --headless --setpref security.sandbox.content.mac.testing_read_path1=$HOME/Developer/arclike/desktop/ zen/teams/tests zen/gallery/tests zen/tests/tabs`.
   A packaged app also needs the read path (desktop root, not just `src/`): its content processes otherwise cannot
   load the harness's `resource://testing-common` actors and every test times out.
5. In omp: `python3 runtime/nightly/pack-nightly.py --app <dmg> --stage`, then
   `python3 runtime/nightly/pack-nightly.py --check`; commit `runtime/nightly/nightly-runtime.json`.
6. Publish the asset the pin's `url` names (`nightly-<version>-<build>` / `nightly-runtime-<version>-<build>.zip`):
   `gh release create nightly-<version>-<build> -R samnolak/nightly-browser --target next --prerelease <omp>/.temp/nightly-runtime/<archive>`;
   check `curl -sIL <url>` ends `200` with the pin's `size`. Then `rm -rf engine/obj-aarch64-apple-darwin/dist/ombra-release`
   (`dist/Nightly.app` stays).
7. Embed trust with the ad-hoc (self-signed) signatures: a Release Ombra launcher (`package.sh`, signed by
   `sign.sh`, no `OMBRA_NIGHTLY_ALLOW_UNSIGNED`) installs the pinned package from the `url` into a fresh `.temp`
   profile; the Nightly log must show `embedder pid … authenticated (cdhash H"…")` with the launcher's cdhash from
   `<package>.signing.txt`, the browser pane must render, and `user.js` must hold `zen.embed.allow-unsigned-peer` false.

**This RC (2026-10-19, re-packed after BrowserMode4).** Version 1.23.2b, BuildID 20261019095634.

| Artifact | sha256 |
|---|---|
| `nightly-1.23.2b.en-US.mac.dmg` (122800993 B) | `b85f9bd3afe6116444927659e6e71cf75f10d33eb6ce99ae4b33f714774d59d1` |
| `nightly-runtime-1.23.2b-20261019095634.zip` (130510619 B, cdhash `7949beed0cb694710b8cea90dc764dc2f8768926`) | `e7d3377ab5b02440aa7bce96c09335142f7bb6d4aaf37a12b0d7a870b1af14fe` |

Packaged-build mochitests: gallery 165 / 0, teams 39 / 0, library 199 / 0, ui 66 / 0, tabs 162 / 0 without `browser_drag_drop_vertical.js` (§4;
fails the same way on the objdir). The signed runtime copy passes `codesign --verify --deep --strict` and renders a
headless `--screenshot` with a `.temp/` profile and `-no-remote`.

**`next` pin (2026-10-19, top row vs traffic lights).** BuildID 20261019154359: `nightly-runtime-1.23.2b-20261019154359.zip`
(130511187 B, cdhash `07a6865c1c72819919fd66bd0cb71f16e0498e0c`, sha256
`3cbdd5ab9dcb68a4f4a8fedf4c32a8e705426824d7c401600d70a82f7ab410e5`), published as release
`nightly-1.23.2b-20261019154359` (`curl -sIL` 200, same size). Step 7 on an SDK 26 Release launcher: authenticated
by cdhash, no unsigned switch; NightlyAcceptance ru 96 / 0, en 95 / 1 (`ime-pinyin-commit`) and 95 / 1
(`cursor-pointer`, foreign mouse moves in the log): each check passed in at least one en run.

## Release trust: ad-hoc signatures pinned by cdhash

(agent ReleaseTrust, 2026-10-18; design in [SECURITY.md › Ombra ↔ Nightly trust](../SECURITY.md#ombra--nightly-trust-macos).)
No Developer ID, so both apps are signed ad hoc with the hardened runtime and each pins the other by cdhash:

- **Nightly** — `runtime/nightly/pack-nightly.py` signs the staged copy (`codesign --force --deep -s - --options runtime
  --entitlements runtime/nightly/nightly.entitlements.plist`) and writes `cdhash` into `nightly-runtime.json`; `--check`
  re-verifies the archive's signature and cdhash. Ombra accepts on the embed channel and the team link only a Nightly
  process with that cdhash (`SecCodeCheckValidity` with `cdhash H"…"` on the audit token).
- **Ombra** — `avalonia/tools/package/sign.sh` (no certificate in the secrets) signs ad hoc with the hardened runtime and
  records the cdhash in `<package>.signing.txt`. At each launch Ombra writes its own cdhash into the runtime profile's
  `user.js` (`zen.embed.peer-cdhash`); Nightly (`ZenEmbedHost.mm`, `ZenTeamLink.sys.mjs` / `ZenTeamLinkWorker.js`) accepts
  only that code. A Developer ID requirement (`zen.embed.peer-requirement`) takes precedence when one exists.
- `OMBRA_NIGHTLY_ALLOW_UNSIGNED=1` / `zen.embed.allow-unsigned-peer` stay development-only; the runs below set neither.

Real run (release Nightly above, Ombra packaged by `package.sh` → `ombra-0.0.0-releasetrust-osx-arm64.zip`
sha256 `e65925bb8721a45b54ff70fa4e7f37051b53fa4df0eca62ca1be4dcc4cfe3924`, Ombra cdhash
`86b093bb2f15fd9eb4c1a85622ae54c7dbdf7f3b`; scripts in `.temp/ReleaseTrust/`):

| Check | Result |
|---|---|
| Embed, NightlyAcceptance, no unsigned switch | Nightly log `embedder pid … authenticated (cdhash H"86b0…7f3b")`; `user.js`: peer-cdhash = Ombra's, allow-unsigned false; 67 / 7 — the 7 are layout/timing (pane 760.5 DIP, checkerboard, device-cleared, cutover-page, second pane) and fail the same way in a control run of the same apps with `OMBRA_NIGHTLY_ALLOW_UNSIGNED=1` (68 / 6), so not trust; no process left |
| Embed, a different Nightly binary (same build re-signed, cdhash `dbca…8e68`) | refused: "This Nightly is not the runtime this Ombra pins" ×5, never authenticated |
| Team link, pinned Nightly ↔ Ombra | answer `{"ok":true,"reason":"signed-out"}` (trusted both ways) |
| Team link, Nightly pinning another Ombra cdhash | `untrusted` |
| Team link, different Nightly binary at the known path | Ombra: `refused pid …: not the pinned Nightly` |
| Unit tests `NightlyTeamShareTests`, `NightlyCutoverTests` (manifest/cdhash parsing, real Mach round trip refused on a mismatching cdhash) | 33 / 0 / 1 |
| Update from the previous version keeps a valid signature: after the in-app update `codesign --verify --deep --strict` passes on the installed `Ombra.app` (the updater unpacks the zip with `ditto -x -k` so the `.dll` signatures in extended attributes survive, and refuses a bundle that fails this check; `UpdateInstallTests.A_signed_bundle_zipped_with_ditto_keeps_a_valid_signature_when_unpacked`) | required |

Found by the run and fixed: `ZenTeamLinkWorker.js` `satisfies()` read `kSecGuestAttributeAudit.value` (throws on a
pointer), so every signed-peer check of the team link failed; never exercised before because only the unsigned pref
was used.

**Gatekeeper, first open (macOS).** Ombra is not notarized, so the first start is blocked: open *Ombra.app* once and
close the warning, then *System Settings › Privacy & Security › Open Anyway* and confirm *Open* (macOS 15+); on macOS 14
Control-click (right-click) the app › *Open* › *Open*. Nightly needs nothing: Ombra unpacks its runtime itself, without
a quarantine attribute. Each Ombra update has a new cdhash, so macOS asks again for permissions (microphone, folders).

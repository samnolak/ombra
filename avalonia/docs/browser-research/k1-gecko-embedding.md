# K1 / R0.5 — real Gecko content inside the Ombra window (macOS): design note

Date: 2026-10-17. Machine: Mac17,8, Apple M5 Pro, 18 cores, main display 5K at 160 Hz.
Load (`vm.loadavg`, 1-minute value) was 2.3–9.4 during the samples; each sample below lists its load.
Nightly: dev build `engine/obj-aarch64-apple-darwin/dist/Nightly.app`. XUL built Oct 16 11:26, ad-hoc signed
(`flags=0x20002(adhoc,linker-signed)`, no hardened runtime). Avalonia 12.1.3 (sparse clone of tag `12.1.3`, commit 8eeda4f).
Section 1 used no Firefox rebuild; its DYLD hook probe (`.temp/K1Spike/probe/`) has been deleted and cannot be run.
Section 1a is the option A patch in the Nightly tree, measured with the embedder probe `.temp/K1Spike/embed-probe/`.

## 1. What was proven today (measured, not inferred)

1. **Real Gecko frames reached an Avalonia 12.1.3 window with no Firefox rebuild, and trusted input went back.**
   Setup: the stock Nightly dev build, a temporary profile with `gfx.webrender.compositor=false`, and a
   probe dylib loaded with `DYLD_INSERT_LIBRARIES`. The dylib hooks `-[CALayer setContents:]` in the parent
   process. For each frame it sends the IOSurface to the probe as a Mach send right (`IOSurfaceCreateMachPort`).
   The probe is an Avalonia window in OpenGL mode, the same mode Ombra uses (`Program.cs:182-186`). It imports
   each IOSurface once with `ICompositionGpuInterop.ImportImage(IOSurfaceRef)` and calls
   `CompositionDrawingSurface.UpdateAsync` for every frame.
   - Whole Nightly window, 2560×2080 px, 1 layer, a pool of 4–5 IOSurfaces. Gecko drove 160 fps (display vsync, CSS
     animation) and Avalonia shows **159.9–160.1 fps** with 0 failures (1 600 frames in 10 s).
   - Gecko `setContents` → Avalonia compositor snapshot done: **p50 7.3–8.1 ms, p95 9.5–13.8 ms** (load 3.1–5.6).
     Under load 6.9–9.4 it was p50 12.0–15.1 ms, p95 16.8–26.0 ms. The Mach message itself takes p50 0.008 ms.
   - Probe process CPU: **87–92 % of one core** at 160 fps in GL mode (see point 3).
   - Input: the probe replays mouse and keyboard as `NSEvent`s sent directly to Gecko's NSWindow. It does not call
     `CGWarpMouseCursorPosition` and does not use `CGEventPost`. The page logged
     `mousedown/mouseup/click isTrusted=true @410,512` and `keydown isTrusted=true key=k … input isTrusted=true`, and
     the field contains "k1 ok". Screenshot: `shot-gecko-click.png`.
   - **When Ombra is the active app, which is the real embedding case** (`app.active=0` in Nightly), keys still arrive
     with `isTrusted=true`. **The left click is dropped**, and `makeKeyWindow`/`makeMainWindow` are refused
     (`key=0 main=0`). This is Gecko's inactive-window gate (`ChildViewMouseTracker::WindowAcceptsEvent`,
     `widget/cocoa/nsCocoaWindow.mm:4980-5025`). Screenshot: `shot-gecko-inactive.png`.
   - Hidden host: `alphaValue=0` plus `ignoresMouseEvents` keeps 159.9 fps. `orderOut:` stops presentation (0 frames).
     A move to (-20000,-20000) was clamped by AppKit to (-2680,-1134), probably onto the second display, and kept
     159.9 fps. That last test is inconclusive. So with today's binary, "no visible window" only works as a
     transparent window. The product path needs a widget patch (§3A).
2. **Avalonia import path (synthetic, Metal producer → Avalonia, triple-buffered, Mach-port transfer).**
   Correctness was checked by reading back pixels from Avalonia's own compositor (`Compositor.CreateCompositionVisualSnapshot`).
   The expected background `0xff733366` and a white bar in the top half only both appeared, so orientation is correct
   with `TopLeftOrigin=true`.

   | backend | size @ fps | frames | effective fps | done p50 / p95 ms | probe CPU (% of one core) | load |
   |---|---|---|---|---|---|---|
   | OpenGL (Ombra's mode) | 1280×800 @60 | 600/600 | 60.1 | 4.3 / 6.7 | 21.3 | 2.9 |
   | OpenGL | 2560×1600 @60 | 600/600 | 60.1 | 5.1–5.4 / 7.2–7.6 | 35.9–40.8 | 2.8–3.8 |
   | OpenGL | 2560×1600 @120 | 1200/1200 | 120.2 | 5.3 / 7.6 | 59.4 | 2.5 |
   | OpenGL, frames received but not shown | 2560×1600 @60 | 600 | 60.0 | — | 2.7 | 2.3 |
   | Metal, timeline semaphore | 2560×1600 @60 | 600/600 | 60.1 | 4.5–5.0 / 6.1–6.2 | 8.5–10.3 | 3.4–3.8 |
   | Metal, timeline semaphore | 2560×1600 @120 | 1200/1200 | 120.0 | 13.7 / 21.6 | 14.8 | 3.7 |
   | Metal, `UpdateAsync` (automatic sync) | 2560×1600 @60 | 0/600 | — | `NotSupportedException` | — | 2.7 |

3. **Cost of OpenGL in Avalonia.Native is the main finding for Ombra.** Each GPU update in GL mode costs about 6–7 ms of
   CPU per 2560×1600 frame in the probe process (3 582–4 063 ms over 600 frames; 5.6 ms per frame at Gecko's 160 fps).
   That is 36–41 % of a core at 60 fps and about 90 % at 160 fps. Metal costs 1.4–1.7 ms per frame, or 8.5–10 % of a
   core at 60 fps. Two causes are known from the source:
   - Avalonia's GL import copies the whole IOSurface texture into a newly allocated texture on every update
     (`GlSkiaExternalObjectsFeature.cs:149-189,266-299`).
   - Avalonia.Native's GL target renders each frame into a new window-sized IOSurface and copies the window
     (`Ombra.App/Controls/Pulse.cs:16-17`: about 4 ms CPU per frame).

   Metal supports only `TimelineSemaphores` for IOSurface images (`Metal.cs:118-120`), not automatic sync.

## 1a. Option A patch: measured (2026-10-17, load 4.0–4.1)

Nightly side (`desktop/src/zen/embed/` plus engine patches exported to `src/widget/cocoa/nsCocoaWindow-{h,mm}.patch`,
`src/widget/nsIWidget-cpp.patch`, `src/gfx/layers/NativeLayerCA-{h,mm}.patch`,
`src/gfx/webrender_bindings/RenderCompositorNative-cpp.patch`):
- `NIGHTLY_EMBED_SERVICE=<bootstrap name>` turns every top-level window into an *embedded* `nsCocoaWindow`:
  borderless (no title bar, so the child view and the surface cover exactly the requested content size),
  never ordered on screen, not deferred (it needs a window number for input), single full-window WebRender
  surface (`AllowNativeCompositor=false`), click gate bypassed, activation from the embedder.
- The embedder starts Nightly with `-chrome "chrome://browser/content/zen-embed.xhtml?url=<page>"`: a window with
  one remote `<browser>` and nothing else. Zen/Firefox UI (sidebar, URL bar, traffic lights) is never created.
- Mach protocol v1 (`ZenEmbedHost.mm` header comment): Hello/HelloAck with audit-token check, Surface (IOSurface
  send right once per surface), Frame{surfaceId, seq}, Release{seq}, Mouse/Key/Focus/Resize. Resize is in points;
  Gecko renders at the window's backing scale (2× here) and reports it in Window{w, h, scale}.

Embedder rules (probe `Program.cs`, `LayoutFrame`/`ToGecko`): drawn size in DIP = surface px / backing scale,
centred, origin snapped to device pixels; the pane sends its DIP size on every size change. A frame that does not match
the pane (only while a resize is in flight) is letterboxed; one larger than the pane is shrunk to fit, never cropped
and never stretched to Fill. Pointer positions map through the drawn rect; clicks on letterbox bars are dropped.

Real screen captures (`screencapture -o -l <probe window>`, 2×, 56 px title bar) of the test page (`.temp/K1Spike-2/`):

| check | 1200×800 DIP pane | 800×1000 DIP pane (after resize) |
|---|---|---|
| frame / Gecko window | 2400×1600 px / 1200×800 pt @2 | 1600×2000 px / 800×1000 pt @2 |
| page `innerWidth×innerHeight` | 1200×800 CSS | 800×1000 CSS |
| 200×100 CSS target in the capture | 400×200 px at (1000,756) | 400×200 px at (600,956) |
| 8×8 CSS top-left / 20×20 CSS bottom-right markers | at pane corner (0,56) / 40×40 px at bottom-right corner | same |
| blended pixels on the target edge (resampling) | 0 | 0 |
| frames letterboxed / exact | 1 / 271 (startup) | 5 / 168 (resize) |
| click at target centre | `mousedown isTrusted=true … on #t`, `click isTrusted=true`, target green | same, at 400,500 |
| embedder active / Nightly active at click | true / false | true / false |

The top 2 device-pixel rows of the pane are darkened by the probe window's own AppKit title-bar separator; the
page content there is present (orange marker visible), not cropped. Text is pixel-aligned (1:1, no scaling).
Clicks were delivered through the same pane→Gecko mapping as pointer events (`SendMouse`), not as HID events: the
probe process is not Accessibility-trusted (`AXIsProcessTrusted=false`), so `CGEventPost` is unavailable to it.

Performance, 1200×800 pane, CSS animation (Gecko at display rate), 10 s:

| Avalonia backend | fps shown | present → mach p50/p95 ms | present → shown p50/p95 ms | embedder CPU | Nightly tree CPU |
|---|---|---|---|---|---|
| OpenGL (Ombra today) | 160.2 | 0.009 / 0.025 | 6.8 / 7.9 | 91.5 % of a core | 19.3 % |
| Metal, timeline semaphore | 155.1 | 0.007 / 0.013 | 15.1 / 24.6 | 12.3 % | 15.8 % |

Remaining for Ombra integration:
1. Ombra backend: GL costs ~7.5× Metal's CPU per frame; switch to Metal (re-test #22215/#22297) or cap embedded fps.
   Metal's present → shown latency is ~2× GL's and needs a look (semaphore wait per frame).
2. Only the chrome-less window may be embedded: today `NIGHTLY_EMBED_SERVICE` embeds every top-level window of the
   process; one window per pane, tab/window ids in the protocol, popups (select, autocomplete, panels) as extra surfaces.
3. Input: scroll (phases, momentum), magnify, IME, edit-command bindings, cursor shape back to Ombra, drag and drop.
4. Production authentication: `zen.embed.peer-requirement` with Ombra's Developer ID requirement instead of
   `zen.embed.allow-unsigned-peer`; hardened-runtime Nightly.
5. Lifecycle: embedder or Nightly crash/restart, surface-pool reuse after `Release`, frame pacing when the pane is hidden.
6. Backing-scale changes (moving Ombra between displays): Gecko follows its off-screen window's screen; the scale must
   come from the embedder.
7. Accessibility bridge; video overlay/low-power loss from single-surface WebRender; Windows/Linux exporters.
8. Ombra code: none yet — `BrowserPane` hosting, Mach channel and Nightly launch are probe-only.

### 1b. First Ombra integration (2026-10-17, in progress)

Renderer data: `tools/verify/performance --seconds 60 --render opengl|metal` (native, 160 Hz display, stand-in model, two
runs each). OpenGL: 159.9–160.0 fps rendered, frame p95 7.16–7.17 ms, CPU 29.8–31.0 % of a core with the probe forcing a
frame per vsync, 13.5–13.7 % while text streams, 6.0 % idle. Metal: 104.4–106.2 fps, p95 14.3–14.7 ms (misses vsyncs),
CPU 10.6–10.9 % / 8.0–8.3 % / 2.9–3.0 %. With a Nightly pane the import cost from §1a adds 91.5 % (GL) vs 12.3 % (Metal)
at 160 fps. Default stays OpenGL until Metal's pacing and #22215/#22297 are fixed; cap embedded fps meanwhile.

Protocol v2 (`ZenEmbedHost.mm`): only windows opened with `zen-embed.xhtml` are embedded (`AutoEmbedScope` in
`nsWindowWatcher::OpenWindowInternal`); Wheel (phases, momentum, precise deltas → APZ pan gestures), Text, mouse exit,
Resize with the pane's screen origin (backing scale follows), OpenWindow{pane}, Command{navigate, reload, back, forward,
stop, close} and Location{flags, url, title} through `zen-embed.js`; Nightly quits when the embedder exits or cannot be
reached. Ombra: `Nightly/` (protocol, Mach channel, supervisor with crash restart and process-tree kill, runtime),
`Controls/NightlyPane.cs`, Settings › General › Agent browser. `NightlyRuntimeTests` covers handshake, frames,
navigation, crash restart and no leftover processes against the dev build.

Window acceptance (Release Ombra, OpenGL, dev Nightly, 2026-10-17): `Nightly/NightlyAcceptance.cs`, run only with
`OMBRA_NIGHTLY_ACCEPTANCE=<out dir>` and `OMBRA_NIGHTLY_ACCEPTANCE_URL=<fixture>` (contract in its summary), plus
`OMBRA_CONFIG` with `"agentBrowserEngine": "nightly"`, `OMBRA_NIGHTLY_APP`, `OMBRA_NIGHTLY_PROFILE` and
`OMBRA_NIGHTLY_ALLOW_UNSIGNED=1`. This shell has no Accessibility trust (`CGEventPost` refused), so the check posts
`NSEvent`s to the app's own queue (`-[NSApp postEvent:atStart:]`; scrolls through `-[NSApp sendEvent:]`, bound to the
window): AppKit → Avalonia's view → `NightlyPane` handlers, and the real `NightlyScrollMonitor`. Results: frames 1:1 at
1320×860 and 940×640 (pane 480×760 and 467×540 DIP, frames 960×1520 and 934×1080 px), and the fixture's one-device-pixel
checkerboard intact in `screencapture -l` shots (0/78 804 pixels off). Clicks focus the field; keys type the native
events' characters into it (a hardware key's: the current layout's); a link click runs the handler and Location reports `#linked`; a 12×40 px pan gesture
scrolls the page by 480 px. Closing a second pane keeps the runtime. Closing the last one (engine switched to the
built-in preview) stops it with no process left. Quitting with a pane open leaves no process from the bundle either.
Bugs it found, now fixed in `NightlyPane`:
- The GPU import was set up in `OnAttachedToVisualTree`, before the surface child had a composition visual. It never
  happened, so no frame was ever shown. It now runs from `OnLoaded`.
- Frames that arrive before the import is set up, or while the pane is hidden, are no longer dropped: the latest one is
  held and shown once it can be. Before, a page that painted once stayed behind "Opening the page…".
- The surface control drew nothing, so hit-testing went to the pane's background. Mouse, focus and the scroll monitor's
  hit test missed it, and no input reached Nightly. It now paints a transparent rectangle.
- A frame whose import a resize had replaced threw `PlatformGraphicsContextLostException`; it is now skipped.

Agent browser tools on Nightly panes (protocol v3, 2026-10-17): omp's cmux requests (`AgentBrowserBridge`) drive a
Nightly tab through `Nightly/NightlyAgent.cs`, over the authenticated Mach channel only (no remote debugging port).
Script{requestId, kind, source, args} / ScriptResult{requestId, status, text} carry their text out of line (OOL
descriptor, up to 64 MiB); `zen-embed.js` relays them to the `ZenEmbedAgent` window actor (`ZenEmbedAgentChild.sys.mjs`,
registered for `messagemanagergroup="zen-embed"` browsers only, `safeForUntrustedWebProcess`). Kind 1 runs in the
page's world (`Debugger.executeInGlobal`, bypassing the page's CSP, as WebDriver BiDi), kind 2 calls a function in a
Xray sandbox (the isolated world, like `WKContentWorld`). Native input reuses the pane's Mouse/Key/Wheel/Text messages
(+ Key `down=2` = `flagsChanged:`); the agent's window becomes Gecko's active one before keys/text and the user's pane
takes it back when they act (`NightlyRuntime.SetActive`). A batch ends with a script round trip. Screenshots read the
pane's latest IOSurface (use count raised) into a PNG. The embed window got Edit-menu keys (⌘A/C/X/V/Z) so `fill`
works. Tern tabs refuse Nightly panes at once (`no_window`). Proven by `NightlyRuntimeTests.OmpsBrowserToolsDriveARealNightlyPane`
(real dev Nightly: eval, isolated world, snapshot, click/hover/type/fill/press trusted, scroll, pixel-exact screenshot)
and in the real window by `NightlyAcceptance` (cmux socket + production `PreviewAgentHost`). Dev builds only: content
processes cannot read the symlinked `src/` files (`testing_read_path`), see `dist/Nightly.app/.../defaults/pref/k1-dev-sandbox.js`.

Popups, page windows, composition and cursor (protocol ids 29/30, 105–108, 2026-10-17): `zen-embed.js` stands in for
SelectParent's `#ContentSelectDropdown`, AutoCompleteParent's popup and ContextMenuParent's `#contentAreaContextMenu`;
each sends Popup{json} and takes PopupChoice{index}, and Ombra draws it (`Controls/NightlyPopupPresenter.cs`, keys
while a dropdown or menu is open). A page's `window.open` window (`AppWindow::CreateNewContentWindow` → embed document
with `initialOpenWindowInfo`, opener from `parent.top.embedderElement.documentGlobal`) and "Open link in new tab" (a
property bag through `Services.ww`) come to the opener's pane as new Ombra tabs; `window.close()` in such a page
(BrowserElementParent's `DOMWindowClose` on the `<browser>`) closes the embed window and the tab. The input method
composes in Ombra (MarkedText / Text, the candidate window at TextRect), and Nightly's cursor comes back as Cursor.
Nightly ends a composition itself while the input method still composes in Ombra (it commits it when the focus moves or
the window goes inactive, or nothing editable takes it); the input method's empty MarkedText after that is nothing
(`ZenEmbedHost.mm` `OnMarkedText` asks the window's `TextInputHandler` whether it composes). It used to reach
`IMEInputHandler::SetMarkedText` with no composition and crash Nightly (MOZ_RELEASE_ASSERT(isSome()) in
`DispatchCompositionCommitEvent`, 2026-10-19: the new-tab checks' probe after an app switch);
`NightlyRuntimeTests.AnEmptyCompositionWithoutOneInNightlyIsNothing` and the acceptance's `ime-ended-*` checks cover it.
`ContextMenuChild` asks the `LoginManager` actor for every context menu, so that actor is registered for the
`zen-embed` group too (`src/toolkit/modules/ActorManagerParent-sys-mjs.patch`); without it no context menu reached the
parent. A closed window's last messages are ignored by the runtime (Nightly never reuses a window id), else a closed
pop-up tab came back as a new, empty one. Proven by `NightlyRuntimeTests.PagePopupsNewWindowsAndCompositionComeToOmbra`
and in the real window by `NightlyAcceptance` (select and context menu answered from the keyboard, both new tabs, the
pop-up closed by its page, Japanese and Chinese composition through `setMarkedText:`/`insertText:` with
`firstRectForCharacterRange:` inside the field, pointer and grab cursors; popups shot with `screencapture -R`). Still
open: `ZenGlanceParent` throws on every link click in embed windows (no `gZenGlanceManager`), and saving a login has no
doorhanger there.

### 1c. Cutover (protocol 4, 2026-10-18)

Protocol 4 (`kProtocolVersion`, `NightlyProtocol.Version`; K1 owns ids 40–49 Ombra→Nightly and 120–129 back, K3's
device mode is `Command` op 7, Ombra's appearance op 8):

- **Quit (40).** Ombra asks Nightly to quit as a user would (`Services.startup.quit`, sessions written, profile
  closed). `NightlySupervisor.Stop(requestQuit, grace)` waits `NightlyRuntime.QuitGrace` (8 s) and only then kills the
  tree and the helpers it left; a start meanwhile waits for the previous process (one profile, one process). App exit
  and the last pane closing both go this way; a crash or a refused handshake still kills at once.
  Nightly is not marked for the app's reaper (`OMBRA_OWNER` is removed from its environment): `OwnedProcesses`'
  SIGTERM at Ombra's quit ended it before this quit (exit 143, no `sessionstore-final-state-write-complete`), so every
  restart restored only its last periodic save. When Ombra dies, Nightly quits by itself (peer exit) the same way.
- **Session.** The runtime's user.js resumes the previous session as a standalone Nightly does
  (`browser.startup.page` 3, `resume_from_crash` true): Zen's `ZenSessionManager` keeps only the pinned tabs (folders
  survive, unpinned tabs and split views do not) unless startup.page is 3. Tabs, spaces, folders, split views, scroll
  and form state come back into the Browser page's window after a quit, a Nightly crash and Ombra being killed; the
  start selects Zen's empty tab with its URL bar, as standalone (`zen.workspaces.continue-where-left-off` false).
  Acceptance: `SessionChecks` (`NightlyAcceptance.Session.cs`, drive command `session`).
- **Dialog (120) / DialogReply (41) / DialogClosed (121), Download (122) / DownloadCancel (42).** The page's questions
  are Ombra's (`src/zen/embed/ZenEmbedDialogs.sys.mjs`): alert/confirm/prompt/beforeunload and HTTP sign-in through the
  embed window's `gBrowser.getTabDialogBox` (what `PromptParent` uses for tab-modal prompts); permissions through its
  `PopupNotifications` (PermissionUI; camera/microphone via WebRTCParent on a hidden copy of its panel content in
  `zen-embed.xhtml`, first device shared; screen sharing is refused — no picker); `@mozilla.org/filepicker;1` and
  `@mozilla.org/helperapplauncherdialog;1` are replaced in the embed process; downloads are followed in the download
  list. Ombra answers through the tab's own `BrowserPageHooks` (`NightlyDialogs`): the same cards, remembered site
  permissions and download destination flow as the built-in preview; downloads show in the pane's strip.
- **Delivery.** Without a Developer ID, Ombra installs the desktop build itself:
  `runtime/nightly/pack-nightly.py` signs a copy of `dist/Nightly.app` (or the app in `./mach package`'s dmg) ad hoc
  with the hardened runtime, zips it and writes the pin `runtime/nightly/nightly-runtime.json` (SHA-256, size, Version,
  BuildID, protocol, cdhash of `Contents/MacOS/nightly`), built into the client.
  `NightlyRuntimePackage` installs only that archive (from `<runtimes>/nightly/packages`, `OMBRA_NIGHTLY_PACKAGE`, or the
  pin's https URL), checks the app's `application.ini` against the pin, puts it in `<runtimes>/nightly/<ver>-<build>`,
  and removes older versions no runtime runs. `OMBRA_NIGHTLY_APP` still overrides for development; `/Applications`
  is no longer looked at. Trust (SECURITY.md › Ombra ↔ Nightly trust): Ombra accepts only a Nightly with the pinned
  cdhash; Nightly checks Ombra's Developer ID requirement (`zen.embed.peer-requirement`) or else the cdhash Ombra
  writes into the runtime profile (`zen.embed.peer-cdhash`); an unsigned peer only with
  `OMBRA_NIGHTLY_ALLOW_UNSIGNED=1` (development).
- **Default and fallback.** On macOS a never-chosen engine shows a first-run card in the pane ("Use Nightly" / "Keep
  the built-in preview"); the answer is saved. While Nightly is chosen but not installed, outdated, or failing, the
  built-in preview shows the pages with a bar saying why, and Install / Try again.
- **Browser page.** The rail's Browser opens a full-size pane of the user's own (its own tabs, same engine and runtime,
  same cards and downloads); the agent never drives it.
- **Cards over a live page.** A Nightly pane is composed by Avalonia (IOSurface import), so a card does not hide it
  behind a picture as the native web view needs (`PreviewViewModel.ShowWebView`, no `CaptureSnapshot` for Nightly):
  the page stays live under the card. Before, the area under a card was black.
- **Window acceptance (2026-10-18, Release Ombra, pinned package `1.23.2b-20261018154855`, first run without a chosen
  engine, en and ru: 78 PASS each, RESULT PASS, no Nightly process left).** `NightlyAcceptance.Cutover.cs` presses
  the consent strip's Use Nightly, opens the rail's Browser page (same runtime pid), clicks fixture buttons whose
  alert, confirm, prompt, notifications permission, download (Save → strip, file written), file picker and HTTP
  sign-in come up as cards and are answered from the cards (the file picker's system open panel cannot be driven
  without Accessibility: the card is given the file), then kills Nightly until the supervisor gives up — fallback bar
  with the reason, Try again brings Nightly back. Fixture: `cutover.html` + a server answering `/auth` (Basic) and
  `/file.txt` (attachment). Geolocation: Allow reaches Gecko but no position or timeout ever comes back to the page
  (macOS Location Services for an unsigned Nightly) — open.
- **Device viewport (K3, 2026-10-18).** The embed window always takes the device's CSS size (`NightlyPane.Viewport`,
  sent in Resize instead of the pane's size), so the page lays out at it whatever the panel's size; the pane is that
  size where it fits (1:1) and otherwise the largest that fits with its aspect, the frame scaled into it as Codex and
  DevTools do. Pointer, popups and the IME caret map through the drawn rect (`ToGecko`/`FromGecko`). No Nightly change:
  an off-screen window is not constrained to a screen (a typed 1000×4000 is taller than both screens here, 1440 and
  2560 pt, and the page gets 1000×4000). Acceptance in a 480×681 panel, en and ru: innerWidth×innerHeight equal every
  preset (375×667 at 1:1 … 1440×900 at 1/3), iPhone 15 Pro landscape 852×393 and 1000×4000; `#tap` (20, 20, 200×80
  CSS px) counts a tap at its scaled centre and none just right of it.
- **Browser window (protocol 4 `OpenBrowserWindow` 43, 2026-10-19).** The Browser page shows Nightly's own
  `browser.xhtml` window. Fixes found driving it:
  - *Double render / ⌘W closed Ombra.* The right panel's agent pane stayed visible under the Browser page: two live
    Nightly surfaces, and focus stayed with the agent's, so ⌘W reached Ombra. `MainWindow.axaml`: RightPanel
    `IsVisible = IsSidePaneOpen && !ShowBrowserPage`.
  - *Popup-looking window* (no sidebar or spaces when the runtime first started for the agent pane): `nsWindowWatcher`
    called without arguments ignores the `all` chrome flag; `ZenEmbedHost.mm` `OnOpenBrowserWindow` lists the chrome
    flags explicitly.
  - *⌘W on Zen's floating new-tab URL bar* (after ⌘T) closed the page under it: `browser-commands.js`
    `closeTabOrWindow` closes the `zen-newtab` session first (`browser-commands-js.patch`).
  - *Share with Team buttons clipped*: `zen-team-share.js` resizes the subdialog after its strings are translated
    (`document.l10n.translateRoots().then(resizeDialog)`).
  - *Appearance.* Nightly kept its own (macOS's) scheme under Ombra's Light. `Command` op 8 (Theme, `"dark"`/`"light"`,
    any window id) sets `ui.systemUsesDarkTheme`, which LookAndFeel applies live to the chrome, Zen's automatic scheme
    and the pages' `prefers-color-scheme`; Ombra writes the same pref and `zen.view.window.scheme`=2 into `user.js`
    before each start, sends it after the handshake and on every `ActualThemeVariantChanged` (`NightlyHost`,
    `NightlyRuntime.DarkTheme`). Driven: started Light, a fixture page reported `prefers light`; Settings → Dark
    turned the sidebar, the page (`prefers dark`) and an open Share dialog dark within 2.5 s, and back.
  - Spaces switch with ⌥⌘←/→; Library opens as Zen's panel inside the window; Share as a subdialog.
  - *A window lying over Ombra* (owner's screenshot, 2026-10-19). The pane spanned the window's top to the frame
    inset, so its rounded corner started at the window's top, the traffic lights sat over Zen's row and Zen's dark
    window background (`rgba(0,0,0,.4)` over macOS's window colour: `#121212`) read as another window; the light
    theme's bottom corners showed a dark 1 px fringe past the hairline (the clip's antialiased curve). Now the window
    is the page sheet's rect (`MainWindow.axaml` `BrowserWindowHost` under `GuiSheetMargin`, the band above it the
    chrome's title bar as on the Team page), clipped 1 DIP inside the sheet (`GuiSheetClipRadius` 11, pane margin −1)
    so the clip's curve lies under `BrowserWindowEdge`'s hairline. Zen: `.titlebar-buttonbox-container` is gone under
    `zen-embedded` (the lights are never over the window), and with a space's default theme `#zen-browser-background`
    / `#zen-toolbar-background` take `light-dark(#fff, #181818)` (Ombra's `GuiWindow`) instead of the translucent
    macOS background. `Command` op 9 (window controls), `zen.embed.window-controls-hidden` and
    `NightlyHost/NightlyRuntime.WindowControlsShown` are removed. Check `browser-window-sheet`
    (`NightlyAcceptance.SheetMatches`, also the driver's `sheet NAME`): the pane is `SheetFrame`'s rect within 1 px,
    `Matches`, the lights' frames end above the sheet, and round all four corners every device pixel on the edge's
    outer half or outside it equals the Chats page's shot at the same size within 24/255. Driven (dev Nightly, plain
    and SDK 26 launchers): dark, light, a page loaded, 1000×700, Ombra sidebar hidden, Zen sidebar collapsed, the
    window on the second display and back — all PASS, worst corner delta 3 (before: 46 in light).

  Acceptance (Release Ombra, pinned package `1.23.2b-20261019095634`, cdhash `7949beed…8926`, en and ru): 96 PASS
  each, 0 FAIL, RESULT PASS, no Nightly process left. `NightlyShortcutMonitorTests` + `NightlyRuntimeTests` (with the
  new `ThemeMessageIsACommandForNoWindow`): 29 passed, 3 skipped, 0 failed.
- **No Nightly window outside Ombra (2026-10-19).** Everything that made a new browser window — a sized
  `window.open` (sign-in popups), ⌘N, Shift-click / Open Link in New Window, ⌘⇧P, moving a tab to a new window,
  ⌘⇧O Library, session restore with several saved windows — put an ordinary Nightly window on screen, usually behind
  Ombra; links from Ombra and other apps went to the system's browser, which could be the running runtime.
  - *Stray-window trap* (`ZenEmbedHost.mm` `mStray`, `HasBrowserWindow`). While a pane shows a browser window, any
    other `browser.xhtml` window is created embedded and never shown or sent to Ombra; `zen-embed-stray-window` is
    notified and `ZenEmbedBrowser.sys.mjs` moves its tabs into the pane's window (`adoptTab`), then closes it
    (private ones are just closed). Covers session restore, extensions and external URLs alike.
  - *Tabs, not windows.* `user.js`: `browser.link.open_newwindow`=3, `.restriction`=0 (sized popups are tabs, opener
    kept). In the pane's window `OpenBrowserWindow` opens a tab, `openLinkIn(where=window)` a tab in front,
    `replaceTab(s)WithWindow` do nothing, `showPlacesOrganizer` is Zen's Library or the organizer in a tab; new/private
    window and Move to New Window menu items are hidden (`zen-browser-ui.css`).
  - *Ombra's links* (chat, cards, context menu, Open in browser) open in the Browser page (`OpenInBrowserPage`);
    non-http stays with the system.
  - *Runtime identity.* `pack-nightly.py` gives the packaged copy `CFBundleIdentifier`
    `io.github.samnolak.ombra.nightly-runtime` and drops `CFBundleURLTypes`/`CFBundleDocumentTypes` before signing
    (`--check` verifies), so LaunchServices never routes a default-browser link to it. Signing comes after the change,
    so the pinned cdhash is the final binary's and embed trust is unchanged; Ombra never matches the bundle id. TCC
    grants (camera, microphone, local network) are keyed to the new id: asked once again, and no longer shared with a
    standalone Nightly.

  Acceptance (`NewWindowChecks`, driver `newwindows`): windows-page, -popup-tab, -popup-opener (postMessage +
  `window.close()` from the popup-as-tab), -shift-click, -cmd-n, -library, -ombra-link all PASS with 0 Nightly
  windows on screen; the colour checks require the probe to have read the page's cyan first (no false PASS from an
  unreadable shot). Relaunch with a profile holding 3 saved extra windows: 4 stray windows trapped, their tabs in the
  Browser page, 0 on screen.
- **Frames as a Core Animation layer (2026-10-19).** The Browser page lagged: Avalonia's OpenGL import of each Nightly
  frame went through Apple's GL-on-Metal driver, which re-uploads the whole IOSurface on the CPU (`GLDTextureRec::
  uploadTextureLevel` → `agxsTwiddleAddressCommon`, ~40 % of a core in a `sample` of Ombra at 146 fps, 2520×1704 px),
  and each frame cost an Ombra window redraw on top. Nightly's frames waited for Ombra's Release (3 in flight), so
  only part of Gecko's composites were shown. `NightlyPane` now shows the frame's IOSurface as the contents of a layer
  under Avalonia's view (`Nightly/NightlyLayer.cs`, a layer-hosting NSView below `AvnView`), and clears a hole in
  what Avalonia draws where the frame is (Skia clear blend in the pane's surface), so cards, popups and the sheet's
  rounded corners and hairline Avalonia draws stay above the page. The GPU import path and its Metal semaphore are
  gone. Measured on the Browser page (dev Nightly, same pages, standalone dev Nightly as the reference,
  `NightlyAcceptance.Perf.cs` driver commands `pan`/`keys`/`stats`, ScreenCaptureKit window probe for latency):
  trackpad scroll shown 78–87 → 159–160 fps (Gecko 134–159, standalone 148–160), Nightly present → shown
  p50 20–24 / p95 30–38 ms → 0.1 ms (Core Animation shows it at the next refresh), key → pixels in a textarea p50
  39 → 11 ms (standalone 16 ms), sidebar hover p50 40 → 16 ms (standalone 22 ms), Ombra CPU while scrolling 55–57 →
  19–27 %, CSS animation 81 → 7 %, video 38 → 3 %. Acceptance `browser-window-frames` pans a long page and wants
  ≥ 90 % of Nightly's frames on screen at ≥ 120 fps (159–160 fps, 191–193 of 192–194 measured);
  `browser-window-overlay` puts a box in the window's adorner layer over the pane and finds its colour in a window shot;
  `browser-window-hidden` wants the layer hidden on the Chats page. Driven checks (driver `frames`) also passed after a
  resize, in full screen, after ⌘M and back, on the other display, and after Nightly was killed and restarted; 5 min of
  pans kept Ombra's IOSurface regions at 18–19 and its footprint at 519–520 MB.

### 1d. Drag and drop (protocol 4, ids 44–45 / 123–124, 2026-10-19)

Before: every drag in the Browser page began an AppKit drag from the off-screen embedded window. No drop target ever saw
it (no dragover/drop, so no tab into a folder, no reorder, no link into the sidebar), its drag image stuck on screen over
other windows, the page's drag session stayed stuck (dragstart, no dragend), and a tab dropped on a folder was torn off
into a separate Nightly window (dragend with dropEffect none).
- **Open in app (2026-10-19).** A link Ombra opens in the Browser page (`OpenInBrowserPage`), or one clicked or typed
  there, to a site whose desktop app is installed (Zoom `/j/`, Figma files, Толк rooms, `t.me`, Teams, Slack, …) goes to
  that app through LaunchServices; the tab opened for it closes, so the page shows what it showed. Nightly decides
  (`src/zen/open-in-app/`, documented in the desktop repo's `prefs/README.md`); Ombra still switches to the Browser
  page first. Acceptance `OpenInAppChecks` (`NightlyAcceptance.OpenInApp.cs`, driver `openinapp`): an AppleScript
  probe claims the scheme of a known app this Mac lacks (Zoom's here), Ombra's link and a click on the page reach it
  with the app URL and the page keeps its colour; the probe is unregistered and deleted afterwards.


Now (desktop `ZenEmbedHost.mm` "Drags", `nsDragService.mm` → `ZenEmbedHost::BeginDrag`, `nsCocoaWindow.mm`
`doDragAction:` reads the embedder's event, `-[ChildView endDragSessionAt:operation:event:]`):
- A drag that starts in an embedded window runs on the pane's mouse: the Mouse messages with the button held call the
  view's NSDraggingDestination methods with a stand-in NSDraggingInfo (`ZenEmbedDragInfo`, its pasteboard a named one per
  runtime), dragover repeats every 100 ms while the pointer rests, the release drops, Escape cancels, Ombra's
  deactivation cancels. DragStarted{PNG image, its frame, the press, pasteboard} lets `NightlyPane` draw the drag image
  under the pointer and show AppKit's drag cursors (DragStatus).
- When the pointer leaves the pane, Ombra carries the drag on as its own AppKit drag (`Nightly/NightlyDrag.cs`): each
  type is read from Nightly's pasteboard only when a destination asks (promised files at the destination's paste
  location); DragSource{carried, ended/cancelled} ends Nightly's session.
- An AppKit drag over the pane (Finder's files, other apps' text, the carried drag coming back) reaches the window as
  DragTarget{enter, over, exit, drop}; its data is the system drag pasteboard. The window-level file drop (composer) no
  longer takes drops over the Browser page.
- A press in the pane is released and dragged there wherever the pointer goes (as AppKit sends to the pressed window).

Driven in the real window (driver `drag X1 Y1 X2 Y2`, Marionette for the chrome's state): tab into a folder, tab
reorder, link from the page into the sidebar (a new tab), tab to the pinned section, tab to the next space at the
sidebar's edge, in-page HTML5 DnD (dragover ×14, drop "from-src", dragend move); no stuck session, one window.
`NightlyAcceptance.DragDrop.cs` (`DragDropChecks`, driver `dnd`): browser-window-drag-page, -drag-out, -drag-files.
Not verifiable here (no Accessibility, no HID drag): the carried drag's drop into another app; AppKit ends it at once
without a held button.

## 2. Gecko facts the options rely on (file:line in `engine/`)

- macOS defaults: GPU process on (`StaticPrefList.yaml:10484-10493`). Native CoreAnimation compositor on
  (`gfx.webrender.compositor`, `:8500-8507`, `mirror: once`). `gfx.webrender.layer-compositor` off (`:8509-8516`).
  So WebRender runs in the GPU process as `RenderCompositorNativeOGL` (`gfx/webrender_bindings/RenderCompositor.cpp:266-278`).
- With the native compositor on, a frame is many tile layers with clips and transforms. With it off,
  `RenderCompositorNative` draws the whole window into one layer, `mNativeLayerForEntireWindow`
  (`RenderCompositorNative.cpp:66-111`). That single IOSurface is what the probe exported. Whether to use the
  native compositor is decided from the global gfxVar only (`:133-135`). The per-widget
  `CompositorOptions::AllowNativeCompositor()` (`nsIWidget.cpp:1506`) is checked when the compositor is chosen
  (`RenderCompositor.cpp:267-268`), but not inside `RenderCompositorNative`. A per-window single-surface mode
  therefore needs a small patch, otherwise the pref affects standalone windows too.
- Cross-process IOSurface transfer already exists: the GPU process sends layer commands to the parent
  (`gfx/layers/ipc/PNativeLayerRemote.ipdl:19-95`), including `CommandChangedSurface{IOSurfacePort}` per new frame.
  `IOSurfacePort` wraps `IOSurfaceCreateMachPort`/`IOSurfaceLookupFromMachPort` (`gfx/layers/ipc/IOSurfacePort.cpp:17-26`).
  The parent applies the commands in `NativeLayerRootRemoteMacParent::HandleChangedSurface`
  (`NativeLayerRootRemoteMacParent.mm:200-215`) → `NativeLayerCA` → `mContentCALayer.contents = surface`
  (`NativeLayerCA.mm:2037,2062`). Readback is allowed only in automation (`NativeLayerRootRemoteMacParent.mm:92-95`)
  and is excluded.
- The window side: `nsCocoaWindow::Create` builds `ChildView` and `NativeLayerRootCA::CreateForCALayer(rootCALayer)`
  (`widget/cocoa/nsCocoaWindow.mm:5183-5195`). Endpoints are set up in `GetCompositorWidgetInitData` (`:913-982`).
  Occlusion pauses the compositor (`DispatchOcclusionEvent`, `:7286-7336`). Inactive windows gate left clicks
  (`:4980-5025`).
- Input primitives:
  - `nsCocoaWindow::SynthesizeNativeMouseEvent` (behind `windowUtils.sendNativeMouseEvent`) **moves the user's real
    cursor** (`CGWarpMouseCursorPosition`, `nsCocoaWindow.mm:420,546`). Unusable for embedding.
  - `windowUtils.sendMouseEvent` / `nsContentUtils::SendMouseEvent` builds trusted `WidgetMouseEvent`s
    (`dom/base/nsContentUtils.cpp:10483-10551`), optionally through APZ (`DispatchInputEvent`).
  - `nsITextInputProcessor` produces trusted key and composition events.
  - macOS edit commands (Option+Backspace, Cmd+Arrow, Emacs bindings) are looked up only from a real `NSEvent`
    keyDown (`widget/cocoa/NativeKeyBindings.mm:199-219`). TIP or WidgetEvent keys lose them. Replayed NSEvents go
    through that path, so [INFERENCE] they keep them. The probe verified only plain keys; edit commands were not tested.
- Headless: `MOZ_HEADLESS` uses the process-wide `HeadlessCompositorWidget` → `RenderCompositorSWGL`, which is CPU
  rasterization (`RenderCompositor.cpp:203-214`).

## 3. Options

### A. Gecko embedded-window exporter: IOSurface over Mach + Avalonia external image (recommended)

- **Shape.** Ombra starts the shared Nightly runtime with an embed bootstrap name. It calls `bootstrap_check_in`, which
  works unsandboxed and was proven by the probe. The Nightly parent looks the name up, then authenticates the peer by
  the audit token in the Mach trailer and its code signature. Nightly opens one chrome-less top-level `nsCocoaWindow`
  per Ombra browser pane, flagged *embedded*:
  - single-surface WebRender for that widget only;
  - the NSWindow is never ordered on screen; visibility and occlusion follow Ombra's pane, not AppKit;
  - activation and focus follow Ombra's focus, so the click gate in `WindowAcceptsEvent` is bypassed;
  - resize and DPI follow the pane size and scale.

  On each `HandleChangedSurface` for an embedded widget, the parent forwards
  `{surface send-right (first time per IOSurfaceID), surfaceId, seq, size, scale}` to Ombra. Ombra caches the
  `ICompositionImportedGpuImage` per IOSurfaceID (one import per pool surface, as in the probe) and calls `Update*`
  per frame. It acks `seq` so Gecko does not reuse a surface Ombra is still copying. Today SurfacePoolCA relies
  on CoreAnimation's use count, which does not cover Avalonia's copy.
  Popups (selects, autocomplete, XUL panels) are separate widgets and become extra exported surfaces positioned by
  Ombra. Set `widget.macos.native-context-menus=false` so context menus are Gecko-drawn and exportable.
  Control (tabs, navigation, DOM, agent) stays on the existing actor and bridge channel.
- **Input path (trusted).** Ombra forwards raw macOS events as `CGEventCreateData` blobs: keys, mouse, scroll with
  phase and momentum, magnify. Nightly recreates them with `CGEventCreateFromData` → `NSEvent eventWithCGEvent:`,
  rewrites the window number and location, and dispatches into the embedded `ChildView`/`TextInputHandler`.
  The probe proved this route with `NSEvent`s it built itself: `isTrusted=true` and no cursor warp. Not tested yet:
  `CGEventCreateData` serialization, scroll phases, edit-command bindings and IME.
  IME: Ombra owns the OS text input context. It forwards `setMarkedText/insertText/unmarkText` to the widget's
  `IMEInputHandler` (or `TextEventDispatcher`). Caret and candidate rectangles come back via query-content events.
  "Open in separate window" re-parents the same tab into a normal window (`gBrowser.replaceTabWithWindow`-style
  swap), so DOM and forms are kept.
- **Gecko patch size** [INFERENCE, estimate]: about 600–1 200 C++/ObjC lines:
  - `widget/cocoa` (embedded flag, visibility, occlusion, activation, event sink): about 300–500;
  - `gfx/layers` exporter and Mach channel with authentication: about 200–400;
  - `gfx/webrender_bindings` (per-widget native-compositor switch in `RenderCompositorNative::ShouldUseNativeCompositor`
    and `CompositorType`): about 10;
  - chrome JS for the embed window and actor: about 200–300.

  No IPDL change is needed if the exporter lives in the parent.
- **Rebuild cost.** These are C++ changes, so not `./mach build faster`. An incremental `./mach build` recompiles a few
  unified sources in three directories and relinks XUL. Time on this machine is unmeasured; [INFERENCE] minutes if
  the objdir is current (non-LTO dev mozconfig), not hours. Release builds: the normal full build.
- **Latency and fps.** Measured with the hook, which is the same data path: Gecko frame present → Avalonia snapshot is
  p50 7–8 ms, p95 10–14 ms at moderate load. Display adds at most one vsync. Full Gecko frame rate (160 fps observed).
  Expected input-to-present: Gecko's own plus about 1 frame.
- **Risks.**
  1. **Ombra's OpenGL backend costs 36–41 % of a core at 60 fps and about 90 % at 160 fps** for one 2560×1600 pane.
     Mitigations:
     - switch Ombra to Metal: 8.5–10 %, but re-test the resize and popup bugs AvaloniaUI/Avalonia#22215 and #22297
       that made Ombra pick GL;
     - cap embedded presentation to the pane's needs, e.g. 60 fps, at the exporter;
     - patch Avalonia to reuse the snapshot texture.
  2. Metal needs real cross-process GPU sync (`MTLSharedEvent`, whose handle crosses processes via XPC
     `MTLSharedEventHandle`), or Gecko must wait for GPU completion before sending, which costs Gecko-side latency.
  3. Single-surface WebRender in embedded windows gives up CA video overlays and low-power video. Measure with
     video before deciding.
  4. Mach channel lifetime and crash recovery, surface reuse, and resize races.
  5. Accessibility: the embedded NSWindow's accessibility tree is not attached to Ombra's window. A separate AX bridge
     is needed.
  6. Windows/Linux need their own exporters (DXGI shared handle / DMA-BUF), as noted in ARCHITECTURE_SOLUTIONS §2.
- **First proof** — done; see §1a (the patch replaced the hook).

### B. Headless Gecko + CPU frames (compatibility mode only)

- **Shape.** `MOZ_HEADLESS=1` → `HeadlessCompositorWidget` + SWGL software WebRender into a CPU buffer. Patch the
  headless compositor widget to rasterize straight into IOSurface-locked memory (zero-copy for Ombra's GPU), or into
  shared memory uploaded as a `WriteableBitmap`.
- **Gecko patch** [INFERENCE]: about 150–300 lines in `widget/headless` plus the same Mach channel as A. Rebuild cost as A.
- **Latency and fps** [INFERENCE, unmeasured]: SWGL at 2560×1600 is CPU-bound, and complex pages and animation cost
  several cores. Expect lower than display rate, plus about 1 frame for upload. The shared-memory variant adds a
  16 MB copy per frame.
- **Input.** `HeadlessWidget::SynthesizeNative*` dispatches widget events directly: trusted, no cursor warp. But there
  are no native key bindings, and IME must be done through `nsITextInputProcessor`.
- **Risks.** Headless is process-global (`gfxPlatform::IsHeadless`). The shared runtime then cannot also show a
  standalone window, which breaks D4's "open the same tab in a separate window" and the "one profile owner" rule.
  No GPU acceleration for WebGL or video. ARCHITECTURE_SOLUTIONS §2 allows CPU frames only as an explicitly labelled
  compatibility mode.
- **First proof (< 1 day).** Run `MOZ_HEADLESS=1 nightly --screenshot`-style rendering of the test page at
  2560×1600. Measure SWGL frame time with the CSS animation via Gecko profiler markers. Pass when it renders above
  30 fps at under 2 cores. Otherwise drop B except as a fallback.

### C. Remote CoreAnimation layer hosting (CAContext → CALayerHost in an NSView inside Ombra)

- **Shape.** For an embedded window, the Nightly parent puts the window's `NativeLayerRootCA` root CALayer into a
  `CAContext` (`contextWithCGSConnection:CGSMainConnectionID()`) and sends the 32-bit `contextId`. Ombra hosts a
  `CALayerHost(contextId)` layer in an NSView placed with `NativeControlHost` over the pane.
  Chromium uses the same private API for its GPU→browser display
  ([remote_layer_api.h](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/ui/base/cocoa/remote_layer_api.h));
  Electron disables it only for Mac App Store builds ([electron#20965](https://github.com/electron/electron/pull/20965)).
- **Gecko patch** [INFERENCE]: about 150–300 lines (CAContext creation, layer re-rooting for embedded widgets,
  contextId export). Input, focus and visibility patches are the same as A. Rebuild cost as A.
- **Latency and fps** [INFERENCE]: zero copy. WindowServer composites Gecko's CA tree directly, so full native-compositor
  features stay (tiles, video overlays, low power) and there is **no Avalonia per-frame cost** — the opposite of
  A's main risk.
- **Input.** Same as A, because events land in Ombra's NSView/Avalonia first and are forwarded.
- **Risks.**
  - Private API: not App Store compatible, can break on OS updates.
  - Airspace: Avalonia cannot draw over the hosted view. Ombra already lives with this for today's WKWebView
    preview (BROWSER_PLAN "airspace: web view hidden behind the page's snapshot").
  - ARCHITECTURE_SOLUTIONS §2 allows native view hosting only if the surface path fails UX.
  - Not verified visually today: this shell has no Screen Recording permission (`screencapture` → "could not
    create image from display"), and in-process readback cannot see WindowServer-composited remote layers.
- **First proof (< 1 day, no rebuild).**
  1. Extend the probe dylib: create a `CAContext`, re-root the Gecko window's layer into it, and send `contextId`.
  2. In the consumer, add a `NativeControlHost` NSView with a `CALayerHost`.
  3. Verify by eye or with a screenshot taken by a user who has Screen Recording permission.
  4. Measure probe CPU, which should be about 0 % at 160 fps.

## 4. Recommendation

Proceed with **A** as the product path. Run the day-1 proof of **C** in parallel only as a performance and fallback
comparison. Keep **B** as a labelled compatibility mode. Before K1 integration, decide Ombra's macOS backend:
- either re-test Metal against #22215 and #22297 (Metal's import cost is about 4× lower);
- or accept GL with a capped embedded frame rate and an upstream fix to Avalonia's per-update texture allocation.

The DYLD hook from §1 was a measurement tool only; it has been deleted and must not ship.

## 5. Reproduce

```
cd .temp/K1Spike/embed-probe
swiftc -O winid.swift -o winid && swiftc -O wins.swift -o wins
~/.dotnet/dotnet build -c Release
# profile with zen.embed.allow-unsigned-peer=true, browser.startup.page=0, resume_from_crash=false
K1_NIGHTLY=<Nightly.app>/Contents/MacOS/nightly K1_PROFILE=<profile> K1_URL=file://<page3.html> K1_WINS=$PWD/wins \
~/.dotnet/dotnet bin/Release/net10.0/EmbedProbe.dll <gl|metal> 10 <dir>/shot.png
python3 .temp/K1Spike-2/measure.py <dir>/shot-*.png
```

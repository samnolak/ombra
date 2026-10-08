# omp 18.8.0 browser tool vs the OMP GUI built-in browser

Read-only research. Source root (abbreviated `S/`): `~/Library/Application Support/OmpGui-Test/runtimes/omp-18.8.0-bun-1.4.2/omp/node_modules/@oh-my-pi/pi-coding-agent/src/`.
Browser code lives in `S/tools/browser.ts` (abbrev. `B`) and `S/tools/browser/` (abbrev. `BR/`). GUI side: `avalonia/src/OmpGui.ClientCore/AgentBrowserBridge.cs` (`AB`), `AgentBrowserScripts.cs` (`AS`), `avalonia/src/OmpGui.App/ViewModels/PreviewAgentPage.cs`, `App.axaml.cs:48-86`.
The complete Tern wire spec is in **local://omp-tern-protocol.md**. Everything there and here comes from the source at the cited lines. Inferences are marked [INFERENCE].

## TL;DR

1. **The browser is not a tool. It is an `eval` prelude.** The model gets a global `browser` object inside the JS/Python `eval` tool (`BR/prelude-definition.ts:13-28`; `S/sdk.ts:2375-2395`). The eval tool description carries only the first doc line, "Drive real Chromium tabs from JavaScript or Python Eval with the global `browser` object.", and points to `xd://eval/browser` for the full doc (`S/tools/eval.ts:248-273`, `S/eval/preludes.ts:73-77`). The prelude disappears if `browser.enabled=false`, if `eval` is not an active tool, or if the tool set is restricted (`S/sdk.ts:2376`).
2. **Backend precedence** (`B:159-227`): `app.cdp_url` / `app.path` → explicit `app.relay` → `app.tern:true` → relay setting → **`browser.cdpUrl`** → **Tern** (`TERN_PANE_SOCKET` + numeric `TERN_PANE`) → **cmux** (`CMUX_SOCKET_PATH`) → managed Chromium (headless by default). Our bridge sits at the cmux step, so anything earlier in that chain beats it.
3. **cmux is the weakest backend omp has.** In `BR/cmux/cmux-tab.ts` about 50 helpers throw "not supported on the cmux backend": dialogs, frames, init scripts, downloads, cookies write, emulation beyond viewport, clipboard, routes, HAR, pdf, tracing, recording, React and WebMCP. Several open options are silently ignored: viewport, user_agent, init_scripts, downloads, ignore_https_errors, dialogs. `setViewport` is a no-op (`cmux-tab.ts:511-517`). All input is synthetic DOM events (`isTrusted=false`), dispatched by omp's own scripts or by our bridge's scripts. With cmux we **cannot** meet requirements 2 (dialogs, popups, file pickers, downloads, HTTP auth), 3 (device emulation driven by the agent) or 4 (isTrusted, no bot tells). This is a protocol limit, not a bug in our bridge.
4. **Tern is the right target.** It is a WKWebView host protocol that omp already speaks, and it ranks above cmux. It has trusted native input (`input` op), `dialogs`/`dialog`, `files` (native chooser preset), `downloads`, `credentials` (HTTP auth), `agent` (UA), `viewport`, `appearance`, `cookies`, `capture` with rect/full-page, `pdf`, `events` (url/title/dialog/download/console/network), isolated-world eval with Promise await and frame paths, and multiple tabs (blocks). omp's model-facing doc already describes Tern tabs correctly: "native WKWebView, visible to the user… input is trusted native mouse/keyboard events" (`S/prompts/tools/browser.md:48-49`). Implementing a Tern host in the GUI (`TERN_PANE_SOCKET`, `TERN_PANE=<n>`, `PI_BROWSER_TERN=1`) is the main recommendation. Keep cmux as a fallback during migration. On Windows a WebView2 CDP endpoint + `browser.cdpUrl` is a strong alternative (full Puppeteer path) (§5.3).
5. **Defaulting without patching omp:** per-process env vars, plus a GUI-owned `PI_CONFIG_FILES` overlay (official settings overlay, `S/config/settings.ts:661-663, 2461-2497`), plus a GUI-owned extension (`--extension`) whose `before_agent_start` appends a short system-prompt note (`S/extensibility/extensions/types.ts:845-852, 1412`). Avoid `--append-system-prompt`: it replaces the user's discovered `APPEND_SYSTEM.md` (`S/main.ts:1348`).

---

## (a) Backend selection, defaults, why our bridge is skipped, how to make it the default

### Settings (all in `BR/settings.ts`)
| key | default | env override | meaning |
|---|---|---|---|
| `browser.enabled` | `true` (`:7-17`) | – | registers the `browser` eval prelude |
| `browser.cdpUrl` | unset (`:19-30`) | **none** | attach to an HTTP CDP discovery endpoint; beats Tern and cmux |
| `browser.relay` | `false` (`:32-43`) | `PI_BROWSER_RELAY=0/1` (`BR/relay/kind.ts:21,36`) | drive the user's Chrome through the relay extension |
| `browser.relayUrl` | `http://127.0.0.1:9224` (`:45-54`) | – | |
| `browser.headless` | `true` (`:56-66`) | – | Chromium display mode; per-open `headed` overrides it (`B:230-238`) |
| `browser.cmux` | `true` (`:68-79`) | `PI_BROWSER_CMUX=0/1` (`BR/cmux/rpc.ts:183-200`) | use the cmux socket when `CMUX_SOCKET_PATH` is set |
| `browser.tern` | `true` (`:81-92`) | `PI_BROWSER_TERN=0/1` (`BR/tern/kind.ts:41-49`) | use Tern when `TERN_PANE_SOCKET` + `TERN_PANE` (`^\d+$`) are set (`kind.ts:22-29`) |
| `browser.freezeOnTurnEnd` | `true` | – | freezes OMP-owned *headless* tabs at turn end |
| `browser.idleCloseSec` | `1800` | – | closes idle headless tabs **and Tern PiPs**; not cmux |
| `browser.screenshotDir` | unset | – | full-res screenshot destination |

`parseFlag(env, setting)` means the env var wins in **both** directions: `PI_BROWSER_CMUX=1` forces cmux on even if the user set `browser.cmux:false`.

### Resolution (`B:153-238`)
`resolveBrowserKind` order:
1. `app.cdp_url` → `connected` (`B:164-166`).
2. `app.path` → `spawned` (`B:167-177`).
3. explicit `app.relay`.
4. `app.tern:true` (throws outside Tern, `B:186-194`).
5. relay setting / `PI_BROWSER_RELAY` (`B:199-208`).
6. `browser.cdpUrl` (`B:209-212`).
7. Tern auto (`B:213-216`).
8. cmux (`B:217-225`).
9. `chromiumKind` → `{kind:"headless", headless: params.headed===undefined ? browser.headless : !headed}` (`B:230-238`).

If an auto-chosen Tern open fails with `no_window|unsupported|connect`, omp falls back to Chromium and adds a note to the result (`B:342-376`). A tab name already bound to another backend fails with "Close it first" (`B:392-397`). The open result names the backend, e.g. `Opened tab "main" on cmux browser (<surface>)` (`B:505-512, 609-628`).

### Why omp does not use our cmux bridge today (checklist)
- **A higher-precedence mode is configured in the user's omp config:** `browser.relay:true` or `browser.cdpUrl`. Both beat cmux, and `cdpUrl` has no env override.
- **The user's config has `browser.cmux:false`.** The bridge does not set `PI_BROWSER_CMUX=1` (`AB:170-183`).
- **The agent picks something else.** The `read` tool doc says "Use `read` for static web; browser only if needed" (`S/prompts/tools/read.md:1`). The browser doc says "Static content? Use `read`" (`browser.md:4`). The prelude summary promises "real Chromium tabs" (`browser.md:1`). Nothing tells the model the GUI shows the page to the user, so it also uses `web_search`, `bash curl` or `bash open <url>` (the system browser). The system prompt mentions the browser only for UI verification (`S/prompts/system/system-prompt.md:215-217`).
- **The agent passes `app.path` / `app.cdp_url` / `app.relay`.** These beat cmux by design. `headed:true` does not.
- **The prelude is absent.** That happens when `browser.enabled:false`, when eval is inactive or restricted (`S/sdk.ts:2376`), or with `--no-tools` / `--tools` lists without eval.
- **The user's GUI environment settings already contain `CMUX_SOCKET_PATH`.** The bridge then steps aside (`AB:178`).
- **One bridge, one page.** A single `_surface` serves every omp process the GUI starts: RPC session plus the TUI terminal (`App.axaml.cs:50,63`). A second `browser.open_split` (another tab name, or another omp process) replaces the first, and later requests for the old surface fail with "This tab was replaced by a newer one" (`AB:423-438`).

### How to make the in-app browser the default for every session (official mechanisms only)
1. **Env per omp process** (already done by `AddTo`). Add the force flag:
   - Tern: `TERN_PANE_SOCKET=<sock>`, `TERN_PANE=<numeric id>`, `PI_BROWSER_TERN=1`.
   - cmux fallback: `CMUX_SOCKET_PATH`, `CMUX_SOCKET_PASSWORD`, `PI_BROWSER_CMUX=1`.
   - Keep removing `CMUX_WORKSPACE_ID/SURFACE_ID/RELAY_*` (`AB:84,182`). If both env sets are present, Tern wins (`B:213-225`).
   - `PI_BROWSER_RELAY=0` neutralises the relay, but only when the user chose "Use OMP GUI browser". The relay is a deliberate user mode.
2. **Settings overlay** (no edits to the user's config.yml). Write a GUI-owned YAML file and append it to `PI_CONFIG_FILES`, `path.delimiter`-separated, keeping any existing value (`S/config/settings.ts:661-663`; overlays load after global/project, `:2461-2497`; `config-cli.ts:352` confirms an overlay overrides config for the process). Example, only when the user opts in:
   ```yaml
   browser:
     enabled: true
     cdpUrl: ""            # neutralise a user CDP default ("" is falsy at B:209-212)
     tern: true
     cmux: true
     idleCloseSec: 0       # optional: don't reap the visible page after 30 min
   ```
   `--config <file>` is equivalent (`S/cli/flag-tables.ts:116`), but the GUI already uses its own `--config` for itself (`Program.cs:42`). Env is cleaner.
3. **Prompt guidance without patching.**
   - Ship a GUI-owned extension, passed with `--extension <path>` (`flag-tables.ts:222`; merges with discovered extensions unless `--no-extensions`). Its `before_agent_start` handler returns `systemPrompt` additions (`types.ts:845-852, 1297-1298, 1412`). Suggested text: "The `browser` eval object drives the OMP GUI's built-in browser, which the user sees live. For any web page the user should see or interact with (logins, previews, localhost apps) use `browser.open`, never `bash open`/system browser. Keep one tab named `main` and pass `persist: true` for multi-step flows."
   - On cmux only, also add: "prefer `tab.ariaSnapshot()` over `tab.observe()`; dialogs/emulation/downloads are unavailable".
   - Do **not** use `--append-system-prompt`: it replaces the user's discovered `APPEND_SYSTEM.md` (`S/main.ts:1348`) and changes the fork cache shape (`main.ts:1383-1386`).
4. **Alternative: an RPC host tool.** `set_host_tools` (`S/modes/rpc/rpc-types.ts:67, 770-789`; `rpc-mode.ts:2131-2135`) registers GUI-executed tools, with `loadMode: "essential" | "discoverable"`. The GUI could expose its own `gui_browser` tool with its own description. Downsides: it duplicates omp's rich helpers, it competes with the `browser` prelude, and it is not available in the TUI process. Not recommended as the primary path; useful only for GUI-only actions such as "show this URL to the user".

---

## (b) The cmux protocol omp speaks, and our bridge's conformance

### Transport (`BR/cmux/socket-client.ts`)
- **Address.** A Unix socket path, or `127.0.0.1:<port>` / `localhost:<port>` meaning a TCP relay with an HMAC challenge (`:181-189`, `:217-265`). On Windows omp passes the path to `net.createConnection({path})` (`:150-152`); `\\.\pipe\…` working in Bun is [INFERENCE].
- **Auth.** If `CMUX_SOCKET_PASSWORD` is set, the first line is `auth <pw>` (10 s). Any reply starting `ERROR:` fails the connection, unless it contains `Unknown command 'auth'` (`:159-164`). Our bridge answers `OK` / `ERROR: Access denied` (`AB:262-269`).
- **Framing.** Newline-delimited JSON. Request: `{"id":<uuid>,"method":M,"params":P}`. Response: `{"ok":true,"result":R}` or `{"ok":false,"error":{"code","message","details?"}}`. A line starting `ERROR:` is also an error (`:304-306, 383-405`). omp shows errors as `code: message[ details=…]` (`:62-67`).
- **Serialisation.** One request at a time per connection; requests queue (`:293-322`). One client per socket path is shared by every tab of one omp process (`BR/registry.ts:180-188`).
- **Timeouts.** Connect 10 s; requests default to 30 s (`:7-8`). Callers pass the run's `timeoutMs` for navigate/wait/screenshot. On timeout the client **destroys the socket** ("desync", `:337-347, 431-436`) and reconnects on the next request (re-auth).
- **Every request carries `surface_id`.** `cmux-tab.ts:1684-1695` adds it.

### Methods and shapes omp uses
| method | params | omp reads from result | call sites |
|---|---|---|---|
| `browser.open_split` | `{url:"about:blank"|url, focus:false, workspace_id?, surface_id?}` | `surface_id` (required), `url` | `BR/tab-supervisor.ts:621-631`; then `browser.wait {surface_id, load_state, timeout_ms}` if `url` (`:632-642`) |
| `surface.close` | `{surface_id}` | – | tab close/rollback (`tab-supervisor.ts:677-679`) |
| `browser.navigate` | `{url}` | `url` | `cmux-tab.ts:752-766` |
| `browser.wait` | `{load_state:"interactive"|"complete"}` / `{selector}` / `{url_contains}` + `timeout_ms` | – | goto/back/forward/reload/waitFor/waitForUrl (`:758, 812, 1188, 1562, 1994`); `mapWaitUntil` maps only `domcontentloaded` → `interactive`, everything else (incl. `networkidle*`) → `complete` (`rpc.ts:174-176`) |
| `browser.url.get` | `{}` | `url` | readyInfo, waitForUrl/Navigation polling (`:567, 859, 1189-1201, 1542-1556`) |
| `browser.eval` | `{script}` | `value` | almost every helper. omp's own envelope (`rpc.ts:137-170`) returns `{__ompOk}` / `{__ompErr}` / `{__ompPromise:true}` and **refuses Promises page-side**, so async `tab.evaluate` can never work on cmux whatever the host does |
| `browser.snapshot` | `{interactive:boolean, max_depth:12}` (observe) or `{interactive:false}` (extract) | `refs{eN:{role,name}}`, `url`/`page.url`, `title`/`page.title`, `page.html` (extract) | `cmux-tab.ts:820-847, 1049-1065`; `rpc.ts:77-123` |
| `browser.click`/`dblclick`/`hover`/`focus`/`check`/`uncheck`/`scroll_into_view` | `{selector}` (CSS or `@eN`) | – | `cmux-tab.ts:1819-1854` |
| `browser.type` / `browser.fill` | `{selector, text}` (`type` appends, `fill` replaces) | – | same |
| `browser.press` | `{key}` (Puppeteer names, `Mod+Key`) | – | `:999-1005` |
| `browser.scroll` | `{dx, dy}` | – | `:976-978, 1007-1013` |
| `browser.screenshot` | `{}` | `png_base64` (required); width/height unused | `:1811-1817`. omp resizes to ≤1024 px / 150 KB and saves (`:1067-1178`) |

Native selectors are **only** CSS and `@eN` refs (`cmux-tab.ts:2134-2138`). Every other selector kind (text/aria/xpath/pierce/label/role/aria-ref…) goes through `browser.eval` with omp's `PAGE_SELECTOR_HELPERS` (`:227-373`, `:1856-1987`). So `tab.select`, `tab.uploadFile`, `keyDown/keyUp`, `mouseMove/Down/Up`, `clickAt`, `drag`, `highlight` and every semantic-selector action **never reach the bridge's action code**. They are synthetic `dispatchEvent` scripts that omp writes itself (`:897-975, 1577-1612`).

### Helpers refused on cmux (throw before any request)
- Emulation and devices (only viewport, and `setViewport` only records the value, `:511-534`), `devices`, all clipboard helpers.
- `setCookies`, `clearCookies`, `saveState`, `loadState`.
- `frames`, `frame`, `dialog`, `handleDialog`, `setDialogs`.
- `observe({selector})`, screenshot `annotate` / `ifChanged` / `jpeg`, `pdf`.
- `addInitScript` / `removeInitScript` / `initScripts`, `waitForDownload` / `downloads`.
- `route` / `unroute` / `routes`, `harStart` / `harStop`, `allowedDomains`.
- `webmcp*`, `react*`, `traceStart/Stop`, `profileStart/Stop`, `recordStart/Stop/Restart`, `recording`.
- Element clicks with non-left button or count≠1 (`:2214-2224`).
- `allowed_domains` at open (`tab-supervisor.ts:607-609`).

Silently ignored at open: `viewport`, `user_agent`, `init_scripts`, `downloads`, `ignore_https_errors`, `dialogs` (`acquireCmuxTab` never uses them, `tab-supervisor.ts:602-682`). Cookies are read only from `document.cookie` (no HttpOnly), current origin only (`:625-670`).

### What `observe` loses on cmux
`cmuxSnapshotToObservation` keeps only `{id, role, name}` and sets `states: []` (`rpc.ts:77-95`). The snapshot text, which our bridge enriches with `[checked]`, `[disabled]`, `[expanded]`, values, levels and hrefs (`AS:144-194`), is **discarded**. On Chromium, `ObservationEntry` also has `value`, `description`, `keyshortcuts` and `states` (`BR/tab-protocol.ts:5-28`). `tab.ariaSnapshot()` does work on cmux: it runs Playwright's ARIA bundle in the page world through `browser.eval` (`cmux-tab.ts:849-863`) and gives YAML with states and `[ref=eN]`. On cmux, the model should prefer it.

### Our bridge vs the protocol (`AB:308-407`, `AS`)
Implemented and correct: `system.ping`, `browser.open_split`, `surface.close`, `browser.navigate`, `browser.url.get`, `browser.eval` (expression, falling back to statements via indirect eval; `{value}`), `browser.wait` (all three forms), `browser.snapshot` (`refs`, `page.html` when `!interactive`), all 9 selector actions with `@eN` refs, `browser.press` (combos, Enter submits, Tab moves focus), `browser.scroll` (window, or the scrollable box under the centre), `browser.screenshot` (`png_base64`, width, height, url). Errors use cmux's `code: message` shape.

Gaps and bugs:
1. **Timeout race.** `browser.wait` loops until `elapsed >= timeout_ms` (`AB:514-536`). omp's socket timer uses the same value (`cmux-tab.ts:758-761` passes `timeoutMs` for both), so the bridge's clear `timeout:` error often arrives after omp has already destroyed the socket ("Timed out waiting for cmux socket response", reconnect). Answer about 250 ms before `timeout_ms`, and apply the same rule to any long eval.
2. **Single surface for all omp processes and tabs** (`AB:410-438`). A second tab name or a second omp process (RPC + TUI) kicks out the first. Needs per-connection, per-surface state, at least one surface per omp connection. Tern handles this natively with blocks.
3. **Synthetic input only** (`AS:195-404`). Gives `isTrusted=false` and **no user activation**. So popups opened from an agent click (`window.open`, OAuth) are blocked, `input[type=file].click()` and `showPicker()` do nothing, clipboard/fullscreen/media autoplay gestures fail, and bot detectors see untrusted events. This cannot be fixed inside the cmux protocol, because omp sends selectors, not coordinates, for most actions. The bridge *could* resolve the selector to a point and inject native events itself; that is a GUI-side improvement, and Tern does exactly this.
4. **Page-world footprint.** `globalThis.__ompAgent` (`AS:405`) plus omp's own page-world patches on every open/goto: `fetch`/`XMLHttpRequest` replaced (`cmux-tab.ts:375-469, 578, 765`) and `console.*` wrapped (`BR/console-capture.ts:471-508`). All are visible to pages (`fetch.toString()` is not native). The bridge could keep its library in a WKContentWorld, but the omp patches are inherent to cmux. Tern has the same trade-off for capture (`BR/tern/page-capture.ts:9-10`), but its kit lives in an isolated world.
5. **No Promise support.** `RunScriptAsync` maps to `NativeWebView.InvokeScript`, i.e. synchronous evaluate in the page world (`PreviewPanel.axaml.cs:179-183`). That matches cmux (omp refuses Promises anyway). Tern needs `callAsyncJavaScript` with content worlds and frames.
6. **Dialogs block the agent.** When the page shows `alert/confirm/prompt` and the GUI shows a modal, every later `InvokeScript` waits. cmux has no dialog channel, so omp cannot see or answer it, and the agent times out. Tern has `dialog` events plus `dialogs`/`dialog` ops.
7. **Viewport/UA/emulation from the agent is impossible on cmux** (see above). The GUI's own device presets are invisible to omp except through `innerWidth`/`dpr` in `GEOMETRY_SCRIPT` (`rpc.ts:73-75`).
8. **`open_split` url handling.** `about:blank` shows an empty preview. Otherwise it navigates and returns immediately, and omp then sends `browser.wait`. Fine. `focus:false` is ignored (fine).

---

## (c) omp's own Chromium path (what to match)

### Stealth / anti-detection (`BR/launch.ts`)
- **Launch flags** (`:501-533`): `--no-sandbox`, `--disable-setuid-sandbox`, `--disable-blink-features=AutomationControlled`, `--hide-scrollbars`, `--window-size=W,H`, optional `--ignore-certificate-errors`, `--allow-file-access-from-files`, `PUPPETEER_PROXY*`.
- **Removed Puppeteer defaults** (`:51-73, 86-89`): `--enable-automation` (the webdriver flag and the infobar; kept for Edge), `--disable-extensions`, `--disable-default-apps`, `--disable-component-extensions-with-background-pages`, `--disable-popup-blocking` (so popup blocking stays **on**), `--disable-client-side-phishing-detection`, `--allow-pre-commit-input`, `--disable-ipc-flooding-protection`, `--metrics-recording-only`.
- **Binary.** Chrome for Testing is preferred over system Chrome on macOS, to avoid stealing LaunchServices URL events (`:232-313`). Each launch gets a fresh temp profile (`:556-560`).
- **UA override** through CDP `Network.` and `Emulation.setUserAgentOverride` on every target (`:672-869`):
  - `HeadlessChrome/` → `Chrome/`; a Linux UA is rewritten to Windows.
  - Full `userAgentMetadata`: brands, fullVersionList, platform, platformVersion (real macOS version), architecture, bitness.
  - `acceptLanguage`.
- **14 page-world stealth scripts**, injected with `evaluateOnNewDocument` (`:893-1021`; files `S/tools/puppeteer/00…13_stealth_*.txt`, about 61 KB):
  - native-looking `toString` for patched functions;
  - `document.hidden=false`, `visibilityState="visible"`, focus;
  - hairline / offsetHeight;
  - `navigator.webdriver` removed, `window.chrome.{app,runtime,csi,loadTimes}` populated;
  - iframe `contentWindow` proxies;
  - WebGL vendor/renderer per platform;
  - `screen.*` consistency;
  - font list;
  - AudioContext latencies;
  - `navigator.language(s)=en-US`;
  - Chrome PDF `plugins`/`mimeTypes`;
  - `hardwareConcurrency`;
  - `canPlayType` codecs;
  - worker patching.
  These are **Chrome-specific** and applied only to the `headless` kind (`BR/tab-worker.ts:1493-1503`). They must **not** be copied into WKWebView/WebKitGTK: a Safari engine pretending to be Chrome is an inconsistent fingerprint. Connected, relay and spawned targets get no stealth.
- **Focus emulation.** `page.emulateFocusedPage(true)` keeps background tabs rendering (`tab-worker.ts:1518-1523`).
- **No human-like timing.** Typing uses `delay: 0` (`BR/interactions.ts:361`, `tab-worker.ts:764, 2148`); `drag` uses `mouse.move(…,{steps:12})` (`tab-worker.ts:2959-2963`). Realism comes only from CDP `Input.dispatch*` events, which are `isTrusted=true` and grant user activation.

### Tabs, popups, dialogs, downloads, upload, viewport, emulation (Chromium)
- **Default viewport** `1365×768 @1.25` (`launch.ts:42`). `applyViewport` (`:637-647`).
- **Dialogs.** `RuntimeDialogController` (`BR/dialogs.ts:20-113`). With no policy, `alert` and `beforeunload` are auto-accepted and `confirm`/`prompt` are held for `tab.handleDialog({accept,text})`. `dialogs:"accept"|"dismiss"` at open, or `setDialogs`, auto-settles. A held dialog is cleared when the main frame navigates.
- **Popups / `window.open`.** No handling at all. No `targetcreated` listener; a popup becomes a separate target that the agent can reach only with raw `browser.pages()` inside `tab.run`. Popup blocking is on.
- **Downloads.** `Browser.setDownloadBehavior {behavior:"allow", downloadPath, eventsEnabled:true}` when `downloads` is given (`BR/downloads.ts:67-71`), plus `waitForDownload` and `downloads()`.
- **File upload.** Clicks the element and accepts `page.waitForFileChooser({timeout:400})`, else uses a direct handle upload (`BR/interactions.ts:649-660`).
- **Emulation.** `BrowserEmulationController` (`BR/emulation.ts:114-201`):
  - Puppeteer `KnownDevices` via `page.emulate`: viewport, DPR, isMobile, hasTouch, UA.
  - viewport, userAgent, geolocation (+permission), network presets, offline, `prefers-color-scheme`, `prefers-reduced-motion`.
  - extra headers, HTTP basic `credentials` (`page.authenticate`), timezone, locale, CPU throttling.
  - clipboard with permission grants.
- **Lifecycle.** Tabs are named (default `main`). `persist:true` opts a tab out of freeze/idle-close. Idle tabs freeze at turn end and close after `idleCloseSec` (`B:277-298`; `browser.md:51`).

### Tern path (what omp already expects from a native WebKit host)
Full details are in local://omp-tern-protocol.md. Highlights:
- **Trusted input.** `input` op with mouse, wheel, key and text steps in viewport CSS px (`BR/tern/keys.ts`, `tern-tab.ts:556-559, 1145-1151, 1285-1365`).
- **Dialogs.** Policy `"default"` is sent on every open (`tern-tab.ts:761`); the agent handles them with `dialog` events and the `dialog` op.
- **File chooser.** A `files` preset plus a `chooser` event (`:1417-1440`).
- **Downloads.** A `downloads` dir plus `download` events (`:2290-2297`).
- **HTTP auth.** `credentials` op (`:2068-2075`).
- **UA, viewport, colour scheme, devices.** `agent`, `viewport`, `appearance` ops; device presets map to viewport + UA, and isMobile/hasTouch are not applied (`:2037-2063`).
- **Refused by omp itself** (never sent): timezone, headers, reducedMotion, CPU and network throttling, tracing (`:2016-2036, 2727-2744`).
- **Popups.** No handling. The host should keep them visible to the user. Loading them in the same view, or as a GUI-owned child window the user can finish (OAuth), is [INFERENCE].

---

## (d) What the model is told, and observe/snapshot/refs

- **Tool surface.**
  - The eval tool description lists the prelude with its one-line summary (`S/tools/eval.ts:263-273`). The full doc is `S/prompts/tools/browser.md` (77 lines), served at `xd://eval/browser` (`eval.ts:248-260`), or inlined when `inlineTopics` is set.
  - Mid-session enabling sends a hidden prelude notice (`S/session/session-tools.ts:1558-1566`).
  - Approval class `exec` (`prelude-definition.ts:24`).
  - The system prompt adds one verification line when the browser is enabled: "Web: `browser.open` tab, direct helpers for actions, `tab.run` for custom JS; visual proof; `tab.close`" (`system-prompt.md:215-217`).
- **Doc content** (`browser.md`):
  - API: `browser.open/tab/tabs/close`, open options, ~90 tab helpers by category (`:4-38`).
  - Selector engines.
  - "Navigation and re-renders invalidate observed ids and refs. Re-observe, then act in the same cell" (`:35`).
  - "Default to `tab.observe()`; use screenshots for visual confirmation" (`:74`).
  - Application modes: the doc describes Chromium, relay and **Tern** in detail (`:42-51`) and **never mentions cmux**. On our current bridge the model therefore assumes Chromium semantics and hits "not supported on the cmux backend" errors for dialogs, emulation, init scripts and so on.
  - Under Tern the doc matches reality, including "visible to the user" (`:48-49`).
- **`tab.observe()`.** Returns `{url,title,viewport,scroll,elements:[{id,role,name,value?,states[]…}]}` (`BR/tab-protocol.ts:5-28`).
  - Chromium: from the CDP AX tree; ids feed `tab.id(n)`, and stale ids raise "Run tab.observe() again" (`BR/tab-worker.ts:3038-3071`).
  - cmux: ids are the bridge's `eN` refs, which become `@eN` native selectors (`cmux-tab.ts:1637-1641, 2140-2150`). Role and name only.
  - Tern: the page kit mirrors the Chromium AX observe in an isolated world (`BR/tern/page-kit.ts:147-148, 180+`).
- **`tab.ariaSnapshot(selector?, {interactive?, compact?, urls?, diff?})`.** Playwright YAML with `[ref=eN]`. `tab.ref("e5")` resolves through `aria-ref=e5` (`cmux-tab.ts:865-871`). `diff` returns changes against the last baseline (`snapshot-plus.ts`). It is the richest text view on every backend.
- **Screenshots.** Resized to ≤1024 px and attached as images. On cmux they are viewport-only, and omp says so in the result (`cmux-tab.ts:1076-1092, 1155-1157`). Tern supports `rect` and full-page captures.

**Judgement.** Today, on cmux, the agent understands the page reasonably well through `ariaSnapshot` and screenshots, but `observe` is degraded and many documented helpers fail at runtime. With a Tern host, the doc the model already reads is accurate, `observe` is full fidelity, and actions are trusted native events. That is the shortest path to "works like a clock" without forking omp.

---

## Recommended plan (omp-side contract)

1. **Implement a Tern host in the GUI** (new `AgentTernHost`). Unix socket (named pipe on Windows [INFERENCE]), u32-LE frames, `hello` → `{"welcome":{"ops":[]}}`. Minimum ops: open, close, dialogs, scripts, goto, nav, viewport, events, state, eval (page + isolated worlds, Promise await, frame paths), input (native events), capture. Then files, downloads, credentials, agent, appearance, cookies, pdf, clipboard, edit. Add `webkit.messageHandlers.stencil` (page world) and `stencilFrame` (isolated world). One block per omp tab, shown as preview tabs. Env: `TERN_PANE_SOCKET`, `TERN_PANE=<n>`, `PI_BROWSER_TERN=1`.
2. **Keep the cmux bridge as a fallback for one release.** Fix the timeout margin (`AB:514-536`) and the single-surface model (`AB:423-438`), and set `PI_BROWSER_CMUX=1`.
3. **Opt-in "Use OMP GUI browser for the agent" switch** (default on). It writes the `PI_CONFIG_FILES` overlay (`browser.cdpUrl:""`, `browser.enabled:true`, optionally `idleCloseSec:0`) and adds `PI_BROWSER_RELAY=0`.
4. **GUI extension (`--extension`) with a `before_agent_start` system-prompt note:** the browser is visible to the user; use it for every page the user must see; never use `bash open`; use `persist:true` for logins.
5. **Windows option.** Start the preview WebView2 with `--remote-debugging-port=<random>` (bound to 127.0.0.1) and point `browser.cdpUrl` at it through the overlay. That gives the full Puppeteer helper set and trusted CDP input. Trade-off: any local process can attach to that port while it is open, and omp attaches to existing page targets without creating tabs (`BR/attach.ts:616-636`). Linux and macOS have no CDP, so they use Tern.
6. **Do not port omp's Chrome stealth scripts to WebKit.** For WKWebView, make the UA a genuine Safari one (`applicationNameForUserAgent = "Version/<n> Safari/605.1.15"`) [INFERENCE: the default WKWebView UA lacks the Safari token]. Keep omp/GUI globals out of the page world and use trusted input. Details belong to the anti-detection slice.

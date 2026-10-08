# Built-in browser — reference implementations & best practices

Scope: what mature agent browsers do (Claude Code Desktop Browser pane, Claude in Chrome, Anthropic `browser_toolset`, Codex/ChatGPT in-app browser, cmux, omp's own backends, Playwright MCP, Chrome DevTools MCP, browser-use, Stagehand), turned into concrete features, UX and an agent-tool contract for the OMP GUI preview (Avalonia 12 `NativeWebView`: WKWebView / WebView2 / WebKitGTK).
Companion file with deep Codex internals (file:line links into the decompiled app): **local://codex-browser-internals.md**.
Verified 2026-10-08. Anything not observed in a source is marked `[INFERENCE]`.

---

## 0. Key findings (read first)

1. **The example.com "window that never appears" is the HTTP Basic auth dialog.** `curl -sI https://studio.example.com` → `HTTP/1.1 401 Unauthorized`, `Server: nginx/1.24.0 (Ubuntu)`, `WWW-Authenticate: Basic realm="Studio"`. It is not an OAuth popup. WKWebView has **no default credential UI**. The app must implement `WKNavigationDelegate webView(_:didReceive:completionHandler:)` for `NSURLAuthenticationMethodHTTPBasic`/`HTTPDigest` and show its own sheet. cmux does exactly this: `BrowserHTTPBasicAuthPromptCoordinator.swift` (§5). omp's Tern backend already sends a `credentials` op (`tern-tab.ts:2069`).
2. **On macOS, WKWebView gives nothing by default.** Without the app's own delegates there are no alerts, confirms or prompts, `window.open` is dropped, there is no `<input type=file>` panel, no downloads and no HTTP auth. Every item of requirement 2 is a delegate method the app must implement (matrix in §9). WebView2 shows most of these natively unless the host suppresses them. On WebKitGTK popups are blocked by default and permission requests are denied by default.
3. **There is a proven way to send trusted (`isTrusted === true`) input to WKWebView without Accessibility permission.** Build synthetic `NSEvent`s and call the WKWebView's own `mouseDown:`/`keyDown:`/`scrollWheel:` responder methods in-process. Text goes through `insertText`/marked text. That is how cmux's driver works ("WebKit turns the events into DOM events with `isTrusted === true`", `CmuxWebView+AutomationInput.swift:5-11`). omp's Tern backend says the same: "input is trusted native mouse/keyboard events at element centres". Injected JS `dispatchEvent` gives `isTrusted=false` and is detectable. On WebView2 the equivalent is CDP `Input.dispatchMouseEvent`/`dispatchKeyEvent`/`insertText` through `CallDevToolsProtocolMethodAsync`, which is what Codex does with `webContents.debugger`.
4. **Real mobile emulation is impossible with public WKWebView API.** There is no device pixel ratio (DPR) override, no touch emulation and no `isMobile`. What works on WKWebView: a logical CSS viewport projected into the pane (NSView `frame` = scaled display rect, `bounds` = CSS size, which is cmux's technique), `customUserAgent`, and `pageZoom`. Codex itself only emulates the viewport size, even on Chromium (`setDeviceMetricsOverride{deviceScaleFactor:1, mobile:false}`). Full emulation (DPR, `mobile:true`, touch, UA) is only possible on WebView2 via CDP `Emulation.*`.
5. **omp 18.8 has two native host-browser backends.** Both are selected by env vars only, so no fork is needed:
   - **cmux**: `CMUX_SOCKET_PATH`, `CMUX_SOCKET_PASSWORD`. The GUI already serves this one (`AgentBrowserBridge.cs:67-85`).
   - **Tern**: `TERN_PANE_SOCKET` + `TERN_PANE`; setting `browser.tern` (default true); override `PI_BROWSER_TERN`.

   omp's cmux client only uses ~18 `browser.*` verbs (navigate, click, fill, snapshot, screenshot…). The Tern op set also covers dialogs, file chooser, downloads, viewport, UA, credentials, cookies, trusted input steps and an event cursor (§6). Compare both before extending the cmux bridge.
6. **Every serious agent browser uses the same contract.** Each step has three parts:
   - **Observe:** an accessibility-tree text snapshot with stable element refs. Screenshots are a fallback for canvas and visual checks.
   - **Act:** on a ref, or on viewport coordinates.
   - **Report:** a short acknowledgement plus tab state, with specific error text: stale ref, timeout, refused scheme, modal open.

   Anthropic's `browser_toolset_20260801` is the cleanest published spec (§3). Copy its error semantics.

---

## 1. Claude Code Desktop — Browser pane

Sources: [code.claude.com/docs/en/desktop](https://code.claude.com/docs/en/desktop) (§Preview your app, §Browse external sites, §Keyboard shortcuts, §Configure preview servers); local notes local://ref-claude-code.md §1.14.

- **Tabbed browser pane** that can be dragged and resized like any other pane. Toggle: **Cmd/Ctrl+Shift+B** or the title-bar **Browser** button. **Cmd/Ctrl+Shift+S** = "Select an element in the Browser".
- **What it opens:** dev servers, static HTML, PDFs, images and videos. Clicking such a path in chat opens it in the Browser pane.
- **Header:** a **Dev servers** menu (start/stop each server, stop all).
- **⋮ menu:** **Keep cookies** (persist after quit), **Clear browsing data**, **Open links in built-in browser**, **Auto-verify changes**.
- **Auto-verify** (on by default): after every edit Claude "takes screenshots, inspects the DOM, clicks elements, fills forms, and fixes issues it finds". Configured in `.claude/launch.json`:
  - Server fields: `name`, `runtimeExecutable`, `runtimeArgs`, `port` (default 3000), `cwd`, `env`, `program`, `args`, `url`.
  - `autoPort`: `true` = find a free port, `false` = fail when the port is taken, unset = ask.
  - `"autoVerify": false` per project.
- **External links:** the first click in chat asks "Browser pane vs default browser". Cmd/Ctrl-click always goes to the default browser.
- **Sign-in:** "You can sign in to sites in the pane, **including popup sign-in flows such as Google OAuth**". So popups must keep `window.opener`.
- **Safety:**
  - A permission card appears the first time Claude acts on an external site: **Allow once / Always allow / Deny**. Approvals are per site, subdomains included, and revocable in Settings.
  - Localhost and project files need no approval.
  - Classifiers review write actions (click/type) on external pages in every mode.
  - Claude never purchases, creates accounts or bypasses CAPTCHAs.
  - Admin settings: `browserExternalPageTools`, `disableBrowserExternalNavigation`.
- **Profile:** "a clean browser profile, separate from your personal browser". Use Claude in Chrome when the task needs the user's logged-in identity.
- **Off switch:** **Settings > Claude Code > Browser tools**.

## 2. Claude in Chrome (extension) — driving the user's real browser

Sources: [code.claude.com/docs/en/chrome](https://code.claude.com/docs/en/chrome); [claude.com/claude-for-chrome](https://claude.com/claude-for-chrome).

- **Transport:** CLI ↔ **native messaging host** (`com.anthropic.claude_code_browser_extension.json` in `~/Library/Application Support/Google/Chrome/NativeMessagingHosts/` and per-browser equivalents) ↔ MV3 extension (Chrome, Edge, Brave, Arc, Vivaldi, Opera).
  - Tools appear as the `claude-in-chrome` MCP server.
  - Tools seen in docs: `tabs_context_mcp` (`createIfEmpty`), `browser_batch`, GIF recording, screenshot `save_to_disk`, file upload (≤10 MB, read-permission checked, hard links refused).
  - The full list is behind `/mcp → claude-in-chrome → View tools`. The same member names are published as the API toolset in §3.
- **Why it isn't flagged as a bot:** it is the user's own Chrome with real cookies, history and fingerprint. There is no `--enable-automation` and no WebDriver, so `navigator.webdriver` stays false. Input comes from the extension (`chrome.debugger` CDP `Input.*` → trusted) `[INFERENCE: mechanism not documented; consistent with tool set]`. The browser window is visible.
- **Tab group per session:** Claude opens new tabs inside a Chrome tab group tied to the session.
  - `/clear` closes the group.
  - Exit and `/resume` close it only if it holds nothing but empty new tabs.
- **Pauses at login pages and CAPTCHAs** and asks the user to handle them.
- **Known failure: modal JS dialogs.** "JavaScript dialogs block browser events and prevent Claude from receiving commands. Dismiss the dialog manually." A host must surface dialogs to both the user and the agent (§9), otherwise the agent hangs.
- **Service worker idle** breaks the connection. Fix: **/chrome → Reconnect extension**.
- **Permission prompts:**
  - "Claude in Chrome wants to …" with "allow all actions on this site for the session".
  - Plan mode prompts before GIF recording, new tab or shortcut.

## 3. Anthropic `browser_toolset_20260801` — the reference agent contract

Source: [platform.claude.com/docs/en/agents-and-tools/tool-use/browser-use-tool](https://platform.claude.com/docs/en/agents-and-tools/tool-use/browser-use-tool) (GA; the executor runs the browser, nothing runs on Anthropic's side).

**Members (27 default + 4 opt-in):**

| Group | Members |
|---|---|
| Navigation, capture | `navigate {url \| "back" \| "forward" \| "reload", tab_id?}`, `screenshot`, `zoom {region:[x0,y0,x1,y1]}` (cropped, upscaled) |
| Pointer | `left_click`/`right_click`/`middle_click`/`double_click`/`triple_click {target, modifiers?}`, `hover`, `left_click_drag {from,target}`, `left_mouse_down/up`, `mouse_move`, `scroll {target:coord, scroll_direction, scroll_amount 1-10 (default 3)}`, `scroll_to {ref}` |
| Keyboard, timing | `type {text}`, `key {text:"Enter" \| "ctrl+a" \| "Backspace Backspace", repeat 1-100}`, `hold_key {duration ≤30 s}`, `wait {duration ≤30 s}` |
| Page reading | `read_page {filter?: "interactive" \| "all", depth? (default 15), ref?}`, output capped at 50 000 chars with a note saying so; `find {query}` (natural language, ≤20 matches, same format); `get_page_text` (main content first) |
| Forms | `form_input {ref, value: string \| number \| bool}`; opt-in `file_upload {ref, paths? \| document_ids?}` |
| Diagnostics | opt-in `read_console`, `read_network` (method, URL, status, MIME, timing; since last read); opt-in `javascript_exec` (last expression value) |
| Tabs | `new_tab`, `list_tabs`, `switch_tab {tab_id}`, `close_tab {tab_id}` |

**Contract details to copy:**

- **Targets:** `{type:"coordinate", x, y}` (viewport CSS px; origin is the page top-left, no window frame) or `{type:"ref", ref:"ref_2"}`.
  - **The executor owns the ref → node map.** Refs stay valid until the tab navigates or its DOM changes materially. Never renumber handed-out refs before a navigation.
  - Stale or unknown ref → `Error: ref_3 is stale or not found on the current page. Re-read the page to get fresh references.`
- **Snapshot format:** one line per element, `role "accessible name" [ref_N]`. Example:
  ```
  link "Getting started" [ref_2]
  textbox "Search docs" [ref_3]
  ```
  Prefer refs. Fall back to coordinates for canvas, video, virtualised lists and cross-origin iframes; the executor resolves which frame a coordinate lands in.
- **Batch semantics:** run the calls of one turn in order and stop at the first failure. Every later call gets `is_error:true` with the exact text `Not executed: an earlier action in this turn failed.`
  - The executor may attach a fresh screenshot or tree to the last result of a batch to save a round trip.
- **Errors:** specific and actionable. For example `Error: Navigation to … timed out after 30 seconds. The page may be unavailable.` or `Error: Navigation refused. Only http and https URLs are allowed.` Disabled members say `… is not enabled in this environment.`
- **Tab state (`browser_state`)** goes on results whenever tabs, the active tab, the title or the URL changed:
  - Full `tabs` inventory, exactly one `active:true`.
  - `state_changes`, e.g. `tab_opened` for popups and `target=_blank`.
  - Rendered to the model as a "Tab Context" footer, deduplicated.
- **Downloads** use `state_changes`:
  - `download_started {download_id, url}` on the result of the call that triggered it.
  - `download_completed {download_id, url, path?, size_bytes?}` on whichever later result is running when it finishes.
  - `download_failed {…, error?}`.
- **Screenshots** must fit image limits. The executor downscales and maps coordinates back by the inverse factor. Keep one consistent viewport size.
- **Security:**
  - `navigate` accepts only http/https; a missing scheme means https; parse with a URL parser.
  - Re-check the allowlist after redirects.
  - Build reads from what is rendered (AX tree or visible text), never raw DOM source, so hidden injected text never reaches the model.
  - Redact tokens in console/network output.
  - `file_upload` only from an allowlisted directory, never the downloads dir.
  - Human confirmation for purchases, account changes, messaging and accepting terms.

## 4. Codex / ChatGPT desktop in-app browser

Sources: [learn.chatgpt.com/docs/browser](https://developers.openai.com/codex/app/browser); [help.openai.com 20001277](https://help.openai.com/en/articles/20001277-using-the-built-in-browser-in-the-chatgpt-desktop-app); decompiled app → **local://codex-browser-internals.md** (all file:line links).

**User-facing features**
- Separate profile. Downloads go to the system Downloads folder (configurable, optional "Ask where to save").
- Address bar searches its own history, else Google.
- **Split view / Full view**; Cmd+Shift+B toggles; Cmd+T opens a tab.
- **Annotation mode** (Cmd+.): click an element or drag an area, write a comment, optionally **Adjust** styles (font/spacing/colour) and preview them live, then send.
- **Agent permissions**, set in Settings > Browser:
  - Per site: Browse / Download / Upload / Debug (CDP), each Allow / Requires approval / Block.
  - Default "Always ask".
- **Developer mode** = "Enable full CDP access", approval per site.
- "ChatGPT can't automate file uploads in the built-in browser."

**Internals** (details and line links in codex-browser-internals.md)
- **Runtime:** patched Electron 42.3 ("Owl"). Native Chromium handles JS dialogs, HTTP auth, the file chooser and permission prompts; the app has no code for them.
- **Agent transport:** the model writes JS in a `node_repl` tool using an SDK. The SDK speaks over a local framed pipe (8 MiB in / 64 MiB out) to main.
  - Main exposes raw CDP via `webContents.debugger` with an allow-list: no `Target.*` except `setAutoAttach`; `Input.*` limited to `dispatchMouseEvent`, `dispatchKeyEvent`, `insertText` → trusted.
  - `Emulation.setFocusEmulationEnabled` is sent before input, so the page acts focused while the panel isn't.
  - Timeouts: 20 s per CDP command, 15 s for tab ready.
- **Device toolbar:** 13 presets, width×height only. Responsive is the default at 390×844; the list then runs from 4K down to iPhone SE 375×667.
  - Clamp 240×160 … 4096×4096.
  - The webview is laid out at full emulated size and visually scaled (`scale = min(1, fitW/w, fitH/h)`).
  - Rotate swaps W/H and keeps the preset only if it still matches.
  - Side-drag changes width by 2×Δ÷scale. Zoom select 50–200%.
  - Applied with `Emulation.setDeviceMetricsOverride {deviceScaleFactor:1, mobile:false}`. **No touch, UA or DPR.** (Issue [openai/codex#18537](https://github.com/openai/codex/issues/18537) asks for real mobile emulation.)
- **Agent cursor overlay:** Bézier path; the click waits up to 1.5 s for the cursor to "arrive".
- **Hidden agent tabs:** "parked" at real size with opacity 0.001 and no background throttling, so they keep painting and screenshots work.
- **End of turn:** temporary tabs close unless marked `handoff` or `deliverable`.
- **Popups:** `foreground-tab`/`background-tab` are adopted as a tab with the opener kept. **`new-window` popups (OAuth) are denied and reopened as a plain tab, so the opener is lost.** Don't copy this; Claude's pane explicitly supports OAuth popups.
- **Downloads** are cancelled unless the user started them or the agent called `allowDownload` (single use, 10 s).
- **Crash:** `render-process-gone`/`unresponsive` → in-tab "This page crashed" page with **Reload** and **Open in external browser**.
- **Options menu order:** Passwords and autofill ▸, Import cookies and passwords…, History, Downloads, Extensions, Clear browsing data ▸ (cookies, cache), Zoom row [− n% +] Reset, Force reload, Print, Find in page, Show/Hide device toolbar, Show/Hide composer, Browser settings.
- **Shortcuts:** Cmd+T, Cmd+L, Cmd+←/→ (Alt+←/→ elsewhere), Cmd+R / Cmd+Shift+R, Cmd+W, Cmd+Shift+T, Cmd+Shift+[ ], Cmd+Shift+B, Cmd+F, Cmd+± / Cmd+0, Cmd+.
- **Anti-bot:**
  - No UA override for third-party sites (plain Chrome UA, no `Electron/` token).
  - CDP runs in-process (no `--remote-debugging-port`, no `--enable-automation`) → `navigator.webdriver` false.
  - Bot walls are reported back to the model (`botDetection.report`) and handed to the user (`browserAuth` handoff).

## 5. cmux — the closest analog (native macOS app, WKWebView, agent-driven)

Repo [manaflow-ai/cmux](https://github.com/manaflow-ai/cmux) @ `09a51fb30b70`, package `Packages/macOS/CmuxBrowser`. Skill doc: [skills/cmux-browser/SKILL.md](https://github.com/manaflow-ai/cmux/blob/main/skills/cmux-browser/SKILL.md). Driver spec: [docs/browser-repl/driver-protocol.md](https://github.com/manaflow-ai/cmux/blob/main/docs/browser-repl/driver-protocol.md). **omp ships a client for its socket API** (`pi-coding-agent/src/tools/browser/cmux/*`), and that is the protocol the OMP GUI already emulates.

Base URL for the file links below: `https://github.com/manaflow-ai/cmux/blob/09a51fb30b70/Packages/macOS/CmuxBrowser/Sources/CmuxBrowser/`.

- **Trusted native input** ([WebView/CmuxWebView+AutomationInput.swift:5-11](https://github.com/manaflow-ai/cmux/blob/09a51fb30b70/Packages/macOS/CmuxBrowser/Sources/CmuxBrowser/WebView/CmuxWebView+AutomationInput.swift#L5-L11)):
  - Synthetic `NSEvent`s go straight to WebKit's own `NSResponder` methods (`super.mouseDown(with:)` etc.), skipping the app's overrides so the user's focus doesn't move.
  - Keys: `NSEvent.keyEvent(...)` → `keyDown`/`flagsChanged` ([Input/BrowserWebKitKeyDownDispatch.swift:308-325](https://github.com/manaflow-ai/cmux/blob/09a51fb30b70/Packages/macOS/CmuxBrowser/Sources/CmuxBrowser/Input/BrowserWebKitKeyDownDispatch.swift#L308-L325)).
  - **Automation keys carry a mark** in `CGEventField.eventSourceUserData` (`0x636D75786B657973`, "cmuxkeys"). WebKit resends keys no page handled through `NSApp.sendEvent`; the app drops marked resends so automation keys never reach the user's terminal or menus ([:343-365](https://github.com/manaflow-ai/cmux/blob/09a51fb30b70/Packages/macOS/CmuxBrowser/Sources/CmuxBrowser/Input/BrowserWebKitKeyDownDispatch.swift#L343-L365)).
  - Mouse state machine: held buttons → `…MouseDragged`, plus `clickCount`. CSS→view point conversion uses `cssPerPoint = 1/(pageZoom·magnification)` and a flipped-y fix ([Repl/BrowserReplMouseEventPlan.swift](https://github.com/manaflow-ai/cmux/blob/09a51fb30b70/Packages/macOS/CmuxBrowser/Sources/CmuxBrowser/Repl/BrowserReplMouseEventPlan.swift)).
  - Automated right-clicks suppress the native context menu.
  - Drags use a private pasteboard.
  - PR [#14639](https://github.com/manaflow-ai/cmux/pull/14639): `type`/`fill` replay text through WebKit's native keyboard path so React-controlled inputs get trusted `input` events. DOM value-setting stays only as a fallback.
- **Viewport emulation in WKWebView** ([BrowserViewportLayout.swift](https://github.com/manaflow-ai/cmux/blob/09a51fb30b70/Packages/macOS/CmuxBrowser/Sources/CmuxBrowser/BrowserViewportLayout.swift), [WebView/WKWebView+BrowserViewport.swift](https://github.com/manaflow-ai/cmux/blob/09a51fb30b70/Packages/macOS/CmuxBrowser/Sources/CmuxBrowser/WebView/WKWebView+BrowserViewport.swift)):
  - The logical CSS viewport is aspect-fitted and centred inside the pane.
  - `frame` = display rect (scale = min(containerW/w, containerH/h)); `bounds` = CSS size × pageZoom, so AppKit scales the view. `window.innerWidth` equals the requested width at any pane size.
  - Pixel quantisation mirrors WebKit's floor-then-divide.
  - Render limits: 8192 px per side and 33 554 432 px² total; otherwise a `maximum_page_zoom` error ([BrowserViewportRenderLimits.swift](https://github.com/manaflow-ai/cmux/blob/09a51fb30b70/Packages/macOS/CmuxBrowser/Sources/CmuxBrowser/BrowserViewportRenderLimits.swift)).
  - Emulation is reset while the attached inspector is open.
  - CLI: `viewport <w> <h>` (1–4096) / `viewport reset`.
- **UA policy** ([BrowserUserAgentPolicy.swift](https://github.com/manaflow-ai/cmux/blob/09a51fb30b70/Packages/macOS/CmuxBrowser/Sources/CmuxBrowser/BrowserUserAgentPolicy.swift)):
  - Every http(s) top-level navigation gets a Safari-identical UA: `Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/<installed Safari, floor 26.6> Safari/605.1.15`.
  - Reason given in the source: "Google Workspace supports only the two most recent browser versions". The default WKWebView UA has no `Version/… Safari/…` token and gets rejected as an embedded browser (Google OAuth `disallowed_useragent`) `[INFERENCE for the OAuth part]`.
- **HTTP Basic auth sheet:** `WebView/BrowserHTTPBasicAuthPromptCoordinator.swift` (matches `NSURLAuthenticationMethodHTTPBasic`). Also client-certificate prompts (`ClientCertificates/*`), a server-trust bypass with fingerprint pinning (`Authentication/BrowserSSLTrustBypassMessageHandler.swift`, `BrowserServerTrustFingerprint.swift`), and a WebAuthn bridge (`WebAuthn/*`).
- **Popups:** issue [#742](https://github.com/manaflow-ai/cmux/issues/742) is the definitive acceptance list for `window.open` in WKWebView:
  - `createWebViewWith` must return a live web view **built from the passed configuration, unmodified**. Otherwise `window.open()` returns null and `opener`/`postMessage` break.
  - Respect `WKWindowFeatures` (size, position, resizable).
  - Implement `webViewDidClose(_:)` so `window.close()` works.
  - Scripted popups become windows; Cmd/middle-click and `target=_blank` become tabs.
  - Limit nested popup depth (3).
  - Close popups when their opener closes; popups are ephemeral.
- **Downloads:** `Download/*` (`WKDownload` delegate, filename resolver, HTTP status decision, records). Agent API: `download list --json` → `{download_id, filename, path, status: downloading|saved|failed, bytes}`, plus `download wait`.
- **Crash/hibernation** (driver-protocol "Hibernated and crashed tabs"):
  - Tab states: `live | hibernated | waking | crashed`.
  - On a crashed tab every call fails fast with `crashed: its web content process ended … Call page.reload() or page.goto(url)`.
  - `tab.reload` starts a new web content process.
  - Events: `tab.crashed`; `tab.replaced` (new web view, so old refs/handles are gone).
- **Agent world:** `WKContentWorld.world(name:"cmux-agent")` with document-start user scripts in all frames, called through `callAsyncJavaScript`. The page cannot see the agent's globals, which hides the automation from fingerprinting scripts.
  - Frame ids come from SPI `-[WKWebView _frames:]`.
  - Network events come from SPI `_setResourceLoadDelegate:`; without the SPI there are no request events. omp's Tern backend instead captures only fetch/XHR in-page.
- **Driver protocol highlights** worth mirroring in the GUI ↔ omp bridge:
  - Error codes: `not_found, stale, timeout, unsupported, invalid, closed, blocked, hibernated, crashed`.
  - `tab.navigate {waitUntil: commit|domcontentloaded|load|networkidle, timeoutMs}`.
  - `dialog.opened {dialogId, type: alert|confirm|prompt|beforeunload, message, defaultValue}` / `dialog.respond`.
  - `filechooser.opened {chooserId, multiple}` / `filechooser.respond` (native panel suppressed only for agent-caused choosers).
  - `download.started/finished`, `console`, `pageerror`.
  - **Routing rule:** a dialog or file chooser caused by the agent's own call (input, the first second of an evaluate, a navigation until commit) goes to the agent. Otherwise it is the user's and shows the native UI.
  - Hidden driven tabs render at 1280×800 (Playwright default) and report key/focused without stealing the user's key window.
- **Limits stated by cmux for WKWebView:** offline emulation, trace/screencast, request interception/mocking and raw low-level input return `not_supported`. Issue [#10084](https://github.com/manaflow-ai/cmux/issues/10084) proposes an opt-in Chromium engine with a loopback CDP endpoint.

## 6. omp's own host-browser backends (pinned omp 18.8.0)

Source root: `~/Library/Application Support/OmpGui-Test/runtimes/omp-18.8.0-bun-1.4.2/omp/node_modules/@oh-my-pi/pi-coding-agent/src/`.

- Tool doc: `prompts/tools/browser.md` (77 lines). The `browser` tool runs inside Eval. Backend precedence: explicit `app` (path / `cdp_url` / `relay`) > Tern pane > cmux > managed Chromium. Related settings: `browser.tern`, `browser.cmux`. `tab.devices()` = Puppeteer `KnownDevices`.
- **cmux backend** `tools/browser/cmux/{rpc.ts,socket-client.ts,cmux-tab.ts}`:
  - Env `CMUX_SOCKET_PATH`, `CMUX_SOCKET_PASSWORD` (`rpc.ts:190-197`); relay `CMUX_RELAY_ID/TOKEN`; `CMUX_WORKSPACE_ID/SURFACE_ID` (`tab-supervisor.ts:613-624`).
  - Verbs used: `browser.open_split, navigate, url.get, eval, wait, snapshot, screenshot, click, dblclick, hover, focus, fill, type, press, check, uncheck, scroll, scroll_into_view`.
  - No dialog, file-chooser, download, viewport or UA verbs.
- **Tern backend** `tools/browser/tern/*`:
  - Env `TERN_PANE_SOCKET` + `TERN_PANE` (numeric) (`tern/kind.ts:22-49`); `PI_BROWSER_TERN=0|1` overrides `browser.tern` (default **true**).
  - Wire format: u32 LE length-prefixed JSON. Hello `{"hello":{}}` → `{"welcome":{"ops":[…]}}`; requests `{"id":N, "browser":{op,…}}` → `{"id":N, "browser":{"ok"|"error":{kind,message}}}` (`tern/wire.ts:1-40`).
  - Error kinds: `invalid, not_found, not_agent, no_window, window_closed, unsupported, js, failed, connect, closed, timeout, protocol`.
  - Ops called from `tern-tab.ts`:

    | Op | Line |
    |---|---|
    | `open {owner, url, width, height}` → `{block}` | 395-420 |
    | `close` | |
    | `eval {function, args, world: page\|isolated, frame?}` | 508 |
    | `input {events:[mouse move/down/up + button/clicks/mods \| wheel dx/dy \| key down/up key/code/text \| text]}` | 558; `keys.ts:11-23` |
    | `events {after: cursor}` (polled event log: console, pageerror, requestfailed, dialogs…) | 565 |
    | `state` | 747 |
    | `dialogs {policy}`, `dialog {accept, text?}` | 761, 1992-2007 |
    | `allow {hosts}`, `agent {value: UA}`, `insecure` | 764-770 |
    | `scripts` (init scripts) | 826 |
    | `goto {url}`, `nav {go}` | 950-963 |
    | `viewport {width, height}` | 1048 |
    | `files {paths}` (file-chooser answer) | 1418 |
    | `capture {format, quality, scale}`, `pdf` | 1777, 1922 |
    | `appearance` | 2065 |
    | `credentials {username, password}` (HTTP auth) | 2069 |
    | `clipboard`, `edit {copy\|paste}` | 2107-2126 |
    | `cookies`, `setCookie`, `deleteCookie` | 2133-2197 |
    | `downloads {dir}` | 2295 |

  - `emulate` explicitly throws on WKWebView for timezone, extra headers, reduced motion, CPU and network throttling (`tern-tab.ts:2014-2035`).
  - `device` = Puppeteer descriptor → `viewport` + `agent` UA; DPR and touch are not applied (`:2036-2051`).
- The GUI today serves the cmux protocol (`avalonia/src/OmpGui.ClientCore/AgentBrowserBridge.cs:67-85`; `docs/PARITY.md:252-255`: "one page at a time; screenshots of the visible area … not full-page or element clips").

## 7. Playwright MCP & Chrome DevTools MCP (tool contracts)

**Playwright MCP** ([README](https://github.com/microsoft/playwright-mcp/blob/main/README.md); code in `playwright-core` 1.64 `lib/coreBundle.js`)
- Flags: `--device "iPhone 15"`, `--mobile` (generic Pixel/iPhone), `--viewport-size 1280x720`, `--user-agent`, `--isolated` (in-memory profile), `--snapshot-mode full|none`, `--snapshot-boxes`.
- Tools:
  - Snapshot and capture: `browser_snapshot {target?, depth?, boxes?}` ("better than screenshot"), `browser_take_screenshot` ("You can't perform actions based on the screenshot").
  - Element actions: `browser_click/hover/type/select_option/drag/drop {target: ref or selector}`, `browser_fill_form`, `browser_file_upload`.
  - Waiting and dialogs: `browser_handle_dialog {accept, promptText?}`, `browser_wait_for {time ≤30 s \| text \| textGone}`.
  - Navigation, tabs, window: `browser_navigate(_back)`, `browser_tabs {action, index?, url?}`, `browser_resize {width,height}`.
  - Diagnostics and media: `browser_console_messages`, `browser_network_requests` + `browser_network_request {index}`, `browser_emulate_media`.
  - State: cookie and storage CRUD.
  - Recording, tracing, coordinates: recording/tracing/video, and `browser_mouse_*_xy` (coordinate mode).
  - Highlight/annotate: `browser_highlight`, `browser_annotate`.
- **Snapshot line format:** `- button "Submit" [ref=e5] [cursor=pointer] [box=x,y,w,h]` (YAML-like aria snapshot; `coreBundle.js:775`, `:20944`).
- **Modal state:** every response includes a `### Modal state` section such as `- ["alert" dialog with message "…"]: can be handled by browser_handle_dialog` or `- [File chooser]: can be handled by browser_file_upload` (`coreBundle.js:66344`, `:67545`). While a modal is open, other actions are refused. This is how the agent learns about dialogs and choosers.

**Chrome DevTools MCP** ([tool-reference.md](https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/tool-reference.md))
- `take_snapshot` (a11y tree with `uid`; "Always use the latest snapshot. Prefer taking a snapshot over taking a screenshot").
- `click/hover/fill/drag {uid, includeSnapshot?}`, `fill_form`, `handle_dialog {accept|dismiss, promptText?}`, `upload_file {uid of input or element that opens a chooser}`.
- `navigate_page {type: url|back|forward|reload, handleBeforeUnload, initScript, timeout}`, `new_page {isolatedContext?}`, `wait_for {text[]}`.
- **`emulate {viewport:"<w>x<h>x<dpr>[,mobile][,touch][,landscape]", userAgent, colorScheme, geolocation, networkConditions, cpuThrottlingRate, extraHttpHeaders}`**, `resize_page`.
- `list_console_messages`/`list_network_requests` paged (preserve the last 3 navigations), `evaluate_script {function, args, waitForStableDom}`, `take_screenshot {fullPage|uid, format, quality, filePath}`, `get_css_styles`, Lighthouse, performance trace.

## 8. Device presets (for the device toolbar)

From Playwright 1.64 `deviceDescriptorsSource` (206 entries; viewport = screen minus browser chrome; `screen` where present). Use these for UA/DPR/touch on WebView2. On WKWebView use only the viewport + UA (see §0 point 4).

| Preset | Viewport | Screen | DPR | Mobile/touch | UA (abridged) |
|---|---|---|---|---|---|
| iPhone SE (3rd gen) | 375×667 | – | 2 | yes | iPhone OS 18_5 … Mobile/19E241 Safari |
| iPhone 15 | 393×659 | 393×852 | 3 | yes | iPhone OS 17_5 … Mobile/15E148 Safari/604.1 |
| iPhone 15 Pro Max | 430×739 | 430×932 | 3 | yes | same family |
| Pixel 7 | 412×839 | 412×915 | 2.625 | yes | Android 14; Pixel 7 … Chrome/156 Mobile |
| Galaxy S24 | 360×780 | – | 3 | yes | Android 14; SM-S921U … Chrome Mobile |
| iPad Mini | 768×1024 | – | 2 | yes | iPad; CPU OS 12_2 … Safari |
| iPad Pro 11 | 834×1194 | – | 2 | yes | iPad … |
| Galaxy Tab S9 | 640×1024 | – | 2.5 | yes | Android 14; SM-X710 … Chrome |
| Desktop Chrome | 1280×720 | 1920×1080 | 1 | no | Windows NT 10.0 … Chrome/156 |
| Desktop Safari | 1280×720 | 1792×1120 | 2 | no | Macintosh … Version/27.2 Safari |

Codex presets (screen sizes, i.e. the Chrome DevTools convention): Responsive 390×844, 4K 2560×1440, Laptop L 1440×900, Laptop 1024×768, Surface Pro 7 912×1368, iPad Air 820×1180, iPad Mini 768×1024, Surface Duo 540×720, iPhone 15 Pro Max 430×932, Pixel 8 412×915, iPhone 15 Pro 393×852, Galaxy S24 Ultra 384×824, iPhone SE 375×667. Use **screen** sizes in the UI toolbar (what designers expect) and say so in the tooltip. Playwright's chrome-subtracted heights matter only for agent parity.

## 9. Native windows — per-engine API matrix

"Default" = behaviour when the host doesn't handle the event.

| Need | WKWebView (macOS) | WebView2 (Windows) | WebKitGTK (Linux) |
|---|---|---|---|
| alert/confirm/prompt | `WKUIDelegate runJavaScriptAlertPanelWithMessage:` / `…ConfirmPanel…` / `…TextInputPanelWithPrompt:defaultText:` — **default: nothing shown, call returns immediately** | `ScriptDialogOpening` (needs `Settings.AreDefaultScriptDialogsEnabled=false` to own it; default: native dialog) | `WebKitWebView::script-dialog` (default: built-in dialog) |
| beforeunload | `runJavaScriptConfirmPanel` path `[INFERENCE]` | `ScriptDialogOpening` kind `BeforeUnload` | `script-dialog` type `BEFORE_UNLOAD_CONFIRM` |
| window.open / popups | `createWebViewWithConfiguration:forNavigationAction:windowFeatures:` → **return a new WKWebView with the passed configuration** (keeps opener/postMessage) + `webViewDidClose:`; **default: dropped** | `NewWindowRequested` → set `e.NewWindow` to another CoreWebView2 (deferral) to keep the opener; default: a separate WebView2-hosted window | `WebKitWebView::create` → return a new related view (`webkit_web_view_new_with_related_view`), then `ready-to-show`/`close`; **default: blocked** |
| file picker | `runOpenPanelWithParameters:initiatedByFrame:completionHandler:` (`allowsMultipleSelection`, `allowsDirectories`) → `NSOpenPanel`; **default: nothing** | built-in picker (default) | `run-file-chooser` (default: `GtkFileChooserDialog`) |
| downloads | `decidePolicyForNavigationAction` `.download` when `shouldPerformDownload`; `navigationResponse` `!canShowMIMEType` → `.download`; `navigationAction:didBecomeDownload:` → `WKDownloadDelegate decideDestinationUsingResponse:suggestedFilename:` (macOS 11.3+); **default: not saved** | `DownloadStarting` (default: Edge download UI) | `WebKitNetworkSession::download-started` (6.0) / `WebKitWebContext::download-started` (4.x); `decide-destination` |
| HTTP auth | `WKNavigationDelegate webView:didReceiveAuthenticationChallenge:completionHandler:` (`NSURLAuthenticationMethodHTTPBasic/Digest/NTLM`, also `ServerTrust`, `ClientCertificate`) → `URLCredential(user:password:persistence:.forSession)`; **default: performDefaultHandling = no UI** | `BasicAuthenticationRequested` (default: native prompt) | `WebKitWebView::authenticate` (default: built-in dialog) |
| permissions | `requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:` (macOS 12+); geolocation via Core Location + `WKUIDelegate` SPI only `[INFERENCE]`; Notifications not supported in WKWebView | `PermissionRequested` (default: prompt) | `permission-request` (default: **deny**) |
| crash | `webViewWebContentProcessDidTerminate:` → show "page crashed" + Reload | `ProcessFailed` (`RenderProcessExited/Unresponsive`, `BrowserProcessExited` → recreate the controller) | `web-process-terminated` (`CRASHED`/`EXCEEDED_MEMORY_LIMIT`) |
| UA | `customUserAgent`, `configuration.applicationNameForUserAgent` | `Settings.UserAgent` / CDP `Emulation.setUserAgentOverride` (+ UA-CH metadata) | `webkit_settings_set_user_agent` |
| viewport/DPR/touch | frame/bounds projection (cmux) + `pageZoom`; no DPR or touch | CDP `Emulation.setDeviceMetricsOverride {width,height,deviceScaleFactor,mobile,screenOrientation}`, `Emulation.setTouchEmulationEnabled {maxTouchPoints}`, `Emulation.setEmitTouchEventsForMouse` | resize + `webkit_web_view_set_zoom_level`; no DPR or touch |
| trusted input | synthetic `NSEvent` → WKWebView responder methods (cmux) | CDP `Input.*` via `CallDevToolsProtocolMethodAsync` | synthesised `GdkEvent` to the widget `[INFERENCE: no public injection API]` |
| isolated agent JS | `WKContentWorld` + `callAsyncJavaScript:arguments:inFrame:inContentWorld:` | `AddScriptToExecuteOnDocumentCreatedAsync` runs in the main world; isolation via CDP `Page.createIsolatedWorld` | `webkit_web_view_evaluate_javascript(world_name)` + `WebKitScriptWorld` |
| clear data | `WKWebsiteDataStore removeDataOfTypes:modifiedSince:`; persistent vs `nonPersistentDataStore`; per-profile `dataStoreForIdentifier:` (macOS 14+) | `CoreWebView2Profile.ClearBrowsingDataAsync(kinds, from, to)` | `webkit_website_data_manager_clear` |
| devtools | `isInspectable = YES` (macOS 13.3+) → Safari Web Inspector | `OpenDevToolsWindow()` | `webkit_settings_set_enable_developer_extras` + inspector |

Docs:
- [WKUIDelegate](https://developer.apple.com/documentation/webkit/wkuidelegate), [WKNavigationDelegate](https://developer.apple.com/documentation/webkit/wknavigationdelegate), [WKDownloadDelegate](https://developer.apple.com/documentation/webkit/wkdownloaddelegate), [WKContentWorld](https://developer.apple.com/documentation/webkit/wkcontentworld)
- [CoreWebView2](https://learn.microsoft.com/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2), [CDP Emulation](https://chromedevtools.github.io/devtools-protocol/tot/Emulation/), [CDP Input](https://chromedevtools.github.io/devtools-protocol/tot/Input/)
- [WebKitWebView (GTK)](https://webkitgtk.org/reference/webkit2gtk/stable/class.WebView.html)

Integration notes `[INFERENCE]`:
- Avalonia's `NativeWebView` adapter already installs its own WKUI/WKNavigation delegates (it raises `NavigationStarted` and `NewWindowRequested`). Overriding them through the platform handle (`IAppleWKWebViewPlatformHandle.WKWebView`) must forward to the original delegate (an `NSProxy`/forwarding wrapper, or `respondsToSelector:` chaining), otherwise Avalonia's events break.
- Popup `createWebView` must create a WKWebView from WebKit's configuration. That means a second native view the app hosts (a popup window or a tab), **not** another `NativeWebView.Navigate(url)`, which would lose the opener.

## 10. Agent-friendly page representation — best practice synthesis

- **Accessibility tree + refs is the primary observation.** Used by: Anthropic `read_page`, Playwright `browser_snapshot`, CDT-MCP `take_snapshot`, cmux `snapshot --interactive`, omp `observe`/`ariaSnapshot`.
  - Filter to visible + interactive by default.
  - Cap the size (Anthropic: 50k chars, with an explicit truncation note).
  - Allow subtree reads (`ref`) and `depth`.
- **Interactivity heuristics** (browser-use [`clickable_elements.py`](https://github.com/browser-use/browser-use/blob/main/browser_use/dom/serializer/clickable_elements.py)):
  - Interactive tags; `onclick`/`onmousedown`/`tabindex`; ARIA roles (button, link, menuitem, checkbox, tab, option…).
  - AX roles from CDP; icon-sized (10–50 px) elements with `aria-label`/`data-action`; `cursor:pointer` fallback; labels wrapping form controls; iframes.
- **Occlusion:** browser-use's paint-order filter drops elements covered by others ([serializer.py:133-138](https://github.com/browser-use/browser-use/blob/main/browser_use/dom/serializer/serializer.py)). Without it the agent "clicks" through modals.
- **Change markers:** browser-use prefixes newly appeared elements with `*` (`*[12]<button>`) so the model notices what changed after an action.
- **Scroll hints:** browser-use marks scroll containers `|scroll element[...]` with pages above/below, and adds `... (N more elements below - scroll to reveal)`. Anthropic exposes `scroll_to {ref}`.
- **Shadow DOM:** traverse open shadow roots for snapshot, find, wait and selectors.
  - Ref paths must keep host→root identity, and accessible names must resolve labels inside the same root.
  - Sources: browser-use serializer (`children_and_shadow_roots`), cmux PR #14639, Stagehand (pierces shadow DOM by default; [docs](https://docs.stagehand.dev/v3/references/page)).
- **iframes:** snapshot same-origin frames inline under their `<iframe>` node.
  - Cross-origin frames need engine support (CDP flattened targets; WKWebView `_frames:` SPI or `WKFrameInfo` in `callAsyncJavaScript inFrame:`).
  - CDP `backendNodeId` is not unique across frames ([Stagehand iframes post](https://www.browserbase.com/blog/taming-iframes-a-stagehand-update)), so refs must encode the frame.
- **Acting on refs:** scroll into view, wait until the element is visible, stable, enabled and receives events (Playwright actionability), then send a native click at the element centre (omp Tern) and report what happened. If the hit-test element ≠ target, report "covered by <x>" rather than clicking blindly.
- **Waiting:**
  - Navigation `waitUntil: commit | domcontentloaded | load | networkidle` with a timeout (cmux, CDT-MCP).
  - `wait_for {text | textGone | selector | url | function}` ≤30 s.
  - After each action wait for a short DOM-quiet window (CDT-MCP `waitForStableDom`).
- **Stagehand** ([v3](https://www.browserbase.com/blog/stagehand-v3)): `observe` returns candidate actions (selector + method + description) that can be cached and replayed; `act` takes natural language; `extract` uses a Zod schema. Pruning the AX tree to a "hybrid" form cuts size by 80–90%.
- **Screenshots:**
  - Keep one viewport size for the session and downscale before sending.
  - Offer `zoom {region}` for small text.
  - Optional set-of-marks overlay (browser-use highlight, omp `screenshot({annotate})`) when coordinates are needed.

## 11. Bot detection — what matters and per-engine recipe

Detection vectors (sources: [rebrowser-patches](https://github.com/rebrowser/rebrowser-patches), [browser-use PR #5447](https://github.com/browser-use/browser-use/pull/5447), [Kasada anti-instrumentation](https://blog.crawlex.net/blog/kasada-anti-instrumentation/), [crawlex on input](https://blog.crawlex.net/blog/synthesizing-human-input-events/)):

1. **`navigator.webdriver`.** True only under WebDriver/`--enable-automation`. In-process CDP (Codex), the extension (Claude in Chrome) and WKWebView/WebView2 hosts don't set it. Don't fake it: defining it as `false` on the instance is itself a tell; it must stay native `undefined`/`false`.
2. **`isTrusted` and event realism.** JS `dispatchEvent` gives `isTrusted=false`. Use native or CDP input (§5, §4).
   - Chromium bug 40280325: CDP `Input.dispatchMouseEvent` produces `screenX==clientX` and `screenY==clientY`. Cloudflare Turnstile checks this. Pass a realistic screen offset.
   - Send a `mouseMoved` path before clicks (Codex's Bézier cursor doubles as realistic motion), with key timings of ~30–120 ms `[INFERENCE]`.
3. **CDP `Runtime.enable` leak** (Cloudflare/DataDome detect it via console serialisation of a Proxy prototype). Evaluate in isolated worlds (`Page.createIsolatedWorld`) and keep `Runtime.enable` off. WKWebView has no CDP, so this doesn't apply there.
4. **Injected-script artefacts.** Page-world globals (`window.__omp…`), patched prototypes, `Page.addScriptToEvaluateOnNewDocument` sourceURLs. Run agent code in `WKContentWorld` / an isolated world.
   - Tern evaluates the page kit in `world: "isolated"` (`tern-tab.ts:518-532`).
   - omp's cmux console capture installs `globalThis.__ompConsoleCapture` in the page world (`cmux-tab.ts:1394`), a visible artefact `[INFERENCE: detectable]`.
5. **UA and Client Hints consistency.**
   - WKWebView: use the Safari UA (cmux policy, §5); never a Chrome UA on WebKit, since feature detection exposes it.
   - WebView2: strip nothing (no `Edg/`… keep the real UA); leave `sec-ch-ua` consistent.
   - Mobile emulation must change UA, `sec-ch-ua-mobile`, `maxTouchPoints`, `devicePixelRatio`, `screen.*` and `matchMedia('(pointer:coarse)')` together, otherwise the mismatch is a stronger signal than desktop (CDT-MCP `emulate` handles this on Chromium; impossible on WKWebView).
6. **Headless signals** (`HeadlessChrome` UA, zero plugins, no GPU) are avoided by using the visible native webview. Hidden agent tabs should stay *rendered*: Codex parks them at opacity 0.001 with throttling off; cmux renders hidden tabs at 1280×800 and emulates focus.
7. **Behaviour when blocked:** pause and hand off to the user (Claude in Chrome: login/CAPTCHA → "handle it manually"; Codex `browserAuth` + `botDetection.report`). Never solve CAPTCHAs (Claude Desktop policy).

## 12. Browser chrome UX — union feature checklist

| Feature | Claude Desktop | Codex | cmux | Recommend |
|---|---|---|---|---|
| Tabs, new tab, close, reopen closed | ✓ | ✓ Cmd+T/W/Shift+T | ✓ | ✓ |
| URL bar with history autocomplete, security indicator (Not secure) | – | ✓ | ✓ | ✓ |
| Back/forward/reload/stop, hard reload | ✓ | ✓ Cmd+←/→, R, Shift+R | ✓ | ✓ |
| Zoom (levels 25–500, Cmd± / 0) | – | ✓ | ✓ | ✓ |
| Device toolbar: presets, W×H inputs, rotate, drag handles, fit scale, zoom % | – | ✓ (size only) | viewport cmd | ✓ (+UA/DPR/touch on WebView2) |
| Select / annotate element (Cmd+Shift+S / Cmd+.) | ✓ | ✓ + Adjust styles | design mode | ✓ (exists in GUI) |
| Screenshot to clipboard | – | ✓ | ✓ | ✓ |
| Find in page | – | ✓ | ✓ | ✓ |
| Downloads list/popover, ask-where-to-save | – | ✓ | ✓ | ✓ |
| Clear browsing data (cookies, cache, history, time range); Keep cookies | ✓ | ✓ | ✓ profiles | ✓ |
| Open in external browser | ✓ (Cmd-click) | ✓ | ✓ | ✓ |
| Dev servers menu / launch config | ✓ | – | – | optional |
| Console & network logs for the agent | via tools | via CDP | ✓ events | ✓ |
| DevTools / inspector | – | Developer mode (CDP) | inspector | ✓ `isInspectable` / `OpenDevToolsWindow` |
| Crash page with Reload + Open externally | – | ✓ | ✓ | ✓ |
| Agent cursor + "agent is driving" state | – | ✓ | – | ✓ |
| Site permission card (Allow once / Always / Deny) | ✓ | ✓ per-site matrix | domain policy | ✓ |
| End-of-turn tab cleanup (handoff/deliverable) | tab group | ✓ | session-owned tabs | ✓ |

## 13. Agent-tool contract recommendation for the GUI bridge `[INFERENCE, derived from §3–§7]`

1. **Observation:** `snapshot {filter: interactive|visible|all, ref?, depth?}`.
   - Lines `role "name" [ref=eN]` plus state flags (checked, disabled, expanded, focused) and an optional `[box=…]`.
   - `*` marks new elements; scroll-container hints; shadow DOM and same-origin frames inlined; truncation note.
   - Refs stable until navigation; stale ref → specific error.
2. **Actions:**
   - Element actions: `click/dblclick/hover/fill/type/press/select/check/upload {ref}`.
   - Coordinate fallback: `click_at {x,y}`.
   - Scrolling, drag, keys.
   - All as **native trusted input** with a pre-action actionability check and a post-action short settle.
3. **Results:** short ack + **page state** (URL, title, tabs, `tab_opened`, downloads) + **modal state** (open dialog type/message; file chooser) + optional screenshot on request.
   - The batch halt rule.
   - Error codes `not_found | stale | timeout | unsupported | invalid | closed | blocked | crashed | hibernated`, each with a remedy sentence.
4. **Timeouts:** navigation 30 s (configurable), action 5–10 s, `wait` ≤30 s, per-call 20 s ceiling (Codex). Never block forever on a JS dialog: report it as modal state.
5. **Native-window routing** (cmux rule): dialogs, choosers and auth caused by the agent's call go to the agent (`handle_dialog`, `upload_file`, `credentials`). User-caused ones show native UI to the user. An agent call while a user dialog is open → `blocked: a <type> dialog is open ("…"); ask the user or call handle_dialog`.
6. **Default backend:** keep the GUI as the only browser omp sees (cmux env already set; consider also serving Tern for its richer op set). Then there is no headless Chromium unless the user opts out (`browser.cmux false` / `PI_BROWSER_TERN=0`).

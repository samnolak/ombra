# Tern daemon wire protocol as used by omp 18.8.0

Source root abbreviations (all under `~/Library/Application Support/OmpGui-Test/runtimes/omp-18.8.0-bun-1.4.2/omp/node_modules/@oh-my-pi/pi-coding-agent/src/`):
- `W` = `tools/browser/tern/wire.ts`, `T` = `tools/browser/tern/tern-tab.ts`, `K` = `tools/browser/tern/keys.ts`,
  `PK` = `tools/browser/tern/page-kit.ts`, `PC` = `tools/browser/tern/page-capture.ts`, `NL` = `tools/browser/tern/network-log.ts`,
  `KD` = `tools/browser/tern/kind.ts`, `OT` = `tools/browser/op-timeouts.ts`, `TS` = `tools/browser/tab-supervisor.ts`,
  `B` = `tools/browser.ts`, `R` = `tools/browser/registry.ts`, `CC` = `modes/controllers/command-controller.ts`.

Everything not marked **[INFERENCE]** was read in the source at the cited line.

---

## 0. Activation

- omp is in "Tern mode" when `TERN_PANE_SOCKET` is non-empty and `TERN_PANE` matches `^\d+$` and is a safe integer (KD:22-29). A non-numeric `TERN_PANE` silently disables Tern.
- `PI_BROWSER_TERN=0|1` overrides the `browser.tern` setting (default true) (KD:33-49).
- Kind resolution order in `resolveBrowserKind` (B:159-227): `app.path` → explicit `app.relay` → `app.tern:true` (forced; throws if env missing, B:186-194) → relay setting → configured `cdpUrl` → Tern (auto, unless `app.tern:false`, B:213-216) → cmux (B:217-225) → Chromium. **Tern wins over cmux** when both env sets are present.
- One `TernSocketClient` per browser handle; `connect()` happens when the handle is created (R:191-195). The `/fork` command opens its own short-lived client (CC:1340, closed at CC:1368).

## 1. Transport

### Framing
- Unix-domain stream socket at `TERN_PANE_SOCKET`, opened with `net.createConnection({ path })` (W:373).
- Every frame both ways: `u32` little-endian byte length, then that many bytes of UTF-8 JSON encoding **one JSON object** (W:67-74, W:2-6).
- Max frame 256 MiB (`MAX_FRAME_BYTES = 256 << 20`, W:22); larger header → `protocol` error and connection fail (W:131-133).
- Non-JSON, non-UTF-8 (`fatal: true` decoder, W:65), or non-object payload → `protocol` error, connection torn down (W:84-95, W:426-432).

### Handshake
1. On socket `connect`, omp writes `{"hello":{}}` (W:381).
2. Daemon must reply `{"welcome":{"ops":[...]}}` within **10 s** (`CONNECT_TIMEOUT_MS`, W:23, W:377-380), else `connect` error.
3. `ops` = string list of request **kinds** (channels) the daemon answers beyond `browser`; non-strings filtered; missing/invalid → `[]` (W:96-102). `supports(op)` checks this set (W:285-288).
   - Only use in omp: `/fork` requires `supports("fork")` (CC:1350). **Browser ops are never gated by `ops`** — an old Tern "lists none and answers only `browser`" (W:6-7).
   - So for a GUI host: `{"welcome":{"ops":[]}}` is sufficient; add `"fork"` only if implementing fork.
4. A peer that closes before welcoming → `connect` error with "update Tern" text (W:393-401).

### Requests / answers
- Request: `{"id": N, "<channel>": BODY}` where channel ∈ `browser` | `fork` (W:325, W:78). `id` is a per-connection counter starting at 1 (W:243, W:324).
- `browser` BODY = `{"op": "<name>", ...fields}`; all per-tab ops add `"block": <PiP id>` (T:497-503).
- Answer: `{"id": N, "<same channel>": ANSWER}`, ANSWER = `{"ok": RESULT}` or `{"error": {"kind": STRING, "message": STRING}}` (W:7-9, W:208-221).
  - Answer without `ok`/`error` → `protocol` error for that op (W:220).
  - Answer on a different channel than the request → `protocol` error for that op (W:447-452).
  - Answer frame with channel key but non-numeric `id` → `protocol` error, whole connection fails (W:106-107, W:426-432).
  - Frames whose top-level key is neither `welcome`, `browser`, `fork` are ignored (`type: "other"`, W:105, W:463-464). Unknown members are skipped (W:14-16).
- Reply detection order: `welcome` key first, then `browser`, then `fork` (W:96-105).

### Concurrency
- Requests are pipelined and correlated by id; any number may be in flight (W:228-233). Answers may arrive in any order.
- Within one tab omp mostly awaits each op sequentially, but `#pull` (events) is single-flight (T:562-590) and recording capture loops concurrently with other ops (T:2648-2664). Host must therefore handle concurrent ops on the same block.

### Timeouts (client-side only; "the daemon never times out itself", W:197)
Default per request 30 s (W:24). `OT`: `budgetBound = max(1, cellMs − 1000)` (OT:43-44; `CELL_BUDGET_SLACK_MS = 1000`, run-scope.ts:300); `quickOpMs = min(budgetBound, 20 000)`; `actionOpMs = min(budgetBound, 8 000)` (OT:18-19, OT:45-49). `cellMs` = run's `timeoutMs`, or 30 000 outside a run (T:493-495).

| op | timeout | ref |
|---|---|---|
| `open` | whole open budget (`opts.timeoutMs`) | T:398-408 |
| `close` | caller's (5 s `DEFAULT_TAB_CLOSE_TIMEOUT_MS`; 5 s for late-answer close) | T:411, T:479, TS:233 |
| any `#op` without explicit | `budgetBound` | T:502 |
| `events` | 10 s | T:565 |
| `state` | `quickOpMs` | T:747 |
| `capture` (screenshot) | `quickOpMs` | T:1777 |
| `capture` (recording loop) | 10 s | T:2652-2655 |
| `pdf` | `budgetBound` | T:1922 |
| `goto` | navigation timeout (default `budgetBound`) | T:946-950 |
| `eval` for `a11y` | run `timeoutMs` | T:1713-1718 |
| `clipboard` | default 30 s (no `block`) | T:2107, T:2114 |
| `fork` | 10 s | CC:107, CC:1354 |

Abort signal (run cancelled/tab closed) abandons the op immediately (W:332-334, TS:832-833).

### Late answers
- Timed-out/aborted ops are "abandoned"; only those with `onLateAnswer` are remembered (max 256, W:204, W:479-487).
- Only `open` registers one: if a late `open` answer has `block:number`, omp sends `{"op":"close","block":B}` (5 s) (T:409-412).
- Answers to any other unknown id are dropped silently (W:489-500).

### Close / reconnect
- Socket `error`/`close` → every pending op rejects with `closed` (after welcome) or `connect` (before) (W:383-402, W:503-513).
- A failed/closed client is **permanently dead**: `connect()` rethrows the stored close error (W:292); there is no reconnect logic in `wire.ts`. **[INFERENCE]** a new handle (new client) is only created if the registry drops the old handle; GUI host restarts will therefore break open tabs until omp reopens them.
- `close()` from omp rejects pending ops and destroys the socket (W:356-358).

### Error kinds
Protocol kinds mapped 1:1: `invalid`, `not_found`, `not_agent`, `no_window`, `window_closed`, `unsupported`, `js`, `failed` (W:224-233); unknown kind strings → `failed` (W:216). Client-own kinds: `connect`, `closed`, `timeout`, `protocol` (W:27-39). Message shown to user: `Tern <label> failed (<kind>): <message>` (W:217).

Special handling:
- **Fallback to Chromium**: `isTernUnavailable` = kind ∈ {`no_window`, `unsupported`, `connect`} (W:54-63). Used only in `openBrowser` when Tern was chosen automatically (not `app.tern:true`) and not aborted (B:350-358). It covers **any** error thrown during the whole open — client connect, `open`, configuration ops (`dialogs`, `allow`, `agent`, `insecure`, `downloads`, `scripts`), the first `goto`, and `readyInfo`'s `state` (T:396-428, TS:693-709). The result then carries a note "Tern cannot show a browser here (...); opened Chromium instead" (B:362).
- `close`: `not_found` or `closed` treated as success (T:480-482).
- `eval`: `js` → page exception text; omp strips the prefix `Tern browser \w+ failed (js): ` (T:248-250) and for kit calls keeps only the first line minus `Error: ` (T:179-181). So the `message` of a `js` error should be the exception's `String(error)`/stack, first line `Error: message`.
- `dialog`: `failed` → "found no pending confirm or prompt" (T:1997-1999).
- Recording loop stops on `closed`/`not_found` (T:2661).
- `/fork`: `closed|timeout|protocol` ⇒ "unconfirmed" (shown as error); any other kind ⇒ silently fork in place (CC:110, CC:1355-1365).

### `fork` channel
Request `{"id":N,"fork":{"block":<TERN_PANE>,"dir"?: "right"|"down"}}` (W:188-194; omp sends only `block`, CC:1354). Result `{"ok":{"block":M}}` – `block` must be a number else `protocol` (W:312-317). Semantics: open `omp --fork` of pane P's session in a new pane beside it (W:11-12). omp flushes the session to disk first (CC:1352). Only sent if welcome listed `"fork"`.

---

## 2. Browser ops

Common: every op except `open` and `clipboard` carries `block: number` (T:497-503). "Result" = the `ok` value. Fields not listed are ignored by omp.

### `open`
- Req: `{op:"open", owner:<TERN_PANE int>, url:"about:blank", width:int, height:int}` (T:398-405). `width/height` = rounded viewport, default 1365×768 (TS:698, launch.ts:42). omp always opens `about:blank` and navigates later.
- Result: `{block:number}` – missing → ToolError (T:415-417). `block` becomes `targetId` (TS:715) and the `tern://block/<n>` endpoint (T:3264).
- Semantics: create a PiP web view owned by pane `owner`. After `open`, omp runs `#configure` (T:758-776) then optional `goto` (T:420-423), then `state` (T:709 → T:357-365). Any failure closes the block (T:424-427).
- Error `no_window`/`unsupported` here ⇒ Chromium fallback (see §1).

### `close`
- Req `{op:"close", block}`. Result ignored. `not_found` OK (T:479-483). Sent on tab close, idle-close (TS:1199-1201: Tern PiPs are reaped by idle-close unless `persist`), force-kill (TS:1700-1701).

### `eval`
- Req `{op:"eval", block, function:STRING, args:JSON[], world:"page"|"isolated", frame?:STRING}` (T:507-513).
  - `function`: JS **function source** – e.g. `"function () {\n...}"`, `"async function (method, args) {...}"` (T:239-240), or user `fn.toString()` which may be an arrow/async arrow/method shorthand (T:538). The host must call it with `args` spread and **await a returned Promise**, returning the settled JSON value.
  - `args`: JSON array; trailing `undefined`s already trimmed (T:212-217).
  - `world`: `"page"` = page's own JS world; `"isolated"` = a separate content world shared by all omp isolated evals in that document (the kit lives at `globalThis.__ompTernKit` there, PK:1303).
  - `frame`: omitted for main frame; else a frame **index path** `"0"`, `"1.0"` = indices into `window.frames` from the top (T:84-85, PK:1305-1323).
- Result: `{value: JSON}`; anything else → `undefined` (T:513). A non-JSON-serialisable return should become `null`/absent **[INFERENCE]**.
- Exceptions (sync or rejected promise) → error kind `js` with the exception text (T:540-545, T:524).
- Must work regardless of page CSP (sources are "no eval", PC:56; PK:1334). **[INFERENCE]** host must use a native evaluate (WKWebView `callAsyncJavaScript:arguments:inFrame:inContentWorld:`), not `eval` in page.
- Uses: kit calls (`KIT_CALL` T:239-240, auto-installs kit when `{missing:true}` T:516-533), user `evaluate`, readyState/inflight probes (T:907-923), rAF wait before capture (T:1770-1776, isolated), `pageContent` (isolated, T:1748-1751), metrics, storage, React, WebMCP, capture body retrieval (T:2414-2420).

### `input`
- Req `{op:"input", block, events: Step[]}` (T:556-559). Steps are "trusted input" (T:5-6, K:2-3) – omp expects native, `isTrusted: true` events (dispatching synthetic DOM events would break sites relying on trust; **[INFERENCE]**).
- Step types (K:12-23):
  - `{type:"mouse", action:"move"|"down"|"up", x, y, button:"left"|"right"|"middle", clicks:int, mods:Mod[]}` – `clicks` 0 for moves, n for the n-th down/up of a multi-click (T:1124-1130). A click = move(clicks 0), down(1), up(1); dblclick adds down(2), up(2) (T:1123-1131). Drag = move, down, 10 moves with button `left` clicks 0, up (T:1338-1350); while a button is held, moves carry that button (T:1291).
  - `{type:"wheel", x, y, dx, dy, mods}` (T:1318, T:1330).
  - `{type:"key", action:"down"|"up", key:DOMkey, code?:DOMcode, mods}` (K:107-117). `key` is already shift-adjusted for letters (K:101-104). `text` field exists in the type but omp never sets it (K:22). Modifier keys arrive as `key:"Shift"|"Control"|"Alt"|"Meta"`, `code:"ShiftLeft"...` (K:72-75). Host must derive the inserted character from `key` for printable keys (typeSteps sends ASCII printable chars as key presses, K:146-156).
  - `{type:"text", text}` – insert text (IME-like `insertText`) for non-ASCII chars and for `fill` (T:1215, K:152).
  - `Mod` = `"shift"|"ctrl"|"alt"|"meta"` (K:9).
- Coordinates: **main-frame viewport CSS px** (frame offsets already added, T:1101-1104, T:1053-1074).
- Unsupported buttons `back`/`forward` are rejected client-side (T:1271-1277).

### `events`
- Req `{op:"events", block, after:<last seq>}` (T:565). Result `{events:[{seq:int, type:string, ...}], dropped?:int}` (T:566-579). Events with `seq <= after` are skipped; seq must increase monotonically per block. omp keeps 4000 (T:235, T:576).
- Event types consumed (T:595-628, T:885-900):
  | type | fields | effect |
  |---|---|---|
  | `url` | `url` | current URL; counts as "changed" for navigation (T:597-599, T:892) |
  | `committed` | `url` | new document: clears pending dialog and frame map (T:600-603) |
  | `loaded` | – | `waitUntil:"load"` satisfied (T:893) |
  | `failed` | `message` | navigation throws `failed: <message>` (T:885-886) |
  | `blocked` | `url` | allowlist block: navigation throws; logged as blocked request (T:887-890, T:620-622) |
  | `title` | `title` | title (T:605-607) |
  | `frame` | `name` (frame path), `url` | frame URL map (T:608-611) |
  | `dialog` | `kind:"alert"|"confirm"|"prompt"|"beforeunload"?`, `message`, `default` (prompt), `handled:null|...` | `handled` null/absent ⇒ pending dialog (T:612-619) |
  | `download` | `id:int`, `url`, `path`, `state:"started"|"finished"|"failed"`, `error` | finished+path ⇒ completed download, size via `fs.stat` of `path` on omp's machine (T:681-704) |
  | `response` | `url`, `status`, `headers`, `mime`, `main:boolean` | navigation response log; `main:false` ⇒ `subdocument` (NL:154-170) |
  | `chooser` | – | file chooser consumed the `files` preset (T:1420-1425) |
  | `message` | `world:"page"`, `body:string` | page-capture message (JSON with `omp:"tern"`) (T:630-679) |
- `message` bodies (PC:86-96): `{omp:"tern", doc, frame, ts, dropped?, kind, ...}` with kinds `console{level,text,args,location?}`, `pageerror{text,location?,stack?}`, `requestfailed{id?,url,error,resourceType?,durationMs?}`, `request{id,method,url,resourceType:"fetch"|"xhr",headers,bodySize}`, `response{id,url,status,statusText,headers,durationMs,routed}` (PC:113-135, 220-333). omp only accepts `world === "page"` (T:631).
- No popup / new-window / auth-challenge event type is consumed anywhere in `T`.

### `state`
- Req `{op:"state", block}`. Result fields read: `url`, `title`, `width`, `height` (viewport), `loading:boolean`, `back:boolean`, `forward:boolean` (T:746-756, T:900-904, T:959-961). `back/forward === false` makes history no-op.

### `dialogs`
- Req `{op:"dialogs", block, policy:"accept"|"dismiss"|"default"}` (T:761, T:2007). `"default"` = "alerts accepted, confirms/prompts held" (T:2005). Sent on every open.

### `dialog`
- Req `{op:"dialog", block, accept:boolean, text?:string}` – resolve the held confirm/prompt. No pending dialog ⇒ error kind `failed` (T:1990-2001).

### `allow`
- Req `{op:"allow", block, hosts:string[]}` (normalised allowlist patterns) – only when `allowedDomains` given (T:762-765). Blocked navigations must emit a `blocked` event (T:887-890). Subresource enforcement: TS:94 says "across navigations and subresources" for workers **[INFERENCE: expected of Tern too]**.

### `agent`
- Req `{op:"agent", block, value:string|null}` – custom user agent; `null` restores default (T:768, T:2049, T:2061).

### `insecure`
- Req `{op:"insecure", block, value:true}` – accept invalid TLS certs (only when `ignoreHttpsErrors`) (T:770).

### `downloads`
- Req `{op:"downloads", block, dir:ABS_PATH}` – enable downloads into `dir` (omp mkdirs it; default `$TMP/omp-downloads-tern-<block>`) (T:2290-2297). Sent at open only if `downloadsPath` given, else lazily on `waitForDownload`/`downloads` (T:2301, T:2322). Host reports via `download` events; `path` must be readable by omp (same machine).

### `scripts`
- Req `{op:"scripts", block, scripts:[{source, world:"page"|"isolated", frames:"main"|"all", at:"start"|"end"}]}` – **replaces** the whole document-start script list for future documents (T:778-827). omp only sends `at:"start"`. Order matters (capture, WebMCP hook, vitals, optional React hook / cursor overlay, user init scripts, then the kit in `isolated`/`all`, T:780-821). Re-sent on init-script changes, route/emulation changes, React enable, recording cursor (T:2264-2285, T:829-842, T:2593-2597, T:2675-2684). Current document is patched separately via `eval` (T:833-841).

### `goto`
- Req `{op:"goto", block, url}` (T:950). Result ignored; completion is detected via events (`committed`/`url`/`loaded`/`failed`/`blocked`) plus `state.loading === false` polled every 4th iteration (T:861-933). Should answer promptly (navigation start), not at load **[INFERENCE from design]**.

### `nav`
- Req `{op:"nav", block, go:"back"|"forward"|"reload"}` (T:956-964).

### `viewport`
- Req `{op:"viewport", block, width:int, height:int}` – resize page layout (T:1047-1050). DPR is not sent; it only becomes capture `scale` (T:1758).

### `files`
- Req `{op:"files", block, paths:string[]|null}` (T:1418, T:1429). Semantics: arm a preset answer for the next native file chooser; `null` disarms. Flow: `files` preset → kit `openChooser` calls `input.showPicker()` or `input.click()` (PK:1143-1156) → host's chooser delegate answers with preset paths and emits a `chooser` event. If no `chooser` within 3 s, omp disarms and falls back to a synthetic drop/`DataTransfer` assignment via kit `setFiles` (T:1431-1440).

### `capture`
- Req `{op:"capture", block, scale:number, format:"png"|"jpeg", quality?:1-100 (jpeg only), rect?:{x,y,width,height}, full?:true}` (T:1758-1769). `rect` in main-frame viewport CSS px; `full` = full page; neither = viewport. Result `{data: base64}` (T:1777-1780). Recording sends `{format:"jpeg", quality, scale:1}` (T:2653).

### `pdf`
- Req `{op:"pdf", block}` → `{data: base64 PDF}` (T:1922-1928). No layout options (T:1911-1920).

### `appearance`
- Req `{op:"appearance", block, value:"light"|"dark"|null}` – `prefers-color-scheme` (T:2065).

### `credentials`
- Req `{op:"credentials", block, username, password}` or `{op:"credentials", block}` (clear) – HTTP auth challenge answers (T:2068-2075).

### `clipboard` (no `block`)
- Read: `{op:"clipboard"}` → `{text}`; write: `{op:"clipboard", text}` (T:2106-2116). System clipboard.

### `edit`
- Req `{op:"edit", block, action:"copy"|"paste"}` – native copy/paste in the page (T:2119-2128).

### `cookies`
- Req `{op:"cookies", block}` → `{cookies:[{name, value, domain, path, expires:number(-1 session), httpOnly, secure, sameSite:"Strict"|"Lax"|"None"}]}` – all cookies of the PiP store; omp filters by URL itself (T:2132-2152, T:219-232).

### `setCookie`
- Req `{op:"setCookie", block, cookie:{name, value, domain, path, expires:number|null, httpOnly, secure, sameSite:string|null}}` (T:2174-2185).

### `deleteCookie`
- Req `{op:"deleteCookie", block, name, domain, path}` (T:2197).

No other op strings exist: the full set of `#op(`/`request({op:` call sites is listed above (grep of `T`); all ops are literals.

---

## 3. Page-side kit and host channels

- **Page world** scripts (via `scripts` and `eval`):
  - Capture (PC): console/pageerror/fetch/XHR observer, routes (abort/delay/fulfill fetch+XHR), geolocation/locale/offline shims; state at `globalThis.__ompTernCapture` (`inflight`, `body(id)`) (PC:51, PC:84-85). Posts `JSON.stringify(message)` to **`webkit.messageHandlers.stencil.postMessage`** (PC:68-69, PC:95); rate-limited 400 msg/s (PC:88-90). The host must surface each as an `events` entry `{type:"message", world:"page", body:<the string>}` (T:630-634).
  - WebMCP hook, vitals, React hook, cursor overlay, user init scripts (T:787-819).
- **Isolated world**: `TERN_KIT_SOURCE` installs `globalThis.__ompTernKit` (idempotent, PK:1336-1351) with methods `count, read, target, focus, prepareFill, caretToEnd, checkState, select, setFiles, isFileInput, openChooser, scrollIntoView, scrollBy, highlight, removeOverlay, observe, ariaSnapshot, annotate, mark, markAll, unmark, frames, frameOf, frameOrigin, hasText, geometry, activeEditable` (PK:109-178). The isolated world shares the DOM (marks via `data-omp-tern-handle` attribute visible to page-world evals, T:243, T:1640-1653).
- **Frame registration**: in child frames the kit posts the frame's index path string (e.g. `"1.0"`) to **`webkit.messageHandlers.stencilFrame`** — from the **isolated** world (PK:1305-1323). The host uses this to map `eval.frame` paths to native frames, and emits `frame` events `{name:<path>, url}` (T:608-611) **[INFERENCE: the mapping purpose; omp only reads the event]**.
- Selectors are resolved entirely in the kit (`css`, `text`, `aria`, `xpath`, `pierce`, semantic `label|placeholder|testid|alt|title|role`, `ariaRef`, `id`, `handle`; selectors.ts:8-25).
- observe/snapshot/refs: `observe()` builds a per-document id registry in the kit (PK:147-148, T:1661-1677); `ariaSnapshot` uses an embedded Playwright ARIA bundle; refs `eN` resolve via `ariaRef` (T:1564-1574). Actions find a point with kit `target()` (actionability checks, T:1080-1118) then send native `input`. No host support beyond `eval`+`input` is needed.
- Network: navigations/subresources are not observable from page script; only `response`/`blocked` events give document responses (PC:11-13). Bodies of non-fetch responses unavailable (T:2797-2802).

## 4. Unsupported on Tern / emulation

Thrown client-side, never sent to the host:
- `traceStart/Stop`, `profileStart/Stop` – no trace/profiler protocol (T:2726-2744).
- `emulate({timezone})`, `headers`, `reducedMotion`, `cpuThrottling≠1`, `network` (T:2016-2036).
- `pdf` with `format/landscape/scale/printBackground/margin/pageRanges` – only `path` (T:1911-1920).
- Mouse `back`/`forward` buttons (T:1271-1277).
- `browser.pages()` returns only this tab's page (T:3256-3258); `version()` = `"Tern WKWebView"` (T:3260-3262).
- WebMCP native tools: `nativeSupported:false` (T:2584-2588).
- Response bodies only for fetch/XHR of the current document (T:2797-2802).

Emulation (T:2015-2101):
- `device`: Puppeteer preset → `viewport` op (w/h), DPR stored for capture scale, `agent` op with device UA (T:2037-2051). `isMobile`/`hasTouch` are **not** applied (no touch op exists).
- `viewport {width,height,scale}` → `viewport` op + capture scale (T:2052-2059). Supervisor uses this on tab reuse (TS:401-404).
- `userAgent` → `agent`; `colorScheme` → `appearance`; `credentials` → `credentials`.
- `geolocation`, `locale`, `offline` → page-world shims in capture script, re-applied via `scripts` + `eval` (T:2076-2096, PC:343-403). Offline only fails fetch/XHR and `navigator.onLine` (PC:223, PC:274, PC:347).

## 5. Popups, tabs, dialogs, auth, chooser, downloads

- **Popups / `window.open` / `target=_blank`**: omp has no event, op or handling for them (no `popup`/new-window type in `#ingest`, T:595-628; `pages()` single, T:3256). **[INFERENCE]** host should load such navigations in the same PiP (or block them); a separate window would be invisible to omp.
- **Multiple tabs**: each omp tab name = its own `open` → separate `block` on the same connection (T:398, TS:693). All ops are block-scoped; `clipboard` is global.
- **Dialogs**: policy `"default"` unless the open passes `dialogs` (T:761): alerts auto-accepted, confirm/prompt held and reported by a `dialog` event with `handled` null; resolved by `dialog` op; `setDialogs(accept|dismiss)` resolves a held one immediately (T:2006-2010). A held dialog is cleared on `committed` (T:600-603). `beforeunload` not specially handled **[INFERENCE: treat like confirm]**.
- **HTTP auth**: `credentials` op; nothing else (T:2068-2075). **[INFERENCE]** without credentials the host should cancel the challenge.
- **File chooser**: `files` preset + `chooser` event (see §2 `files`).
- **Downloads**: `downloads` op + `download` events (see §2).

## 6. Implementation notes for a GUI host

**Minimum for omp to work at all** (anything failing during open ⇒ fallback only for `no_window|unsupported|connect`, otherwise the open errors):
1. Socket + framing + `hello`→`welcome` (≤10 s; `ops` may be `[]`).
2. `open` (return `block`), `close`.
3. `dialogs` and `scripts` — **sent unconditionally on every open** (T:761, T:776); must succeed (answering `unsupported` here makes omp fall back to Chromium).
4. `goto` + `events` with at least `committed`, `url`, `loaded`, `failed`, `title`; `state` (`url,title,width,height,loading,back,forward`) — used by open's `readyInfo` (T:357-365).
5. `eval` with both worlds (`page`, `isolated`), Promise awaiting, and `frame` paths (main frame mandatory; child frames for iframe helpers).
6. `input` (mouse, wheel, key, text) — all clicks/typing go through it.
7. `capture` (png + jpeg, `rect`, `full`, `scale`) — screenshots/recording.
8. `viewport`, `nav`.
9. Script-message handlers `stencil` (page world) and `stencilFrame` (isolated world) feeding `message`/`frame` events.

**Can answer `{"error":{"kind":"unsupported",...}}` safely** (only that helper fails; not sent during open unless the corresponding open option is used): `pdf`, `appearance`, `credentials`, `clipboard`, `edit`, `cookies`, `setCookie`, `deleteCookie`, `files` (but then `uploadFile` on a real file input fails rather than falling back — the `files` error propagates, T:1418), `downloads` (only `waitForDownload`/`downloads()` fail), `dialog` (prefer `failed` when none pending). `allow`, `agent`, `insecure`, `downloads` are sent during open **only** if `allowedDomains`/`userAgent`/`ignoreHttpsErrors`/`downloadsPath` were requested — `unsupported` there causes Chromium fallback in auto mode.

**Gotchas**
- `TERN_PANE` must be a decimal integer (KD:25-28); it is echoed back as `open.owner` and `fork.block`.
- Tern mode outranks cmux (B:213-225); exporting both env sets means omp uses Tern only.
- Welcome must arrive before 10 s and must be a JSON object frame with a `welcome` key; a peer that never welcomes yields `connect` ⇒ fallback (good failure mode).
- Every answer needs the exact request `id` and the same channel key; a malformed `id` kills the connection (W:106-107).
- Client never reconnects after a socket drop (W:292, W:503-513).
- `eval` `message` for `js` errors: first line `Error: <msg>` is shown to the agent (T:179-181).
- `events` `seq` must be strictly increasing and stable per block; `after` is the last seen seq.
- Script handler names are WebKit-specific (`webkit.messageHandlers.stencil|stencilFrame`, PC:68, PK:1321). **[INFERENCE]** WebKitGTK supports the same `window.webkit.messageHandlers.<name>` API and script worlds natively; on WebView2 the host must inject a shim object `webkit.messageHandlers.{stencil,stencilFrame}.postMessage` forwarding to `chrome.webview.postMessage` in each world, and emulate the isolated world (e.g. CDP `Page.createIsolatedWorld` via `CallDevToolsProtocolMethod`); WebView2 `ExecuteScriptAsync` has no frame/world parameters, so `frame` paths need CDP or `CoreWebView2Frame.ExecuteScriptAsync`.
- Windows: omp uses `net.createConnection({ path })` (W:373). **[INFERENCE]** in Node/Bun on Windows a `path` of the form `\\.\pipe\<name>` connects to a named pipe, so `TERN_PANE_SOCKET=\\.\pipe\omp-gui-…` should work; AF_UNIX socket files on Windows 10+ are also an option. Not verified for Bun 1.4.2.
- Idle-close reaps Tern PiPs like headless tabs (TS:1199-1201) — expect unsolicited `close` ops.
- `capture` is called after a rAF wait in the isolated world (T:1770-1776); background/hidden web views may never deliver rAF — kit uses a 50 ms timeout fallback (PK:650-654), but the host should keep the view rendering (hidden views may capture blank) **[INFERENCE]**.

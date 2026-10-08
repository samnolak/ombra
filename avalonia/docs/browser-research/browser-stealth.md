# Driving the embedded browser without bot signals: research and design (2026-10-08)

Scope: the preview's web view (Avalonia `NativeWebView` 12.1: WKWebView on macOS, WebView2 on Windows, WebKitGTK 4.1 on Linux/X11), driven by omp through our bridge.
Constraints: no macOS Accessibility or Screen Recording permission, no `CGEventPost`, never activate the app or reorder windows, no omp patches.
Method:
- Read WebKit `main`, Chromium `main` and CDP sources.
- Decompiled the shipped `Avalonia.Controls.WebView.dll` 12.1.0 with ilspycmd into `/tmp/avwv`.
- Read the omp 18.8.0 Tern sources and the repo.
- **Nothing was run against a live web view.** `[INFERENCE]` marks a conclusion I reasoned out but did not see in a source.

Companion notes (no duplication here):
- `local://browser-input.md`: input mechanics (how to build NSEvents, CDP calls and GdkEvents).
- `local://omp-tern-protocol.md`: the Tern host protocol omp speaks.
- `local://omp-browser.md`: backend precedence and defaulting.

This note covers **what a site can observe** and how to avoid every signal that is not inherent to "a real person using a real WebKit/Edge browser on their own Mac/PC".

---

## 0. Bottom line

1. **Principle: be the user's real browser and never fake an identity.**
   - The engine, IP, cookies, fonts, GPU and screen are all genuinely the user's. Most bot verdicts come from automation artefacts: untrusted events, page-world globals, CDP side effects, missing mouse trajectories, impossible timings.
   - Remove those artefacts. Do **not** spoof the fingerprint (no Chrome UA on WebKit, no fake `window.chrome`, no canvas noise). Spoofing makes a WebKit browser *inconsistent*, and inconsistency is what detectors score.
2. **Trusted native input is feasible on all three platforms without permissions.** See the verdict table in §3. On macOS, three details decide whether the events look hardware-made, and Safari's own WebDriver gets two of them wrong:
   - the timestamp base;
   - `MouseEvent.buttons`;
   - `movementX/Y`.
3. **Today's footprint is large** (§1): synthetic `isTrusted=false` events, about five page-world globals, an `<omp-annotate>` element in every page, and Avalonia's `invokeCSharpAction` bridge. Moving our code into an isolated world (WKContentWorld, CDP isolated world, WebKitGTK script world) removes most of it.
4. **omp's own Tern page-world capture** (console/fetch/XHR wrappers) is visible to pages by design (omp `tern/page-capture.ts:9-10`). We cannot change omp. The host decides whether to install it per site (§6.4).
5. **CAPTCHAs and human checks are never automated.** We detect them, pause, and hand control to the user (§7). This is the same policy as Claude in Chrome and Codex.

---

## 1. Current footprint (repo evidence)

| # | Artefact | Where | What a page can see |
|---|---|---|---|
| F1 | Clicks are `new MouseEvent/PointerEvent` + `dispatchEvent` + `el.click()` | `AgentBrowserScripts.cs:199-214` | `isTrusted === false`. No user activation, so popups, file pickers and clipboard are blocked. `pointerdown` without a preceding `pointermove`. `screenX/Y = 0`. |
| F2 | Typing is synthetic `KeyboardEvent`, then `setRangeText` and a synthetic `InputEvent` | `:216-243`, `:289-304` | Untrusted keydown/input. No `beforeinput`. The `keypress` order differs from the engine's. `data`/`inputType` are set by us. |
| F3 | `fill` sets `.value` through the native setter, with no keys | `:219-223`, `:305-321` | A value appears with no key, paste or composition events. A classic bot signal. |
| F4 | Scroll is `window.scrollBy`/`el.scrollBy`; reveal is `scrollIntoView({block:"center"})` | `:215`, `:333-336`, `:386-404` | `scroll` events with no `wheel`, `touch` or `key` events. Instant jumps. |
| F5 | Focus is `el.focus()` without a click | `:209`, `:277`, `:291`, `:307` | `focus` with no pointer or keyboard cause. |
| F6 | Page-world globals `__ompAgent` (non-enumerable but `in`-detectable) and `__ompBridgeRun` (enumerable) | `:20-21`, `:405`, `:426`, `:442` | `'__ompAgent' in window`. Pages can also *forge* it and feed us a fake snapshot (prompt injection). |
| F7 | `annotate.js` is injected on **every** successful navigation, even with annotation mode off | `PreviewPanel.axaml.cs:202-206, 232-244`; `annotate.js:12, 19-48, 231` | `<omp-annotate>` appended to `<html>` (shows up in `MutationObserver` and `document.documentElement.lastChild`), plus `window.__ompAnnotate`. |
| F8 | Avalonia's own bridge, macOS: a page-world handler `postAvWebViewMessage` plus a global `function invokeCSharpAction` re-defined after every navigation | decompiled `MaciosWebViewAdapter.cs:133-139, 332` | `window.webkit.messageHandlers.postAvWebViewMessage` (also tells the page "embedded WKWebView, not Safari") and `typeof invokeCSharpAction === "function"`, a known .NET hybrid-webview marker. |
| F8w | Same bridge on Windows: `AddScriptToExecuteOnDocumentCreated(invokeCSharpAction → chrome.webview.postMessage)` | decompiled `WebView2BaseAdapter.cs:387-389` | `invokeCSharpAction`, `window.chrome.webview`. |
| F8g | Same bridge on Linux: `register_script_message_handler("postAvWebViewMessage")` plus a user script | decompiled `GtkWebViewAdapter.cs:179-180` | Same as F8. |
| F9 | Title read with `InvokeScript("document.title")` in the page world | `PreviewPanel.axaml.cs:209` | A page that hooks the `document.title` getter sees an access from a stackless frame. WKWebView's KVO `title` gives the same value natively. |
| F10 | All script runs in the page world: Avalonia mac `InvokeScript` → `evaluateJavaScript:completionHandler:` | decompiled `MaciosWebViewAdapter.cs:193-197`, `WKWebView.cs:197-207`; WebView2 `ExecuteScript` (`WebView2BaseAdapter.cs:198-205`); GTK `webkit_web_view_run_javascript` (`GtkWebViewAdapter.cs:239-253`) | Pages can monkey-patch `getBoundingClientRect`, `querySelector`, `MouseEvent`, `JSON.stringify`, and so on. That both detects us and **lies to the agent**. |
| F11 | UA: none set (`PreviewPanel.axaml.cs:129`) | — | WKWebView's default UA lacks `Version/x Safari/605.1.15`. That is the textbook "in-app WebKit browser" UA, which Google sign-in rejects (NativeDialogs covers this; §4.6). |
| F12 | omp's cmux backend and, later, Tern page-world capture: console wrapped, `fetch`/`XHR` replaced, `__ompTernCapture` (omp `page-capture.ts:51-85, 111-112, 276, 347-375`) | omp | `console.log.toString()` is not `[native code]`, `'__ompTernCapture' in window`, `webkit.messageHandlers.stencil`. |
| F13 | omp Tern kit DOM marks: `data-omp-tern-handle` on elements while an ElementHandle is alive; highlight/annotation overlay `<div data-omp-tern-overlay>` | omp `page-kit.ts:223-243, 1166-1231` | Attribute and DOM mutations. Short-lived; used only by handle/highlight/annotated-screenshot APIs. |

---

## 2. Signals detectors use, and how each one is addressed

"Inherent" means a property of the real engine and user. It is left alone on purpose.

| Signal | What detectors check | Our status after the fix | How |
|---|---|---|---|
| `Event.isTrusted` | `false` ⇒ bot | ✅ true | native input per platform (§3) |
| User activation | `navigator.userActivation`; popups or pickers refused | ✅ granted by trusted press/keydown | same |
| `event.timeStamp` vs `performance.now()` | must be ≈ the dispatch time | ⚠️ mac needs care | WebKit takes the DOM timestamp from the NSEvent: `MonotonicTime::fromRawSeconds(event.timestamp)` (`Shared/mac/WebEventFactory.mm` L187, L264, L339) → `Event::m_createTime` → `timeStampForBindings` (`WebCore/dom/Event.cpp` L68, L186-197). **Use `NSProcessInfo.systemUptime`.** Safari's WebDriver passes `[NSDate timeIntervalSinceReferenceDate]` (`WebAutomationSessionMac.mm` L223, L811, L915). That would date our events ~25 years off: an instant tell [INFERENCE: arithmetic from the cited code]. |
| `MouseEvent.buttons` on mousedown/drag | real press ⇒ `buttons=1` | ⚠️ mac: 0 unless fixed | The user-driven path reads the **hardware** state: `WebEventFactory.mm` L181 → `currentlyPressedMouseButtons()` = `[NSEvent pressedMouseButtons]` (`WebCore/platform/mac/PlatformEventFactoryMac.mm` L163-166). Safari swizzles that class method for the duration of dispatch (`WebAutomationSessionMac.mm` L85-104). We do the same: scoped `method_setImplementation` on the NSEvent metaclass around our synchronous responder call, restored in `finally`. It is main-thread only, so no real event is processed inside the scope. |
| `movementX/Y` on mousemove | zero deltas on every move ⇒ synthetic | ⚠️ mac: set deltas | build the move with `mouseEventWithType`, take `.CGEvent`, set `kCGMouseEventDeltaX/Y`, rebuild with `eventWithCGEvent:` (Safari does this, `WebAutomationSessionMac.mm` L255-266). PlatformMouseEvent reads `event.deltaX/Y` (`PlatformEventFactoryMac.mm` L748). Creating a CGEvent needs no permission; posting one does, and we never post. |
| `screenX − clientX` | must equal the window/page offset on screen | ✅ mac/GTK (native coordinates); ⚠️ WebView2, verify | Chromium CDP `Input.dispatchMouseEvent` historically set screen = widget position (crbug [40280325](https://issues.chromium.org/issues/40280325); Turnstile flags it, see [ObjectAscended readme](https://github.com/ObjectAscended/CDP-bug-MouseEvent-.screenX-.screenY-patcher)). Chromium `main` builds the event with screen = widget (`content/browser/devtools/protocol/input_handler.cc` L505-506) but then, once the target widget is resolved, sets `SetPositionInScreen(ConvertWidgetPointToScreenPoint(...))` = widget point + view bounds on screen (L1629-1640, L626-632). So it is **fixed on current Chromium** [INFERENCE: the installed Evergreen WebView2 includes it; check with the self-test, §9]. |
| Pointer trajectory | click with no preceding moves, straight lines, constant speed, exact centre | ✅ host humaniser | §5. mac hover needs `_simulateMouseMove:` (§4.2). |
| Coordinate quantisation | fractional CSS coordinates at DPR 1, or always integer-centre | ✅ | generate in device pixels (integers), then divide by DPR |
| Scroll without input | `scroll` with no `wheel` | ✅ | wheel events (CGEvent scroll / CDP `mouseWheel` / GDK scroll) for reveal and scroll; `scrollIntoView` only as a fallback for wheel-proof containers |
| Typing rhythm | 0 ms intervals; `keyup` before `keydown`; identical dwell | ✅ | §5 |
| Value-without-keys (`fill`) | `input` with no keydown/paste | ⚠️ partly | ASCII ≤ 64 chars: typed as keys; longer or non-ASCII: `insertText` (trusted `beforeinput`/`input`, IME-like, the same as a dictation/IME commit) |
| `document.hasFocus()` / `visibilityState` while acting | keystrokes into a blurred or hidden document | ⚠️ inherent when the user is in another app | §4.3. Do not fake on mac (no public API). On Windows, scope `Emulation.setFocusEmulationEnabled` to the action, as Codex does. |
| `navigator.webdriver` | `true` ⇒ automation | ✅ false on all three | WebKit returns `page.isControlledByAutomation()` (`Source/WebCore/Modules/webdriver/NavigatorWebDriver.cpp`, [PR 73746](https://github.com/WebKit/WebKit/pull/73746)). That is set only by an automation session or the private `_controlledByAutomation` config (`WKWebViewConfigurationPrivate.h` L92). **Never set it.** Chromium sets it only for `--enable-automation`, `--headless` or `--remote-debugging-port=0` ([MDN](https://developer.mozilla.org/en-US/docs/Web/API/Navigator/webdriver)). `CallDevToolsProtocolMethod` is an in-process session and uses none of these [INFERENCE; Codex relies on the same]. |
| Page-world globals and handlers | `Object.keys(window)` diff, `'x' in window`, `fn.toString()`, `webkit.messageHandlers`, `chrome.webview` | ✅ for ours; ⚠️ omp capture and Avalonia bridge | §6 |
| CDP side effects (Windows) | `Runtime.enable` makes `console.debug(err)` serialise the error, which fires a `stack` getter ([DataDome write-up](https://github.com/zackiles/cdp-proxy-interceptor/blob/main/docs/detecting-runtime-enable.md)); `Debugger.enable` makes `debugger;` traps pause | ✅ | Never call `Runtime.enable`, `Debugger.enable`, `Console.enable` or `Log.enable`. Use `Page.createIsolatedWorld` / `Runtime.addBinding` instead ([rebrowser-patches](https://github.com/rebrowser/rebrowser-patches#fix-runtimeenable-leak)). |
| UA ↔ platform ↔ Client Hints ↔ engine features | Safari UA but Chrome APIs, or the reverse; UA-CH missing or contradicting the UA | ✅ by not spoofing | §4.6 / §5.4 / §6.5 |
| WebGL, fonts, audio, canvas, timezone, language, screen | consistency with UA and IP | inherent | untouched: they are the user's real machine |
| IP reputation | datacenter ⇒ bot | inherent | the user's own network (we are not a cloud agent) |

---

## 3. Feasibility verdicts per technique

| Technique | Verdict | Evidence |
|---|---|---|
| **mac:** in-process NSEvent → `-[WKWebView mouseDown:/mouseUp:/mouseDragged:/rightMouse…/otherMouse…/keyDown:/keyUp:/flagsChanged:/scrollWheel:]` | ✅ **Recommended.** Trusted DOM events plus user activation. No permission: nothing reaches the WindowServer. | WKWebView forwards these to `WebViewImpl` as `WebEventInputSource::UserDriven` (`UIProcess/API/mac/WKWebViewMac.mm` L562-614, L644-652). Safari's own WebDriver synthesises NSEvents the same way (`UIProcess/Automation/mac/WebAutomationSessionMac.mm` L80-124, L207, L821-850, L930-936). Activation comes from WebCore `UserGestureIndicator` (see browser-input.md §2.1). |
| **mac:** `-[NSWindow sendEvent:]` on our window | ⚠️ Works, but not on our shared window | Safari also calls `makeKeyAndOrderFront:` before every event (`WebAutomationSessionMac.mm` L110), which our no-focus-steal constraint forbids. Avalonia's `AvnWindow sendEvent:` also has popup/light-dismiss side effects (browser-input.md §2.5). Direct responder calls run the identical WebKit path. |
| **mac:** does the window need to be key? | Not for delivery. **Yes for page focus.** | `WebViewImpl::isFocused` = window first responder is the view (`WebViewImpl.mm` L1786-1793). `PageClientImpl::isViewWindowActive` = `isKeyWindow` (`PageClientImplMac.mm` L177-181). `Document::hasFocus` requires `focusController().isActive() && isFocused()` (`WebCore/dom/Document.cpp` L10429-10438). In a background app, `hasFocus()` is false. |
| **mac:** any permission? | None | Constructing NSEvent/CGEvent objects is unprivileged. TCC gates only `CGEventPost`/`CGEventTap` (Accessibility/Input Monitoring) and screen capture. Screenshots already use `takeSnapshotWithConfiguration:` (`MacWebViewSnapshot.cs:9-11`). |
| **mac:** text via `insertText:replacementRange:` (NSTextInputClient) | ✅ for non-ASCII, long text and IME-like commit. Trusted `beforeinput`/`input`, **no** activation. | `WKWebViewMac.mm` L1584; `WebViewImpl::insertText` executes immediately outside key handling (`WebViewImpl.mm` L6149-6200). |
| **mac:** IME | ⚠️ synthetic keyDown goes through the user's active input method (Japanese IME, Russian layout) | browser-input.md §2.3. Mitigation: only ASCII printable keys as keyDown, and only when the current input source is ASCII-capable [INFERENCE: `TISCopyCurrentKeyboardInputSource`, unprivileged]. Otherwise use `insertText`. |
| **mac:** hover/mousemove | ⚠️ needs SPI `-[WKWebView _simulateMouseMove:]` (macOS 13+) | WKWebView does not implement `mouseMoved:`. Moves arrive through a tracking-area owner `WKMouseTrackingObserver` (`WebViewImpl.mm` L240-276, L1314-1354). Safari calls `_simulateMouseMove:` (`WebAutomationSessionMac.mm` L116-124; declared `WKWebViewPrivate.h` L961). Public-API fallback: iterate `webView.trackingAreas` and send `mouseMoved:` to the area whose `owner` responds to it. That reaches the same `impl->mouseMoved` after a hit-test (`WebViewImpl.mm` L271-276) [INFERENCE: relies on an internal object, but no private selectors]. |
| **mac:** private SPI risk overall | Acceptable with `respondsToSelector:` guards. Our build is not distributed through the Mac App Store, where private API use is a rejection reason [INFERENCE: `tools/package` builds a notarised DMG/zip]. Notarisation does not scan for SPI. | SPIs worth using: `_simulateMouseMove:` (13+), optionally `_setWindowOcclusionDetectionEnabled:` (`WKWebViewPrivate.h` L891) and `_overrideDeviceScaleFactor` (L542). **Never** use `_controlledByAutomation` or `_WKAutomationSession`: they set `navigator.webdriver`. |
| **mac:** isolated script world | ✅ public API (macOS 11+) | `+[WKContentWorld worldWithName:]` (`WKContentWorld.h` L76), `callAsyncJavaScript:arguments:inFrame:inContentWorld:completionHandler:`, `WKUserScript …inContentWorld:`, `addScriptMessageHandler:contentWorld:name:`. Avalonia already binds `callAsyncJavaScript` but only uses it with `defaultClientWorld` for JSON conversion (decompiled `WKWebView.cs:244-266`). |
| **Win:** CDP `Input.dispatchMouseEvent/dispatchKeyEvent/insertText/dispatchTouchEvent` via `ICoreWebView2::CallDevToolsProtocolMethod` (vtable slot 36, decompiled `CallDevToolsProtocolMethod_36`) | ✅ **Recommended.** Trusted, with activation. | Codex does exactly this, preceded by `Emulation.setFocusEmulationEnabled` (codex-browser-internals §2.2). Chromium routes it through `RenderWidgetHost` (browser-input.md §3). |
| **Win:** `ICoreWebView2CompositionController::SendMouseInput` | ⚠️ visual-hosting mode only (Avalonia `WebView2CompAdapter`, used with `ExperimentalOffscreen`); mouse only | decompiled `WebView2CompAdapter.cs:22, 335` |
| **Win:** `ExecuteScript` world | ❌ main world only, no isolated-world API | [WebView CG usage doc](https://webview-cg.github.io/usage-and-challenges/) ("Android and Windows WebViews can only execute code in the page context"); MS docs [javascript.md](https://github.com/MicrosoftDocs/edge-developer/blob/main/microsoft-edge/webview2/how-to/javascript.md). Use CDP worlds instead (§6.3). |
| **Win:** mobile emulation with CDP `Emulation.setDeviceMetricsOverride{mobile,deviceScaleFactor,screenWidth/Height}` + `setTouchEmulationEnabled{maxTouchPoints}` + `setUserAgentOverride{userAgent,userAgentMetadata}` | ✅ for a dev preview. ⚠️ not undetectable: WebGL renderer (ANGLE/D3D11), fonts and hardware stay Windows. | Keep the Chromium major in the UA and the metadata identical to the real engine's. Settings `UserAgent` alone **clears** `navigator.userAgentData` brands ([WebView2Feedback #2576](https://github.com/MicrosoftEdge/WebView2Feedback/issues/2576), [docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2settings.useragent)), an inconsistency, so use the CDP override with metadata. |
| **Linux:** GdkEvent (GTK3) delivered to the WebKitWebView widget on the GLib thread | ✅ feasible, needs a spike | WebKit's GTK WebDriver synthesises through internal, non-exported `webkitWebViewBaseSynthesize*Event` (`UIProcess/Automation/gtk/WebAutomationSessionGtk.cpp` L82-104, L323-340), so we build GdkEvents ourselves. Avalonia's offscreen GTK adapter already does (browser-input.md §4). On GDK, `buttons` comes from the event's `state` mask, so set `GDK_BUTTON1_MASK` on motion while pressed [INFERENCE]. Event `time` must be on the X server clock: use `gdk_x11_get_server_time` or the last real event time plus elapsed [INFERENCE]. |
| **Linux:** isolated world | ✅ | `webkit_web_view_evaluate_javascript(…, world_name, …)` and `webkit_web_view_call_async_javascript_function` (2.40+); `webkit_web_view_run_javascript_in_world`, `webkit_user_script_new_for_world`, `webkit_user_content_manager_register_script_message_handler_in_world` (2.22+). Avalonia uses page-world `run_javascript` (decompiled `GtkWebViewAdapter.cs:253`). |
| Human-like timing | ✅ host-side, inside the `input` executor | omp sends a click as `[move, down, up]` with no delays (`tern-tab.ts:1146-1151`) and typing as one batch of key steps (`keys.ts:144-157`). Pacing is ours to add. The `input` op budget is `budgetBound` (≥ 29 s default; omp-tern-protocol §1). |

---

## 4. macOS specifics (stealth-relevant only)

### 4.1 Event construction checklist
For every synthetic NSEvent:
- **`timestamp` = `NSProcessInfo.processInfo.systemUptime`**, never `timeIntervalSinceReferenceDate` (§2).
- `windowNumber` = the web view's window; `eventNumber` increasing (not 0; Safari uses 0 as its automation marker, L74, L137-148).
- `clickCount` per sequence: 1, then 2 within the double-click interval.
- `pressure` 1.0 on down, 0 on up.
- Mouse moves carry CGEvent deltas.
- Scoped `pressedMouseButtons` swizzle around `mouseDown:`/`mouseDragged:`/`mouseUp:`: bitmask while held, 0 after up.
- Coordinates: CSS → points (`× pageZoom × magnification`) → `convertPoint:toView:nil`. Quantise to device pixels (`backingScaleFactor`).
- ⌘-shortcuts go through `-[WKWebView performKeyEquivalent:]` directly, so our menus never see them (browser-input.md §2.2).

### 4.2 Hover
Prefer `_simulateMouseMove:` with a `respondsToSelector:` guard. Fall back to the tracking-area owner (§3). If both are missing, **no mousemove at all**. That is a trajectory gap: report `input:"partial"` to the agent and skip humanised paths, since they would not be delivered anyway.

### 4.3 Focus and visibility, without stealing focus
- Do not call `makeKeyAndOrderFront:` or `NSApp activate`.
- Before keyboard actions, `[window makeFirstResponder:webView]` **only if** the user is not typing in one of our text fields; restore the previous first responder afterwards (browser-input.md §2.3). Clicks make the web view first responder anyway when the user later interacts.
- If our window is not key, `document.hasFocus()` is false while we type. Real users cannot type into an inactive window, so this is a mild anomaly. Policy:
  - **Clicks/hover:** allowed.
  - **Keyboard:** allowed, but the result carries `"focus":"background"` so omp/the model knows.
  - No fake focus is available publicly on WebKit.
- Keep the page alive when the user switches away: `WKPreferences.inactiveSchedulingPolicy = WKInactiveSchedulingPolicyNone` (public, macOS 14+, `WKPreferences.h` L85-99). Our minimum is 14 (browser-input.md §2.3). Optional SPI `_setWindowOcclusionDetectionEnabled:NO` only while an agent action runs: occluded windows otherwise report `visibilityState:"hidden"` and stop rAF (`PageClientImplMac.mm` L209-213). Off by default, because it misreports visibility.

### 4.4 Scripts
- Move everything we own into `[WKContentWorld worldWithName:@"ompgui"]`: the snapshot library, title/state probes, annotations logic.
- Tern's `world:"isolated"` maps to a second named world `"omp"`.
- Message handlers for our code use `addScriptMessageHandler:contentWorld:name:` in **our** world. That world is invisible to `window.webkit` in the page world [INFERENCE: handlers are per world, by WebKit API design].

### 4.5 Avalonia's page-world bridge (F8)
- Avalonia unconditionally registers `postAvWebViewMessage` in the page world and re-defines `invokeCSharpAction` after every navigation (decompiled `MaciosWebViewAdapter.cs:139, 332`). There is no option to disable either; `ScriptHandlerMessageName` only renames the handler.
- Options:
  - (a) After creation, take `WKWebView.configuration.userContentController` from the platform handle and `removeScriptMessageHandlerForName:` it. Move annotations to our world's handler. The leftover `invokeCSharpAction` global stays (a dead function).
  - (b) Host our own WKWebView in a `NativeControlHost`. That also gives `WKUIDelegate` (dialogs, `window.open`), downloads and a navigation delegate, which NativeDialogs needs anyway. It removes F8 completely.
- **Recommend (b)** if the native-dialogs work goes that way; otherwise (a).

### 4.6 Identity
- `applicationNameForUserAgent = "Version/<v> Safari/605.1.15"` (Avalonia: `AppleWKWebViewEnvironmentRequestedEventArgs.ApplicationNameForUserAgent`). This yields Safari's exact UA format and keeps WebKit's own platform part.
- `<v>`: Safari.app is a cryptex symlink. Safari updates ship a **staged WebKit** that every app's WKWebView loads. On this machine `/System/Cryptexes/App/System/Library/StagedFrameworks/Safari/WebKit.framework` reports `CFBundleVersion 21625.1.29.18.28`, equal to Safari 27.0's `CFBundleVersion`, while `/System/Library/Frameworks/WebKit.framework` says `21624.5.1.11.3`.
  - So take Safari's `CFBundleShortVersionString` only when Safari's `CFBundleVersion` equals `[[NSBundle bundleForClass:WKWebView.class] objectForInfoDictionaryKey:@"CFBundleVersion"]` (the engine actually loaded).
  - Otherwise fall back to the OS major.minor (Safari numbering = macOS numbering from 26 on).
  - Shared with NativeDialogs.
- Leave `navigator.platform/vendor` and the absence of `userAgentData` alone: they are Safari-consistent.
- Residual, inherent tells that this is not Safari.app: no `window.safari`, and `window.webkit.messageHandlers` if any page-world handler exists. Do not fake `window.safari`.
- **Mobile presets on WKWebView** = viewport size (+ optional DPR via `_overrideDeviceScaleFactor`) only. An iPhone UA on a Mac engine without touch is inconsistent (`ontouchstart`, `maxTouchPoints=0`, `pointer:fine`, Mac `screen`). Offer UA swap only as an explicit "Mobile user agent" toggle, labelled "Sites can tell this is not a phone". Codex's device mode is viewport-only too (codex-browser-internals §1, L46-49).

---

## 5. Human-like pacing (host-side "humaniser")

- **Purpose:** remove the timing and trajectory artefacts of instantaneous steps, and give pages time to react (debounce, autocomplete).
- **Not a goal:** beating CAPTCHAs; those go to the user (§7).
- **Where:** the native-input executor that runs Tern `input` steps (and the cmux fallback's resolved actions). It is deterministic under a seed. It is **off** in headless tests (`FakeAgentInput`), and there is a setting "Human-like pacing" (default on).
- The host tracks the last pointer position per page. On the first action, start from the point where the user's real pointer last left the web view, or else the nearest viewport edge.

| Step | Parameters (sampled; lognormal unless noted) |
|---|---|
| Move to target | Duration by Fitts: `T = 80 + 120·log2(1 + D/W)` ms, clamped 120–650 ms (D = distance, W = target width). Path: cubic Bézier, control points offset perpendicular by 5–15 % of D. Minimum-jerk progress `s(t)=10t³−15t⁴+6t⁵`. Samples every 8–16 ms (60–125 Hz), ±0.5 px jitter, device-pixel quantised. For D > 400 px, 20 % chance of 2–6 px overshoot plus a correction. Moves inside the target fire `pointermove` naturally. |
| Click point | Gaussian around the element centre, σ = 15 % of box size, clamped to the inner 70 %. Re-check the hit with the isolated-world kit (`elementFromPoint` inside target). |
| Pre-press dwell | 40–120 ms after arrival |
| Press → release | 50–130 ms (median 85) |
| Double click | second press 90–160 ms after the first release, same point ±1 px |
| Drag | 4–8 px "slop" first, then the path at 60–125 Hz; release 60–150 ms after the last move |
| Key dwell (down → up) | 40–110 ms (median 70) |
| Inter-key flight | median 110 ms, range 45–280 ms. +80–200 ms after space or punctuation. Shift down 20–60 ms before an uppercase letter and up 10–40 ms after. **No typos.** |
| Long text | Keystroke typing caps at about 4 s per `input` op. Beyond that, the remainder goes through `insertText` in 1–3 word chunks 40–120 ms apart, or as a single chunk for > 500 chars. |
| Wheel | Bursts of 3–8 events, 12–20 ms apart. Pixel deltas 40–120 with ease-out decay. Continue until the target's rect is inside the viewport with ≥ 10 % margin. |
| Between actions | ≥ 150–400 ms. The LLM's own latency already adds seconds of "think time", which looks human. |

Budget guard:
- Total added delay per `input` op ≤ min(3 s, 25 % of the op's timeout).
- Abort the pacing (send the remaining steps immediately) if the user starts interacting. The user always wins (browser-input.md §9.4).

---

## 6. Script isolation and footprint policy

### 6.1 Rules
1. **Everything the GUI owns runs in a named isolated world.** This covers the snapshot library (today's `__ompAgent`), probes, title (better: native `title` KVO / `DocumentTitle`) and annotations. `RunScriptAsync` gets a `world` parameter.
2. **Page world only when the page's own JS objects are the point:** the agent's explicit `browser.eval` (Tern `world:"page"`), and omp's capture (§6.4).
3. **No DOM footprint unless the user is using the feature.**
   - Annotations inject only while annotation mode is on or pins exist for this URL.
   - Better: draw pins and highlights in a **native overlay** above the web view (hit-testing off), with positions computed in the isolated world. The page then sees nothing.
   - Codex keeps its comment UI in-page; Claude Desktop's pane has no annotations; we can do better.
4. Never define enumerable globals; never leave functions whose `toString()` is not native in the page world.

### 6.2 macOS
WKContentWorld (§4.4). A CSP-proof function call is `callAsyncJavaScript` (it is not `eval`, so page CSP does not apply), as Tern requires (omp-tern-protocol §2 `eval`).

### 6.3 Windows (CDP isolated world without `Runtime.enable`)
- Install: `Page.addScriptToEvaluateOnNewDocument{source, worldName:"ompgui", runImmediately:true}`.
- Evaluate: `Page.createIsolatedWorld{frameId, worldName:"ompgui"}` → `executionContextId` → `Runtime.evaluate{contextId, expression, awaitPromise:true, returnByValue:true}`. Re-create after each main-frame navigation.
  - Navigation is known from the `NavigationCompleted`/`ContentLoading` WebView2 events, so no `Page.enable`-driven `Runtime` events are needed.
  - [INFERENCE: Chromium reuses one isolated world per name per frame, so the document-start script and later evaluations share globals. Puppeteer's utility world relies on this; verify in the spike.]
- Isolated → host channel: `Runtime.addBinding{name, executionContextName:"ompgui"}`, received as `Runtime.bindingCalled` through `GetDevToolsProtocolEventReceiver` (slot 42). This is rebrowser's technique, chosen precisely because it avoids `Runtime.enable` ([rebrowser-patches](https://github.com/rebrowser/rebrowser-patches)).
- Page-world surface:
  - Set `ICoreWebView2Settings.IsWebMessageEnabled = false` once annotations use the binding. Docs: "window.chrome.webview.postMessage fails" when false ([docs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2settings.iswebmessageenabled)). Whether `chrome.webview` disappears entirely: [INFERENCE: verify].
  - Avalonia's `invokeCSharpAction` document-created script (F8w) remains unless we own the WebView2 controller.
- CDP allow-list:
  - Allowed: `Input.*` (dispatchMouseEvent, dispatchKeyEvent, insertText, dispatchTouchEvent), `Emulation.*`, `Page.createIsolatedWorld`, `Page.addScriptToEvaluateOnNewDocument`, `Runtime.evaluate`/`callFunctionOn` with explicit `contextId`, `Runtime.addBinding`.
  - **Banned:** `Runtime.enable`, `Debugger.*`, `Console.enable`, `Log.enable`, `Profiler.*`, `Overlay.*` (it draws in-page).
  - Codex's allow-list restricts `Input.*` the same way (codex-browser-internals §2.2).
- Never pass `--remote-debugging-port`, `--enable-automation` or `--headless` in `AdditionalBrowserArguments`.

### 6.4 omp's Tern page-world capture (F12): host policy
- omp says it plainly: "Everything here runs in the page's own world, so pages can see (and tamper with) the wrappers" (omp `tern/page-capture.ts:9-10`).
- It powers console logs, page errors, fetch/XHR logs, routes, geolocation/locale/offline shims and the `inflight` network-idle probe. If missing, omp reads `inflight` as 0 (`tern-tab.ts:896-905`), so network-idle waits degrade gracefully.
- Proposed GUI setting **"Page instrumentation"**:
  - **Local and dev sites** (default): the capture is installed for `localhost`, `127.0.0.1`, `*.local`, private IPs, `file:` and the user's own dev origins. That is the agent's main job: verifying the user's app, where console and network logs matter.
  - **All sites**: omp's behaviour.
  - **Never**.
- For other origins the host drops page-world `scripts` entries whose source is omp's capture installer, refuses `eval world:"page"` calls of `TERN_CAPTURE_INSTALLER`, and answers with an `unsupported`-kind error mentioning the setting.
- Policy applies per main-frame navigation in `decidePolicyForNavigationAction` (before the document exists): swap the user-script set.
- Cross-origin child frames inherit the main frame's decision [INFERENCE: acceptable].
- Same for the page-world `stencil` handler: register it only while capture is allowed. Otherwise `window.webkit.messageHandlers` does not exist on that page.

### 6.5 Windows/Linux identity
- **Windows:** keep the default Edge UA and UA-CH. The brands include `"Microsoft Edge WebView2"` (WebView2Feedback #2576). That is honest and not a bot signal. Removing it means spoofing metadata; do it only inside the explicit mobile-emulation mode, where we must set metadata anyway.
- **Linux:** WebKitGTK's default UA is already Safari-format (`… Version/x Safari/605.1.15`); keep it. `ApplicationNameForUserAgent` appends a token: skip it.

---

## 7. CAPTCHA and human-verification policy (never solve; hand to the user)

**Reference behaviour**
- Claude: "Claude respects all bot detection systems (CAPTCHA, human verification) and never attempts to bypass or complete these on the user's behalf" (quoted in [anthropics/claude-code#16334](https://github.com/anthropics/claude-code/issues/16334); [Use Claude in Chrome safely](https://support.claude.com/en/articles/12902428-use-claude-in-chrome-safely)).
- That issue shows the model alone can be argued out of it, so **enforce it in the host, not only in the prompt**.
- Codex: `botDetection.report({reason: captcha_failed|access_denied|challenge_loop|…})` and a `browserAuth` hand-off (codex-browser-internals L70, L117).

**Detection**
- Runs in our isolated world after every navigation and before every agent input.
- Iframes or scripts from `google.com/recaptcha`, `recaptcha.net`, `hcaptcha.com`, `challenges.cloudflare.com`, `arkoselabs.com`/`funcaptcha`, `geo.captcha-delivery.com` (DataDome), `px-captcha`/`perimeterx` (HUMAN), `awswaf` captcha.
- Interstitial titles and bodies: "Just a moment…", "Verify you are human", "Access denied", "Press & Hold".
- HTTP 403/429 main-document responses carrying those vendors' headers (`cf-mitigated: challenge`, `x-datadome`) [INFERENCE: header names from vendor docs; verify].

**Host enforcement**
- While a challenge is present, `input` steps whose hit point lies inside a challenge iframe/element, or **any** input on a full-page interstitial, are refused with error kind `failed`. Message: "This page asks for human verification (<vendor>). The user must complete it in the preview; observe the page again afterwards."
- Navigation and observation stay allowed.

**UX**
- A non-modal strip in the preview: "omp paused — this site wants to confirm you're human. Complete it here, then click Continue."
- The run pauses, the same way permission prompts pause it. On Continue, omp's next action proceeds.
- No screenshots are sent to the model while the strip is up (Operator-style privacy for the user's own input).
- The same hand-off covers sign-in pages and password fields, which NativeDialogs and Native auth own.

**Prompt**
- The GUI extension's `before_agent_start` note (omp-browser.md takeaway 4) adds: "Never attempt CAPTCHAs or 'verify you are human' checks; ask the user to complete them in the preview."

---

## 8. Recommended architecture

```
omp (unchanged) ──Tern protocol (preferred) / cmux fallback──▶ GUI host (per tab/block)
   ├─ Identity: engine-native UA (+Safari token on mac); no fingerprint spoofing
   ├─ ScriptWorlds: "ompgui" (our kit, probes, annotations) · "omp" (Tern isolated kit) · page (agent eval; omp capture per §6.4 policy)
   ├─ HumanVerificationGuard: detect → pause → hand to user (§7)
   ├─ Humaniser: plans paths/cadence for each input op, seeded, budgeted (§5)
   └─ IAgentInput (native actuator, browser-input.md §9.1)
        ├─ MacAgentInput: NSEvent → WKWebView responders; systemUptime timestamps; buttons swizzle; CGEvent deltas;
        │                 _simulateMouseMove (guarded) → tracking-area fallback; insertText; performKeyEquivalent; scrollWheel
        ├─ WebView2AgentInput: CallDevToolsProtocolMethod (slot 36) Input.* + scoped setFocusEmulationEnabled; banned-method list
        └─ GtkAgentInput: GdkEvents on the GLib thread with correct state mask and server time; evaluate_javascript(world_name)
```

Order of work (stealth-specific; it slots into browser-input.md §9.5):
1. Isolated worlds for our scripts; native title. Lazy annotations, then a native overlay. Remove Avalonia's page handler (or own the WKWebView, §4.5). This closes F6–F10 and the page-tamper hole.
2. mac native input with the §4.1 checklist (timestamp, buttons, deltas) and the humaniser.
3. Human-verification guard and the hand-off UI.
4. Tern host: page-instrumentation policy (§6.4) and the `input` executor that reuses steps 2–3.
5. WebView2 CDP actuator, world and banned list. Then GTK.

---

## 9. Verification (the user runs it; headless tests cannot host native web views)

Unit tests (headless):
- humaniser plans (seeded: monotonic times, Fitts bounds, budget cap);
- CDP banned-method guard;
- policy selection for origins;
- human-verification detector on recorded HTML fixtures.

A local self-test page (shipped under `tools/`, served from localhost) logs per event:
- `isTrusted`;
- `timeStamp − performance.now()` (expect between −50 and 0 ms);
- `buttons` on `mousedown`/`mousemove` while held (expect 1);
- `movementX/Y` non-zero on moves;
- `screenX − clientX` = `window.screenX + (outerWidth − innerWidth)`-ish (non-zero and constant), on WebView2 too;
- `navigator.userActivation.isActive` after click;
- `window.open` inside a click handler;
- `document.hasFocus()` during typing;
- `navigator.webdriver === false`;
- the list of own-property globals vs a pristine iframe (`Object.getOwnPropertyNames(window)` diff; expect no `__omp*` or `invokeCSharpAction` with policy "Local and dev sites" on a non-local origin);
- `console.log.toString()` native;
- `typeof window.webkit?.messageHandlers`;
- `MutationObserver` log (expect no nodes from us unless annotating).

External checks the user can open in the preview, with the agent driving: [CreepJS](https://abrahamjuliot.github.io/creepjs/), [bot.sannysoft.com](https://bot.sannysoft.com/), [BrowserScan bot detection](https://www.browserscan.net/bot-detection), [deviceandbrowserinfo.com/are_you_a_bot](https://deviceandbrowserinfo.com/are_you_a_bot).
- Expect: "human/clean" with the same results as plain Safari/Edge, except the inherent WKWebView tells (§4.6).
- Repeat with the app in the background, with a Japanese IME and a Russian layout (mac), and on Windows with the Evergreen runtime (screenX check).

---

## 10. Risks and open questions

1. **mac `buttons` swizzle** is process-global for microseconds. It is safe on the main thread, but it is still method swizzling. The alternative is accepting `buttons=0` (detectable on mousedown). Safari ships this exact pattern.
2. **`_simulateMouseMove:` SPI** could change. Guarded, with a tracking-area fallback and an honest "partial" flag.
3. **Background typing** (`hasFocus()` false) is unavoidable on mac without stealing focus. The result is flagged.
4. **WebView2 screenX**: fixed in Chromium `main` per source, unverified on the installed runtime.
5. **WebView2 isolated-world reuse by name**: [INFERENCE], spike needed.
6. **omp page-world capture policy** trades omp logs on third-party sites for invisibility. This is a product decision; the default proposed is "Local and dev sites".
7. **Google sign-in in embedded views**: a Safari-format UA removes the UA heuristic, but Google's policy disallows embedded webviews for OAuth. Keep the "Open sign-in in your browser" fallback (NativeDialogs). Faking more than the engine's true Safari identity is out of bounds.
8. **Humaniser vs flakiness**: pacing must never change semantics (no typos, no missed targets). The hit re-check before each press guards this.

# Agent input the engine treats like the user's own: research (2026-10-08)

Scope: the preview's embedded web view (Avalonia `NativeWebView` 12.1: WKWebView on macOS, WebView2 on Windows, WebKitGTK 4.1 on Linux/X11) which the user and omp share. Today the agent's clicks and typing are JS-synthesised (`AgentBrowserScripts.cs`). Goal: input that the engine processes like the user's, sent from inside our process, with no OS permissions.
Method: WebKit, Chromium, CDP and Avalonia sources read; the shipped `Avalonia.Controls.WebView.dll` 12.1.0 decompiled (ilspycmd into /tmp); repo code read. **Nothing was run against a live web view.** I launched no GUI and made no edits. Anything marked [INFERENCE] I reasoned out but did not see in a source.

---

## 0. Verdicts at a glance

| Technique | Verdict | Trusted events? | Grants activation? | Permissions |
|---|---|---|---|---|
| macOS: NSEvent → WKWebView responder methods (`mouseDown:`, `keyDown:`, `scrollWheel:`…) | **Feasible. Recommended.** | Yes. They take the same `NativeWebMouseEvent`/`NativeWebKeyboardEvent` path as hardware input. | Yes. WebCore `EventHandler` wraps press, release and key handling in `UserGestureIndicator(IsProcessingUserGesture::Yes)`. | None. In-process objects only; nothing is posted to the WindowServer. |
| macOS: same events through `-[NSWindow sendEvent:]` (Safari's WebDriver does this) | Works, but **not for a shared window**. See §2.5. | Yes | Yes | None |
| macOS: `insertText:replacementRange:` (NSTextInputClient) for text | **Feasible. Recommended for text.** | Yes (`beforeinput`/`input` from Editor) | **No.** `InsertTextOptions.processingUserGesture` defaults to false. The activation comes from the click that focused the field. | None |
| macOS: `setMarkedText:…` + `insertText:` for IME composition | Feasible | Yes (`composition*` events) | No | None |
| macOS: hover via `-[WKWebView _simulateMouseMove:]` | Feasible, **private SPI** (macOS 13+). Guard with `respondsToSelector:`. | Yes | n/a | None |
| Windows: CDP `Input.dispatchMouseEvent`/`dispatchKeyEvent`/`dispatchTouchEvent`/`insertText` via `ICoreWebView2::CallDevToolsProtocolMethod` | **Feasible. Recommended.** | Yes. `RenderWidgetHost::ForwardMouseEvent`/`ForwardKeyboardEventWithCommands`, the browser-side input path. | Yes. Blink `NotifyUserActivation` on mouse press and key down. | None |
| Windows: `ICoreWebView2CompositionController::SendMouseInput` | Only in visual-hosting mode. Avalonia uses HWND hosting unless `ExperimentalOffscreen` is set. Mouse only. **Not recommended.** | Yes | Yes | None |
| Windows: CDP `Emulation.setDeviceMetricsOverride`/`setUserAgentOverride` (+`userAgentMetadata`)/`setTouchEmulationEnabled` | **Feasible.** Only with a real Android-Chrome device profile that matches the engine's real Chromium major version. | n/a | n/a | None |
| Linux: synthesise a GTK3 `GdkEvent` for the `WebKitWebView` (`gtk_widget_event`/`gtk_main_do_event`) | **Feasible.** WebKit's own GTK WebDriver and Avalonia's offscreen GTK adapter both do this. | Yes (`NativeWebMouseEvent::create(GdkEvent…)`) | Yes (same WebCore `EventHandler`) | None |
| Isolated script world: `WKContentWorld` / CDP `Page.createIsolatedWorld` + `Runtime.evaluate{contextId}` / WebKitGTK `evaluate_javascript(world_name)` | **Feasible on all three.** Avalonia's `InvokeScript` uses the page world everywhere, so we must call the engines ourselves. | n/a | n/a | None |
| Mobile emulation on macOS WKWebView / WebKitGTK | **Not offered.** These engines have no touch events, so a phone UA would be a lie. Offer responsive width only. | | | |
| CAPTCHA / human-verification automation | **Never.** Detect it and hand it to the user (§8). | | | |

The rest of the document backs each row and ends with the architecture (§9).

---

## 1. Current state (evidence)

- Clicks are `el.dispatchEvent(new MouseEvent/PointerEvent(...))` plus `el.click()` (`AgentBrowserScripts.cs:199-214`). Typing is `KeyboardEvent` dispatch, then `setRangeText`/a native value setter, then a synthetic `InputEvent`, or `document.execCommand("insertText")` for contenteditable (`:216-243`, `:289-303`). `press` handles Enter itself and calls `form.requestSubmit()` itself (`:350-385`). All of these are `isTrusted=false` and carry no activation.
- The library is `globalThis.__ompAgent`, cached by `v === 1` (`:20-21`), together with `globalThis.__ompBridgeRun` (`:426`). Both live in the **page world**. A page can pre-define `__ompAgent = {v:1, snapshot: () => fake}` and feed omp a fabricated snapshot, a prompt-injection vector. It can also monkey-patch `getBoundingClientRect`, `MouseEvent`, `Element.prototype.click` and so on, which our code then trusts.
- The annotation overlay `window.__ompAnnotate` (`Preview/annotate.js:6,231`) posts through `window.invokeCSharpAction`. Avalonia defines that in the page world, so the page can forge posts. A token mitigates this (`annotate.js:5`), but the page can read the token by wrapping `invokeCSharpAction` before our script runs [INFERENCE].
- Avalonia `NativeWebView.InvokeScript` (decompiled 12.1.0):
  - macOS calls `evaluateJavaScript:completionHandler:`, which runs in the page world. `WKContentWorld.DefaultClientWorld` is used only to JSON-stringify non-string results.
  - WebView2 calls `ICoreWebView2::ExecuteScript`, which runs in the main world.
  - GTK calls the deprecated `webkit_web_view_run_javascript`, which runs in the page world.
- Platform handles we already use: `IAppleWKWebViewPlatformHandle.WKWebView`, `IWindowsWebView2PlatformHandle.CoreWebView2`/`.CoreWebView2Controller`, `IGtkWebViewPlatformHandle.WebKitWebView` (`Platform/WebViewSnapshot.cs:53-58`). The snapshot helpers already call Objective-C and COM vtables directly (`MacWebViewSnapshot.cs`, `WebView2Snapshot.cs`, where `CapturePreview` is slot 30). The decompiled interop lists `CallDevToolsProtocolMethod_36` and `GetDevToolsProtocolEventReceiver_42`, so the same vtable-call pattern reaches CDP.
- Windows hosting: `WebViewAdapter.cs:60-66` (decompiled) uses `WebView2HwndAdapter` unless `ExperimentalOffscreen` is set. In that mode Windows delivers the user's input straight to the WebView2 HWND.
- Popups are blocked by design today, whatever the input. On macOS the Avalonia adapter has no `WKUIDelegate` at all; target-less navigations become `NewWindowRequested` (`MaciosWebViewAdapter.cs:310-317`). `PreviewPanel.OnWebNewWindowRequested` always sets `Handled = true` and reroutes the URL (`PreviewPanel.axaml.cs:282-288`). So `window.open` from an OAuth button never gets a real popup with `window.opener`. Trusted input is necessary for OAuth popups but **not sufficient** (§7).

---

## 2. macOS — WKWebView

### 2.1 Evidence that native NSEvents become trusted DOM events with user activation
- **Apple's own Safari WebDriver works this way.** `WebAutomationSessionMac.mm` (WebKit main) builds events with `+[NSEvent mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:]` (L207) and `+[NSEvent keyEventWithType:…characters:charactersIgnoringModifiers:isARepeat:keyCode:]` (L821-850, L930-936). Wheel events come from `CGEventCreateScrollWheelEvent` → `+[NSEvent eventWithCGEvent:]` (L948-959). It then delivers them with `[window sendEvent:event]` (L114). https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/Automation/mac/WebAutomationSessionMac.mm
- **WKWebView responder methods mark the input `UserDriven`.** `-[WKWebView mouseDown:]` calls `_impl->mouseDown(event, WebEventInputSource::UserDriven)` (`WKWebViewMac.mm` L562-575); `keyDown:` calls `_impl->keyDown` (L644); `insertText:replacementRange:` calls `_impl->insertText` (L1583). The enum is just `{ UserDriven, Automation }` (`Shared/WebEvent.h` L48).
- **Activation comes from WebCore's native-event handling.** It is the same code for every platform event: `UserGestureIndicator gestureIndicator(IsProcessingUserGesture::Yes, …)` in the mouse press, mouse release and key paths of `WebCore/page/EventHandler.cpp` (L2072, L2232, L2641, L4422). Events created by the UA from platform events are trusted by definition; only `dispatchEvent` from script yields `isTrusted=false`.
- **Mouse path.** `WebViewImpl::nativeMouseEventHandler` (`WebViewImpl.mm` L6904) → `NativeWebMouseEvent::create(event, …)` → `WebPageProxy::handleMouseEvent`. There is no "is this from the WindowServer" check, only `m_ignoresNonWheelEvents`, a warning view and Screen Time blocking.
- **Text path.** Outside a key-down, `WebViewImpl::insertText` inserts immediately ("sent outside of keyboard event processing (e.g. from Character Viewer…) then we also execute it immediately", L6187-6205) via `WebPage::insertTextAsync`. That method wraps the insertion in `UserGestureIndicator{options.processingUserGesture ? Yes : No}` (`WebPageCocoa.mm` L2126-2137), and `processingUserGesture` defaults to `false` (`Shared/Cocoa/InsertTextOptions.h` L39). Result: trusted `beforeinput`/`input` (inputType `insertText`) **without** keydown/keyup and **without** new activation.

### 2.2 How to build and deliver the events (all public AppKit, apart from hover)
- **Coordinates.** Take the CSS client point, multiply by `webView.pageZoom` (macOS 11+) and by `webView.magnification`, then call `[webView convertPoint:p toView:nil]`. That gives `locationInWindow`. Safari does the equivalent with `rootViewToWindow` (L171-180). Our insets are 0.
- **`windowNumber`** = `webView.window.windowNumber`; `context` = nil.
- **`timestamp`**: use `NSProcessInfo.systemUptime`, the base real events use. Safari passes `timeIntervalSinceReferenceDate`, which also works.
- **`eventNumber`**: any value; Safari uses the constant 0 as a marker (L82, L152). WebKit uses it only to correlate `acceptsFirstMouse` (L2539).
- **`clickCount`**: 1, then 2 for a double click within `NSEvent.doubleClickInterval`. Pressure is 1 on down and 0 on up.
- **Mouse:** `mouseDown:`/`mouseUp:`/`mouseDragged:` and the `right…`/`other…` variants on the WKWebView.
- **Hover:** WKWebView does not override `mouseMoved:`; moves reach WebKit only through its tracking-area observer. Safari's code says so explicitly: "NSEventTypeMouseMoved events are not forwarded from the WKWebView … We still dispatch …" and then calls `[webView _simulateMouseMove:event]` (L116-123). That SPI is declared in `WKWebViewPrivate.h` L960: `_simulateMouseMove:` macOS 13+, `_simulateMouseExit:` 14+, `_simulateMouseEnter:` 15+. Our minimum is macOS 14 (`packaging/Info.plist`). Without the SPI, hover menus cannot get trusted `pointermove`/`mouseover`.
- **Keys:** `keyDown:`/`keyUp:`/`flagsChanged:` with Carbon `kVK_*` key codes plus `characters`/`charactersIgnoringModifiers`. WebKit derives DOM `code` from the key code and `key` from the characters. For ⌘-shortcuts (⌘A/C/V/Z) call `[webView performKeyEquivalent:]` **directly**. Never route them through the window or `NSApp`, or ⌘W/⌘Q/⌘, would hit our own menu.
- **Wheel:** `CGEventCreateScrollWheelEvent(NULL, kCGScrollEventUnitPixel, 2, dy, dx)` → `eventWithCGEvent:` → `[webView scrollWheel:]`. Creating a CGEvent needs no permission; only *posting* one does, and we never call `CGEventPost`.
  - Associating the window is the fiddly part. Safari uses the private `-[NSEvent _eventRelativeToWindow:]` (L959) to work around rdar 17180591.
  - Public route: set `kCGMouseEventWindowUnderMousePointer` and `kCGMouseEventWindowUnderMousePointerThatCanHandleThisEvent` (public `CGEventField`s) to the window number, plus `CGEventSetLocation` in screen coordinates [INFERENCE: needs a spike].
  - Fallback: JS `scrollBy` still fires trusted `scroll` events, but no `wheel` events.
- **Text:** `[webView insertText:@"…" replacementRange:NSMakeRange(NSNotFound,0)]`. This bypasses the keyboard layout and input method, so it works for any Unicode and any user layout (Russian, CJK and so on).
- **IME:** `setMarkedText:selectedRange:replacementRange:` (`WebViewImpl.mm` L6565) produces composition events, then `insertText:` commits.

### 2.3 Requirements and pitfalls
- **The keyboard goes through the user's active input method.** `WebViewImpl::interpretKeyEvent` hands every keyDown to `[inputContext handleEventByInputMethod:]` whenever the focused content is editable (L5980-6032, L6418-6424).
  - With a Japanese or Pinyin IME active, a synthetic "a" key can start a composition.
  - With a non-Latin layout active, the IM may reinterpret the key code [INFERENCE].
  - So: use **`insertText:` for characters** and key events only for non-text keys (Enter, Tab, Backspace, Escape, arrows, Home/End, PageUp/Down) and shortcuts. Those pass through the IM unchanged in practice [INFERENCE].
- **`MouseEvent.buttons` comes from the real hardware state.** Safari swizzles `+[NSEvent pressedMouseButtons]` for the length of its synchronous send loop, because "+[NSEvent pressedMouseButtons] does not account for the NSEvent objects created …" (L86-108).
  - Without the swizzle, our `pointerdown`/`mousemove` report `buttons=0`. Clicks are fine (they use `button`), but pages that drag on `e.buttons===1` (sliders, DnD, canvas) will not drag.
  - Option: copy Safari's scoped swizzle. It is process-global for one synchronous call on the main thread; mouse handling is synchronous when no editable input context is involved (L6904-6922).
- **First responder and focus.**
  - Calling the responder methods directly skips AppKit's "clicked view becomes first responder" step.
  - WebKit's page focus (`ActivityState::IsFocused`) needs the window to be key and the WKWebView to be first responder. Otherwise `document.hasFocus()` is false and the caret is hidden.
  - Key events are still dispatched to `document.activeElement` [INFERENCE: WebCore `EventHandler::keyEvent` targets the focused element; needs a spike].
  - Recommendation: before keyboard actions, `[window makeFirstResponder:webView]` **only if the user is not focused in the composer or another text field of ours**; restore the previous first responder afterwards. Never call `makeKeyAndOrderFront:` or `NSApp activate`. Safari does call `makeKeyAndOrderFront:` before every event (L108-111); we must not.
- **Background or minimised window.**
  - Responder calls need no key window and no front window.
  - Minimised or occluded: WebKit marks the page not visible, so `visibilityState` is "hidden", rAF stops and timers are throttled. Input is still processed [INFERENCE: `WebPageProxy::handleMouseEvent` has no visibility gate]. Any "settle" logic must not wait on rAF alone.
  - Preview panel hidden (`ShowWebView=false`) or the native view detached: hit-testing and coordinates are meaningless. Require the preview to be shown in the window first (show it without activating the app).
- **Stability across macOS versions.**
  - NSEvent factory methods: public since 10.0. `NSTextInputClient`: 10.5. `pageZoom`: 11. `WKContentWorld`: 11.
  - The private parts (`_simulateMouseMove:`, and Safari's `_eventRelativeToWindow:` if we copied it) have been stable since macOS 13 but carry no promise. Guard every private selector with `respondsToSelector:` and fall back.
  - There is no evidence WebKit rejects in-process synthetic NSEvents; its own test harness (WebKitTestRunner `EventSenderProxy`) and Safari rely on them.

### 2.4 Effect on a real page (expected; needs the manual self-test in §9.6)
- A trusted click → `pointerdown`/`mousedown`/`pointerup`/`mouseup`/`click` with `isTrusted=true` and `navigator.userActivation.isActive=true` for about 5 s. Activation-gated APIs then work: `window.open` (once a UI delegate exists, §7), `requestFullscreen`, `navigator.clipboard.write`, file `<input>` (opens an `NSOpenPanel`, see §7), media autoplay with sound.
- contenteditable, CodeMirror 6 and Monaco get real `keydown` plus `beforeinput`/`input` from the editor engine, which is the path they listen to.

### 2.5 Why not `-[NSWindow sendEvent:]`
- Avalonia's `AvnWindow` overrides `sendEvent:` (`native/Avalonia.Native/src/OSX/AvnWindow.mm` L511-520). It tracks titlebar sessions and "non-client clicks" for popup/flyout light-dismiss, so synthetic clicks would close the user's open flyouts and menus [INFERENCE].
- AppKit's click-through rule: a mouse-down in a non-key window first makes the window key and is swallowed unless `acceptsFirstMouse:` says yes. WebKit's version returns NO in most cases (`WebViewImpl.mm` L2524-2541).
- Key events through the window go to whatever is first responder and to menu key equivalents.

Direct responder calls avoid all three while running the identical WebKit path.

---

## 3. Windows — WebView2

- **API.** `ICoreWebView2::CallDevToolsProtocolMethod(methodName, parametersAsJson, handler)` is vtable slot 36, confirmed by the decompiled Avalonia interop name `CallDevToolsProtocolMethod_36`. It uses the same COM-handler pattern as our `WebView2Snapshot.cs`. Events come through `GetDevToolsProtocolEventReceiver` (slot 42). `CallDevToolsProtocolMethodForSession` (ICoreWebView2_11) reaches child targets. Docs: https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2#calldevtoolsprotocolmethod
- **Trusted, with activation.**
  - Chromium's DevTools `InputHandler` forwards to `widget_host_->ForwardMouseEvent(mouse_event)` and `ForwardKeyboardEventWithCommands(...)`, the browser-side entry points OS input also uses (`content/browser/devtools/protocol/input_handler.cc` L757, L789, L2066).
  - Blink grants activation on mouse press (`third_party/blink/renderer/core/input/event_handler.cc` L930) and on keydown/rawKeyDown (`keyboard_event_manager.cc` L258).
  - This is why Puppeteer and Playwright clicks are `isTrusted` and can open popups.
  - Note: a DeepWiki page claims `dispatchMouseEvent` gives no activation. The Chromium source above contradicts it.
- **Methods.** Full spec: https://chromedevtools.github.io/devtools-protocol/tot/Input/
  - `Input.dispatchMouseEvent{type: mouseMoved|mousePressed|mouseReleased|mouseWheel, x, y (CSS px, main-frame viewport), button, buttons, clickCount, modifiers}`. CSS pixels mean no DPI maths, and `buttons` is honoured, so drags work, unlike macOS.
  - `Input.dispatchKeyEvent{type: keyDown|rawKeyDown|char|keyUp, key, code, text, windowsVirtualKeyCode, modifiers}`.
  - `Input.insertText{text}` (experimental) commits via `ImeCommitText` after `widget_host->Focus()` (input_handler.cc L1281-1303), the text-without-keys path for any Unicode.
  - `Input.imeSetComposition` for IME tests.
  - `Input.dispatchTouchEvent` for taps in mobile emulation.
- **Hit-testing.** The browser routes events to cross-origin iframes (OOPIFs), so coordinates work across Google/Stripe/CAPTCHA frames, which our JS cannot reach.
- **Focus.**
  - CDP input does not need the HWND focused, but the page must be focused for keys to reach the caret.
  - Option A: `ICoreWebView2Controller::MoveFocus(PROGRAMMATIC)` before typing, then hand focus back. This takes keyboard focus within our window only, under the same rule as macOS.
  - Option B: `Emulation.setFocusEmulationEnabled(true)` (experimental). It makes `document.hasFocus()` true while the window is not focused. That bends "truthful", so use it only while an agent action runs.
- **Hidden or minimised.** When Avalonia hides the controller (`IsVisible=false`) the renderer is hidden and compositor hit-test data may go stale [INFERENCE]. Require the preview to be visible, as on macOS.
- **`SendMouseInput`.** `ICoreWebView2CompositionController::SendMouseInput`/`SendPointerInput` exist only in visual hosting (Avalonia's `WebView2CompAdapter`, `ExperimentalOffscreen`). They are mouse and pointer only; WebView2 has no keyboard counterpart. CDP covers everything in both modes, so use CDP.
- **Device emulation.** Methods per `browser_protocol.json`:
  - `Emulation.setDeviceMetricsOverride{width,height,deviceScaleFactor,mobile,screenWidth,screenHeight,screenOrientation}`
  - `Emulation.setUserAgentOverride{userAgent, acceptLanguage, platform, userAgentMetadata}`. Per the spec, "`userAgentMetadata` must be set for Client Hint headers to be sent". Without it, `navigator.userAgentData` and `Sec-CH-UA-*` keep saying Windows desktop Edge, which contradicts a phone UA. `ICoreWebView2Settings2::put_UserAgent` has exactly this problem; do not use it for emulation.
  - `Emulation.setTouchEmulationEnabled{enabled,maxTouchPoints}` and `Emulation.setEmitTouchEventsForMouse` (experimental).
  - These overrides are per target. Re-apply them on navigation to a new process and to popups.

---

## 4. Linux — WebKitGTK (webkit2gtk-4.1 = GTK3)

- **Prior art in our own dependency.** Avalonia's `GtkOffscreenWebViewAdapter` already forwards the user's Avalonia input to an offscreen WebKitWebView by building GdkEvents. Its `EventSendState` calls `gdk_event_new`, sets `any.window = gtk_widget_get_window(webView)` and `send_event=1`, then `gdk_event_put`. Keys translate the hardware keycode to a keyval with `gdk_keymap_translate_keyboard_state` (decompiled `GtkOffscreenWebViewAdapter.cs` L15-45, L200-230). In that mode the user's input and the agent's would use the identical call.
- **WebKit's own GTK WebDriver** synthesises the same way: `webkitWebViewBaseSynthesizeMouseEvent`/`…KeyEvent`/`…WheelEvent` (`UIProcess/Automation/gtk/WebAutomationSessionGtk.cpp` L82-104, L323-340). For text it sends one key event per code point with `gdk_unicode_to_keyval(codePoint)` (L330). Those helpers are internal, not exported.
- **Public route:** fill a `GdkEventButton`/`GdkEventMotion`/`GdkEventKey`/`GdkEventScroll`:
  - `window` = the web view's GdkWindow (ref'd)
  - widget-relative x/y and root x/y
  - `time = GDK_CURRENT_TIME`
  - `device` from the default seat

  Deliver it with `gtk_widget_event(webView, ev)`. That skips focus routing in WebKit's toplevel GtkWindow, which Avalonia's X11 adapter reparents into our window (`GtkX11WebViewAdapter.cs` L37-76). `gtk_main_do_event` also works. WebKit's GTK3 handlers turn the event into `NativeWebMouseEvent::create(event, …)` → `pageProxy->handleMouseEvent` (`WebKitWebViewBase.cpp` L1362), so the events are trusted and carry activation through the same WebCore `EventHandler`.
- **Threading.** Avalonia runs GTK on its own GLib thread (`AvaloniaGtk.RunOnGlibThread`, internal), so we marshal with `g_main_context_invoke`.
- **Text and IME:** two options.
  - (a) Key events with `gdk_unicode_to_keyval`, like WebKit WebDriver. These still pass the user's GtkIMContext (IBus or Fcitx may intercept) [INFERENCE].
  - (b) A custom `WebKitInputMethodContext` set with `webkit_web_view_set_input_method_context` (WebKitGTK 2.28+), emitting `committed`/`preedit-changed`. That is exact, but it replaces the user's IME for that view, so install it only while the agent types.
- **Emulation.** WebKitGTK has no touch or device-metrics override. Desktop UA only; responsive width by resizing.

---

## 5. Isolated script world (pages cannot see or tamper with our helpers)

- **macOS (11+).**
  - Create `WKContentWorld *w = [WKContentWorld worldWithName:@"omp"]`.
  - Run with `-[WKWebView evaluateJavaScript:inFrame:nil inContentWorld:w completionHandler:]` or `callAsyncJavaScript:arguments:inFrame:inContentWorld:completionHandler:`; the latter takes typed arguments, so no string splicing.
  - Persistent library: `[[WKUserScript alloc] initWithSource:injectionTime:forMainFrameOnly:inContentWorld:w]`.
  - Messages: `[userContentController addScriptMessageHandler:contentWorld:w name:@"omp"]`. Only code in our world can then post. This replaces Avalonia's page-world `invokeCSharpAction` for annotations.
  - Apple: "Your application's JavaScript run in a client world can still … change the DOM itself, but it will never see the application state set up by the page's JavaScript. Likewise, the page's JavaScript will never see yours" (WWDC20 "Discover WKWebView enhancements", https://developer.apple.com/videos/play/wwdc2020/10188/; API: https://developer.apple.com/documentation/webkit/wkwebview/3824704-evaluatejavascript).
  - We call these selectors through objc_msgSend on the handle, as `MacWebViewSnapshot` does. Avalonia does not expose them.
- **WebView2 (CDP).** All parameters below were checked in `browser_protocol.json`/`js_protocol.json`.
  - `Page.getFrameTree` → `Page.createIsolatedWorld{frameId, worldName:"omp", grantUniveralAccess:false}` → `executionContextId`.
  - Then `Runtime.evaluate{expression, contextId (or uniqueContextId), returnByValue, awaitPromise}` or `Runtime.callFunctionOn{executionContextId, arguments}`.
  - Install-once library: `Page.addScriptToEvaluateOnNewDocument{source, worldName:"omp", runImmediately:true}`.
  - Page→host channel scoped to our world: `Runtime.addBinding{name, executionContextName:"omp"}` → `Runtime.bindingCalled` event.
  - Cache the context id per (frameId, loaderId) and re-create it after navigation. This avoids `Runtime.enable`, which would stream every context and console event; we do not need them.
- **WebKitGTK.**
  - `webkit_web_view_evaluate_javascript(view, src, len, world_name="omp", source_uri, …)` (2.40+; Avalonia itself P/Invokes this signature in `WpeInterop.cs` L203).
  - `webkit_script_world_new_with_name`, `webkit_user_script_new_for_world`, `webkit_user_content_manager_register_script_message_handler_in_world`.
  - Docs: https://webkitgtk.org/reference/webkit2gtk/stable/method.WebView.evaluate_javascript.html
- **What stays visible to the page.**
  - The DOM is shared. Any element we insert (the `omp-annotate` host, `SKIP` set at `AgentBrowserScripts.cs:22`) is observable through MutationObserver, but the page can no longer reach our functions, refs map or token.
  - Agent-supplied `browser.eval` scripts must keep running in the **page** world (the agent wants page state). Only our library moves.
  - Built-ins are pristine in the isolated world, so a page can no longer lie to us through patched prototypes.

---

## 6. Truthful, consistent identity

- **Default:** no overrides. Keep the engine's own UA, Client Hints, platform, screen and devicePixelRatio.
  - Appending an honest product token through `applicationNameForUserAgent` / `ApplicationNameForUserAgent` (e.g. `OMPGUI/<version>`) is fine.
  - Do **not** add `Version/x Safari/…` to WKWebView's UA. WKWebView's default UA deliberately omits it, and adding it would claim to be Safari.
- **No fingerprint patching of any kind:** no `navigator.webdriver`, `plugins`, canvas or WebGL noise, timezone or locale changes. Input that is genuinely user-equivalent makes these unnecessary.
- **Mobile emulation, only where the engine can be consistent (WebView2).** Use a real device profile from Chromium DevTools' list (`front_end/models/emulation/EmulatedDevices.ts` in devtools-frontend). Apply as one bundle:
  - UA with the **actual** WebView2 Chromium major
  - `userAgentMetadata` with `mobile:true`, `platform:"Android"`, `platformVersion`, `model`, and brands matching the real browser
  - `setDeviceMetricsOverride{mobile:true, deviceScaleFactor, screen dims}`
  - `setTouchEmulationEnabled{maxTouchPoints}`
  - agent taps sent as `dispatchTouchEvent`

  Do not offer iPhone or iPad presets on Chromium (WebKit-only features would contradict the UA). On macOS and Linux offer "responsive width" only.
- **OAuth in embedded views.** Some identity providers refuse embedded user agents by policy (Google: https://developers.googleblog.com/2016/08/modernizing-oauth-interactions-in-native-apps.html). Truthful identity means we do not disguise the view; hand such sign-ins to the system browser instead.

---

## 7. Activation-gated features beyond input (needed for "behaves like the user")

- **Popups / `window.open` (OAuth).** We need a real child view that keeps `window.opener`:
  - macOS: install our own `WKUIDelegate` and implement `webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:`, returning a new WKWebView **built with the passed configuration**. Avalonia sets no UI delegate (none in the decompiled Macios adapter).
  - WebView2: `NewWindowRequested` → `put_NewWindow(childCoreWebView2)` (with a deferral).
  - GTK: the `create` signal → `webkit_web_view_new_with_related_view`.
  - Today `PreviewPanel` cancels every new window (§1), so this is required for OAuth whatever the input method. Coordinate with whoever owns dialogs and popups (peer `NativeDialogs`).
- **File inputs.** A trusted click on `<input type=file>` opens a native picker on the user's screen. Intercept it instead:
  - macOS: `WKUIDelegate webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:`
  - WebView2: CDP `Page.setInterceptFileChooserDialog` + `Page.fileChooserOpened` → `DOM.setFileInputFiles`
  - GTK: `run-file-chooser` → `webkit_file_chooser_request_select_files`

  The agent supplies only paths the user approved; otherwise the user picks.
- **Clipboard.** Trusted ⌘V/Ctrl+V pastes the **user's** clipboard. The agent must not use paste or copy shortcuts unless asked; use `insertText` for its own text.

---

## 8. Policy: CAPTCHAs and human verification go to the user, never automated

- **Detect before every action and after every navigation**, in our isolated world plus native frame info:
  - iframes or scripts from `google.com/recaptcha`, `recaptcha.net`, `hcaptcha.com`, `challenges.cloudflare.com` (Turnstile and managed challenge, e.g. the "Just a moment…" interstitial), `arkoselabs.com`/FunCaptcha, `geo.captcha-delivery.com` (DataDome), `px-captcha` (HUMAN/PerimeterX), AWS WAF captcha
  - plus title and text heuristics ("verify you are human", "I'm not a robot")
- **On detection:**
  - The bridge fails the current and later input actions with code `human_verification_required` and the message "This page wants to confirm a person is using it. Ask the user to complete it in the preview." omp's tool surfaces that error.
  - The GUI shows a banner on the preview ("This page wants to check that you're human. Complete it here; omp will continue when you're done.") and brings the preview into view without stealing app focus.
  - `browser.wait` may poll until the challenge disappears, with a timeout.
- **Never:** clicking a checkbox widget (including Turnstile's), solving images or audio, third-party solver services, retry loops meant to get past a challenge, or changing identity to avoid one.
- The same hand-off applies to 2FA codes, passkeys and WebAuthn prompts, and payment confirmations [recommendation].

---

## 9. Recommended architecture

### 9.1 Split: page-side locator in an isolated world, native input actuator
1. `AgentBrowserScripts` library moves into the "omp" world (§5); `RunScriptAsync` gains a world parameter. The agent's own `browser.eval` stays in the page world.
2. For `click`/`dblclick`/`hover`/`check`/`type`/`press`/`scroll`, the library no longer dispatches events. It returns an **action target**: the element's client rect after `scrollIntoView({block:"center"})`, a hit point (centre, or the first point where `elementFromPoint` hits the element or a descendant), `devicePixelRatio`, and actionability flags (visible, enabled, not covered, stable rect across 2 frames, or a 100 ms timer when the page is hidden). This is Playwright's actionability model.
3. C# turns the hit point into native input through a new `IAgentInput`, implemented per platform in `OmpGui.App/Platform` beside the snapshot helpers:
   - `MacAgentInput`: objc_msgSend to WKWebView responder methods; `_simulateMouseMove:` when available; `insertText:replacementRange:`; `performKeyEquivalent:` for shortcuts.
   - `WebView2AgentInput`: `CallDevToolsProtocolMethod` COM handler (slot 36); also isolated world and emulation.
   - `GtkAgentInput`: GdkEvent + `gtk_widget_event` on the GLib thread; `evaluate_javascript` with `world_name`.
4. After acting, the library verifies the outcome: focus moved, value changed, the click target received a trusted click (one-shot listener in our world checks `e.isTrusted`). It reports a mismatch instead of silently "succeeding".
5. **Fallback:** when no native path exists (headless tests, a missing SPI, a hidden view), the JS path stays, but results report `"input":"synthetic"`. omp then knows activation-gated actions may not work. Headless tests use a `FakeAgentInput` that records calls.

### 9.2 Action recipes
- **click:** move (8-15 steps over 120-250 ms from the last pointer position) → 50-120 ms → down → 40-90 ms → up. Mouse events on macOS and Windows; motion and button GdkEvents on Linux.
- **dblclick:** click with clickCount 1, then within 150 ms another with clickCount 2.
- **type:** click to focus if not focused → for each grapheme, `insertText` (macOS / CDP `insertText`) or a keyval key event (GTK), 25-60 ms apart. `\n` → Enter key event; `\t` → Tab key event.
- **fill (replace):** focus → select-all shortcut (⌘A via `performKeyEquivalent:` / Ctrl+A via CDP) → Backspace → one `insertText` of the whole string, which behaves like a paste.
- **press:** real key event with modifiers. macOS ⌘-combinations only through `performKeyEquivalent:`.
- **scroll:** wheel events in 3-6 steps of ≤120 px, 16-40 ms apart; JS `scrollBy` as fallback.
- **check/uncheck:** read state → click only if needed → re-read.

### 9.3 Pacing and settling
The purpose is page stability (debounce, animations, autocomplete, lazy loading), not evasion; small fixed ranges are enough.
- After each action, wait until settled: two rAFs plus no layout change of the target; a 300-500 ms quiet window with no DOM mutations and no in-flight fetch/XHR, which the isolated world can observe through `PerformanceObserver` resource entries; a 3 s cap. Use timers if the page is hidden.
- At least 150 ms between consecutive agent actions.
- Never burst a whole form in one tick.

### 9.4 Sharing the view with the user
- User input always wins. If the user generated real input in the preview within the last ~1 s (track via an Avalonia pointer/key hook over the host, or a native local event monitor), pause the agent's queue.
- Show agent activity: an "omp is using this page" strip in the preview chrome and, optionally, a pointer marker. The marker should be a native overlay NSView/HWND above the web view with hit-testing off, so the page cannot see it (unlike a DOM overlay).
- Save and restore first responder / keyboard focus around agent keyboard actions. Never activate the app or reorder windows.

### 9.5 Order of work
1. Isolated worlds: smallest change, and it closes the page-tamper hole today.
2. macOS input (primary platform).
3. WebView2 CDP input + world.
4. GTK.
5. Popup UI delegate and file-chooser interception.
6. CAPTCHA hand-off.
7. Optional WebView2 mobile emulation.

### 9.6 Verification (the user runs it; headless tests cannot host native web views)
A local self-test page logs `isTrusted`, `navigator.userActivation.isActive` (Safari 16.4+, Chromium), and the result of `window.open('about:blank')` inside the click handler. It also checks `requestFullscreen()` resolving, `keydown`/`beforeinput` in a contenteditable, a CodeMirror 6 and a Monaco instance, `buttons` during a drag, and `wheel` events.
- Expected: all trusted. Activation is true after click and keydown, false after `insertText` alone. Popups open only after §7.
- Repeat with the window in the background, the panel hidden and the window minimised.
- On macOS, repeat with a Japanese IME and a Russian layout active.

---

## 10. Risks (ranked)

1. **macOS keyboard through the user's input method.** Synthetic keys can be composed or remapped. Mitigation: `insertText` for text, keys only for non-text keys and shortcuts.
2. **macOS hover depends on private SPI** (`_simulateMouseMove:`). Mitigation: a `respondsToSelector:` guard and an honest "synthetic" fallback flag.
3. **macOS `MouseEvent.buttons` = real hardware state**, which breaks drags. Mitigation: Safari-style scoped swizzle, or accept the limitation for drag.
4. **Focus semantics.** Typing into a page that is not focused (window not key) works [INFERENCE] but shows no caret, and `document.hasFocus()` is false. Some sites gate on focus. Do not fake focus except, if ever, via scoped `Emulation.setFocusEmulationEnabled` on Windows.
5. **Interference with the user.** Stealing first responder from the composer, or ⌘-shortcuts reaching our menus. Mitigation: direct responder calls, save and restore, user-input-wins.
6. **Popups and file pickers** need the extra delegates (§7) or the "trusted" work will not fix OAuth.
7. **WebView2 hidden/minimised** hit-testing [INFERENCE]; **CDP experimental methods** (`insertText`, `setFocusEmulationEnabled`) may change. The stable `dispatchKeyEvent` with `text` covers most text.
8. **GTK:** IME interception of synthetic keys; GLib-thread marshalling; webkit2gtk < 2.40 lacks `evaluate_javascript` worlds (`webkit_web_view_run_javascript_in_world` exists in older 4.0 builds [INFERENCE]).

---

## Sources
- WebKit, macOS automation: https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/Automation/mac/WebAutomationSessionMac.mm (L82-152 send loop, swizzle and markers; L171-180 coordinate mapping; L207 mouse; L216-300 interactions; L821-850, L909-939 keys; L947-962 wheel)
- WebKit, WebViewImpl: https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/mac/WebViewImpl.mm (L2524 acceptsFirstMouse; L5980-6032 interpretKeyEvent/IM; L6139-6205 insertText; L6418 inputContext; L6565 setMarkedText; L6837 keyDown; L6904 nativeMouseEventHandler; L7046 mouseMoved)
- WebKit, WKWebView responders: https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/API/mac/WKWebViewMac.mm (L562-614, L644, L1578-1585, L2231)
- `_simulateMouseMove:` availability: https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/API/Cocoa/WKWebViewPrivate.h (L960-962)
- WebCore activation: https://github.com/WebKit/WebKit/blob/main/Source/WebCore/page/EventHandler.cpp (L2072, L2232, L2641, L4422)
- insertText without gesture: https://github.com/WebKit/WebKit/blob/main/Source/WebKit/WebProcess/WebPage/Cocoa/WebPageCocoa.mm (L2126-2137); https://github.com/WebKit/WebKit/blob/main/Source/WebKit/Shared/Cocoa/InsertTextOptions.h (L39)
- WebKitGTK: https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/Automation/gtk/WebAutomationSessionGtk.cpp ; https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/API/gtk/WebKitWebViewBase.cpp (L1362, L3205-3307)
- Chromium CDP input: https://source.chromium.org/chromium/chromium/src/+/main:content/browser/devtools/protocol/input_handler.cc (L757, L789, L1281-1303, L2066); Blink activation: `third_party/blink/renderer/core/input/event_handler.cc` L930, `keyboard_event_manager.cc` L258
- CDP spec: https://chromedevtools.github.io/devtools-protocol/tot/Input/ ; https://github.com/ChromeDevTools/devtools-protocol/blob/master/json/browser_protocol.json (Emulation.*, Page.createIsolatedWorld, Page.addScriptToEvaluateOnNewDocument.worldName) ; `js_protocol.json` (Runtime.evaluate contextId/uniqueContextId, Runtime.addBinding executionContextName)
- WebView2: https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2#calldevtoolsprotocolmethod
- WKContentWorld: https://developer.apple.com/videos/play/wwdc2020/10188/ ; https://developer.apple.com/documentation/webkit/wkwebview/3824704-evaluatejavascript
- Avalonia native window: https://github.com/AvaloniaUI/Avalonia/blob/master/native/Avalonia.Native/src/OSX/AvnWindow.mm (L511-520)
- Avalonia.Controls.WebView 12.1.0, decompiled in /tmp/avwv-src (local only): `Avalonia.Controls.Macios.Interop.WebKit/WKWebView.cs` (EvaluateJavaScriptAsync → `evaluateJavaScript:completionHandler:`), `Avalonia.Controls/WebViewAdapter.cs` L60-66, `Avalonia.Controls.Gtk/GtkOffscreenWebViewAdapter.cs`, `Avalonia.Controls.Win.WebView2/WebView2CompAdapter.cs` L320-358, `Avalonia.Controls.Macios/MaciosWebViewAdapter.cs` L290-320, `Avalonia.Controls.Gtk/GtkWebViewAdapter.cs` L253
- Google embedded-webview OAuth policy: https://developers.googleblog.com/2016/08/modernizing-oauth-interactions-in-native-apps.html

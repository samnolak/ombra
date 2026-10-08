# Built-in browser: native windows (requirement 2) — root cause and design

Scope: JS alert/confirm/prompt, popups (`window.open` / `target=_blank`, OAuth with `window.opener`), `window.close()`, file inputs, downloads, HTTP auth, client certs, permission prompts, WebAuthn/passkeys, cookies/ITP/persistence, user agent/Google, plus navigation failures and web-process crashes (needed so these flows recover). macOS (WKWebView) first, then WebView2 and WebKitGTK/WPE.

Evidence key:
- `AV:` = Avalonia.Controls.WebView **12.1.0**, decompiled with `dotnet tool install ilspycmd --tool-path /tmp/ilspy && /tmp/ilspy/ilspycmd -p -o /tmp/avwv ~/.nuget/packages/avalonia.controls.webview/12.1.0/lib/net8.0/Avalonia.Controls.WebView.dll`. Paths below are relative to `/tmp/avwv`. I spot-checked that the `lib/net10.0` build has the same `WKNavigationDelegate` and `MaciosWebViewAdapter`.
- `WK:` = WebKit `main`, from https://github.com/WebKit/WebKit (headers under `Source/WebKit/UIProcess/API/Cocoa/`, implementation in `Source/WebKit/UIProcess/Cocoa/{NavigationState,UIDelegate}.mm`).
- Repo paths are relative to `avalonia/src/OmpGui.App/`.
- `[INFERENCE]` marks anything I did not observe directly.

---

## 0. TL;DR

1. **studio.example.com uses HTTP Basic authentication.** Every path answers `401` with `WWW-Authenticate: Basic realm="Studio"` (nginx/1.24.0; see §1). Safari or Chrome would show a username/password sheet. WKWebView only shows one if the **navigation delegate implements `webView:didReceiveAuthenticationChallenge:completionHandler:`**. Avalonia's delegate does not, so WebKit takes the documented default: *"the web view will respond to the authentication challenge with the NSURLSessionAuthChallengeRejectProtectionSpace disposition"* (WK `WKNavigationDelegate.h:160`; code in `NavigationState.mm:1203-1215`). The user then sees nginx's "401 Authorization Required" page, and the credential window never appears. This is the "window that never appears".
2. **Avalonia 12.1.0 on macOS sets no `WKUIDelegate`.** No file in the assembly mentions `UIDelegate`. Its navigation delegate implements only 2 selectors: `webView:didFinishNavigation:` and `webView:decidePolicyForNavigationAction:decisionHandler:` (AV `Avalonia.Controls.Macios.Interop.WebKit/WKNavigationDelegate.cs:40-51`). The consequences follow WebKit's documented defaults:
   - alert = OK
   - confirm and prompt = Cancel
   - `window.open` / `target=_blank` = cancelled
   - `<input type=file>` = Cancel
   - downloads ignored
   - Storage Access API denied
   - no failure events
   - crashes go unreported
3. **Avalonia gives us enough public surface to fix all of this without forking it or omp:**
   - `NativeWebView.AdapterCreated` / `AdapterDestroyed`
   - `TryGetPlatformHandle()`, which returns the raw `WKWebView*` (`IAppleWKWebViewPlatformHandle.WKWebView`), `ICoreWebView2*` (`IWindowsWebView2PlatformHandle.CoreWebView2`), or `WebKitWebView*` (`IGtkWebViewPlatformHandle` / `ILinuxWpePlatformHandle`)
   - `EnvironmentRequested`, which exposes data store, user agent, devtools, and for WebView2 `ExplicitEnvironment`

   On macOS we need our own Objective-C interop, in the same style as `Platform/MacWebViewSnapshot.cs`:
   - (a) our own `WKUIDelegate` class, set with `setUIDelegate:`; Avalonia never sets one, so nothing conflicts.
   - (b) extra methods added with `class_addMethod` to the runtime class of Avalonia's navigation delegate (`ManagedWKNavigationDelegate`), followed by re-assigning the same delegate. WebKit caches `respondsToSelector:` in `setNavigationDelegate:` (WK `NavigationState.mm:182-214`).
   - (c) our own `WKDownloadDelegate`.
   - (d) a raw-`WKWebView` popup host. It must be built with WebKit's configuration, and Avalonia's `NativeWebView` cannot accept an external configuration.
4. **Popup / OAuth:** host `window.open` targets as **tabs inside the browser pane** that keep `window.opener`. `webViewDidClose:` closes the tab. The agent's commands follow the topmost popup. Google's "disallowed_useragent" is enforced on Google's servers. Use a Safari-identical user agent plus a "Continue in your browser" fallback card (§3.9).
5. **Every page dialog goes through one cross-platform `BrowserDialogBroker`.** The user sees an in-pane card. The agent learns about it through the cmux bridge: script calls return a `dialog_open` error with the text, `browser.snapshot` lists the dialog, and Enter/Escape/type answer it. omp's cmux protocol has no dialog methods (§4).

---

## 1. Root cause of the studio.example.com failure (observed)

```
$ curl -sIL https://studio.example.com            (also /login, /api, /robots.txt — identical)
HTTP/1.1 401 Unauthorized
Server: nginx/1.24.0 (Ubuntu)
WWW-Authenticate: Basic realm="Studio"
Content-Type: text/html   (188-byte nginx "401 Authorization Required" page)
```
`https://example.com` itself did not answer. The whole studio is behind nginx `auth_basic`. No OAuth, GIS, Apple/Telegram widget or passkey is reachable before Basic auth succeeds; the app's own bundles are behind the 401, so I could not inspect them.

Chain in OMP GUI today:
1. `Controls/PreviewPanel.axaml.cs:129-140` creates `new NativeWebView()`. It attaches no `EnvironmentRequested` and no `AdapterCreated` handler, so it uses Avalonia's defaults.
2. AV `Avalonia.Controls.Macios/MaciosWebViewAdapter.cs:159-165` sets only `navigationDelegate = ManagedWKNavigationDelegate` and never calls `setUIDelegate:`.
3. The class has no `webView:didReceiveAuthenticationChallenge:completionHandler:`, so WK `NavigationState.mm:1208-1209` calls `completeChallenge(RejectProtectionSpaceAndContinue)`.
4. nginx's 401 body loads. `didFinishNavigation` fires, and the GUI reports a successful load of "401 Authorization Required" (`PreviewPanel.axaml.cs:202-216`).

Fix: §3.1. Even after Basic auth, the app behind it may use popups, confirm() and similar. §3.2–§3.8 cover those, so the next layer does not fail the same way.

---

## 2. What Avalonia 12.1.0 does on each engine (gap matrix)

| Feature | macOS WKWebView (Avalonia) | Windows WebView2 (Avalonia, HWND mode) | Linux WebKitGTK (Avalonia X11) | Linux WPE (Avalonia picks it when installed) |
|---|---|---|---|---|
| alert/confirm/prompt | **Silently auto-answered**: alert = OK; confirm and prompt = Cancel (WK `WKUIDelegate.h:118,134,152`) | Native WebView2 dialog (`AreDefaultScriptDialogsEnabled` default TRUE) | WebKit's built-in dialog (class handler `script_dialog`, `WebKitWebView.cpp:1393`) | No UI toolkit [INFERENCE: effectively dismissed] |
| `window.open` / `_blank` | **Nothing** (`WKUIDelegate.h:95`: "will cancel the navigation"). `_blank` link clicks raise `NewWindowRequested` (AV `MaciosWebViewAdapter.cs:310-318`, `targetFrame==nil`). `window.open()` does not raise it [INFERENCE: WebCore createWindow skips the new-window policy] | Default: WebView2 opens its own popup window with opener, **but** `PreviewPanel.axaml.cs:282-288` sets `Handled=true` → suppressed | `create` default returns NULL (`WebKitWebView.cpp:654-657`) → nothing; `_blank` raises `NewWindowRequested` (AV `GtkWebViewAdapter.cs:438-457`) | Avalonia connects `create` and always returns NULL (AV `Avalonia.Controls.Linux/WpeWebViewAdapter.cs:480-491`) |
| `window.close()` | Nothing (no `webViewDidClose:`) | Default closes WebView2's own popup | `close` signal not connected | same |
| file input | **Cancel** on macOS (`WKUIDelegate.h:291`) | Native picker (no API to intercept) | GtkFileChooserDialog (class handler, `WebKitWebView.cpp:1396`) | none [INFERENCE] |
| downloads | `<a download>` **navigates instead**: Avalonia answers Allow and WK `NavigationState.mm:693-701` then calls `use()`. Non-showable responses are ignored (`NavigationState.mm:805-825`) | Default download flyout | **Broken**: Avalonia's `decide-policy` returns TRUE for RESPONSE decisions without deciding (AV `GtkWebViewAdapter.cs:459-460`). The decision's dispose calls `use()` (`WebKitPolicyDecision.cpp:56-58`), which skips WebKit's attachment→download default (`WebKitWebView.cpp:665-685`) | [INFERENCE] same as GTK |
| HTTP Basic/Digest/NTLM | **Rejected, 401 page** (the Basic-auth bug) | Native credential dialog unless handled | Built-in dialog (class handler `authenticate`, `WebKitWebView.cpp:1397`) | none |
| client certificate | Rejected (same path) | Native cert picker by default [INFERENCE from docs] | Not handled | none |
| getUserMedia | No UI delegate. WebKit shows its own prompt (`UIDelegate.mm:1360-1364` → `doDefaultAction` → `promptForGetUserMedia`). Camera still fails: Info.plist has only `NSMicrophoneUsageDescription` (`packaging/Info.plist:15`) and the hardened-runtime entitlements have only `device.audio-input` | Default permission prompt | **Denied** (`WebKitWebView.cpp:687-697` denies all but pointer lock) | denied |
| Storage Access API (3p cookies in iframes) | **Denied** with no UI delegate (`UIDelegate.mm:514-516`). With any UI delegate, WebKit shows its own alert (`UIDelegate.mm:567-570`) | Chromium 3p cookies allowed by default | ITP off; 3p cookie policy default | — |
| element fullscreen | `elementFullscreenEnabled` default **NO** (`WKPreferences.h:79-82`) | works | works | — |
| nav failure | **No event**: no `didFailProvisionalNavigation` / `didFailNavigation`, so the GUI's spinner and the agent's wait never end | `NavigationCompleted` with `IsSuccess=false` | `load-changed` only | `load-failed` connected |
| web-process crash | Auto-reload by WebKit, invisible to the GUI and agent (`NavigationState.mm:1304-1320` returns false → page proxy reloads) [INFERENCE: reload path in WebPageProxy] | `ProcessFailed` not connected | `web-process-terminated` not connected | — |
| data store | `WKWebsiteDataStore.defaultDataStore` (persistent per bundle id), since `DataStoreIdentifier` is unset (AV `MaciosWebViewAdapter.cs:153-156`) | default user-data folder | `webkit_web_context_get_default()` | default |

Public Avalonia hooks we use:
- `NativeWebView.EnvironmentRequested` (fires once per adapter; deferrable via `GetDeferral()`). It carries:
  - `AppleWKWebViewEnvironmentRequestedEventArgs { NonPersistentDataStore, DataStoreIdentifier (Guid, macOS 14+), ApplicationNameForUserAgent, UpgradeKnownHostsToHTTPS=true, LimitsNavigationsToAppBoundDomains, ScriptHandlerMessageName, EnableDevTools }`
  - `WindowsWebView2EnvironmentRequestedEventArgs { ExplicitEnvironment (ICoreWebView2Environment*), ProfileName, UserDataFolder, AdditionalBrowserArguments, Language, IsInPrivateModeEnabled, AllowSingleSignOnUsingOSPrimaryAccount, ExperimentalOffscreen, … }`
  - `GtkWebViewEnvironmentRequestedEventArgs { ApplicationNameForUserAgent, BaseDataDirectory, BaseCacheDirectory, EphemeralDataManager, … }`
  - `LinuxWpeWebViewEnvironmentRequestedEventArgs { DataDirectory, CacheDirectory, PreferWebKitGtkInstead }`
- `NativeWebView.AdapterCreated` (AV `NativeWebView.cs:244`). It is raised at `:593`, **after** `adapter.Navigate(_lastSource)` at `:584-587`. That is still synchronous on the UI thread, so delegate callbacks for the first load have not run yet: they come over IPC on a later run-loop turn [INFERENCE]. Hooks installed in this handler therefore apply to the first load.
- `WebViewAdapterEventArgs.TryGetPlatformHandle()` gives:
  - `IAppleWKWebViewPlatformHandle.WKWebView` (`nint WKWebView*`, plus `GetWKWebViewRetained()`)
  - `IWindowsWebView2PlatformHandle.CoreWebView2` / `.CoreWebView2Controller`. These come from `ComInterfaceMarshaller.ConvertToUnmanaged`, i.e. AddRef'ed, so the caller must Release.
  - `IGtkWebViewPlatformHandle.WebKitWebView`
  - `ILinuxWpePlatformHandle.WebKitWebView`

---

## 3. macOS design (WKWebView)

### 3.0 Interop mechanism: no fork, no conflict with Avalonia's delegate

New code: `Platform/Mac/WebKitHooks.cs` (static, `[SupportedOSPlatform("macos")]`, `unsafe`), in the style of `Platform/MacWebViewSnapshot.cs`.

**A. Our UI delegate (new class, nothing to merge).**
- At first use: `objc_allocateClassPair(NSObject, "OmpWKUIDelegate")`, `class_addProtocol(objc_getProtocol("WKUIDelegate"))`, `class_addMethod` for each selector below, then `objc_registerClassPair`.
- One instance per web view. Call `[wk setUIDelegate:obj]`. `uiDelegate` is a **weak** property, so a C# `Hooks` object keeps the +1 reference until `AdapterDestroyed`. There we call `[wk setUIDelegate:nil]` and release.
- IMPs map `self` → hooks through a `Dictionary<nint, MacBrowserHooks>` keyed by the delegate pointer. An alternative is an ivar added before registration (Avalonia's `_managedSelf` pattern, AV `NSManagedObjectBase.RegisterManagedMembers`).

**B. Augmenting Avalonia's navigation delegate (`ManagedWKNavigationDelegate`).**
- `nav = [wk navigationDelegate]`; `cls = object_getClass(nav)`.
- For each selector, only if `class_getInstanceMethod(cls, sel) == 0`: `class_addMethod(cls, sel, imp, types)`. It returns NO if Avalonia ever adds the same selector; log that and degrade.
- Then `[wk setNavigationDelegate:nav]`, reusing the same object, so WebKit recomputes its `m_navigationDelegateMethods` (WK `NavigationState.mm:182-214`). Do this on every `AdapterCreated` (cheap and idempotent).
- IMPs find their hooks by the **`webView` argument** (`Dictionary<nint WKWebView*, MacBrowserHooks>`), because `self` is Avalonia's object.

Added selectors:

| selector | types | purpose |
|---|---|---|
| `webView:didReceiveAuthenticationChallenge:completionHandler:` | `v@:@@@` | §3.1 (the Basic-auth fix) |
| `webView:decidePolicyForNavigationAction:preferences:decisionHandler:` | `v@:@@@@` | WebKit **prefers** this over Avalonia's 3-arg variant (`NavigationState.mm:653-658`). Our IMP forwards to Avalonia's 3-arg IMP with **our own block**, so Avalonia's `NavigationStarted` / `NewWindowRequested` / `WebResourceRequested` still fire. It then maps `Allow && navigationAction.shouldPerformDownload` → `WKNavigationActionPolicyDownload (2)` (macOS 11.3+; app min is 14.0, `packaging/Info.plist:13`). This fixes `<a download>` (§3.5). |
| `webView:decidePolicyForNavigationResponse:decisionHandler:` | `v@:@@@` | §3.5 |
| `webView:navigationAction:didBecomeDownload:` / `webView:navigationResponse:didBecomeDownload:` | `v@:@@@` | §3.5 |
| `webView:didStartProvisionalNavigation:` | `v@:@@` | loading state for the GUI and the agent wait |
| `webView:didFailProvisionalNavigation:withError:` / `webView:didFailNavigation:withError:` | `v@:@@@` | §3.10 |
| `webViewWebContentProcessDidTerminate:` | `v@:@` | §3.10 |

**C. Block ABI rules.** Every completion/decision handler WebKit passes us is a block. Its invoke pointer is at offset 16 (`isa` 8 + `flags` 4 + `reserved` 4); Avalonia reads it the same way in `BlockLiteral.GetCallback`. Call it as `((delegate* unmanaged[Cdecl]<nint, …, void>)*(nint*)(blk+16))(blk, args…)`.
- Handlers answered **later** (any user dialog) must be `_Block_copy`'d on entry and `_Block_release`'d after the single invocation.
- Every handler must be called **exactly once**. WebKit wraps each one in `CompletionHandlerCallChecker` (e.g. `UIDelegate.mm:928`); a handler that is never called raises `NSInternalInconsistencyException` when it is deallocated.
- Hence a `PendingNativeCompletion` type with an idempotent `Complete(...)`, plus default answers on tab close, `AdapterDestroyed`, web-process crash and app quit.

**D. Threading.** All delegate callbacks arrive on the main thread, which is Avalonia's UI thread on macOS. Never block in them: post to the broker, return, and complete the copied block later on the UI thread.

**E. Lifetime.** On `AdapterDestroyed`:
- complete all pending blocks with their defaults;
- `setUIDelegate:nil`;
- remove the `webView` key from the registry;
- release our delegate objects.

Avalonia's own `Dispose` already sets `navigationDelegate = nil` (AV `MaciosWebViewAdapter.cs:240`).

### 3.1 HTTP authentication (Basic/Digest/NTLM/Negotiate, proxies): **fixes Basic-auth**

`didReceiveAuthenticationChallenge(webView, challenge, handler)`:
- `space = [challenge protectionSpace]`; `method = [space authenticationMethod]`. Compare against the exported `NSString*` constants, loaded with `NativeLibrary.GetExport(Foundation, "NSURLAuthenticationMethodHTTPBasic")` etc., not literals.
- `ServerTrust` → `handler(PerformDefaultHandling=1, nil)`. Normal TLS validation must stay intact.
- `ClientCertificate` → `PerformDefaultHandling`. A client-certificate picker would need keychain `SecIdentity` access, and keychain ACL prompts for an ad-hoc-signed app, so that is out of scope.
- `HTTPBasic` / `HTTPDigest` / `NTLM` / `Negotiate` / `HTMLForm` → `broker.RequestCredentials`, with:
  - `host`, `port`, `realm` (`[space realm]`), `isProxy` (`[space isProxy]`)
  - `scheme` (https or not; warn for plain-http Basic)
  - `previousFailureCount` (> 0 → show "That username or password didn't work")
  - prefill from `[challenge proposedCredential]`
- Answers:
  - OK → `handler(UseCredential=0, [NSURLCredential credentialWithUser:password:persistence:NSURLCredentialPersistenceForSession(=1)])`
  - Cancel → `handler(CancelAuthenticationChallenge=2, nil)`, which shows the 401 body like Safari. If the webview goes away, cancel the same way.
- Persistence: **ForSession only**. It lives as long as WebKit's network process, so later requests to the same realm are not challenged again.
  - Do not use `Permanent`: it writes the login keychain, and ad-hoc re-signing on every build (`tools/package/test-app.sh:36`) turns later reads into keychain permission prompts.
  - Optional later: an "Always for this site" option stored by the app in its own encrypted store and replayed at challenge time. Not needed for the fix.
- Card wording: **"studio.example.com wants you to sign in"**, then "Realm: Studio · Your password is sent to this site only.", fields Username/Password, buttons **Sign in** / **Cancel**. Add an extra warning line for `http:`: "This site doesn't use a secure connection."
- Agent: never fill credentials from the agent. Bridge behaviour is in §4.

### 3.2 alert / confirm / prompt

UI-delegate selectors (all optional):

| selector | types |
|---|---|
| `webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:` | `v@:@@@@` |
| `webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:` | `v@:@@@@` |
| `webView:runJavaScriptTextInputPanelWithPrompt:defaultText:initiatedByFrame:completionHandler:` | `v@:@@@@@` |

- Origin for the card title: `[[frame securityOrigin] host]`, plus `[frame isMainFrame]` to mark "an embedded page on …".
- Blocks: alert `void(^)(void)`; confirm `void(^)(BOOL)`; prompt `void(^)(NSString*)`, where nil means Cancel.
- Page JS is **suspended** until the block is called. This matters for the agent (§4).
- UX: one dialog at a time per tab (WebKit already serialises them).
  - Card title: "studio.example.com says" (Chrome/Safari convention; "An embedded page on X says" for subframes).
  - Body: message (max ~2000 chars, scrollable). Buttons: **OK** / **Cancel**. Prompt adds a text field prefilled with `defaultText`.
  - Repeated dialogs: after the 3rd within ~10 s from the same tab, offer "Don't let this page show more dialogs". That auto-answers (alert OK, confirm false, prompt nil) until the next navigation, as Chrome does.
- Never auto-time-out a user dialog. Release pending blocks on navigation away, tab close and crash.

### 3.3 Popups: `window.open`, `target=_blank`, OAuth with `window.opener`, `window.close()`

`webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:` (`@@:@@@@`) must **synchronously return a new `WKWebView` created with exactly the passed `configuration`**. WebKit verifies `_relatedWebView` / opener frame and raises `"Returned WKWebView was not created with the given configuration."` (`UIDelegate.mm:397-417`). WebKit then loads the request into it itself, so never call `loadRequest:` on it.

Consequence: popups **cannot** be Avalonia `NativeWebView`s. `MaciosWebViewAdapter` always builds its own `WKWebViewConfiguration` (AV `MaciosWebViewAdapter.cs:135-158`) and offers no way to inject one. Design:
- `[[WKWebView alloc] initWithFrame:CGRectZero configuration:configuration]` through our interop.
  - The configuration copy inherits the opener's `websiteDataStore`, `processPool` and `userContentController`, so cookies and session are shared.
  - Install our own full delegates on it: a **separate** class `OmpWKNavigationDelegate` (we own this view, so we implement every navigation selector ourselves, including the ones Avalonia has) plus `OmpWKUIDelegate`. Nested alerts, Basic auth, downloads and popups-from-popups then all work.
- Host it with a new control `Controls/MacRawWebViewHost : NativeControlHost`.
  - `CreateNativeControlCore(parent)` returns `new PlatformHandle(wkWebView, "NSView")`; `DestroyNativeControlCore` removes it from its superview.
  - Avalonia's macOS `NativeControlHost` accepts `"NSView"` handles; this is the same embedding `NativeWebView` uses (`NativeHostAdapterFactory`).
- **Presentation: a tab in the browser pane** (Claude Desktop's browser is tabbed; ref-claude-code.md:957). Separate windows hide behind the main window and are invisible to the agent's screenshot.
  - The new tab is selected immediately and labelled with its origin and a "Pop-up" badge.
  - The opener tab keeps running while hidden: an `IsVisible=false` NativeControlHost keeps the `WKWebView` alive. For OAuth reliability, set `[[wk configuration] preferences].inactiveSchedulingPolicy = WKInactiveSchedulingPolicyNone` (macOS 14+, `WKPreferences.h:95-99`) on the opener while a popup it opened is alive, so `postMessage`/polling of `popup.closed` is not suspended [INFERENCE: the effect of the default policy on a hidden opener is untested].
  - `windowFeatures` (`width`, `height` NSNumber?) are ignored: the tab fills the pane. Small "window" sizes can be shown as a centred viewport inside the tab via the mobile-emulation viewport (requirement 3, other slice).
- `webViewDidClose:` (`v@:@`) closes that popup tab and reselects the opener. Typical OAuth popups `postMessage` the code to `window.opener` and call `window.close()`, so the user returns to the opener automatically. Also close popup tabs when their opener tab closes.
- `NewWindowRequested` (Avalonia event, `_blank` link clicks only) in `PreviewPanel.axaml.cs:282-288`:
  - Stop setting `Handled=true` for http(s), so WebKit continues to `createWebView`.
  - Keep the system-browser route only when the user Cmd-clicks, or picks "Open in your browser" from the context menu (Claude convention, ref-claude-code.md:959).
  - Keep the scheme check.
- `javaScriptCanOpenWindowsAutomatically` defaults to YES on macOS (`WKPreferences.h:43-47`), so non-gesture `window.open` is allowed. Pop-up blocking policy: allow during a user gesture or agent action; otherwise show a small "Pop-up blocked — Show" bar [INFERENCE: `navigationAction` gives no user-gesture flag publicly; use `navigationAction.navigationType`, buttonNumber and modifierFlags as the hint].
- Agent: while a popup tab is selected, all bridge commands target it (§4).

### 3.4 File inputs

`webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:` (`v@:@@@@`, macOS 10.12+):
- Read `allowsMultipleSelection` and `allowsDirectories` (`WKOpenPanelParameters.h:39,43`).
- Show `TopLevel.StorageProvider.OpenFilePickerAsync(new() { AllowMultiple })`, or `OpenFolderPickerAsync` when directories are allowed.
  - This is Avalonia's `NSOpenPanel`, so no new interop UI. A user-selected file needs no TCC prompt.
  - Map results to `NSURL fileURLWithPath:` in an `NSArray`; nil means cancel.
- `accept=` filters are SPI only (`_acceptedMIMETypes`, `_acceptedFileExtensions`). Use them via `respondsToSelector:` if present; otherwise allow all files.
- Agent: omp's cmux has no upload method (the methods omp uses, grep of the omp 18.8.0 runtime: navigate, url.get, eval, wait, snapshot, click, dblclick, hover, focus, check, uncheck, scroll_into_view, type, fill, press, scroll, screenshot, open_split, close). If an agent action opens a file chooser, show the picker to the user and report `file_chooser_open` to the agent (§4). Agent-side uploads can be done in JS: `DataTransfer` + `new File([bytes], name)`, assigned to `input.files`, then dispatch `change`. That is a page-script technique, no native hook.

### 3.5 Downloads

Navigation-delegate additions (§3.0B) plus our own `OmpWKDownloadDelegate` (protocol `WKDownloadDelegate`, macOS 11.3+):
- `decidePolicyForNavigationResponse`: `Download (2)` when `![navigationResponse canShowMIMEType]`, or when the response is an `NSHTTPURLResponse` whose `Content-Disposition` starts with `attachment`; otherwise `Allow (1)`. Mirror WebKitGTK's rule (`WebKitWebView.cpp:672-683`) for consistency.
- `<a download>`: the preferences-variant shim in §3.0B converts Allow → Download when `shouldPerformDownload`. Also covers `blob:` and `data:` downloads.
- `navigationAction/navigationResponse:didBecomeDownload:` → `[download setDelegate:ours]`. The property is weak, so keep a strong reference until finish or fail.
- `download:decideDestinationUsingResponse:suggestedFilename:completionHandler:` (required) → `StorageProvider.SaveFilePickerAsync(SuggestedFileName = suggestedFilename, SuggestedStartLocation = Downloads)`. The completion receives the chosen file URL, or nil to cancel.
  - Why a save panel instead of auto-saving to `~/Downloads`: for a non-sandboxed app, `~/Downloads` is TCC-protected and the first silent write triggers a "would like to access files in your Downloads folder" prompt. A user-chosen save path does not [INFERENCE: TCC user-intent exemption for panel selections].
  - Option "Always save to Downloads without asking" (off by default) accepts that one-time runtime prompt.
- `downloadDidFinish:` / `download:didFailWithError:resumeData:` / `download:didReceiveAuthenticationChallenge:completionHandler:` (route to §3.1) → a downloads strip in the pane: name, progress via KVO on `[download progress]` (polled every 250 ms instead of KVO interop), and "Show in Finder" through Avalonia `Launcher.LaunchFileInfoAsync` on the folder.
- Agent: reported as `download_started` / `download_finished(path)` in the next command's result notes (§4).

### 3.6 Permissions (camera/microphone, Storage Access, fullscreen, geolocation)

- `webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:` (`v@:@@@q@`, macOS 12+). `type`: 0 camera, 1 microphone, 2 both. Decision: 0 Prompt, 1 Grant, 2 Deny.
  - Card: "**example.com wants to use your microphone**", buttons **Allow once** / **Always allow on this site** / **Deny**. This is Claude's external-site permission card pattern (ref-claude-code.md:960). Store the choice per origin; revocable in Settings → Browser.
  - On Grant, macOS shows its own TCC prompt the first time. That is a runtime prompt and acceptable.
  - Build-time prerequisites: add `NSCameraUsageDescription` to `packaging/Info.plist`, and `com.apple.security.device.camera` to `packaging/entitlements.plist` (Developer ID hardened runtime only). Microphone is already present (`Info.plist:15`, `entitlements.plist` `device.audio-input`). Without the camera key, WebKit denies camera requests [INFERENCE: WebKit checks the usage description before prompting].
  - Display capture (`getDisplayMedia`) keeps WebKit's own picker (`UIDelegate.mm:1364-1369`).
- Storage Access API (`document.requestStorageAccess()`, which some embedded logins and SSO iframes need): setting *any* UI delegate switches WebKit from "deny" (`UIDelegate.mm:514-516`) to its own built-in `presentStorageAccessAlert` (`UIDelegate.mm:567-570`). No extra code beyond §3.0A.
- Element fullscreen: `[[[wk configuration] preferences] setElementFullscreenEnabled:YES]` (`WKPreferences.h:79-82`). `-configuration` returns a copy, but it shares the same `WKPreferences` object (verified in the harness: `run.sh trusted-input` sets `javaScriptCanOpenWindowsAutomatically` this way on a live view and it applies). Without this, video sites cannot go fullscreen.
- Geolocation: the public delegate API exists only on macOS 27 (`WKUIDelegate.h:302`). On 14–26, WebKit's default needs `NSLocationUsageDescription`. Leave it denied (do not add the key) and show "Location isn't available in the built-in browser".
- Notifications / Web Push: not supported for third-party `WKWebView` on macOS. Leave as is.

### 3.7 WebAuthn / passkeys

- In a third-party `WKWebView`, passkeys and security keys work only for RP IDs in the app's **Associated Domains** (`webcredentials:`; Apple DTS answer, https://developer.apple.com/forums/thread/734513). For *any* RP they need the request-only entitlement `com.apple.developer.web-browser.public-key-credential` (https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.developer.web-browser.public-key-credential), which Apple grants to web-browser apps.
- Both entitlements require a provisioning profile and a Developer ID team. Neither works with the ad-hoc test app (`tools/package/test-app.sh:36`). Without them, `navigator.credentials.create/get({publicKey})` fails with `NotAllowedError`, while `PublicKeyCredential` still exists. Sites then offer a passkey button that silently fails (seen in t3code PR #16952, luna-browser #90).
- Design (no new entitlements, no build-time prompts):
  1. A `WKUserScript` (document-start, all frames), added via `[[[wk configuration] userContentController] addUserScript:]`. It wraps only `navigator.credentials.create/get` **when `publicKey` is present**:
     - It calls the original first, so a future entitlement just works.
     - On `NotAllowedError` it posts `{type:'webauthn-unavailable', origin}` to the existing script message handler. Avalonia's handler name defaults to `postAvWebViewMessage` (AV `MaciosWebViewAdapter.cs:133`); `PreviewPanel.OnWebMessageReceived` already routes messages.
     - It does **not** stub `isUserVerifyingPlatformAuthenticatorAvailable` by default (fingerprint noise; coordinate with AntiDetection).
  2. On that message, show the card: "**Passkeys aren't available in the built-in browser.** Use another sign-in method, or continue in your browser." with buttons **Continue in your browser** (`Launcher.LaunchUriAsync(currentUrl)`) and **Dismiss**.
  3. Document that a signed release could add `webcredentials:` for the user's own domains if they control the AASA file.
- `ASWebAuthenticationSession` (exposed by Avalonia as `WebAuthenticationBroker`) is **not** a general fix. It needs a callback scheme or URL the site redirects to, and its cookies do not flow back into our `WKWebView`. Use it only for OAuth flows the GUI itself owns.

### 3.8 Cookies, ITP, persistence

- Today: `WKWebsiteDataStore.defaultDataStore`, persistent per bundle id `io.github.samnolak.omp-gui` (`packaging/Info.plist:7`). Logins already survive restarts in the packaged app. Unbundled runs (`dotnet run`) use a store keyed by the process name instead [INFERENCE].
- Make it explicit through `EnvironmentRequested`:
  - `DataStoreIdentifier = <fixed Guid "OMP GUI browser profile">`. `WKWebsiteDataStore dataStoreForIdentifier:` is macOS 14+, matching the app minimum.
  - `NonPersistentDataStore = false`.
  - `EnableDevTools = true` (Web Inspector via right-click).
  - Benefits: "Clear browsing data" (Claude's ⋮ menu, ref-claude-code.md:958) becomes `+[WKWebsiteDataStore removeDataStoreForIdentifier:completionHandler:]`, or `removeDataOfTypes:modifiedSince:` while views are alive. "Keep cookies" off becomes `NonPersistentDataStore = true`.
  - Popups share the opener's store automatically (§3.3).
- ITP / third-party cookies: WebKit blocks third-party cookies in all `WKWebView`s and runs ITP; there is no public opt-out on macOS (https://webkit.org/blog/10218/full-third-party-cookie-blocking-and-more/).
  - Popup-based OAuth (first-party in the popup) works.
  - Iframe-embedded logins need the Storage Access API, which §3.6 enables.
  - `UpgradeKnownHostsToHTTPS` stays at Avalonia's default `true`.

### 3.9 User agent and Google sign-in

- Default WKWebView UA is Safari's without `Version/x Safari/605.1.15`. Set `ApplicationNameForUserAgent = $"Version/{SafariVersion} Safari/605.1.15"`. `SafariVersion` is read from `/Applications/Safari.app/Contents/Info.plist` `CFBundleShortVersionString` (readable without prompts; here: `27.0` on macOS 26.6.2). The UA then matches Safari byte for byte. Use the `EnvironmentRequested` property, not `customUserAgent`, so WebKit still appends the correct platform part. Details are deferred to the AntiDetection slice (message sent).
- Google: embedded webviews have been blocked since 2016/2017 and enforced more strictly since 2023 ("Error 403: disallowed_useragent", https://developers.googleblog.com/upcoming-security-changes-to-googles-oauth-20-authorization-endpoint-in-embedded-webviews/). Reports conflict on whether a Safari-identical UA suffices (Apple forum https://developer.apple.com/forums/thread/808734: "changing the user-agent had no effect") [INFERENCE: detection is server-side and partly not UA-based]. Design:
  - Navigation hook: when a page in any tab reaches `accounts.google.com/…` with `disallowed_useragent` (or the "This browser or app may not be secure" page — detect by URL `…/signin/rejected` or title) [INFERENCE: exact URL patterns to confirm], show the card "**Google doesn't allow sign-in inside embedded browsers.** Continue in your browser, then come back." with **Open in your browser**.
  - Cookies cannot be transferred from the system browser, so for Google-gated apps the agent works on the system browser path (omp's own browser tool) or the user signs in with another method.
- References: Codex's in-app browser officially "does not support authentication flows, signed-in pages… cookies" (https://developers.openai.com/codex/app/browser, quoted in https://github.com/openai/codex/issues/19276, where GIS logs `Failed to open popup window`). Claude Desktop's pane is Chromium (Electron), so popups and dialogs are native there; its UX conventions are the external-link dialog, permission card, "Keep cookies" and "Clear browsing data" (ref-claude-code.md:957-960). Our target: Claude parity on flows that WebKit supports, plus an explicit hand-off card where it does not.

### 3.10 Robustness hooks the dialogs depend on

- `didFailProvisionalNavigation` / `didFailNavigation`:
  - Raise a failure into `PreviewViewModel.OnNavigationCompleted(uri, isSuccess:false)`, since Avalonia raises nothing on macOS.
  - Show an in-pane error page: `NSError` domain/code/`localizedDescription`, e.g. "Safari can't open the page"-style wording "The page couldn't be opened: <description>", with **Try again**.
  - Ignore `NSURLErrorCancelled (-999)`, and `WebKitErrorFrameLoadInterruptedByPolicyChange (102)` when a download started.
- TLS errors arrive here (`NSURLErrorServerCertificate*`). Show "This connection isn't private" and **Go back**. No "proceed anyway" in v1.
- `webViewWebContentProcessDidTerminate:`:
  1. Complete all pending dialog blocks with their defaults.
  2. Mark the tab "This page stopped working" with **Reload**; auto-reload once if the last crash was more than 30 s ago.
  3. Fail in-flight agent scripts with `page_crashed`. `evaluateJavaScript` callbacks get `WKErrorWebContentProcessTerminated`.

---

## 4. Agent integration (cmux bridge; omp unchanged)

Facts:
- omp drives the pane through the cmux protocol implemented in `OmpGui.ClientCore/AgentBrowserBridge.cs`, method switch at `:314-395`. The method list (grep of the omp 18.8.0 runtime) has **no dialog, popup, tab or upload methods**.
- While an alert/confirm/prompt is open, the page's JS is blocked, so `RunScriptAsync` (`IAgentBrowserPage.RunScriptAsync`, `:59`) would hang until omp's own timeout.

Design (`OmpGui.ClientCore/BrowserDialogBroker.cs`, platform-neutral and headless-testable):
- `BrowserDialogRequest { Id, TabId, Kind (Alert|Confirm|Prompt|Credentials|FileChooser|Permission|Download|PopupBlocked|PasskeyUnavailable|SignInBlocked), Origin, IsMainFrame, Message, DefaultText, Realm, IsRetry, CreatedAt, Respond(BrowserDialogAnswer) }`. Platform hooks (§3, §5, §6) create requests and get their native completion called from `Respond`.
- `PreviewViewModel` gets `PendingDialog`, `Tabs` (opener + popups), `ActiveTab` and `Downloads`. Views render cards outside the native view (see §4.1).
- Bridge rules (`AgentBrowserBridge` + the app's `IAgentBrowserPage` impl):
  1. **Race every script against the broker.** If a dialog opens while a command is running, or one is already open when a command arrives, do not call into the page. Throw `AgentBrowserException("dialog_open", …)` with a model-readable message, for example: `The page is showing a confirm dialog: "Delete project?". Answer it with browser.press Enter (OK) or Escape (Cancel), or ask the user.` [INFERENCE: omp's cmux backend passes error messages to the model; MapCurrent/RefImplementations can confirm].
  2. **Keyboard answers**, as a human would:
     - `browser.press Enter` → OK/accept
     - `browser.press Escape` → Cancel
     - `browser.type` / `browser.fill` while a prompt is open → sets the prompt text (then Enter accepts)
  3. **`browser.snapshot`** while a dialog is open returns a synthetic tree: `dialog "<origin> says: <message>" [ref=d1]`, `textbox [ref=d2]` (prompt only), `button "OK" [ref=d3]`, `button "Cancel" [ref=d4]`. `browser.click d3/d4` answers.
  4. **Alerts triggered by the agent's own action** (opened during a bridge command): auto-OK after they have been visible for ≥ 1.5 s, then return the result with the note `The page showed an alert: "…" (closed).` This matches Playwright's default of auto-dismissing dialogs. Confirm and prompt are never auto-answered.
  5. **Credentials and passkeys: user only.** Bridge commands return `needs_user` with "studio.example.com asks for a username and password (realm "Studio"). Ask the user to sign in in the browser pane, then continue." The synthetic snapshot shows the card as non-interactive. The composer shows the pane's status dot "Needs your input" (S2 waiting status).
  6. **Popups:** when a popup tab opens during an agent command, it becomes the command target. The result carries the note `A pop-up opened (accounts.example.com). Commands now go to it; it closes itself after sign-in.` On `webViewDidClose`, the target returns to the opener, with the note `The pop-up closed; back on <opener url>.`
  7. **File chooser opened by an agent action:** `needs_user` ("The page opened a file picker; ask the user to choose the file") while the picker is shown to the user.
  8. **Downloads:** command notes `Downloaded <name> to <path>`, or `Download waiting for the user to choose where to save`.
  9. **Navigation failure / crash:** `browser.wait` and `browser.navigate` return `navigation_failed` / `page_crashed` with the `NSError` text, instead of hanging.

### 4.1 UX placement (airspace)

The `NativeWebView` and the popup hosts are native views drawn above Avalonia content, so Avalonia cannot draw cards over them. While a modal page dialog is pending:
1. Take a snapshot of the page (`Platform/WebViewSnapshot.CaptureAsync`, already used for agent screenshots; no screen-recording permission).
2. Show it as a dimmed `Image` and set the native host to `IsVisible=false`.
3. Show the card centred over the frozen page.
4. Restore on answer.

Non-modal items (pop-up blocked, downloads, passkey/Google hand-off) are bars between the toolbar and the page.

Alternative considered: native `NSAlert beginSheetModalForWindow:`. Simpler on macOS, but it cannot be tested headlessly, behaves differently per OS, and is invisible to agent screenshots, so it is rejected.

Wording follows the plan: sentence case, "omp" as subject where the agent is involved ("omp is waiting for you to answer the page").

---

## 5. Windows (WebView2) design

Prerequisite: the app sets `<BuiltInComInteropSupport>false</BuiltInComInteropSupport>` (`OmpGui.App.csproj:8`). That rules out the `Microsoft.Web.WebView2.Core` managed wrappers (classic COM interop RCWs). Instead, declare the few interfaces we need with **`[GeneratedComInterface]`** (source-generated, trim/AOT-safe). For one-offs, keep the raw-vtable style of `Platform/WebView2Snapshot.cs`. Entry point: `IWindowsWebView2PlatformHandle.CoreWebView2` on `AdapterCreated`; it is AddRef'ed, so Release it on `AdapterDestroyed`. Events from our handlers coexist with Avalonia's (WebView2 supports multiple handlers per event).

| Feature | WebView2 API | Design |
|---|---|---|
| alert/confirm/prompt/beforeunload | `ICoreWebView2Settings::put_AreDefaultScriptDialogsEnabled(FALSE)` + `add_ScriptDialogOpening` (`get_Kind`, `get_Message`, `get_DefaultText`, `put_ResultText`, `Accept()`, `GetDeferral()`) | Route to the broker so the agent knows (§4). The defaults would work for the user, but an agent action would hang on them. |
| HTTP auth | `ICoreWebView2_10::add_BasicAuthenticationRequested` (`get_Uri`, `get_Challenge`, `get_Response` → `put_UserName` / `put_Password`, `put_Cancel`, `GetDeferral`) | Same card as §3.1. If `Cancel=false` without credentials, WebView2 shows its default dialog (MS docs), so always fill credentials or cancel. |
| popups with opener | `add_NewWindowRequested` (`get_WindowFeatures`, `GetDeferral`, `put_NewWindow(ICoreWebView2*)`, `put_Handled`) | Take a deferral. Create a second **`NativeWebView` that is never navigated** (`_lastSource` null means no navigation, AV `NativeWebView.cs:584-591`), with `EnvironmentRequested → ExplicitEnvironment = ICoreWebView2_2::get_Environment(opener)` (AV `CoreWebView2Environment.cs:41-47`). Await its `AdapterCreated`, then `put_NewWindow(itsCoreWebView2)`, `Complete` the deferral, and select the new tab. `add_WindowCloseRequested` closes it. **Remove** `Handled=true` in `PreviewPanel.axaml.cs:284` for http(s). Pitfall: MicrosoftEdge/WebView2Feedback#5664 (first navigation of an assigned NewWindow). |
| file input | Native picker; no API | Keep. Agent: none needed (WebView2 does not block script while the picker is open [INFERENCE]). |
| downloads | `ICoreWebView2_4::add_DownloadStarting` (`put_ResultFilePath`, `put_Handled` hides the flyout, `get_DownloadOperation` for progress) | Unify into the pane's downloads strip. Default folder: Downloads (no TCC on Windows). |
| permissions | `add_PermissionRequested` (`get_PermissionKind`, `put_State`, `GetDeferral`, `ICoreWebView2PermissionRequestedEventArgs3::put_SavesInProfile`) | Same per-origin card as §3.6. |
| client certificates | `ICoreWebView2_5::add_ClientCertificateRequested` | Leave the default UI. |
| WebAuthn/passkeys | Supported natively (Windows Hello) | Nothing to do. |
| crash | `add_ProcessFailed` | §3.10 behaviour. |
| failures | Avalonia already sends `NavigationCompleted(IsSuccess=false)` (AV `Win.WebView2/WebViewCallbacks.cs:36-44`) | Show the error page. |
| data/profile | `EnvironmentRequested`: `UserDataFolder` = app data `/browser`, `ProfileName = "omp"` | Popups inherit the profile via `ExplicitEnvironment`. |
| UA/Google | Edge UA by default | Google may still block (same card). |

---

## 6. Linux design (WebKitGTK 4.1 X11, and WPE)

Avalonia picks **WPE** when it is installed, unless `LinuxWpeWebViewEnvironmentRequestedEventArgs.PreferWebKitGtkInstead = true` (AV `Avalonia.Controls/WebViewAdapter.cs:79-96`). `PreviewPanel.Probe` checks only WebKitGTK (`:315-323`). Set `PreferWebKitGtkInstead = true` so there is one code path, and WebKitGTK's built-in dialogs serve as a fallback. Signals must be connected on Avalonia's GLib thread (`AvaloniaGtk.RunOnGlibThread`, internal). Our own GLib dispatch, `g_main_context_invoke(NULL, …)` on the default context Avalonia iterates, works [INFERENCE]. Callbacks then post to the UI thread and complete asynchronously by keeping a ref (`g_object_ref` / `webkit_*_ref`) and returning TRUE.

| Feature | Signal / API | Design / notes |
|---|---|---|
| alert/confirm/prompt | `script-dialog` (`webkit_script_dialog_get_dialog_type`, `get_message`, `confirm_set_confirmed`, `prompt_get_default_text`, `prompt_set_text`, `webkit_script_dialog_ref` + `webkit_script_dialog_close`) | Return TRUE and answer later via the broker. |
| HTTP auth | `authenticate` (`webkit_authentication_request_get_host`, `get_realm`, `is_retry`, `is_for_proxy`, `get_proposed_credential`, `webkit_authentication_request_authenticate(req, webkit_credential_new(u, p, WEBKIT_CREDENTIAL_PERSISTENCE_FOR_SESSION))`, `cancel`) | Same card. |
| popups | `create` → return `webkit_web_view_new_with_related_view(parent)` (shares process and opener); `ready-to-show`; `close` | WPE: Avalonia's `create` handler returns NULL (AV `WpeWebViewAdapter.cs:480-491`). WebKit's object accumulator continues to later handlers on NULL [INFERENCE], so our handler still runs. Hosting: a GTK X11 child cannot easily become an Avalonia tab, so on Linux show popups as a **separate GtkWindow** owned by the GTK thread, titled with the origin. Agent targets it as in §4 (snapshot/eval through that view). |
| file input | `run-file-chooser` default = GtkFileChooserDialog (works) | Keep the default for the user; connect only to notify the broker (return FALSE). |
| downloads | Avalonia's `decide-policy` handler eats RESPONSE decisions (AV `GtkWebViewAdapter.cs:459-460`) | Install a **`g_signal_add_emission_hook`** on `decide-policy` (hooks run before handlers). For RESPONSE decisions that are attachments or have unsupported MIME types, call `webkit_policy_decision_download`. The later `use()` from Avalonia's path / dispose is then a no-op [INFERENCE: listener cleared after a decision; verify in the Xvfb e2e]. Then `WebKitWebContext::download-started` → `decide-destination` → `webkit_download_set_destination`. |
| permissions | `permission-request` (default **deny**, `WebKitWebView.cpp:687-697`) | Per-origin card; `webkit_permission_request_allow/deny`. |
| crash / failures | `web-process-terminated`, `load-failed`, `load-failed-with-tls-errors` | §3.10. |
| data | `GtkWebViewEnvironmentRequestedEventArgs.BaseDataDirectory/BaseCacheDirectory` = app data dir | Persistent profile; "Clear browsing data" via `webkit_website_data_manager_clear`. |
| UA | `ApplicationNameForUserAgent` / `webkit_settings_set_user_agent` | AntiDetection slice. |

---

## 7. Implementation slices, files and tests

1. **Broker + VM + cards** (ClientCore + ViewModels + `Controls/PreviewPanel.axaml`). Fully headless-testable with FakeOmp:
   - card states and wording;
   - one dialog at a time;
   - every request answered exactly once (dispose, crash, navigate-away);
   - bridge rules §4 (`dialog_open`, Enter/Escape/type mapping, synthetic snapshot, popup targeting, `needs_user`, auto-OK alerts).
2. **macOS hooks** (`Platform/Mac/WebKitHooks.cs`, `Platform/Mac/MacRawWebViewHost.cs`, `PreviewPanel` wiring via `EnvironmentRequested` + `AdapterCreated`/`AdapterDestroyed`):
   - §3.1 first: it alone fixes Basic-auth.
   - Then §3.2, §3.3, §3.10, §3.5, §3.4, §3.6, §3.7.
   - Packaging: `NSCameraUsageDescription` + camera entitlement (optional, §3.6).
3. **WebView2 hooks** (`Platform/Windows/WebView2Hooks.cs`, GeneratedComInterface).
4. **Linux hooks** (`Platform/Linux/WebKitGtkHooks.cs`; `PreferWebKitGtkInstead = true`).

Verification without touching the user's screen:
- Headless (Avalonia.Headless): everything in slice 1.
- macOS native: a console harness that runs `NSApplication` with `NSApplicationActivationPolicyProhibited` (no Dock icon, no windows). It creates an off-screen `WKWebView` with the hooks and `inactiveSchedulingPolicy = None`, plus a local `HttpListener` that serves:
  - (a) a `401 Basic realm="Test"` page → expect a `Credentials` request; answer → 200 body;
  - (b) `alert` / `confirm` / `prompt` pages → expect requests and returned values;
  - (c) `window.open` + opener `postMessage` + `window.close()` → expect a popup tab, then the message received, then `webViewDidClose`;
  - (d) `Content-Disposition: attachment` + `<a download>` → files written to a temp dir;
  - (e) a killed web process (`kill -9` of the WebContent pid) → `page_crashed`.

  Verified (B0, `tools/verify/webview-harness`): an off-screen WKWebView in a never-ordered window runs JS, delegates and downloads with activation policy prohibited; tccd sees only preflight status queries (ListenEvent, Camera, Microphone, AllFiles), never a prompting request. No camera, no `~/Downloads`, no keychain.
- Linux: extend `tools/verify/annotate-e2e` (its own Xvfb + openbox) with the same pages.
- Real check for the reported case: open `https://studio.example.com` → the "studio.example.com wants you to sign in (Realm: Studio)" card appears; correct credentials load the app; wrong ones show "That username or password didn't work". The user does this; I have no credentials.

## 8. Sources

- WebKit headers:
  - https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/API/Cocoa/WKNavigationDelegate.h (L52/64 Download policies, L160 auth default, L183/192 didBecomeDownload)
  - https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/API/Cocoa/WKUIDelegate.h (L95 createWebView, L104 didClose, L118/134/152 dialogs, L163 media default, L291 open panel)
  - WKDownloadDelegate.h (L70 decideDestination), WKPreferences.h (L45 popups default, L80 fullscreen, L95 inactiveSchedulingPolicy), WKWebsiteDataStore.h (L109-123 identifiers), WKOpenPanelParameters.h
- WebKit implementation:
  - https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/Cocoa/NavigationState.mm (182-214 delegate caching, 620-712 action policy, 805-825 response default, 1203-1215 auth reject, 1304-1320 crash)
  - https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/Cocoa/UIDelegate.mm (357-427 createNewPage config check, 512-570 storage access, 1348-1380 media)
- WebKitGTK:
  - https://github.com/WebKit/WebKit/blob/main/Source/WebKit/UIProcess/API/glib/WebKitWebView.cpp (654-697 defaults, 1393-1397 class handlers)
  - WebKitPolicyDecision.cpp (56-60 dispose → use)
- WebView2:
  - https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.basicauthenticationrequested
  - https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.newwindowrequested
  - https://github.com/MicrosoftEdge/WebView2Feedback/issues/5664
- Avalonia: https://docs.avaloniaui.net/accelerate/components/webview/nativewebview (AdapterCreated / EnvironmentRequested / TryGetPlatformHandle; its own sample calls `alert()`, which does nothing on macOS for the reasons in §0).
- Passkeys:
  - https://developer.apple.com/forums/thread/734513
  - https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.developer.web-browser.public-key-credential
  - https://github.com/pingdotgg/t3code/pull/16952
- Google embedded-webview block:
  - https://developers.googleblog.com/upcoming-security-changes-to-googles-oauth-20-authorization-endpoint-in-embedded-webviews/
  - https://developer.apple.com/forums/thread/808734
- Codex in-app browser limits: https://github.com/openai/codex/issues/19276 (quotes https://developers.openai.com/codex/app/browser).
- ITP / third-party cookies: https://webkit.org/blog/10218/full-third-party-cookie-blocking-and-more/

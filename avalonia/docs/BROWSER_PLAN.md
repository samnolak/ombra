# Built-in browser plan: default, native windows, stability, emulation, agent control

Status: plan (2026-10-08). Nothing below is implemented yet. Research behind every claim is in
[`browser-research/`](browser-research): `browser-native.md` (native windows, root cause), `omp-browser.md` and
`omp-tern-protocol.md` (how omp 18.8 picks and drives a browser), `browser-input.md` (agent input the engine treats
like the user's), `browser-refs.md` and `codex-browser-internals.md` (Claude Code, Codex, cmux, Playwright).
Rule for whoever executes: **reproduce first, fix, then repeat the same reproduction**. Paths are relative to
`avalonia/src/OmpGui.App` unless noted.

## 0. What is wrong today (verified)

| # | Symptom | Root cause | Evidence |
|---|---|---|---|
| R1 | studio.example.com never shows a sign-in window | The site is behind **HTTP Basic auth** (`401`, `WWW-Authenticate: Basic realm="Studio"`). Avalonia 12.1's WKWebView navigation delegate implements only `didFinishNavigation` and `decidePolicyForNavigationAction`, so WebKit rejects the challenge and shows nginx's 401 page. | `curl -I https://studio.example.com`; decompiled `Avalonia.Controls.Macios.Interop.WebKit/WKNavigationDelegate.cs:40-51`; `browser-native.md` §1 |
| R2 | No `alert`/`confirm`/`prompt`, no popups, no file picker, no downloads, no permission prompts, no crash/failed-load events | Avalonia sets **no WKUIDelegate** and no download delegate; WebKit defaults: alert=OK, confirm/prompt=Cancel, `window.open` cancelled, file input cancelled, `<a download>` navigates. On Windows our own `PreviewPanel.OnWebNewWindowRequested` sets `Handled=true` for every popup. | `browser-native.md` §2; `Controls/PreviewPanel.axaml.cs:282-288` |
| R3 | The agent often doesn't use the built-in browser | omp's browser is an `eval` prelude; backend order is `app.*` → relay → `browser.cdpUrl` → **Tern** → **cmux** (ours) → managed headless Chromium. We don't set `PI_BROWSER_CMUX=1`; a user `cdpUrl`/relay setting wins; omp's docs tell the model "real Chromium tabs" and "static content? use `read`", and nothing says the user sees our pane, so it uses `bash open`, curl, `web_search`. | `omp-browser.md` (a) |
| R4 | Agent clicks can't open OAuth popups, file pickers; some sites ignore them | Input is JS-dispatched synthetic events: `isTrusted=false`, no user activation. | `ClientCore/AgentBrowserScripts.cs`; `browser-input.md` §1 |
| R5 | cmux can't do dialogs, frames, downloads, emulation, cookies; `setViewport` is a no-op | cmux is omp's weakest backend: ~50 helpers refuse to run on it. | `omp-browser.md` (b) |
| R6 | One page for every omp process; a second tab evicts the first | `AgentBrowserBridge` has one `_surface` shared by all sessions and the TUI. | `ClientCore/AgentBrowserBridge.cs:423-438` |
| R7 | `browser.wait` timeout races omp's socket timer | Our wait gives up at exactly `timeout_ms`, the same moment omp destroys the socket. | `AgentBrowserBridge.cs:514-536` |
| R8 | A page can forge what the agent sees | `globalThis.__ompAgent` lives in the page world and is reused if present; annotate messages go through a page-world `invokeCSharpAction`. | `AgentBrowserScripts.cs:20`; `browser-input.md` §0 |
| R9 | No mobile emulation; resizing changes nothing the agent asked for | WKWebView has no DPR/touch/`isMobile` override; we expose no viewport control at all. | `browser-refs.md` §0.4 |

## 1. Decisions

1. **Serve omp's Tern host protocol from the GUI** (new `ClientCore/Browser/TernHost.cs`). omp 18.8 already speaks it to
   a native WKWebView host and ranks it above cmux; its ops cover trusted `input`, `dialogs`/`dialog`, `files`,
   `downloads`, `credentials`, `agent` (UA), `viewport`, `appearance`, `cookies`, `capture` (full page), `pdf`,
   `events` (url/title/dialog/download/console/network), isolated-world `eval` with Promise await and frames, and
   **multiple tabs**. omp's model-facing doc already describes Tern tabs as "visible to the user, trusted native input".
   Keep the cmux bridge one release as a fallback, with R6/R7 fixed.
2. **Native hooks without forking Avalonia**: public `NativeWebView.EnvironmentRequested`,
   `AdapterCreated`/`AdapterDestroyed`, `TryGetPlatformHandle()` give the raw `WKWebView` / `ICoreWebView2` /
   `WebKitWebView`. macOS: our own `WKUIDelegate` (Avalonia sets none); extra navigation-delegate selectors added to
   Avalonia's delegate class with `class_addMethod` and re-assigned; our own `WKDownloadDelegate`. Same ObjC interop
   style as `Platform/MacWebViewSnapshot.cs`.
3. **One dialog broker, in-pane cards** for every native window (auth, alert/confirm/prompt, file, download,
   permission, popup). Routing rule (cmux): what the agent caused goes to the agent as *modal state*; what the user
   caused is shown to the user; credentials and passkeys are always the user's.
4. **User-equivalent agent input** (trusted, grants user activation), from inside our process, no OS permissions:
   macOS = `NSEvent`s delivered to the WKWebView's own responder methods (+ `insertText:` for text), the way Safari's
   WebDriver and cmux do it; Windows = CDP `Input.*` via `CallDevToolsProtocolMethod`; Linux = `gtk_widget_event`.
   Our helper scripts move to an **isolated content world** (`WKContentWorld`, CDP isolated world, GTK world).
5. **Truthful identity, no evasion tricks.** The page sees the real engine: a Safari-identical UA on WebKit (adds the
   `Version/… Safari/…` token WKWebView lacks), no page-world globals of ours, native input with natural pacing. We do
   **not** spoof fingerprints, hide automation from fraud checks, or solve CAPTCHAs: a human-verification challenge is
   handed to the user with a card. Result: sites see an ordinary Safari/Edge used by a person, because input and
   identity *are* ordinary.
6. **Default = the built-in browser** for every omp the GUI starts, by official mechanisms only: per-process env
   (`TERN_PANE_SOCKET`, `TERN_PANE`, `PI_BROWSER_TERN=1`; cmux fallback `PI_BROWSER_CMUX=1`), a GUI-owned settings
   overlay appended to `PI_CONFIG_FILES` (`browser.enabled: true`, `browser.cdpUrl: ""`, `idleCloseSec: 0`), and a
   GUI extension (`--extension`) whose `before_agent_start` adds a short system-prompt note ("the browser is visible
   to the user; use `browser.open` for any page the user should see or sign in to; never `bash open`; `persist:true`
   for logins"). Not `--append-system-prompt` (it replaces the user's `APPEND_SYSTEM.md`). A Settings switch
   "Agent uses the OMP GUI browser" (default on) turns the overlay/flags off.
7. **Mobile emulation is honest per engine**: WebView2 gets the full bundle (viewport, DPR, `mobile`, touch, UA +
   client hints, all from one real device profile); WKWebView gets viewport projection + real device UA + page zoom,
   labelled "size and user agent only" in the toolbar.

## 2. Phases and slices

Order = priority. Each slice lists owner agent profile (§4), files, acceptance. "Headless" = Avalonia.Headless +
FakeOmp; "harness" = the windowless native harness of slice B0.

### Phase A — unblock (release-blocking)

**A1. HTTP auth card (fixes Basic-auth)** — macOS `webView:didReceiveAuthenticationChallenge:completionHandler:` for
Basic/Digest/NTLM/Negotiate and proxy auth → broker card "studio.example.com wants you to sign in (Realm: Studio)" with
user/password, "Remember for this session", Cancel. `URLCredential(persistence: .forSession)`; server-trust and client
certificates keep WebKit's default handling. WebView2 `BasicAuthenticationRequested`; GTK `authenticate`.
Accept: harness `401 Basic` page → card → right credentials load 200, wrong ones "That username or password didn't
work", cancel shows the 401 page; completion handler called exactly once on dispose/navigate-away; user check on
studio.example.com.
Done (`Platform/Mac/WebKitHooks.cs`, `Platform/Windows/WebView2Hooks.cs`, `Platform/Linux/WebKitGtkHooks.cs`, wired per
web view by `Platform/BrowserHooks.cs` from `PreviewPanel` `AdapterCreated`/`AdapterDestroyed`; card wording in B1's
`BrowserDialogsViewModel`). No "Remember for this session" box: WebKit needs a session credential or every subresource
asks again, so sign-ins always last until the app quits (never the keychain), as in Safari.
- [x] R1 reproduced: Avalonia-shaped delegate, no hook → 401 body, no credentials sent — `run.sh auth-baseline`
- [x] harness (macOS, app's own `WebKitHooks`, 0 on-screen windows) `run.sh auth-basic` (`Scenarios/AuthScenarios.cs`):
  asked with host/port/realm; wrong password → asked again with `FailedBefore`; right → 200; session reuse; cancel →
  401 body; pending answered once on navigate-away (`CancelPending`) and on detach while pending, no WebKit exception
- [x] card in the pane, credentials back to the engine — `BrowserAuthTests.A_sign_in_request_shows_the_card_in_the_pane_and_gives_the_engine_what_the_user_typed`
- [x] "That username or password didn't work" with the user name kept — `…A_refused_password_asks_again_with_the_error_and_the_user_name_filled_in`
- [x] plain-http warning — `…Plain_http_sign_in_warns_that_the_connection_is_not_secure`
- [x] navigate-away / engine cancel answer exactly once — `…Navigating_away_answers_a_waiting_sign_in_request_with_cancel_exactly_once`,
  `…The_engine_cancelling_its_challenge_takes_the_card_away`, `…Requests_go_to_the_page_model_the_panel_shows_now_and_none_means_cancel`
- [x] WebView2 challenge header → scheme/realm — `…WebView2_challenge_headers_give_the_scheme_and_realm_shown_on_the_card`
- [ ] WebView2 and WebKitGTK hooks compiled only (vtable slots/IIDs from WebView2 SDK 1.0.2903.40 `WebView2.h`); run
  on Windows / Linux (C5). WPE (Avalonia's pick when installed) keeps no dialog until C5 sets `PreferWebKitGtkInstead`.
- [x] Safari-identical UA — done in B2 (`run.sh user-agent`).
- [ ] user check on studio.example.com (test app).

**A2. Make the built-in browser the default** — env flags, `PI_CONFIG_FILES` overlay, GUI extension prompt note,
Settings switch, keep stripping `CMUX_*`/relay from inherited env. Accept (headless, FakeOmp records env/args): every
omp the GUI starts (sessions, TUI tab) gets them; the user's own config.yml untouched; switch off → none of them.
Real-omp test: `eval` `browser.open("http://127.0.0.1:<port>")` reaches our host, not headless Chromium.
Done (`ClientCore/Browser/AgentBrowserDefaults.cs`; `App.axaml.cs` routes every chat and the TUI tab through it):
- [x] every chat omp (first, sibling chat, restart) gets socket, `PI_BROWSER_CMUX=1`, `PI_BROWSER_RELAY=0`, overlay
  appended to the user's `PI_CONFIG_FILES`, `--extension`; cmux terminal ids stripped —
  `AgentBrowserDefaultsTests.Every_chat_omp_is_sent_to_the_built_in_browser_and_keeps_the_users_overlays`
- [x] TUI tab gets the same, keeping an inherited overlay — `…The_terminals_omp_gets_the_same_and_keeps_an_inherited_overlay`
- [x] switch off → nothing added, nothing written — `…With_the_setting_off_omp_starts_as_its_own_configuration_says`
- [x] user's own socket/flags win; unwritable folder still starts omp — `…The_users_own_choices_win_and_unwritable_files_still_start_omp`
- [x] overlay keys and note text — `…The_overlay_and_the_note_say_what_the_plan_asks_and_nothing_else`
- [x] Settings → General switch, default on, saved — `…The_switch_in_general_settings_is_on_by_default_and_saved_off`
- [x] real omp 18.8.0 (stand-in model, user config with `cdpUrl`, `relay: true`, `cmux: false`): `eval`
  `browser.open` → "Opened tab "main" on cmux browser (split)", navigation reached the bridge, the note was in the
  system prompt, the user's config.yml unchanged — `RealOmpBrowserTests.Browser_open_in_eval_reaches_the_apps_browser_and_the_note_is_in_the_prompt`
  (`BROWSERCALL` in `tools/mock-model/server.mjs`; runs in `tools/ci/real-omp-smoke.sh`). `omp config get` confirms
  the overlay: `cdpUrl` "" / `cmux` true / `idleCloseSec` 0 over the user's values.
- Seam for B3: `AgentBrowserBackend` (add `Tern`: TERN_* env next to the bridge's, `tern: true` in the overlay, its note).

**A3. cmux fallback fixes** — R6 one surface per omp process/tab (tabs in the pane), R7 answer `wait` ~1 s before
`timeout_ms`, a page dialog no longer stalls later scripts (`dialog_open` error with its text). Headless tests.
Done (`ClientCore/AgentBrowserBridge.cs`, `AgentBrowserScripts.cs`; `ViewModels/PreviewTab.cs`, `PreviewAgentPage.cs`,
`PreviewViewModel` tabs; `Controls/PreviewPanel` one `NativeWebView` per tab + tab strip):
- [x] R6: each `browser.open_split` is its own pane tab and web view; other tabs (other omp processes) stay valid
  until `surface.close` or the user's ×; a refused open closes its tab — `AgentBrowserTests.Every_tab_stays_valid_beside_the_others_until_it_is_closed`,
  `…Opening_a_page_opens_a_tab_of_its_own_and_navigates_it`, `…The_preview_gives_each_page_omp_opens_a_tab_of_its_own`
  (real `PreviewAgentHost` over `MainViewModel`)
- [x] pane follows omp's tab unless the user picked another; omp closing the tab on screen leaves it for the user —
  `AgentBrowserTests.Omps_tabs_show_in_the_preview_and_the_users_pick_holds`, `PreviewTests.Omps_tabs_show_in_a_strip_the_user_can_switch_and_close`
- [x] R7: `browser.wait` answers 1 s before `timeout_ms` (¼ earlier under 4 s), also when the page never answers a
  script — `AgentBrowserTests.Waiting_answers_about_a_second_before_omp_gives_up_on_the_socket`
- [x] open JS dialog → `dialog_open` with kind and text, nothing sent into the page, `browser.press` Enter/Escape
  answers it; a script that opens one answers at once — `…An_open_dialog_is_reported_instead_of_stalling_later_requests`,
  `…A_script_that_opens_a_dialog_answers_dialog_open_at_once` (modal state from B1's `DialogBroker.CurrentModal`)
- [x] an alert omp's action caused is accepted (OK) at once; a confirm/prompt/"Leave page?" omp caused and left
  unanswered goes to the user's card after 10 s; the user's own dialogs are never answered for them —
  `…Alerts_omp_caused_are_accepted_and_its_unanswered_questions_go_to_the_user`
- [x] R8 (minimum): no `__ompAgent`/`__ompBridgeRun`; library under a random per-run global, non-enumerable,
  non-writable, non-configurable, frozen, sealed; reused only when verified, a configurable look-alike replaced, a
  non-configurable one refused (`blocked`) — `…The_helper_scripts_live_under_a_random_sealed_name`,
  `…A_page_cannot_swap_or_forge_the_helper_library` (runs the library in bun's JavaScriptCore; skips without bun)
- Open: one `DialogBroker` for all tabs (a dialog in any tab reports `dialog_open` for every tab; per-tab brokers with
  C1); the page world can still patch builtins the library calls (isolated world: B4); a script abandoned for
  `dialog_open` still finishes in the page once the dialog is answered.

### Phase B — native windows and the Tern host

**B0. Windowless native harness (spike, first)** — console `NSApplication` with activation policy *prohibited*,
off-screen WKWebView, local `HttpListener` test pages (401, dialogs, popup+opener+postMessage+close, download,
file input, killed web process). Proves offscreen JS + delegates run with no window and no TCC prompt. Runs in CI on
macOS runners and locally without touching the screen. Linux: extend `tools/verify/annotate-e2e` (Xvfb).
Done (`tools/verify/webview-harness`, `run.sh <scenario|all>`, JSON per scenario; the app's `Platform/Mac/*.cs` are
compiled in, so they must stay free of Avalonia types; `--hooks harness` runs the reference delegates instead):
- [x] off-screen JS + delegates, nothing on screen: `smoke` (activation policy prohibited, LaunchServices type
  `BackgroundOnly`, 0 on-screen windows, evaluateJavaScript / callAsyncJavaScript round-trips, timers unthrottled with
  `inactiveSchedulingPolicy = None`; the page is `visibilityState: hidden` and `requestAnimationFrame` never fires)
- [x] no TCC prompt: `HARNESS_ISOLATION_CHECK=1 run.sh all` — only preflight status queries (ListenEvent, Camera,
  Microphone, SystemPolicyAllFiles; AppKit/WebKit start-up), no prompting request, no process left
- [x] 401: `auth-baseline` (R1 reproduced), `auth-basic` (A1's `WebKitHooks` on the Avalonia-shaped delegate)
- [x] dialogs: `dialogs-baseline` (R2: confirm false/prompt null, nobody asked), `dialogs` (text, default, origin,
  answers, page suspended while open, pending answered once on dispose; evaluateJavaScript blocks while a dialog is open)
- [x] `popup` (window.open → view from WebKit's configuration, opener postMessage, window.name, window.close →
  webViewDidClose), `download` (attachment, `<a download>` http + blob via the forwarded 4-arg policy, unknown MIME;
  baseline navigates), `file-input` (parameters, files reach the page, cancel), `crash` (kill -9 → termination
  callback, pending dialog defaulted, scripts fail instead of hanging — WKErrorDomain 5, not 2 — reload recovers)
- [x] `trusted-input`: synthetic click isTrusted=false, but evaluateJavaScript/callAsyncJavaScript run as a user
  gesture (today's agent clicks get activation); in-process NSEvents to the never-shown view: click trusted + activation
  + window.open, `insertText:` trusted without activation (Unicode), keyDown trusted + activation. Popups need
  `javaScriptCanOpenWindowsAutomatically = NO` to depend on activation (default YES); `configuration.preferences` is live.
- [x] CI: macOS job of `avalonia-platform-gates.yml` (`webview-harness.json` artifact); PROJECT_STATE tools list
- [ ] Linux: annotate-e2e (Xvfb) extension with the same pages — not done (no Linux host in this wave)

**B1. Dialog broker + cards** (`ClientCore/Browser/DialogBroker.cs`, `ViewModels/BrowserDialogsViewModel.cs`,
`PreviewPanel.axaml`) — one card at a time over a frozen snapshot of the page (airspace: native views draw over
Avalonia); alert/confirm/prompt, beforeunload, file chooser, download ask, permission (Allow once / Always for site /
Deny), auth (A1), popup notice. Exactly-once answers on dispose/crash/navigation. Fully headless-tested.

Done (`ClientCore/Browser/DialogBroker.cs`, `ViewModels/BrowserDialogsViewModel.cs`, `BrowserDialogLayer` in
`Controls/PreviewPanel.axaml(.cs)`; tests `tests/OmpGui.Tests/BrowserDialogTests.cs`, screenshots light/dark × 560/1180
with `OMPGUI_REVIEW_DIR`):
- [x] one user card at a time, FIFO, "N more" — `…Questions_are_shown_one_at_a_time_in_the_order_they_came`
- [x] exactly once (double answer, cancel after answer, 32 racing threads) — `…A_question_is_answered_exactly_once`,
  wrong answer type refused — `…The_wrong_kind_of_answer_is_refused`
- [x] cancel paths give WebKit's no-delegate answers (token, `CancelAll` on navigation/crash, `Dispose` on page close,
  pre-cancelled token) — `…Cancelling_gives_each_kind_the_answer_the_engine_gives_when_nobody_answers`,
  `…Navigation_page_close_and_a_cancelled_token_close_the_questions`, `…Navigating_the_pane_closes_the_questions_of_the_page_left`
- [x] routing: agent-caused → `AgentPending` / `CurrentModal` (any pending JS modal, whoever caused it, for
  `dialog_open`), credentials always the user's, `HandToUser` — `…What_the_agent_caused_waits_for_the_agent_but_sign_in_is_always_the_users`
- [x] airspace: web view hidden behind the page's snapshot (or plain surface when capture fails / times out at 700 ms) —
  `…While_a_card_is_open_the_web_view_is_hidden_behind_the_pages_picture`, `…A_picture_that_fails_or_never_comes_leaves_the_plain_surface`
- [x] wording and answers per kind — `…The_sign_in_card_says_who_asks_and_why`, `…Cards_word_each_question_in_sentence_case`,
  `…Each_button_gives_its_answer`
- [x] keyboard: first field focused, Enter = main button, Esc = cancel (Stay on beforeunload) —
  `…The_sign_in_card_takes_the_keyboard_and_Enter_signs_in`, `…Esc_cancels_and_Enter_answers_with_the_main_button`
- [x] light/dark at 560/1180, card ≤ 400 wide — `…Cards_render_over_the_frozen_page_in_both_themes`
- [ ] per-tab brokers (today one `PreviewViewModel.Dialogs` for every pane tab; move to `PreviewTab` with B3)

**B2. macOS hooks** (`Platform/Mac/WebKitHooks.cs`, `Platform/Mac/PopupWebViewHost.cs`): JS dialogs; **popups**
(`createWebViewWithConfiguration` returns a raw WKWebView built from WebKit's configuration so `window.opener`
survives OAuth; shown as a tab in the pane; `webViewDidClose` closes it); file input (`runOpenPanelWithParameters` →
NSOpenPanel or agent preset); downloads (`WKDownloadDelegate`, to `~/Downloads` with ask-where option, list in the
pane); permissions (camera/mic per site; fullscreen; geolocation denied with card); Storage Access; failed load and
web-process crash → crash page with Reload / Open externally; persistent `WKWebsiteDataStore` per profile so logins
survive restarts; Safari-identical UA. Passkeys need Apple-gated entitlements → "Continue in your browser" card.
Google's `disallowed_useragent` is server-side → same hand-off card.
Done (`Platform/Mac/WebKitPage{,.Navigation,.UI}.cs`, `WebKitDownloads.cs`, `PopupWebViewHost.cs`, `SafariIdentity.cs`,
`MacObjC.cs`; contract `ClientCore/Browser/BrowserPageHooks.cs` — one per `PreviewTab.Page`, attached by `PreviewPanel` on
`AdapterCreated` through `Platform/BrowserHooks`; `BrowserDownloads`, `BrowserSiteSettings`, `SafariUserAgent`;
`Controls/PopupViewHost.cs`; error page, crash page and downloads strip in `PreviewPanel.axaml`). Harness: the reference
scenarios now also run on the app's hooks (`--hooks app`, default when linked; `Native/AppHooksBridge.cs` answers the app's
`DialogBroker` with the scenario's handlers); `HARNESS_ISOLATION_CHECK=1 run.sh all`: 0 on-screen windows, TCC
preflight only.
- [x] alert/confirm/prompt through the app's UI delegate and broker: text, default, origin, answers, page suspended,
  pending answered once on dispose — `run.sh dialogs` (app); *Leave this page?* (SPI selector) Stay / Leave —
  `run.sh beforeunload`
- [x] pop-ups: `createWebViewWithConfiguration` → `PopupWebViewHost` from WebKit's configuration, opener postMessage,
  `window.name`, `window.close` → tab closed — `run.sh popup` (app); pane tab after its opener, selected when the opener
  is on screen, its own delegate drives the toolbar, address box drives it, closes with its opener / ×, opener shown
  again — `BrowserHooksTests.A_popup_opens_as_a_tab_after_its_opener_and_closing_itself_brings_the_opener_back`,
  `…Popups_go_with_their_opener_the_user_can_close_one_and_a_closed_tab_opens_none`; macOS `target=_blank` no longer
  sent to the system browser (WebKit opens the pop-up tab)
- [x] file input → broker file card (accept list via SPI when present) — `run.sh file-input` (app)
- [x] downloads: response policy (non-showable / attachment), 4-arg policy shim (`<a download>` http/blob → download
  without Avalonia's navigation; the rest forwarded to Avalonia's 3-arg IMP), `WKDownloadDelegate`; card per file, saved
  only into the folder the user chose (save panel until one is chosen; never `~/Downloads` by default), progress,
  stopped-while-asking saves nothing — `run.sh download` (app: 10 checks), `BrowserHooksTests.A_download_is_saved_only_where_the_user_chose`,
  `…Without_a_chosen_folder_or_a_save_panel_nothing_is_downloaded`, `…The_pane_lists_downloads_being_saved_with_progress_stop_and_show_in_finder`,
  `…A_navigation_that_turns_into_a_download_leaves_the_page_where_it_was`, `…Download_names_never_leave_the_chosen_folder`
- [x] failed navigation → `Failed` with NSError (−999 and 102 dropped) → error page with *Try again* on the failed
  address; responses with status/type/headers — `run.sh load-failure`, `BrowserHooksTests.A_failed_navigation_shows_why_with_try_again_on_the_address_that_failed`
- [x] web-process crash → pending handlers defaulted, broker `CancelAll`, crash page with Reload / Open in browser —
  `run.sh crash` (app), `BrowserHooksTests.A_crashed_page_cancels_its_questions_shows_the_crash_page_and_reload_brings_it_back`,
  `…A_tab_in_the_background_keeps_its_crash_and_error_until_it_shows`
- [x] camera/microphone → permission card, *Always allow on this site* remembered per origin and kind
  (`browser-sites.json` next to the client settings), Deny, pending denied once on detach — `run.sh permission` (IMP
  called with a synthetic origin), `BrowserHooksTests.Always_allow_on_this_site_is_remembered_per_site_and_kind_across_restarts`;
  no entitlement / Info.plist change
- [x] Safari-identical UA (`EnvironmentRequested` → `ApplicationNameForUserAgent`; Safari's version only when its build
  equals the loaded WebKit's, else this macOS's Safari version) in the page, on the wire and in pop-ups —
  `run.sh user-agent`, `BrowserHooksTests.The_user_agent_token_is_safaris_for_the_webkit_that_runs`
- [x] omp-caused questions: `PreviewTab.AgentAction()` (+1 s grace) → `CausedByAgent`; cmux hands non-modal ones
  (file, download, permission, pop-up) to the user at once — `BrowserHooksTests.What_the_page_asks_while_omp_acts_on_it_and_a_second_after_is_omps`,
  `AgentBrowserTests.Alerts_omp_caused_are_accepted_and_its_unanswered_questions_go_to_the_user`
- [x] persistent data store: Avalonia already uses `WKWebsiteDataStore.defaultDataStore` (persistent, per bundle id) when
  no identifier is set (`MaciosWebViewAdapter` ctor); kept, so existing logins stay (an identifier would start empty)
- [ ] not done: element fullscreen, geolocation card, Storage Access card (WebKit shows its own alert once any UI
  delegate is set), camera (WebKit refuses: no `NSCameraUsageDescription`, unchanged on purpose), passkey / Google
  hand-off cards, pop-up blocking for non-gesture `window.open` (WebKit default allows), agent commands following the
  pop-up tab (B3/C4); real `getUserMedia` and the NativeControlHost pop-up embedding only in the test app (user check)

**B3. Tern host** (`ClientCore/Browser/TernHost.cs`, `ViewModels/BrowserTabs*`) — u32-LE framed socket, `hello` →
`welcome`, one block per omp tab shown as pane tabs, ops in order: open, close, state, events, goto, nav, eval
(isolated world, Promise await, frame paths), input, capture, dialogs/dialog, viewport, scripts; then files,
downloads, credentials, agent, appearance, cookies/setCookie/deleteCookie, pdf, clipboard, edit. Conformance tests
drive it with omp's own Tern client (`omp-tern-protocol.md` gotchas: late answers, timeouts are client-side, fork
channel). Env switch from cmux to Tern once the minimum op set passes against real omp.
Done so far (`ClientCore/Browser/TernHost.cs`, `TernBlock.cs`, `TernPage.cs`, `TernPageEvents.cs`;
`Platform/Mac/TernWebKit.cs`, `TernWebKitPage.cs`; `ViewModels/TernPreviewHost.cs`; per-tab `PreviewTab.Dialogs` +
`BrowserDialogsViewModel.Follow`; conformance script `tools/verify/tern-conformance/conformance.ts`):
- [x] every op omp 18.8 sends, driven by omp's own `TernSocketClient`/`TernTab` (pinned pack, bun) on real off-screen
  WKWebViews with the app's hooks, input and engine, 0 on-screen windows — harness `run.sh tern` 21/21 (open/state/
  goto/nav/pushState, page + isolated eval with Promise and frame path, kit text/count/ariaSnapshot, trusted click with
  activation, fill/type Unicode + Enter, alert auto-accepted + confirm held/answered, console/network via `stencil`,
  capture viewport/element/full/jpeg, pdf, viewport/appearance/UA, cookies, file chooser preset, download into omp's
  folder, allowed domains, close)
- [x] wire-level tests `TernHostTests` (framing, pipelining, protocol errors, events/dropped, dialog policies and
  hand-off, presets, omp's own client over the wire incl. late `open` and client timeouts) and
  `AgentBrowserDefaultsTests.With_Tern_every_omp_gets_its_own_pane_and_cmux_stays_as_the_fallback` — 14/14
- [x] background tabs: omp waits for a frame before each capture; a web view not on screen never draws one, so omp's
  isolated world gets a `requestAnimationFrame` that also fires after 100 ms (page world untouched) — `run.sh tern`
  119 s → 3 s; `HARNESS_ISOLATION_CHECK=1 run.sh all` 17/17 scenarios, preflight-only TCC
- [ ] real-omp end-to-end test with the app's pane (`TernPreviewHost`) not run. Default stays cmux (`AgentBrowserDefaults.DefaultBackend`);
  `OMPGUI_AGENT_BROWSER=tern` opts in. `fork` answered `unsupported` (omp forks in place); clipboard read and paste
  refused (macOS paste privacy); invalid certificates never accepted; a viewport larger than the pane is limited to it.

**B4. User-equivalent input + isolated world** (`Platform/Mac/NativeInput.cs`, `Platform/Windows/CdpInput.cs`,
`Platform/Linux/GtkInput.cs`, `ClientCore/Browser/Locator.js`) — locator (refs, actionability, scroll into view,
shadow DOM, same-origin frames) runs in an isolated world and returns coordinates; the actuator sends native events:
move → down → up with a short natural delay, text via `insertText:` (bypasses the user's input method), keys only for
non-text keys/shortcuts marked so unhandled ones never reach our menus; hover via guarded `_simulateMouseMove:`;
never steals first responder from the composer (save/restore), user input always wins. Accept (harness self-test
page): `isTrusted` true, `navigator.userActivation.isActive` true after click/keydown, `window.open` inside the
click handler succeeds, contenteditable / CodeMirror 6 / Monaco receive typing, `wheel` works; repeated with the
window in the background, the pane hidden, Russian and Japanese layouts active.
Done (`ClientCore/Browser/INativeInput.cs`, `IIsolatedScripts.cs`, `NativeInputActions.cs` (pacing),
`AgentBrowserBridge.Native.cs`; `Platform/Mac/NativeInput.cs`, `IsolatedWorld.cs`, `MacRuntime.cs`; `Platform/NativeInputs.cs`
per tab via `PreviewTab.Input/Isolated/NativeHandle`, set on `AdapterCreated`; the locator lives in `AgentBrowserScripts`
(`target`/`arm`/`quiet`), not a separate Locator.js). Harness `run.sh native-input` (34 checks) and `run.sh agent-native`
(14 checks, the real bridge on a real WKWebView), 0 windows on screen, `HARNESS_ISOLATION_CHECK=1` preflight-only:
- [x] click: `isTrusted`, activation in the handler, `window.open` succeeds, lands where the isolated world measured —
  `native-input` "click: …", `agent-native` "browser.click: …"
- [x] text through `insertText:` (Cyrillic, Japanese), no key events for it; contenteditable; Enter/Backspace; ⌘A/⌘K
  (key equivalents via `keyDown:`; a ⌘ key the page leaves is re-sent by WebKit to `NSApp` and dropped by the guarded
  `sendEvent:`, Edit-menu commands run on the web view only) — `native-input` "type: …", "contenteditable: …", "shortcut …"
- [x] wheel (trusted, deltaY > 0, scrolls), drag (`buttons === 1` via scoped `pressedMouseButtons`), double click
- [x] hover (`_simulateMouseMove:`) when the app window is active; in an inactive window WebKit passes no moves (Safari's
  rule), the bridge then falls back to script events and says so — `native-input` "hover (app window active): …"
- [x] pane hidden (view in no window): click trusted + activation + popup, typing — "pane hidden …"
- [x] composer keeps first responder (WebKit's own grabs suppressed during agent input, SPI guarded) — "the composer …"
- [x] user input wins: a step waits until the user's own clicks/keys on the view pause 1 s (10 s cap → `busy`) — "user input wins …"
- [x] isolated world: Promise await, JSON args, undefined → null, errors as text, page cannot see our globals, our world
  sees no page patches, message handler reachable only from our world; helper scripts give the page no activation
  (SPI `forceUserGesture:NO`, guarded) — `native-input` "isolated world: …", `agent-native` "the bridge's own scripts …"
- [x] bridge: native click/dblclick/type/fill/check/press/scroll, `<select>`/date/colour fields and missing native input
  → script events with `"input":"synthetic","reason"`; actionability (visible, enabled, uncovered, stable), settle on
  DOM+network quiet — `AgentNativeInputTests` (headless); `navigator.webdriver` false; no page-world library
- [ ] CodeMirror 6 / Monaco pages, Russian/Japanese input sources active (needs the user's machine: switching input
  sources is a system setting), real background window (harness windows can never be key)
- [ ] Windows (CDP `Input.*`, `Page.createIsolatedWorld`) and Linux (GTK events, script worlds): not built, no host to
  verify; `NativeInputs.For` reports them unsupported with the reason; right-click refused (WebKit's context menu is modal)

### Phase C — stability, emulation, chrome

**C1. Resize and lifecycle stability** — web view survives pane open/close, splitter drags, window resize, theme
switch, sleep/wake, session switch (multi-session: each session's tabs kept, hidden ones throttled); web-process
crash recovery; no frame of stale size (the Metal stretch issue shares the render path: verify with OpenGL).
Accept: headless VM tests for state; harness resize sweep 320→2560 with screenshots compared to expected viewport.

**C2. Device toolbar + emulation** — presets (screen sizes as Codex/DevTools: iPhone SE 375×667, iPhone 15 Pro
393×852, iPhone 15 Pro Max 430×932, Pixel 8 412×915, Galaxy S24 Ultra 384×824, iPad Mini 768×1024, iPad Air
820×1180, Laptop 1024×768, Laptop L 1440×900, 4K), W×H fields, rotate, fit-to-pane scale, zoom 25–500 %, Responsive
mode with drag handles. WebView2: full CDP bundle from one real device profile; WKWebView: viewport projection + real
device UA + zoom, toolbar says so. The agent's `viewport`/`agent` ops drive the same state; the toolbar shows when
the agent set it.

**C3. Browser chrome** (from `browser-refs.md` §12) — tabs (new/close/reopen, ⌘T/⌘W/⌘⇧T), URL bar with history and
"Not secure", back/forward/reload/stop/hard reload, find in page (⌘F), downloads popover, clear site data / keep
cookies, open in system browser, screenshot to clipboard, console + network logs (for the agent via `events`; a
small panel for the user), Web Inspector toggle (`isInspectable` / `OpenDevToolsWindow`), "omp is driving" pill +
agent cursor, end-of-turn tab cleanup option. Remove the http/https-only and local-only limits where they block real
sites (keep `file:` refused, offer to serve it).

**C4. Agent contract quality** — snapshot = accessibility tree with stable refs `role "name" [ref=eN]` + state flags,
`*` for new elements, frames/shadow DOM inlined, truncation note; results carry page state (URL, title, tabs,
tab_opened, downloads) + modal state; error codes `not_found | stale | timeout | unsupported | blocked | crashed`
each with a remedy sentence; timeouts nav 30 s, action 5–10 s, never block on a JS dialog. Pacing: after each action
wait for a settle (network quiet ~500 ms or DOM stable), not a fixed sleep. CAPTCHA / human-check detection →
`needs_user` + card "omp needs you to complete a check on this page". Measured with a fixed task set (§5).

**C5. Windows WebView2 and Linux WebKitGTK parity** — WebView2: `[GeneratedComInterface]` interop (app has
`BuiltInComInteropSupport=false`), popups via `NewWindowRequested` + deferral with a real child WebView2, script
dialogs, downloads, auth, permissions, CDP input/emulation/isolated world. Linux: `PreferWebKitGtkInstead=true`
(WPE has no dialogs), `create`/`script-dialog`/`run-file-chooser`/`decide-policy` (fix Avalonia's swallowed
RESPONSE decisions)/`authenticate`/`permission-request`.

### Phase D — security, performance, release

**D1. Security review** — socket auth (per-process password, 0700 dir), isolated world everywhere, no
page-reachable bridge (annotate channel moved to a script message handler in our world), downloads sandboxed to the
chosen folder, credentials never logged or sent to omp, per-site permission store, `file:`/custom schemes refused.

**D2. Performance** — no polling (events instead of `url.get` loops), screenshots scaled ≤1024 px before sending,
snapshot size cap with paging, hidden tabs throttled (`inactiveSchedulingPolicy`), one web process per profile, idle
CPU ~0 with the pane closed. Measured with `OMPGUI_PERF=1`.

**D3. Docs and release** — USER_GUIDE "Browser" chapter, PARITY rows, DESIGN_SYSTEM cards, `test-app.sh` build for
the user's real-site checks, then release.

## 3. Acceptance per user requirement

1. **Default:** with a fresh omp config and the switch on, a prompt like "open studio.example.com and sign in" opens our
   pane (not Chrome, not Safari, not `curl`) in 10/10 runs of the fixed task set; the TUI tab uses it too.
2. **Native windows:** harness pages for auth, alert/confirm/prompt, popup+opener OAuth, file input, download,
   permission, crash all behave as in Safari; studio.example.com shows the sign-in card and loads after sign-in (user
   check).
3. **Stable, resizes, mobile:** resize sweep and pane/session switching produce no blank, stretched or stale frames;
   device presets apply in ≤1 frame and survive reload; WebView2 passes a mobile-detection page as the device;
   crash recovers with one click.
4. **Agent drives it perfectly, like a person:** trusted-input self-test all green; fixed task set (sign in to a test
   app, fill a multi-step form, upload a file, download a report, handle a confirm dialog, work in a popup OAuth
   mock, scroll an infinite list, use a contenteditable editor) ≥ 95 % success with no "element not found" loops;
   page-side detection of our helpers (`window.__omp*`, `isTrusted`, `navigator.webdriver`) all negative.

## 4. Agent swarm: who does what

| Slice | Agent | Model / thinking | Why |
|---|---|---|---|
| B0 harness spike, B3 Tern host, B4 native input | `task` | opus-5.5 **high** | ObjC/COM interop, block ABI, threading, protocol conformance: mistakes crash the app |
| A1 auth, B2 macOS hooks, C5 WebView2/GTK | `task` | opus-5.5 **high** | delegate forwarding, exactly-once completion handlers |
| A2 default wiring, A3 cmux fixes, B1 broker+cards, C1 stability | `task` | opus-5.5 **medium** | well-specified, headless-testable |
| C2 device toolbar, C3 chrome, C4 contract | `task` | opus-5.5 **medium** | UI + behaviour, reference specs exist |
| Mapping/greps before each slice | `scout` | fast default | read-only, cheap |
| Test fixtures, doc rows, PARITY/USER_GUIDE updates | `sonic` | low | mechanical |
| D1 | `security-reviewer` | high | page→host trust boundary |
| After each phase | `reviewer` | medium | regressions, wording, UX states |

Waves: **1** = B0 + A1 + A2 + A3 + B1 (parallel; B0 gates B2–B4 on macOS). **2** = B2 + B3 + B4. **3** = C1–C4.
**4** = C5 + D1 + D2. **5** = D3, test app, user checks, release. File ownership per slice as in FIX_PLAN; shared
files (`PreviewPanel.axaml(.cs)`, `App.axaml.cs`) get small targeted edits only.

Isolation: all agents verify headless or in the windowless harness; nothing opens windows on the user's screen or
asks for macOS permissions. The user does the final real-site checks (studio.example.com credentials) in
*OMP GUI Test.app*.

## 5. Improvements and optimizations (beyond the requirements)

- Persistent profiles: logins survive restarts; "Clear site data" per site; optional private tab.
- Session-owned tabs: each chat keeps its own tabs; switching chats switches tabs; closing a chat offers to close them.
- Agent sees console errors and failed requests of the page after each action (from `events`), so it can fix the
  app it is building without asking.
- Screenshot diffing for "did my change render?" (before/after capture in one call).
- Dev-server detection already exists: auto-open the pane on the first `localhost` URL omp prints, reuse the tab.
- Page comments (annotate) work in every tab and on mobile presets.
- One-click "Open this tab in Safari/Chrome" with cookies left behind (privacy) and a note.
- Network-idle and DOM-stable waits replace fixed sleeps in the bridge (faster, fewer flakes).
- Snapshot cache keyed by DOM mutation counter: unchanged pages answer instantly.
- Screenshots: JPEG for photos, PNG for UI; scaled to the model's limit before sending (fewer tokens).
- Hidden tabs throttled and discarded after N minutes with state restore (memory).
- Telemetry-free local metrics in the debug panel: per action latency, failures by code.

## 6. Risks

1. Avalonia delegate forwarding (`class_addMethod` on its class) may break on an Avalonia update → pin version, test
   in the harness on every bump.
2. macOS keyboard via the user's input method → `insertText:` for text; keys only for non-text.
3. Hover uses private `_simulateMouseMove:` → `respondsToSelector:` guard and honest fallback.
4. `MouseEvent.buttons` reflects real hardware → drags need a scoped swizzle or stay synthetic-labelled.
5. Native views draw over Avalonia (airspace) → cards over a frozen snapshot image of the page.
6. Tern protocol is omp-internal and may change in later omp → conformance tests against the pinned omp; cmux
   fallback stays until Tern is stable across one omp upgrade.
7. WebView2 hidden/minimised hit-testing and experimental CDP methods → stable `dispatchKeyEvent` with `text`.

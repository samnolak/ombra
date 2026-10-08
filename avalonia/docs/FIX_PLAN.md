# Fix plan: chat stability, performance, context menus, pets, attachments

Execution order = priority. Each item: symptom → root cause (with evidence) → fix → acceptance checks.
Rule for whoever executes: **reproduce first, fix, then repeat the same reproduction**. Do not mark an item done
from tests alone. Code paths are relative to `avalonia/src/OmpGui.App` unless noted.

Baseline: about 20 tests already fail before any of this work (the `/private/var` vs `/var` path assertion,
`QaEdgeCaseTests` line 158, `FilesPane*`, `LayoutAudit*`, `UpdateUi*`). Record the failing list before you start.
Don't fix them as a side effect, and don't hide them.

---

## P0-0. Measure first (instrumentation, ~1h)

Without numbers, the performance work turns into guessing.

- Add `RendererDiagnostics.DebugOverlays = Fps | LayoutTimeGraph | RenderTimeGraph` under an env flag
  (`OMPGUI_PERF=1`) in `App.axaml.cs` / `MainWindow` ctor.
- Add a Stopwatch around `MainViewModel.Apply` (`ViewModels/MainViewModel.cs:411`). Log any call >4 ms
  together with a count of `Rows.Add` and `row.Update` in that call.
- Count the `LayoutUpdated` invocations of `Transcript` per second (`Views/MainWindow.axaml.cs:682`) and the
  `MarkdownView` rebuilds per second.
- Scenario for every later item: a long session (≥300 rows, code blocks, tool outputs) plus a reply that streams
  ~2000 tokens. Record FPS, Apply p95 and layout time **before** each fix and after it.

## P0-1. Chat twitches, blinks and content disappears while streaming (user item 3)

Root causes, ranked:
1. **Full re-anchoring on every layout.** `Views/MainWindow.axaml.cs:656-682`: the `ScrollChanged` and
   `PropertyChanged(Extent/Viewport)` handlers call `PinToBottom()`, and `Transcript.LayoutUpdated` calls
   `FollowTranscript()` after *every* layout pass. Meanwhile `OnTranscriptChanged` (`:1070`) posts
   `ScrollToEnd()`, which calls `Transcript.ScrollIntoView(last)` and `scroll.ScrollToEnd()` and then
   `PinToBottom` again in a one-shot `LayoutUpdated` (`:1096-1109`). Several writers set `Offset` within the
   same frame, so the content jumps by ±N px.
2. **Markdown rebuilds the whole control tree.** `Controls/MarkdownView.cs:70-78` assigns
   `Content = Build(text)` every 250 ms for the streaming row. The old controls are destroyed, the row's height
   changes in steps (code blocks collapse to 0 and come back), and the panel re-measures.
3. **Virtualization with variable-height rows.** The `ListBox` (`Views/MainWindow.axaml` ~280) uses the default
   `VirtualizingStackPanel`. Rows that leave the viewport are recycled, and when they come back their height is
   estimated, which changes the extent and the offset. This combines with (1).

Fix:
- Give scrolling **one owner**: a `ChatScrollController` (new file `Views/ChatScrollController.cs`) owns
  `_stickToBottom`, `JumpToLatest` and every write to `Offset`. Delete `PinToBottom` from the
  Extent/Viewport handlers, the one-shot `Settled`, and `ScrollIntoView` from `ScrollToEnd`.
  - Following the bottom: on `ScrollViewer.ExtentProperty` change, **if** `_stickToBottom`, set
    `Offset.Y = Extent - Viewport` once (no Post, no ScrollIntoView).
  - The user detaches by **direct input only** (wheel, scrollbar drag, PageUp/Home, keyboard): treat
    `PointerWheelChanged` / `ScrollBar.Scroll` / `KeyDown` as user scrolls. A programmatic offset change never
    flips `_stickToBottom`. Delete the 600 ms timing heuristic (`_readerScrolledAt`).
  - The user re-attaches by scrolling to within 24 px of the bottom, or with the Jump-to-latest button.
- `MarkdownView`: switch to **incremental rendering**. Split the text into blocks (paragraph, list, code
  fence…), keep the controls of finished blocks, and rebuild only the last, still-open block. Key the cache by
  the block's start offset and its text. For a block that is already rendered, a token arriving only updates
  the `Inlines` of the last `SelectableTextBlock`. Throttle to the frame (`DispatcherTimer` 33 ms or
  `RequestAnimationFrame`), not 250 ms. Without incremental rendering the twitching does not go away at all.
- Virtualization: switch on `VirtualizingStackPanel` with `CacheLength="2"` (Avalonia 12) to keep realized rows
  above and below the viewport. **Or** use a plain `StackPanel` inside a `ScrollViewer` up to ~400 rows and
  virtualize only beyond that. Choose by the numbers from P0-0. In both cases keep the **anchor element**:
  before the extent changes above the viewport, remember the first visible row and its Y; after layout,
  restore it (Avalonia has no built-in scroll anchoring; implement it in the `ChatScrollController`).
- Don't toggle the `IsVisible` of rows during streaming (thinking/notice rows): a hidden row has height 0, and
  showing it again moves everything below. Keep its height and change opacity, or show the row once only.

Acceptance (manual, all of them):
- [x] A 2000-token reply at the bottom: the text grows smoothly and no line above it moves by even 1 px (screen
      recording, step through frames). *Headless, frame by frame: `ChatScrollTests.A_streaming_reply_moves_the_rows_above_it_only_by_what_it_grew`.*
- [x] During streaming, scroll up with the wheel by 3 notches: the position holds for the whole reply, and
      "Jump to latest" appears. *`ChatScrollTests.Scrolling_up_during_a_reply_holds_the_view_…`*
- [x] Scroll back to the bottom with the wheel: following the reply resumes on its own. *(same test)*
- [x] A code block that is still being written and a table growing row by row: neighbouring blocks don't blink.
      *Finished blocks keep their control and height on every frame (first streaming test).*
- [x] A tool call with expandable output arriving above the viewport: the visible text doesn't jump.
      *`ChatScrollTests.A_tool_row_opening_above_the_view_moves_nothing_on_screen`.*
- [x] Resizing the window during streaming, and opening/closing the side pane: no jumps. *(first streaming test, preview pane)*
- [x] Switching sessions during streaming: the new chat opens at the bottom, and the old one doesn't scroll in
      the background. *`MultiSessionTests.Switching_chats_while_one_streams_opens_each_at_its_latest_message`; a reply with
      links that ends while its chat is hidden no longer throws in the render pass (`MarkdownViewRenderTests`).*
- [x] Selecting text in a reply that is streaming: the selection doesn't drop on each token.
      *`ChatScrollTests.A_streaming_reply_keeps_its_blocks_and_the_selection_made_in_them`.*

## P0-2. Long chat: scrolling jumps, can't navigate (user item 7)

The same mechanisms as P0-1 (estimated heights plus several offset writers). After P0-1, check separately:
- [x] 500+ rows, scrollbar thumb dragged from top to bottom: the thumb doesn't jump back, and the content
      matches the thumb's position. *`ChatScrollTests.Dragging_the_thumb_through_a_long_conversation_never_jumps_back`.*
- [x] Home / End / PageUp / PageDown in the chat (focus not in the composer) work and go to the exact place.
      *`ChatScrollTests.Page_Home_and_End_scroll_the_conversation_when_it_has_the_focus`.*
- [x] Opening a session from the sidebar: it lands exactly on the last row, and doesn't stop 1-2 rows short
      after the heights have been measured. *`ChatScrollTests.A_long_conversation_opens_exactly_on_its_last_row` (launch path).*
- [x] Expanding/collapsing a tool row above the viewport: the visible area doesn't move (the anchor).
- [ ] A "go to message" (search, or a link to a message, if one exists) lands on that message and doesn't
      overshoot while the rows above are being measured.
Extra fix if heights still jump: cache each row's measured height in the row VM (`RowViewModel.MeasuredHeight`,
filled in `ArrangeOverride` or the row's `SizeChanged`) and give the panel that height instead of an estimate.

## P0-3. Lag (user item 4)

Confirmed sources (in order of expected impact; confirm with P0-0):
1. MarkdownView rebuilds (see P0-1).
2. `Transcript.LayoutUpdated → FollowTranscript()` (`MainWindow.axaml.cs:682`) runs on *every* layout of the
   whole window. Replace it with a subscription to the specific property it follows (the row's Bounds / the
   ScrollViewer's Extent).
3. `Apply` at ~30/s (`MainViewModel.cs:339-345`, `MinApplyInterval`) runs `ApplySessionInfo`,
   `ApplySessionArea`, `CheckAttention`, `UpdateStatus`, `ApplyPet` and a pass over **all** `s.Items` with
   `OfferPreviewUrls(item)` (`:430`) on every call. Fix: walk only the items that changed (keep the index of the
   first changed one, or check `ReferenceEquals` before `OfferPreviewUrls`). Call the secondary
   `Apply*` methods only when their part of the snapshot changed (compare references/versions).
4. The sidebar is rebuilt in full (P1-1).
5. `FilesViewModel` watchers (`ViewModels/FilesViewModel.cs:572-592`, refresh timer 300 ms `:427`): during a
   run omp writes into the project, and each event triggers a tree rescan. Fix: when the pane is closed, no
   watchers. When it is open, update only the affected directory and debounce to ≥1 s during a run.
6. `DebugLog` (`MainViewModel.cs:449-456`) is filled even when the panel is hidden, and `RemoveAt(0)` is O(n).
   Fill it only when the panel is visible, or use a ring buffer.
7. Check `BoxShadow` on `menu-item` / `GuiFocusRing` and every effect inside list items (a shadow on every row
   is expensive).

Acceptance:
- [ ] During streaming: FPS ≥ 55 in the overlay, Apply p95 < 4 ms, layout < 8 ms per frame on a 300-row session.
- [ ] Typing in the composer during streaming: no visible input delay (fast typing, no dropped characters).
- [ ] Idle with the window open: CPU near 0% (no timers spinning while idle; check `_elapsedTimer`,
      `_ageTimer`, the pets' animation).
- [ ] Memory: after 10 long replies, RSS does not grow without limit (dotnet-counters, gen2 size).

Status (agent B, measured headless, Debug build, 325-row session + 2000-token stream with a live tool output;
harness was a throwaway test): items 3, 5, 6 done — `Apply` skips the snapshot slots it already applied, the todo
list, widgets and the recover-card notifications only when their slice changed; the event log fills only while
shown; the Files pane throttles disk events (one UI job per burst, a refresh at most every 300 ms, 1 s during a run,
no longer postponed forever by continuous writes). Item 7: no shadows on transcript or sidebar rows, the focus
ring only on the focused item. Apply p50 0.26 → 0.12 ms, p95 0.82 → 0.30 ms, allocations p50 10 → 2.5 KB. The
input-latency p95 (~200 ms) and 1.5 GB of UI-thread allocations per stream were MarkdownView rebuilds and transcript
re-realization (items 1–2, P0-1); with those fixes in, the same run measured: an input-priority job waits p95 1.4 ms
(max 7 ms, was 205 / 390 ms), 101 MB allocated in all (was 1.57 GB), GC pauses 0.18 s (was 1.4 s), layout + render
p95 1.0 ms per frame; the managed heap levels off at ~123 MB over 10 further replies. Still open: the real-display
FPS reading, and idle wakeups — the only app timer left ticking at idle is the pet's 30 Hz one (P2-2).
`OMPGUI_PERF=1` (Services/PerfLog.cs) shows the overlays, logs slow applies and counts Markdown builds, transcript
follows and Files refreshes per second.

## P1-1. The sidebar hover blinks while a chat is running (user item 1)

Root cause (confirmed in code): `ViewModels/MainViewModel.Sessions.cs:297` compares
`p.First == p.Second.Model`, which is a **record** comparison by every field. During a run, `LastMessageAt`, the
size and the title of the current session change, so the comparison fails and the code runs `Sessions.Clear()`
plus creating every `SessionItemViewModel` anew (`:313-326`) plus `RebuildSessionGroups()` with
`SessionGroups.Clear()` (`:356`). All the row controls are destroyed and created again: the hover is lost (one
blink), the sidebar's scroll resets, and a flyout open on a row closes. This runs on each
`RequestCatalogRefresh` (`MainViewModel.cs:476`: start and end of a run, a file change) and after a pin.

Fix:
- Reconcile **by `Id`**, not by the record: a dictionary `Id → SessionItemViewModel`. For an existing item, set
  `item.Model = newRecord` (make `Model` observable and raise `Title`/`When`/… notifications from it). Remove
  only the ids that disappeared, add only new ones, and move rows with `Move` instead of Clear.
- `RebuildSessionGroups`: the same diff for the groups (key = Cwd / "pinned") and for `group.Items`.
  `ObservableCollection.Move` for reordering. No `Clear()`.
- `UpdateSessionStatuses` / `LiveTitle`: set the value only when it differs (the CommunityToolkit setter
  already compares, but check the computed properties too).

Acceptance:
- [x] The pointer rests on a row of another session during a whole run, from start to finish: the hover
      doesn't blink once. (Also: the row Button no longer disables during a run, which greyed every row and moved
      the pointer-over off the row at each run start/end.)
- [x] The "…" menu of a row is open while a run starts or ends: it stays open.
- [x] The sidebar scrolled down: it stays in place after a run.
- [x] A new session appears at the top without a blink; deleting/renaming/pinning moves only one row.
- [x] Pinning a session (P1-1 plus the earlier fix): its row moves into Pinned, and the action buttons don't
      stay "stuck" on another row.

Done: keyed by session file (unique; a copied file can share an id) in `ApplySessionFilter`, `Reconcile<T>` for
`Sessions`, `SessionGroups` and each `group.Items`; regression tests `SidebarRefreshTests`.

## P1-1b. Can't work in several chats at once (user item 8)

This isn't a bug: the architecture is built for one session. Confirmed in code:
- `MainViewModel` has **one** `SessionController _session` (`ViewModels/MainViewModel.cs:22`), which means one
  omp process for the whole window.
- `SessionController.OpenSessionAsync` (`../OmpGui.ClientCore/SessionController.cs:166-191`) refuses during a
  run ("Stop the current run before changing sessions"). Within the same project it switches the same process
  (`switch_session`); in another project it **restarts** omp with `--resume`.
- `CanChangeSession()` (`ViewModels/MainViewModel.Sessions.cs:100`) disables opening a session, a new session,
  Open folder and Ctrl+Tab between sessions while omp works.
- `UpdateSessionStatuses()` (`:242-245`) shows a status (working / waiting / unread) only for the current
  session; every other session is always `None`.

Target model (like Codex / Claude desktop): every open chat is its own omp process that keeps running in the
background, and the window shows one of them.

Fix:
1. **`SessionHost`** (new, ClientCore or App/ViewModels): `Dictionary<sessionKey, SessionController>`, where the
   key is the session file (for a new chat not saved yet, a temporary GUID until the first `get_state`). Methods:
   `GetOrStart(file, cwd)`, `Close(key)` (stop the process), `Active`, the `ActiveChanged` event. Limit how many
   processes run at once (setting, default 4): when a new one would exceed it, stop the oldest **idle** one;
   never stop one that is running or waiting for an answer.
2. **`MainViewModel` binds to `SessionHost.Active`.** Each controller gets its own pump (the current
   `PumpAsync` pumps a single `_session`). The active one is applied in full through `Apply`. A background one
   runs only `CheckAttention` plus the session's status for the sidebar, at most once a second, like the
   minimized mode in `WaitWhileMinimizedAsync`.
   - Switching: `Rows.Clear()` and rebuilding the rows from the new controller's snapshot. Better: keep a
     `Rows` cache per session (up to N recent ones) so that switching back is instant and the scroll position
     is kept (store the anchor of P0-1 per session).
   - Everything bound to "the session" — the composer text and attachments, the queue, the plan, background
     tasks, approvals/dialogs, the context ring, the model, the thinking level, pending approval mode, pets —
     must be **per session**. Go through `MainViewModel.*.cs` and move the state into a per-session object
     (`SessionViewState`). Most cross-session bugs come from forgetting one of these fields; list them in a
     table before you start.
3. **Remove the run guards** from `OpenSessionAsync`, `CanChangeSession`, Ctrl+Tab, New session, Open folder
   and rename/delete of a *different* session. Keep them only for operations on *this* session that really
   need it (delete/rewind the running session, restarting omp for settings → apply to idle processes only,
   and ask about the running ones).
4. **The sidebar shows each session's status** from its controller (`UpdateSessionStatuses` → the host's
   statuses): working (pulsing), needs an answer, unread reply. Notifications ("omp finished", "omp asks")
   come from background sessions too, and a click opens that session.
5. **Global settings that restart omp** (approval mode, runtime, updates, connectors): apply them to every
   process in the host, restarting the idle ones now and the running ones after their run ends (reuse the
   `PendingApprovalMode` logic).
6. **Two sessions on the same file** must not happen: the host returns the existing controller. Also guard
   against the same session open in omp's terminal (TUI): find out whether omp locks the file; if it doesn't,
   warn the user.
7. **Resources:** each omp is a separate process (check its RSS). Show the number of live processes in the
   debug panel; close the window → stop all of them (and check that no zombie processes are left).

Acceptance (headless over FakeOmp's "multi" scenario: `tests/OmpGui.Tests/MultiSessionTests.cs`, `SessionHostTests.cs`):
- [x] Start a long run in chat A, switch to chat B, send a message there: both run at once, and A's reply keeps
      streaming in the background.
- [x] Return to A: the whole reply is there, the scroll position is where you left it (or at the bottom if it
      was following), nothing is duplicated.
- [x] A asks for an approval while you are in B: the sidebar marks A as "needs an answer", a notification
      arrives, and a click on it opens A on the approval card. (macOS: osascript reports no click; Linux via
      `notify-send --action`, Windows by activating the flashed window.)
- [x] The composer: text and an attachment typed in A, then switching to B and back: A's text and attachment
      are in place, and B's box is empty.
- [x] Sessions in **different projects** at the same time (the old restart with `--resume` is no longer
      needed).
- [x] New session / Open folder during a run: a new process starts, and the current run is not interrupted.
- [x] Deleting a session that runs in the background: ask, stop its process, then delete.
- [x] Changing the approval mode with 3 processes, one of them running: the idle ones restart at once, the
      running one after its run, and the user sees that.
- [x] Over the process limit: an idle one is stopped, a running one never.
- [x] Quitting the app with 3 processes: none is left in `ps`.
- [x] omp crashes in a background session: its status in the sidebar is "error", and opening it offers
      recovery; the other sessions are unaffected.

Dependency: do P1-1b after P0-1 and P1-1. Switching sessions relies on the anchor and the keyed sidebar
update; without them every switch twitches.

## P1-2. Right click works nowhere (user item 2)

Context flyouts are declared: the project header (`Views/MainWindow.axaml:85-87`), the session row (`:135-139`,
the content is `Views/Sidebar/SessionRowMenu.axaml`), the file tree (`Views/Panes/FilesPane.axaml:90-139`),
and `Controls/CodeView.cs:113`. The cause on macOS is **not confirmed**. First, a reproduction:
1. A throwaway window with `<Border Background="Red"><Border.ContextFlyout>…` → two-finger click / ctrl+click /
   a mouse with a right button. Write down which of these open the menu.
2. If the throwaway window works, look for what swallows the event in our app: `PointerPressed` handlers with
   `e.Handled = true` (grep `PointerPressedEvent`, `Handled = true`, especially in `MainWindow.axaml.cs`, the
   handlers that drag the window/sidebar, and `CodeView.OnPointerPressed` `:296`). Also check `Button.session`:
   a Button captures the pointer on press, and `ContextRequested` may not get through. If so, move
   `ContextFlyout` onto the row's container (`Panel.session-row`, give it a `Background="Transparent"`).
3. If even the throwaway window doesn't open on macOS with ctrl+click: add a global handler in `MainWindow` —
   `PointerPressed` with `KeyModifiers.Control` + left button on macOS → raise `ContextRequestedEvent` on the
   element under the pointer.

Add context menus (reusing existing commands):
- Chat: user message → Copy, Edit and resend (if there is an edit command), Copy as markdown. Assistant reply →
  Copy, Copy as markdown, Retry (if `CanRetry`). Code block → Copy code. Link → Open, Copy link. Text selection →
  Copy (SelectableTextBlock has it by default: check that our styles haven't removed it).
- The composer `TextBox`: Cut/Copy/Paste/Select All (Fluent has this by default: check that it isn't
  overridden).
- Files search results (`FilesPane.axaml:179-213`): the same menu as the tree.
- Composer attachment chip: Open, Remove.

Status: done — Controls/ContextMenus.cs (Control+click on macOS, text defers to its message's menu, menus made in
code), Views/MainWindow.ContextMenus.cs; Esc closes an open menu before anything else (a Control+click leaves the
keyboard where it was, so the menu never saw Esc and the window took it as "stop omp"); `ContextMenuUiTests`. Not
tried on real hardware: a two-finger trackpad click and a USB mouse.

Acceptance: on macOS, a two-finger click, ctrl+click and a USB mouse's right button open the menu on each
surface above. The menu opens at the pointer, Esc closes it, and the command runs on the row that was
clicked, not on the selected one.

## P2-1. Images: no preview, can't open (user item 6)

What exists: in the composer, `AttachmentViewModel` builds `Thumbnail` synchronously, and the chip template
(`Views/MainWindow.axaml` ~978) shows `Image Source={Binding Thumbnail}`. **Check first** why the user didn't
see a preview: a thumbnail of `null` (a decode error), a chip size that is too small, or a paste via a path that
skips `TryAddImage`.
What is broken: after sending, the image data is dropped. `UserItem` and `QueuedMessage`
(`../OmpGui.ClientCore/Transcript.cs:12,20`) keep only `ImageCount`; `ConversationState.AddUserPrompt` (`:222`),
`Enqueue` (`:247`), message_start (`:527`) and history (`:455`) count images and never store them. In the chat
the row shows only "1 image" (`MainWindow.axaml` ~300).

Fix:
- `UserItem` / `QueuedMessage`: add `IReadOnlyList<ImageAttachment> Images`. Pass it from
  `SessionController.PromptAsync` (`:447`) / `QueueAsync` (`:533`). In message_start and history, take the
  data from content `{type:"image", data, mimeType}` (find out whether omp sends the base64 back in
  `get_messages`; if it doesn't, keep the bytes on the client side by message key).
- `UserRowViewModel`: thumbnails decoded **in the background** with `Bitmap.DecodeToWidth(stream, 160)`, a
  cache by row key; the template shows a WrapPanel of 64-80 px previews, each one a button.
- A viewer: an in-window overlay with the picture fitted to the window, Esc/a click on the backdrop to close,
  ←/→ between the message's images, Copy, "Open in default app" (temp file), and a zoom to 100% on click.
- The composer chip: a larger preview (48-56 px), a click opens the same viewer, a remove × on hover.

Acceptance:
- [x] Paste from the clipboard, drag & drop, and choose via the paperclip: each shows a preview in the chip.
      (All three end in `TryAddImage`; the old chip did show a thumbnail, but 36 px high and at most 64 wide,
      decoded at 48 px on the UI thread: a wide screenshot became a blurry 64×36 strip. Now a 52 px square,
      cropped to fill, decoded off the UI thread at ≥160 px.)
- [x] Send: the message has the same previews; a click opens the viewer; Esc closes it.
- [x] Reopen the session from history: previews are there (or an honest placeholder, if omp didn't keep the
      data). (omp 18.8 resolves its stored blobs and returns base64 in get_messages; a part left as a
      `blob:sha256:` reference shows "not kept".)
- [x] An image of 8000×8000, a corrupted file, GIF, WebP, HEIC: no hang; an error shows as a placeholder.
      (8000² decodes off the UI thread: thumbnail 86 ms, viewer capped at 4096 px in 218 ms; GIF shows its
      first frame; HEIC is not an attachment type — dropped it goes in as a path, as before.)
- [x] The source file deleted after attaching: sending works, and the preview stays.
- [x] A queued message with an image: the preview shows in the queue.

## P2-2. Pets stay inside the window and don't roam the monitors (user item 5)

Current state: `Controls/PetPerch.axaml.cs:34-79,152-165` is a Canvas layer inside `MainWindow`. The position is
a fraction 0..1 of the window, clamped to the window's size. `ViewModels/PetsViewModel.cs:165-200`
(`MoveTo`, `Position`), `../OmpGui.ClientCore/PetOptions.cs` (X/Y as fractions), `Controls/PetView.cs:242-248`
(`RenderScale` from the TopLevel).

Fix:
- `PetWindow`: one separate window per pet. Settings: `SystemDecorations=None`,
  `TransparencyLevelHint=[Transparent]`, `Background=Transparent`, `Topmost=true`, `ShowInTaskbar=false`,
  `CanResize=false`, size = the pet's sprite. Move it with `Position = PixelPoint`.
- The roaming area = the union of `Screens.All[i].WorkingArea` (pixels). Walking: inside the current screen;
  switching screens happens only where the screens touch (otherwise the pet "falls into the gap"). Teleport to
  the nearest screen if the pet ends up outside every one of them.
- Scale: the `RenderScaling` of the screen the pet is on (Retina + an external 1x monitor). Recompute when it
  crosses.
- Click-through on macOS: clicks pass through the transparent pixels and the pet itself is clickable — a
  window of exactly the sprite's size is enough. If the window is larger (a speech bubble), use
  `NSWindow.ignoresMouseEvents` toggled by hit-testing (via `TryGetPlatformHandle`).
- Store the position as `{screen id/bounds, x, y in pixels}`. If that monitor is gone at start, use the nearest
  one.
- `Screens.Changed` → recompute the area and pull the pets back inside.
- Settings: "Pets: inside the window / across the whole desktop" (default: the whole desktop, as the user
  expects).

Acceptance:
- [x] Two monitors side by side and one above the other: the pet goes onto the second one and doesn't get stuck
      at the edge. (Headless with fake screens, `PetsTests.On_the_desktop_*`: it straddles a shared edge, is pulled
      onto the monitor its middle is on across a gap or the menu bar, never rests outside a work area. Not tried on
      real hardware.)
- [ ] Different DPI (Retina + 1080p): the size doesn't jump on crossing (or jumps exactly once to the correct
      size). (Code: the window keeps its size in layout units, the sprite re-renders on `ScalingChanged` — tested
      headless with `SetRenderScaling`; macOS reports every screen at scaling 1 in points, so positions need no
      conversion there. Not seen on a real Retina + 1× pair.)
- [x] Unplugging the monitor the pet is on: the pet returns to the remaining one (and back where it was when the
      monitor comes back). (Headless, fake `Screens.Changed`.)
- [x] The app minimized/hidden: decide and pin down whether pets stay visible. Decided: the desktop pet stays
      while the app runs (also with the settings page open); behind other apps it animates only while omp is
      busy / asks / reports, idle or asleep it shows a still with no timer. Cmd-H hides it with the app.
- [ ] Clicks into other apps through the area around the pet work; a click on the pet itself works. (The window
      is exactly the sprite, plus the bubble / message box while open; a click on the pet works headless. Not
      tried on a real desktop.)
- [ ] Full-screen apps and Spaces on macOS: the pet doesn't hide under the menu bar or the Dock (WorkingArea).
      (Clamped to `WorkingArea`, tested headless. On real macOS `collectionBehavior` reads back 337 (all Spaces,
      stationary, ignores cycle, full-screen auxiliary), level 3 (floating), no shadow, not key on show; not
      watched across Spaces / full-screen apps.)
- [ ] CPU of the pets' animation with nothing happening: < 1%. (Not measured as CPU. Each pet frame no longer
      lays out the window: 8 → 0 `MainWindow.LayoutUpdated` in 3 s of idle animation, headless. Idle in front
      it still animates until it falls asleep, ~3 min 40 s.)

---

## Cross-cutting checks (run after each P0/P1)

- The light and dark themes; window widths 640 / 900 / 1180+; Retina and a 1x screen.
- Keyboard only: Tab across the sidebar and the chat, Esc closes popups, the focus ring shows only from the
  keyboard (after the earlier hover fix, the action buttons don't get "stuck").
- A long session (≥500 rows), an empty session, a session with an error, a session that is running.
- Two windows / two sessions at once (if supported): an update in one doesn't twitch the other.
- Minimize during streaming and restore: the chat shows the final state without jumps.

## Note on the references

The Codex / Claude Code repositories were not found locally (`~/Documents/Codex` is empty, and there are no
links in docs/ or in the prompt history). Before P0-1, put them at a known path and check how they solve the
following (not taken from their code; verify against it):
- following the bottom, and detaching from it only by user input;
- appending to the end during streaming without rebuilding the finished markdown blocks;
- stable row keys / no remounting of rows;
- scroll anchoring when content above the viewport changes.

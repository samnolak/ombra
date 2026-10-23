# Ombra — user guide

Ombra is a desktop coding app with an owned engine derived from **omp** (oh-my-pi).
Its integrated source runs independent chat sessions in one app-managed host. You do not need to
launch a separate omp application for each chat; upstream changes are reviewed before integration.

## Requirements

| OS | Architectures | Notes |
|---|---|---|
| Windows 11 (24H2 or later) | x64, arm64 | Windows 10 only in the Enterprise LTSC / IoT editions .NET 10 still supports; ordinary Windows 10 left support on 14 October 2025. The preview needs the WebView2 runtime (part of Windows 11). arm64: dictation's microphone is not available (no PortAudio build) |
| macOS 14 (Sonoma) or later | Apple Silicon (arm64), Intel (x64) | .NET 10 supports macOS 14, 15 and 26; the package declares macOS 14 as its minimum |
| Linux (glibc 2.27 or later) | x64, arm64 | For example Ubuntu 22.04+, Debian 12+, Fedora 42+, RHEL 8+. X11 or XWayland. The preview needs WebKitGTK (`libwebkit2gtk-4.1`), notifications need `notify-send` (libnotify). Dictation records through the bundled PortAudio (needs `libasound2` and the JACK client library `libjack-jackd2-0` / `jack-audio-connection-kit`) or, when that library is missing, through `parecord`, `pw-record` or `arecord` if one is installed. musl distributions (Alpine) are not supported |

The download is self-contained: no .NET installation is needed. Supported OS versions follow
[.NET 10's list](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). Only Linux x64 has
been run so far (see the README); Windows, macOS and arm64 are built but not yet verified at run time.

## Install

**No release of this client is published yet**: the packages come from the package workflow (a CI build) or from
`tools/package/package.sh`.

Each package is `ombra-<version>-<platform>` (`.tar.gz` on Linux, `.zip` on Windows and macOS) and unpacks into a
folder of the same name, with the licence notices and this guide. Every archive has a `.sha256` file next to it
(and a `.signing.txt` that says how it was signed, an `.engine.txt` that says whether the engine is inside). Check it
before you install (`sha256sum -c ombra-….tar.gz.sha256` on Linux, `shasum -a 256 -c …` on macOS, `Get-FileHash` on
Windows).

**The agent engine is part of the app.** The package carries the pinned engine (`ombra-engine-18.12.2-bun-1.4.2-v1`:
Bun 1.4.2, the integrity-locked npm dependencies and Ombra's own coding-agent sources, checked when the package was
built), so the first start downloads nothing and works offline. On macOS it is inside *Ombra.app*
(`Contents/Resources/engine`) and signed with the app; elsewhere it is the `engine` folder beside the executable.
It runs from there read-only (the app never writes into its own folder; Bun's cache goes to the runtimes folder,
see [Where things are kept](#where-things-are-kept)), an update replaces it together with the app, and copies of
the app each use their own. The macOS package for Apple silicon is about 265 MB to download and about 780 MB once
unpacked. Only the linux-arm64 and win-arm64 packages (built on another CPU, where the engine's check cannot run)
come without it and set it up on first start as below.

The packages are **not signed** with a developer certificate (no signing certificate is available to this
project yet), so the OS warns on first start:

- **Windows** — extract the zip anywhere and run `Ombra.exe`. SmartScreen: *More info → Run anyway*.
- **macOS** — unzip and move *Ombra.app* to Applications. The app is signed with the project's own certificate, not
  an Apple Developer ID, and not notarized, so
  macOS blocks its first start. macOS 15 (Sequoia) and later: open the app once, close the warning, then
  *System Settings → Privacy & Security → Open Anyway* and confirm with *Open*. macOS 14 (Sonoma): Control-click the
  app → *Open* → *Open* (Sequoia removed this way). On any version you can instead run
  `xattr -dr com.apple.quarantine "/Applications/Ombra.app"`. After that it starts from its icon as usual. macOS
  asks for microphone access the first time you dictate, and for a folder such as Documents the first time omp works
  in it; what you allow stays across updates (every release is signed with the same certificate). Releases before
  0.0.6 were ad-hoc signed, which macOS treats as a new app at every update: after updating to 0.0.6 it asks once more.
- **Linux** — `tar -xzf ombra-….tar.gz` and run `./Ombra` in the extracted folder. `./install-desktop-entry.sh`
  adds it to your application menu (only `~/.local/share/applications/ombra.desktop` is written; it points at the
  folder, so run it again if you move the folder).

## First start

1. **Agent engine.** Ready at once: the app uses the engine it carries. Only when it has none for this computer
   (a package without the engine, a development build run from source, or an x64 package on an Apple silicon Mac, which
   picks the native Bun) does the startup card offer to set it up: the client downloads the pinned Bun 1.4.2 and npm
   dependencies, verifies their integrity, then installs its complete owned coding-agent sources and shared host into
   `ombra-engine-18.12.2-bun-1.4.2-v1` in the runtimes folder (size varies by platform). **Settings → Advanced → Repair
   engine** does the same download for an app that carries its engine; the repaired copy is then used instead of the
   app's own. Older packs are retained, not modified or silently launched as a fallback. No separate omp application
   is required for GUI chats.
   A custom command under **Settings → Advanced** must implement Ombra's shared-engine, history and
   input-admission contracts; an ordinary upstream omp CLI is not a compatible GUI engine.
2. **No model provider.** omp needs a model. If it has none, the window says *Connect a model provider*.
   **Open omp setup** runs `omp login` in the terminal beside the conversation, with the keyboard already in it:
   pick a provider (arrow keys, Enter) and finish in the browser (or paste an API key the way omp describes), then
   press **Try again**. `omp login` lists every provider and afterwards fetches the models of the one you chose only
   (see [Network privacy](#network-privacy)). API keys stay in omp's configuration; the GUI never stores them.
3. Pick a project folder — the folder chip under the message box (*Choose a folder*) or **Open folder…** in the
   project menu — and write your first message.

## Daily use

- **Language** — Settings › General › Language: English or Русский (until you choose, the system's language if the app
  has it). The folder button next to it opens `languages/` in the settings folder with `template.json` (every text of
  the app in English): copy it to `<code>.json` (e.g. `de.json`), set `"name"` and translate the values; it is offered
  after reopening Settings, and a file named like a built-in language (`ru.json`) overrides only the texts it has.
  The new language shows after the app is restarted.
  Plural entries use the English plural key followed by `|one`, `|few`, `|many`, or `|other`; keep `{0}` for the count.
  Russian selects integer CLDR one/few/many (including 11–14 and 21–24); English and other language files retain
  their one/other fallback. The category suffix belongs to the key, never to the displayed translation.
- **Sessions** — the sidebar lists omp's sessions of all your projects, grouped by project folder, and the chats
  that belong to no project under **Chats** (after pinned projects, before the other projects). Sessions are
  ordered by their last message (yours or the agent's), newest first, and groups by their newest session; opening a
  session does not reorder the list. Each row shows how long ago its last message was (*now*, *5m*, *3h*, *2d*, then
  the date), or a mark for a chat that is open: a green pulsing dot while omp works in it, a yellow pulsing dot when it
  waits for your approval or answer (yellow always means the chat needs you), a green check when omp finished and you
  have not seen the reply yet, a red dot when its omp stopped with an error. A pencil before the title means that
  chat's message box holds something you have not sent (text, pasted text, images or comments) — in a chat open in
  the background or one you left earlier; it goes once you send or clear it. A **pinned** session stays in its own
  project (or Chats), at its top, marked with a pin (pins are omp's own, so `/pin` and omp's session picker agree). **Search** filters by title or
  folder at once; after a short pause in typing it also searches what was said in the sessions (your messages and the
  agent's answers; not tool output or thinking). Sessions found only by their messages come under the title matches in
  each group, with the words around the match and the match emphasized; clicking one opens it with **Find in chat** on
  your query, at its newest match. Each keystroke cancels the search before it. Sessions that could not be read are
  named in a notice under the search box, and the rows already found stay listed. Click a session to continue it; the
  list updates in place as omp writes, so a hover, an open row menu and the scroll position stay put.
  If the session folder or a file cannot be read, a notice says the list may be out of date: previously listed
  sessions stay visible while readable updates are merged. The same refresh retries failures up to three times
  (after 250 ms, 1 s and 4 s); **Retry** starts another refresh. A complete successful scan removes deleted sessions
  and clears the notice. Scans read at most the 500 newest files: reaching that cap also shows an incomplete-list
  notice and retains older known sessions, rather than treating them as deleted. Switching session roots / profiles
  does not carry the previous root's cached list into the new one.
  Session summaries are cached in a private settings-sidecar file, including across restarts. Each refresh checks
  file size and modification time; unchanged journals are not reopened. Returning focus to the window reconciles
  external additions, edits and deletions. A missing or damaged cache is rebuilt from the session files; it is not
  a backup of the conversation.
  **Several chats at once**: each chat is an independent logical session in the window's shared agent engine.
  It keeps working while you read or write in another one; changing chats never stops a run. Coming back shows
  it as you left it: the whole reply that came meanwhile, your place in the conversation (or its end, if you were
  following it), what you had typed and attached in its message box, a card or question it showed. A chat in the
  background that finishes, asks something or stops with an error gets its mark (a check, a yellow or a red dot) and a notification (see
  Notifications); a question waits for you in that chat. Chats in different projects run side by side.
  Closing or stopping one chat does not stop the others. There is no per-chat process limit or spare omp.
  A failure of the shared engine affects its open chats; unsent input is recovered, never automatically resent.
  After an OS wake notification, Ombra reads the state of every live chat, including background chats.
  It does not resend queued input, abort work or restart the engine; read-only transcripts are excluded.
  If the engine does not answer, a notice asks you to check the connection and use Stop or restart explicitly.
  Missing OS notification support is reported rather than silently treated as working.
  **New chat** (Ctrl/⌘+N, the button above the search) starts a chat outside any project, as ChatGPT, Claude and
  Codex do: it gets an empty folder of its own in the app's data folder (see *Where things are kept*), the header and
  the chip under the message box read *No project*, and its project menu offers a new session in one of your
  projects or **Open folder…** instead. A new logical session reuses the already loaded engine, without starting
  another omp CLI. A chat's folder is deleted when the chat is closed with nothing in it (an empty folder, no
  session file). A project's **+** (or its menu's *New session here*) starts a session in that project while the
  other chats go on; `/new` does the same in the current project (in a chat without a project, a new chat).
  A new chat is listed as soon as it is open — omp saves
  its file only once the first reply is complete, but the row is there before, so a chat you leave while its first
  reply streams stays one click away. A
  session omp cannot open (a damaged file) leaves you where you were, with a card saying why.
  If its working folder was deleted, choose **Open in current project** or **Read only**. Without an available
  current project, choose an existing folder instead. Dismissing the choice preserves your current chat and draft.
  Read-only mode shows the saved conversation's active branch, with older-page loading and explicit reload.
  It does not start a logical agent session, load project extensions, claim its draft, or write the source file.
  Input and mutation actions are disabled. If the file changes while paging, the current view stays intact;
  **Reload latest messages** adopts the new history. The read-only banner can open a separate writable session
  in the explicitly chosen project; it never silently converts the reader into a writer.
  Ctrl+Tab and Ctrl+Shift+Tab go to the next and previous session. Hover a session for **Pin**
  and **⋯** (or right-click it): **Rename** (opens it and edits its name in the header), **Pin / Unpin**, **Copy path**
  (the session file) and **Delete…**, after a confirmation; deleting the open session works like the session menu's
  Delete (a new session starts), and deleting a chat that works in the background says so and stops its omp first.
  **Several chats at once**: right-click a chat → **Select** (or ⌘-click a chat — Ctrl-click on Windows and Linux —
  or Shift-click for a range from the open chat) to pick chats; every row then shows a check box and a click toggles
  it instead of opening it (⌘/Ctrl-click toggles, Shift-click adds the range from the chat picked last). The bar over
  the list says how many are selected, with **Select all** (the chats the list shows now — a search or a status chip
  narrows it), **Delete** and **Cancel**; a selected chat's right-click menu also has **Delete N chats**, and Delete or
  Backspace in the list does the same. One confirmation names the count and that the files are deleted; then each
  chat goes as a single delete does (an idle chat open in this window is closed first). A chat that works, or that
  another Ombra window or a terminal omp has open, is skipped, never stopped: the card says what was deleted and what
  was skipped and why (*Deleted 4, 1 skipped: working*). Esc or **Cancel** leaves without deleting; a search keeps only
  the selected chats it lists.
  The folder button at the top of the sidebar **adds a project folder** to the list;
  right-click a project's header for **New session here**, **Rename** (the name the sidebar and the header show; the
  folder is not renamed, an empty name brings the folder's back), **Pin project** (pinned projects stay first, in the
  order pinned), **Show in Finder** / **Show in Explorer** / **Open in file manager**, **Copy path**; hover it (or
  right-click) to **remove it from the sidebar** (⊖). A project without chats goes at
  once (**Undo** brings it back). One with chats asks first: **Remove from sidebar** keeps its chats on disk (adding or
  opening the folder again lists them), **Delete N chats too** deletes their files as well (cannot be undone; an open
  chat is closed first). The folder itself is never deleted. The open chat's project goes too. Drag the
  sidebar's right edge to make it wider or narrower (220–480 px; double-click the edge for the default); Ctrl/⌘+B
  hides and shows it. Added and removed projects and the width are remembered in the client's settings file. The
  title opens the session menu (below).
- **Models and thinking** — in the message box: the *Thinking effort* chip sets omp's reasoning level (*Off*,
  *Minimal*, *Low*, *Medium*, *High*, *Extra high*, *Max* — each with a line on what it does; only the levels the
  model accepts; hidden for models that don't reason). A new chat or session — and omp's terminal UI in the terminal
  panel — starts at *Medium* (omp's own default is *High*); the chip changes it for that chat, and a session you open
  again keeps the level it had. While omp is working, a level you choose leaves the current run as it is and applies
  to your next message: a message queued then shows the level as a small tag on its chip (*Queued* · *High*), waits
  in Ombra's queue, and Ombra sets the level right before omp starts it. With nothing queued, the level becomes the
  chat's once omp is idle. Editing a queued message (↑) or **Stop** brings its text back to the box and its level
  back to the *Thinking effort* chip.
  `"defaultThinkingLevel"` in the client's settings file (`ombra.local.json`; any of
  `off`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`, `auto`) sets another starting level. The model chip lists the models omp can use, one row per
  family (*Claude Sonnet*, *Claude Haiku*…) that opens to its versions, newest first (→ opens, ← or Esc goes back);
  two entries under one name show their ids. With models from more than one provider they fold under the provider's
  name (*anthropic · 18 models*): a click, Enter or → opens it, a click or ← closes it (← on a model goes to its
  provider). The current model's provider is open when the menu opens; a closed provider with the current model is
  marked and names it. Typing searches every model, flat. The model options sit below the list. The
  model menu opens above the message box, so what you are writing
  stays in view.
- **Permissions** — the mode chip under the message box says when omp asks before running tools: *Ask permissions*
  (edits and commands ask), *Accept edits* (edits run, commands ask; the default) or *Bypass permissions* (nothing
  asks). The mode colours the chip, the box's border while you type and the Send button: grey, violet, red. Shift+Tab
  in the message box steps through the modes (**Plan mode**, below, comes between Accept edits and Bypass when omp
  offers it); Bypass always asks for a second confirmation first. Changing the mode
  restarts omp with it, in every open chat: once you stop pressing Shift+Tab, and never in the middle of a run (a chat
  that works switches when its run ends; a note says so when that chat is in the background). When omp asks, a card in the conversation says what it wants (*omp wants to run a command*),
  tags what the call may do (*Reads only*, *Changes files*, *Runs programs*), gives the tool's reason, an MCP origin
  and any provider safety checks, and shows the complete command or file details in a scrolling box you can select and
  copy, with **Allow** / **Deny** (localized Alt/⌥ access keys; 1 / 2 with focus in the
  card; 1–9 pick an answer to a question). Keyboard approval is guarded for 500 ms after a new card or window
  activation, and requires a released key before a fresh press; a hint explains when to release and press again.
  Typing `1` in the composer cannot approve a command. Positive mouse actions also wait 500 ms and require
  a fresh press and release on the same card; replacing the card or opening an overlay cancels that gesture.
  **Deny** remains available immediately.
  Esc closes the card without answering.
  *Don't ask again for …* (only the durations omp offers for that request; none when the provider asks for safety
  checks) allows the request and keeps a rule for **This session** (until the app quits), **This
  project** (the project folder) or **Always** (every project): for a command it covers commands starting with the
  same words (`npm test` covers `npm test --watch`, not `npm testing`; `git status`, `npm run build` keep their
  subcommand). Commands chained with `;` `&&` `|`, redirected or substituted, run through a known wrapper (`sudo`,
  `env`, `bash -c`) or using a known destructive root (`rm`, `mv`, `chmod`, `kill`…) require a fresh approval each
  time, even if an older client saved an exact or tool-wide grant. Other tools can retain tool-wide rules.
  Automatic bash rules accept only conservative plain ASCII tokens: quotes, expansions (`$HOME`, `~`), escapes,
  globs, shell operators and non-ASCII text require a person. Git rules require a known direct subcommand;
  option-first commands, aliases and destructive subcommands never inherit automatic approval.
  Hidden/control/bidi characters are displayed as `[U+XXXX]` and prevent automatic command approval.
  The original command is not rewritten. This classifier is not a shell sandbox and does not inspect script bodies.
  Command-specific rules do not match commands omp shortened in its request. When a rule answers, the conversation
  says *Allowed automatically — …*. **Deny and say why…** (key 3) opens a box: what you type (Enter sends, Esc closes the
  box) denies the request and goes with the denial; the agent reads it in the denied tool's result. Rules are the
  app's own (omp 18.8 can only allow a whole tool), listed with **Remove** in *Settings → Permissions*.
- **Plan mode** — when the chat's omp offers it, the mode chip's menu has **Plan mode** (*omp explores read-only and
  writes a plan; nothing changes until you approve it.*), Shift+Tab reaches it after *Accept edits* (Ask → Accept
  edits → Plan mode → Bypass), and `/plan` turns it on or off (text after `/plan` stays in the box as the first
  planning message). While it is on, a **Plan mode** chip sits next to the mode chip: a click leaves it, and so does
  the next Shift+Tab. Your permission mode stays as it was underneath. Leaving plan mode stops a planning turn in
  flight. The plan omp proposes shows as a card above the message box: its title, *Proposed plan: nothing changes
  until you approve it*, the plan file's path and the plan itself as formatted text. **Approve** leaves plan mode and
  omp carries out exactly that plan. **Reject…** opens *What should change in the plan?*: **Reject and send** keeps
  omp in plan mode and sends your words as its next planning turn, and it proposes a revised plan; with nothing
  written it just closes the plan. **Cancel** goes back to the card. A new revision replaces the card, and what you
  typed for the old one is dropped. Approve and Reject wait while omp works — a planning turn still running may
  propose another version — and omp refuses them then too; a refusal shows as a note saying why. Both buttons have
  localized Alt/⌥ access keys. `/plan-review` says whether a plan waits above the message box. An omp without plan
  mode opens its terminal for `/plan` instead.
- **Folder trust** — a project folder can bring its own tools that omp runs on your computer: MCP servers
  (`.mcp.json`, `.omp/mcp.json`, `.cursor/`, `.vscode/`…), hooks, extensions, commands, custom tools, project plugins,
  language servers and project settings — and its own skills, slash commands, prompt templates and subagents
  (`.omp/skills`, `.claude/skills`, `.agents/skills`, `.omp/commands`, `.omp/prompts`, `.omp/agents`…). Until you trust
  a folder, omp works there *without* any of them, and without language servers: the folder's skills and commands are
  not in the slash menu, and the agent can neither use its skills nor start its subagents. Your own extensions, MCP
  servers, tools, skills, subagents and settings still load, and so do the folder's `AGENTS.md` and rules. When omp
  reports that it left something of the folder's out, a card above the message box says *Do you trust this folder?*
  and lists those items by kind and name (*Skills: …*, *Subagents: …*). **Trust and restart** reopens that chat — the
  same conversation — with the folder's tools, skills and subagents; other chats keep running as they are. **Keep
  restricted** (or ×, Esc) closes the card and the chat goes on without them; the card comes back at the chat's next
  start. The chat works either way. Once trusted, the folder asks again only when its configuration changes (*This
  folder's tools changed*), including a skill, slash command or subagent added or edited; a change to code that
  configuration runs from elsewhere in the repository does not ask. Folders you used before this version are not
  trusted yet. A folder omp finds nothing of its own in shows no card. A chat that omp moves to another folder
  (`/move`, a worktree) restarts under that folder's trust. omp's terminal UI in the terminal panel follows the same
  choice, and the app's own omp commands that load extensions (usage, sign-in) run outside an untrusted folder.
  Trusted folders are listed in *Settings → Permissions*.
- **Message box** — Enter sends; Shift+Enter adds a line (*Settings → General → Send messages with* switches to
  Ctrl/⌘+Enter to send, Enter then adds a line). While omp is working, **Queue** (or Enter) adds a
  follow-up and **Steer** (or Alt/⌥+Enter) hands a message to the current run at its next step. Queued messages are
  listed in grey above the box until omp takes them: **×** takes one back, and ↑ in the empty box brings the last
  one back to edit (with its images). A long paste (over 800 characters or 3 lines) becomes a *Pasted text +N lines*
  chip instead of filling the box (hover it to see the start, × removes it); it is sent in full after what you typed.
  Pasted text is cleaned of unsafe invisible/control/bidi characters, with a notice of what was removed.
  Joining characters used by languages and emoji are preserved. If cleanup happens when sending a restored or
  typed draft, review the visible text and send again; cleanup never silently exposes a slash or shell command.
  An all-hidden paste leaves the selected draft intact. Attached files and images are not rewritten.
  If the composed message exceeds the RPC limit, the input stays intact and **Send text as file** offers an
  explicit alternative. It saves the full text, pasted chips and comments in a private UTF-8 file, then sends
  its `@file` reference with the images. The notice shows the destination beside the draft journal. Runtime
  previews may truncate; the complete file remains available. These files are retained until you delete them
  manually, including after closing chats, so saved conversations and recovered queues do not lose their source.
  File, validation and journal errors preserve the original input; queued recovery keeps one reference and
  one set of images, never resends automatically. Slash commands are not converted. Unsupported reference paths
  fail before writing or clearing input; oversized images still require a smaller attachment.
  CRLF line endings become LF on paste and send. While the focused text box has an active IME preedit,
  Enter, Esc, ↑/↓ and Tab do not send, stop the agent or operate command suggestions. Normal shortcuts resume
  after composition ends or focus moves to another control.
  **+** opens *Attach images* (up to 3 per message; paste or drag & drop works too, other files are inserted as
  paths), *Commands and skills*, *MCP servers*, *Plugins* and *Comment on the page*. Type `/` for commands: ↑/↓ or
  Tab/Shift+Tab choose, Tab or Enter completes, Esc closes; what only omp's terminal runs is listed last, under
  *Terminal only*. A skill can also go after text and with images: type `/` after a space anywhere in the message (the
  menu lists skills only — other commands work only at the start), or pick **+ → Commands and skills** with something
  already written, which adds ` /` at the end. omp runs one skill per message: further skills are sent as
  `skill://name` references the model reads. A skill sent while omp works queues like a follow-up and still runs as a
  skill. Type `@` anywhere in a message for a file or folder of the project: the menu lists the project's
  top level, then narrows as you type a name (letters in order work, as in **Go to file**; what `.gitignore` excludes
  is left out); ↑/↓ choose, Tab or Enter puts `@path` in place of what you typed — as the Files pane's **@** button
  writes it, so omp reads the file with your message. A folder ends in `/` and lists what is in it next. Esc closes
  the menu only (it stays closed for that word; omp keeps working). Before the first message the chips under the box
  also pick the project (a new session in another project, or **Open folder…**).
- **Commands while working** — RPC-capable builtins such as `/rename` wait locally until the run is idle, then run
  as commands, not model messages. Later queued follow-ups and skills wait behind that command in submission order.
  Explicit **Steer** still interrupts the current run; a builtin submitted with Steer waits for idle instead.
  The same queue chips let you withdraw or edit waiting input. **Stop**, a crash or restart restores it for review
  without automatic replay. An already dispatched command cannot be withdrawn; check its result before resending.
- **Large conversations** — opening a session loads its latest page (up to 100 messages). Scroll upward near the
  beginning or choose **Load older messages** to prepend one page without losing your reading position.
  Older pages pause while the agent is working. If the conversation changed since the page was loaded, the current
  view stays intact; **Reload latest messages (moves view to end)** explicitly reloads the recent page, after which
  older pages are available again. Errors remain visible with retry/reload actions rather than an empty transcript.
  The page byte target is 768 KiB, except that one indivisible large message can exceed it and still uses the
  transport's frame limit. Paging does not make an individually oversized message unlimited.
- **Draft recovery** — text, image bytes, long-paste chips and comments (local marks: code and page, with their
  anchors, notes and status) are saved locally per project/session after 500 ms of typing, and flushed before sending,
  changing/closing a chat and quitting. After restart, open the saved session to recover its draft; unsaved chats
  recover in their project. Page comments saved by an older version come back as one comment chip and are sent as
  they were saved.
  **Stop** withdraws the runtime queue and returns its unsent text/images beside newer composer edits. A runtime
  crash/restart or a revoked question likewise restores recoverable input for review, never automatic sending.
  Cancelled question replies include quoted question context so a leading `/` or `!` is not treated as a command.
  Sign-in reply fields are excluded. If delivery was not confirmed, check the conversation before resending:
  lost runtime acknowledgments cannot provide an exactly-once guarantee.
  A save failure or an oversized RPC message leaves the input available and displays an error instead of silently
  clearing it. A crash can still lose the last 500 ms of typing before the debounce checkpoint.
  Deleting a session also removes its old recovery entry; newer input still in the active composer moves to a new
  unsaved chat rather than being discarded. A failed session deletion keeps its recovery entry.
- **Images** — each attached image shows as a chip with its preview, name and size (× removes it). Right-click
  an image to **Open**, **Copy image**, **Save image…**, **Open in default app**, or reveal a temporary copy in the file manager;
  a composer attachment also offers **Remove**. Save preserves the original encoded bytes. The sent message shows
  the same previews above its text, a queued message small ones, and
  a session reopened from history shows them again (an image omp no longer has shows an image mark instead).
  Click a preview to open it in the viewer: the image fits the window; a click on it, Space or **100 %** shows it
  pixel for pixel (scroll to move around), and back. ←/→ go through the message's images; **Copy** puts the image on
  the clipboard, **Open in default app** opens it in your image app (from a temporary copy). Esc, × or a click
  around the image closes it. An image that cannot be read (a corrupt file) shows an image mark and says so; an
  animated GIF shows its first frame.
- **New session** — the page asks, in Dio’s words, *Oh? You’re approaching ‹project›?* under the app’s mark; the project opens the same project menu,
  and the starter prompts under it (*Explain how this project is organized*, *Find a bug and fix it*, *Add tests for
  the code I changed last*) fill the message box to edit and send. A new chat without a project asks *Oh? You’re
  approaching ‹me›?* — "me" opens the project menu too — with general starters (*Explain a concept in simple terms*, *Write a script that automates a task*, *Help
  me plan something step by step*). Notices from omp's start (or a session that could not be opened) do not hide it:
  they are listed above it.
- **Tool calls** — each is one line that says what happened, in the tense of its state: *Running* / *Ran*,
  *Reading* / *Read*, *Searching* / *Searched*, *Editing* / *Edited*, *Created*, *Deleted*, *Renamed* (same folder) or
  *Moved* (`old → new`),
  *Wrote*, then what it works on, what it found (*2 lines*, *3 matches in 2 files*), its +N −M and how long it took
  (from a tenth of a second). The mark in front pulses while it runs, is a check when it is done, a red ✕ when it
  failed (with the reason as a chip: *exit 127*, *timed out*) and a dashed circle when it was interrupted or skipped (a
  step omp set aside to handle a message that came in first — yours, another agent's, a finished background job — says
  why, e.g. *Skipped: a background job finished first*; omp runs it again if it is still needed); while omp
  waits for your approval the step reads *Waiting for approval*. A message one agent sends another (omp's subagents
  talking over IRC) is a card naming who wrote to whom (*GallerySolutions → omp*), its first two lines under it; a
  click opens the whole message. A command shows as a console block: `$ command` and
  its newest lines while it runs, its first three lines and **… +N lines** when it is done (a failed one has a red
  edge); a command with no output stays one line. Two or more reads and searches in a row fold into one line,
  *Explored 2 files, 2 searches* (*Exploring…* with the current step while they run); click it for the steps. What a
  line folds sits under it as a tree: its rows one step to the right, with a thin guide down from the line (a finished
  turn's *Worked for…* line, then a group's line), so you see what goes away before you fold it. Click a
  line to open the whole output: an open console or output starts with a bar that says what it is (**Terminal** and the
  command, **Output** and what the call read) and folds it again. *Thinking…* counts the seconds while the model
  thinks, then reads *Thought for 4s*; click it for the thoughts. While an open block is longer than the view, its
  header (the bar, a file card's header, the *Explored* line, the *Thought for* line) stays at the top of the
  conversation until the block ends, so it can be folded from anywhere in it.
  Live durations use a monotonic clock, so changing the system date does not make them jump; message dates
  still use the wall clock. Status, extension widgets and command/task output show hidden control characters
  as visible markers, not terminal styling, while preserving ordinary Unicode, line breaks, tabs and raw data.
  In a short window, cards scroll and the message input reduces its visible height so the conversation and
  **Jump to latest** button stay accessible; scrolling the input still reaches the whole draft.
  Approval cards reveal their primary answer after layout and resize without snapping back on ordinary manual
  scrolling. A permission-mode choice stays displayed while its session restarts; a newer choice is not lost.
- Large Markdown tables show at most 100 rows (including the header), 16 columns and 1,024 UTF-16 units per
  oversized cell. A notice and **Copy original table** retain access to the complete table. Oversized cells
  use a plain Markdown prefix. Very large replies shown as plain text have a bounded preview; right-click
  **Copy code** to copy the complete original. Display clipping does not change stored conversation text.
- **File changes** — a change shows as it happens, one card per file (a patch that changes several files has a card
  for each). The card's header has the file's folder and name, a badge for a **New file**, a **Deleted** one (folded
  to *Show N deleted lines*), a **Renamed** or **Moved** one (`old → new`), and +N −M. Changed lines have a coloured
  bar on their left edge and a tinted background, with the words that changed inside a line marked more strongly;
  two line-number columns give the line in the old file and in the new one. Click the path to open the file in the
  Files pane at its first change, double-click a line to open it there; the chevron at the header's end folds the
  change back into its tool line. The end of a run (*Worked for 1m · 13 tools ·
  7 files changed ›*) lists the files the run changed, with their +N −M and New / Deleted / Renamed / Moved marks (open when
  the run changed two or more files); click one to open it. The Files pane's *Changes* review uses the same colours,
  bars, word marks and line numbers.
  Long diff lines show a grapheme-safe prefix of at most 4,096 UTF-16 units with a truncation marker.
  Right-click a shortened row → **Copy code** copies the original line, including tabs and omitted text.
- **Keyboard shortcuts** — Ctrl/⌘+/ (or `/hotkeys`) shows them all; Esc closes the sheet.
- **Command palette** — Ctrl/⌘+K (or *View → Command Palette…*) finds and runs anything by part of its name: the
  window's actions (each greyed with the reason when it cannot run now, e.g. *Stop omp* while nothing runs), the panels
  (*Panel: Files*…), every settings page (*Settings: Permissions*…), the themes, your chats (unread ones first, then the
  newest) and the open project's files (listed under the rest). Typing "set perm" is enough: whole-name prefixes rank
  first, then word starts, then text inside a name, then scattered letters; the matched letters are bold. ↑/↓ choose,
  Enter runs, Esc closes and puts the keyboard back where it was. What you ran last is listed first next time.
  Ctrl/⌘+P (*Go to File…*) opens it on the project's files only. ⌘K and ⌘P were chosen because they were free: ⇧⌘F/G/P/T/B/S/M
  are the panels and the preview, ⌘F / ⌘G find in the chat; ⌘P is "Go to file" in Codex and VS Code.
- **Stop** — the Stop button or Esc stops the current run; if omp does not stop, the button offers Force stop. Esc
  stops the run only from the message box, the conversation or a button: in the sidebar's search and the Files filter
  the first Esc clears what you typed (as a Mac search field) and the next one hands the keyboard back to the message
  box, as Esc in the preview's address does; in a terminal tab or the preview it stays there (vim keeps its Esc).
- **Closing or quitting while omp works** — ⌘W first closes the focused terminal/panel/browser tab; outside those
  surfaces it closes the window. Closing the window (⌘Q, **Quit** in the tray menu) stops every
  chat's omp and ends every terminal tab, and with them everything they started: omp's background jobs (a dev server
  omp keeps running, its daemon broker) and what you started in a terminal tab, even when it was detached. Only an
  editor or file manager you opened from the app stays. macOS and Linux; on Windows omp's background jobs may stay
  (not verified there). If the app was not quit normally (a crash, a forced quit, an update of a version before
  0.0.6), the next start ends what it left. While a run is going in any chat, omp waits for your answer, or a terminal
  tab still runs something (a dev server), a card above the message box asks first, naming the chat when it is not
  the one shown (or how many chats work): **Quit** or **Keep working** (the keyboard starts on Keep working; Esc
  keeps working too).
- **Terminal** — Ctrl+` (Ctrl on macOS too), the terminal button in the header (or **⋮ → Terminal**) opens a
  terminal beside the conversation (drag its edge to resize it; in a narrow window it covers the conversation until
  it is hidden), in the project folder, with the keyboard in it: **+ Shell** for a shell, **+ omp TUI** for omp's own
  terminal UI, for features omp offers only there (plan, goal, vibe…). With no terminal open the panel offers
  **New shell** and **omp TUI**. In omp's terminal UI `/login` lists only the providers you have added (the others
  are off for omp: [Network privacy](#network-privacy)); add a new one under **Settings → Model providers**.
  Right-click terminal output for **Copy selection**, **Copy all**, **Paste**, **Clear**, or **Open selected link**;
  right-click never pastes automatically. Ctrl-click a web URL opens it. Right-click a tab for **Close other tabs**
  or **Close tabs to the right**; a middle click closes the pointed tab.
  Browser tabs have the same close actions plus **Copy page URL** and **Reload**. The Browser rail has its own tab
  strip; Ctrl+Tab / Ctrl+Shift+Tab cycle its tabs, not chat history. Ctrl/⌘+L focuses the address, +T opens a tab,
  +R reloads, +[ / +] move through page history, and +Shift+C copies the address.
  Four or more pages of one site (localhost: of one port) show in both strips as one chip — the site or the pages'
  shared title, how many, a chevron; click it for a list under it to switch to or close a page. The chip stays at
  three pages and turns back into tabs at two. A page omp has finished with leaves the strip once you look at
  another tab (open an address in it to keep it).
  Normal window bounds/maximized state and panel sizes/visibility survive a normal close and are clamped to the
  available screen on reopening. A restored terminal panel does not start a shell by itself.
- **Project menu** — click the project's name next to the session title: **Browse files** (the Files pane),
  **Show in Finder** / **Show in Explorer** / **Open in file manager**, **Open in VS Code** (or Cursor, Windsurf,
  Zed, Sublime Text — only the editors whose command is on your PATH), **Open terminal here**, **Copy path**, a new
  session in one of your recent projects, or **Open folder…**.
- **Files pane** — the project as a tree beside the conversation. Folders open on click; dependencies and build
  output (`node_modules`, `bin`, `obj`, `.venv`, `dist`…) and what `.gitignore` excludes are dimmed, and `.git` is
  never shown. **Go to file** finds a file by name anywhere in the project (letters in order work too: `fvm` finds
  `FilesViewModel.cs`); Enter opens the first match.
  Search and `@` mentions match canonically equivalent Unicode spellings (for example, a composed accent and the
  same accent stored as a combining character). The index's original paths are retained for display, insertion
  and opening files. This also applies to the **Changed** and **This session** filters.
  In a git repository each changed file gets a mark — M modified, U new, A added, R renamed, ! conflict,
  D deleted (still listed, struck through) — and a folder with
  changes inside gets a dot. The segments above the tree: **All**, **Changed** (only in a git repository) and
  **This session** — the files omp edited or wrote in this conversation (marked ✦ in the tree). The pane follows the
  disk while it is open and after every tool omp runs. Supported text files open in the built-in editor, with
  syntax colours, undo/redo, search/replace and **Save**. Unsaved buffers survive navigation; **Editor actions**
  offers copying unsaved text, saving a copy and explicit reload. An external change produces a conflict instead
  of overwriting either version. Language services require explicit trust and run through the project's engine.
  Right-click inside the editor (Control-click on macOS) for **Cut**, **Copy**, **Paste** and **Select all**.
  Opening the menu preserves the selection. Cut/Paste change the unsaved buffer, not the disk file; read-only
  documents allow copying but not editing.
  Markdown can be previewed; images remain previews. Large, binary or unsupported files are not silently
  shortened and then saved. **@** puts `@path` in the message box — omp reads the saved disk file, not an unsaved
  buffer. Use **Editor actions → Add selection to message** to attach an explicit versioned editor snapshot with
  your message; the **⋯** menu opens it in an editor (those on your PATH) or the default app, reveals it in the file
  manager, or copies its path or relative path. Right-click a row to open it, add it to the message, open it in the
  default app, reveal it or copy its path. A file's path in the conversation (a tool's line, the header of a change)
  opens it here, at the changed line. Keys: Enter or Space opens, → / ← open or close a folder, Esc clears the
  search. A file git sees changed shows **File · Changes +N −M** under its name (opened from **Changed** or **This
  session** it starts on Changes): what changed since the last commit, part by part (hunks); the first 3,000 lines
  show, and changes over about 4 MB are not shown at all. **Revert** on a part puts just that part back as the last
  commit has it (also out of git's staging area; a part that was staged and then edited again is left alone, with the
  reason). A file new since the last commit (not in git yet, added, or renamed) has no **Revert** on its part, since
  that would delete it: **Revert file…** puts the whole file back after asking — a new file is deleted, a renamed one
  gets its old name back (for a file git does not have, the button says **Delete file…**). Neither can be undone.
  The **+** at the start of a line (hover it, or Tab to it) opens a comment box: Enter adds the comment to the
  message box as a chip (*app.py:41 — your comment*; × removes it), Shift+Enter starts a new line, Esc closes the
  box. In the editor, right-click → **Comment…** comments on the selected lines (or the caret's line); in the
  conversation, right-click a line of a change → **Comment on this line…**. See **Comments (marks)** below.
- **Preview** — the 🌐 button in the header (Ctrl/⌘+Shift+B) opens the web app you are building beside the
  conversation, with back, forward, reload, an address box and *open in your browser*. Until a page is open, the
  local addresses that appear in omp's tool output (dev servers it starts) are offered as chips. Once a page is open,
  **+ New tab** under the toolbar opens another tab of your own (the address box takes the keyboard); with several
  tabs, **+** sits right after the last one. A tab you opened and left empty closes when you leave it; × closes the
  ones you opened (the first stays). It uses the system's
  web engine: WebView2 on Windows, WebKit on macOS, WebKitGTK (`libwebkit2gtk-4.1`) on Linux; when the engine is
  missing the panel says what to install and offers **Open in browser**. On macOS, SVG icons that draw through a
  `<mask>` of their own (common in Figma exports) look soft: WebKit, the engine Safari uses too, draws such a mask at
  low resolution when the SVG is an image (`<img>`, CSS `mask-image` or `background`). The same icons inline in the
  page, or exported without the mask, are sharp.
- **omp browses in the preview** — when omp opens a web page with its browser tool, the page opens here rather
  than in a hidden browser: the preview opens by itself and you watch each step; a pulsing **omp** pill in its
  toolbar shows while omp reads, clicks or types on the page. Each page omp opens gets a tab of its own (a strip of
  tabs appears above the page): the preview shows omp's newest tab, your own page stays in the first tab, and tabs of
  different chats don't push each other out. With many tabs their titles shrink to share the strip (the tooltip has
  the whole title) and each keeps its ×; when even that is too wide, the strip scrolls sideways (wheel or swipe; a
  faded edge means more tabs past it) and keeps the tab shown in view. Pick another tab and the preview stays on it
  while omp works in the background (picking omp's latest tab follows omp again); × closes one of omp's tabs (omp is
  told it is gone). If a
  page shows an alert or *OK / Cancel* question, omp is told what it says instead of waiting, and answers it with
  Enter or Esc like you would; an alert omp's own click causes is simply accepted, and a question omp caused but
  hasn't answered within 10 seconds comes to you as a card. omp reads pages as text (their elements and content) and can take screenshots of what the preview
  shows: the visible part of the page (not the whole page, not one element), taken by the web view itself, so no
  Screen Recording permission is needed; keep the preview open while omp works (a screenshot of a tab in the
  background shows that tab for a moment). Only http and https pages open
  (for a local file, ask omp to serve it, e.g. `python3 -m http.server`).
  Every omp the app starts uses it by default: each chat and the terminal's omp. omp is told that you see this
  browser, so pages you should look at or sign in to, and anything on localhost, open here rather than in your system
  browser; a `browser.cdpUrl` or browser relay set in omp's own settings does not take over. Your omp configuration
  files are not changed (the app passes its own settings layer and note only to the omp processes it starts). To
  give omp its own browser back, turn off **Settings → General → Agent uses the Ombra browser**; chats started or
  restarted after that use omp's own browser settings.
  On macOS omp's clicks, typing, keys and scrolling reach the page as if you did them (pages treat them as real input,
  so pop-ups, sign-in windows and file pickers work); the composer keeps your keyboard, and when you use the page
  yourself omp waits until you pause. Hovering only works while the Ombra window is the active one, as in Safari.
  omp's helper scripts live apart from the page, which cannot see or change them. On Windows and Linux omp's input is
  still simulated by scripts, and omp is told so.
- **Questions from the page** — when a page in the preview asks something (a sign-in for a password-protected site,
  an alert, *OK / Cancel*, a text answer, *Leave this page?*, a file to upload, a download, the camera or another
  permission, a new window), the preview shows it as a card over a still picture of the page, one at a time (*1 more*
  says others wait). Enter answers with the main button, Esc cancels; the first field takes the keyboard. Going to
  another address, reloading, back or forward closes the questions of the page you left. What omp's own clicks on the
  page cause is left to omp; a sign-in card is always yours, and omp never sees what you type there.
  A password-protected site (HTTP sign-in, such as a staging server behind nginx's `auth_basic`) shows *<site> wants
  you to sign in* with its realm and **Username** / **Password**: **Sign in** loads the page, a wrong password asks
  again with *That username or password didn't work*, **Cancel** shows the site's own *401* page. The sign-in is kept
  until you quit the app (never written to the keychain) and goes to that site only; on a plain `http:` site the card
  warns that the connection isn't secure.
  On macOS: a new window a page opens (a sign-in pop-up, a `target=_blank` link) opens as a tab marked *Pop-up* next to
  the page that opened it, still connected to it, so *Sign in with…* pop-ups hand their result back and close
  themselves; × closes one, and closing a page closes its pop-ups. A camera or microphone request asks with **Allow
  once** / **Always allow on this site** / **Don't allow**. Each remembered-permission decision reads the current
  saved permissions, so another process's completed revocation takes effect at the next request. Missing,
  unreadable or malformed storage cannot authorize; unsaved grants do not auto-allow. A failed revocation
  still denies locally and is retried with the next successful settings mutation.
  A page that can't be opened shows why with **Try again**; a page whose web process stopped shows *This page stopped working* with
  **Reload**. Downloads ask first: **Save** puts the file in the folder you chose last time (the save panel asks the
  first time, so nothing lands in a folder you didn't pick), **Save as…** always asks; they are listed under the page
  with their progress, **Stop** and **Show in Finder**. Logins (cookies) are kept between runs. Sites see Safari's own
  user agent. While a tooltip or a menu of the window lies over the page, the page shows as a still picture until it
  closes (on macOS the page would otherwise cover it).
- **Comments on the page** — **Annotate** in the preview's toolbar (or **+ → Comment on the page**, or
  Ctrl/⌘+Shift+S) turns on the mode: click to mark a point, or drag to select an area, write what to change and press
  **Comment** (or Enter). Nothing is outlined while you move the pointer. A numbered pin (and, for an area, its dashed
  outline) stays on the page it was left on, following the element as the page scrolls; another page shows only its
  own pins — also in a single-page app that changes its page without loading one (a route like `/cart` or `#/cart`;
  an anchor such as `#top` is the same page). The comment becomes a
  chip in the message box with its page's address (× removes it). To change a comment, click its pin on the page
  (**Save** or **Delete**, in or out of the mode) or the chip's text (an editor with **Save** and **Show**,
  which opens that page — its tab if one shows it). The page's own links and buttons do nothing until you leave the
  mode (Esc, or Annotate again). The comments go with your next message — with no text, Send asks omp to make the
  changes they ask for — grouped by page (each group starts with the page's address and title, so omp knows which
  page a comment is on), each with where the mark is (in the viewport and on the page) and what finds the element
  holding it in the code: its selector, opening tag, text, position and size and — for React ≤ 18, Vue and Svelte dev
  builds — the source file; an area also lists the elements it shows. Comments on several pages can go together.
  This works the same with either engine of the preview (the system's web view, or Nightly): with Nightly the
  comment tools run in Ombra's own script world of the page, which the page's scripts can neither see nor reach.
- **Comments (marks)** — comments on code (Changes, the editor, a change in the conversation) and on pages are one
  numbered list of chips in the message box, up to 40 at a time; a page comment's number is its pin's. Each has its
  place (file and line or lines with the lines around them, or the page), a short note and a status. Click a chip to
  change its note, **Show** its place (the file at its line, or the page), or **Resolve** it: a resolved comment stays
  as a dimmed chip but is not sent (**Reopen** brings it back). The next message carries the open comments — code
  first, then pages — and they leave the box once sent.
  With a team (see [Team](#team)), **Send N comments to the team…** under the chips sends the open code and page
  comments to team members instead.
- **Device size** — the device button in the preview's toolbar (Ctrl/⌘+Shift+M) lays the page out at a device's size,
  centred in the pane, with a bar under the toolbar: a preset (iPhone SE, iPhone 15 Pro, Pixel 8, iPad mini, iPad Pro,
  Laptop, Desktop) or a size typed in (Enter applies, 200–4000 each side), turn it (portrait / landscape), and the
  platform the page sees: Desktop, iPhone, iPad or Android — its user agent, set on macOS (elsewhere only the size
  changes, and the platform list is off); a page already open loads again with it. × or the button again fits the
  pane; the button brings back the device used last. It applies to every tab except pop-ups; a size omp set for its
  own tab wins there. With the Nightly agent browser the page always gets the device's size: where the pane is smaller,
  the page is shown scaled down to fit (as a browser's device toolbar does), and clicks and taps land where they are
  shown. In the built-in preview a pane smaller than the device limits it.
- **Nightly agent browser** (macOS, experimental) — *Settings → General → Agent browser* chooses where the preview
  and the Browser page render pages: **Built-in preview** (the system's web engine, above) or **Nightly
  (experimental)**, a full browser engine that Ombra installs and runs with its own profile; open pages load again
  in the chosen engine. Until you choose, the pane asks once: *Show pages with Nightly?* — *Ombra can show pages in
  Nightly, a full browser engine it installs and runs with its own profile. Pages, sign-ins and downloads stay on
  this Mac.* — with **Use Nightly** and **Keep the built-in preview**; the answer is saved. Ombra installs only the
  Nightly version this release pins, checked against its SHA-256, and removes older versions it no longer runs;
  Nightly in turn talks only to a signed Ombra. With Nightly, omp's browser tool works in its tabs as described above
  (reading, clicks, typing, screenshots of what the pane shows). The page's questions — alerts, *OK / Cancel*, text
  answers, *Leave this page?*, HTTP sign-in, permissions (screen sharing is refused), file pickers, downloads — come
  up as the same cards, over the live page instead of a still picture, with the same remembered site permissions and
  download folder; downloads are listed under the page. A page's new windows open as tabs next to it. While Nightly is
  chosen but can't show pages — not installed, outdated, it could not start or stopped responding (*Nightly is not
  responding.*) — the built-in preview shows them, with a bar that says why and *The built-in preview shows pages
  meanwhile.*: **Install Nightly** when installing helps (with its progress, e.g. *Downloading Nightly …*), otherwise
  **Try again**; **Show details** has what the process reported. Windows and Linux have the built-in preview only.
- **Browser page** — **Browser** in the rail at the window's left edge opens a full-size browser of your own in the
  conversation's place. With the built-in preview: its own tabs (keys: see *Terminal* above), the same cards,
  downloads and site permissions as the preview. With Nightly (macOS): a whole Nightly browser window fills the page
  from the window's top to its bottom, with the window's rounded corners — its own sidebar, spaces, address bar,
  library, menus and settings, all Nightly's (the same Nightly process as the preview). Nightly follows Ombra's
  *Appearance* (Light, Dark, or the Mac's under System), its windows and the pages' light or dark styles alike, and
  switches with it. While it has the keyboard,
  ⌘/Ctrl shortcuts are Nightly's (⌘T, ⌘L, ⌘W, ⇧⌘T, Ctrl+Tab, switching spaces…); Ombra keeps only ⌘Q, ⌘H, ⌘M, ⌘`
  and ⌘Esc (back to the chats). Its menus, the library and share dialogs open as windows over Ombra's. omp never
  drives it. **Chats** in the rail goes back to the conversation; the tabs stay. Links and pages from your team open
  here once you accept them, and so does a chat link's **Open in Browser page**.
- **Panes** — the buttons at the right of the header open **Files** (Ctrl/⌘+Shift+F), **Plan** (Ctrl/⌘+Shift+P),
  **Background tasks** (Ctrl/⌘+Shift+T), the **Terminal** (Ctrl+`) and the **Browser** preview (Ctrl/⌘+Shift+B); a
  lit button means its pane is open, and its tooltip names its shortcut. When the header has too little room for
  them beside the title, project and branch, they fold into **⋮**, a menu that lists them by name with their shortcuts (with how many background tasks
  run now, and how many plan steps are done); a tick marks what is shown. Files, Background tasks and Plan open in a
  pane right of the conversation (drag its edge to resize; in a narrow window it covers the conversation until you
  close it with ×). A small blue dot on the Background tasks button (or on ⋮) means background tasks are running.
  - *Plan* — the agent's steps (omp's todo list) by phase, each with a checkbox: checked when done, a blue box with a
    dot for the step in progress (with what the run does now under it), an orange bar and what it waits on when
    blocked, a cross when dropped. Finished steps stay; a plan the agent replaces or clears stays under *Earlier
    plan*, and *History* lists every change with its time — also for a session you open later. While the pane is
    open, the one-line task card above the message box is hidden: the pane shows the same list.
  - *Background tasks* — the subagents the agent starts (its task tool): what each works on, its status, how long it
    has run, its current tool, tool calls and tokens; open one to read its task and latest output. Hover highlights
    the whole rounded card, including its message row; the summary opens details, while typing, **Send** and
    **Stop** keep their own actions without opening details. **Show its conversation** enables a live preview of
    the latest 40 entries, with long text shortened. It reads new complete
    transcript lines once a second while the expanded card is visible; collapsing it, scrolling it out of view,
    closing the pane or switching chats stops those reads. Reopening continues from the saved byte cursor;
    a replaced or truncated transcript resets the preview. Finished ones stay listed for the session.
    Below them, *Background jobs*: omp's background jobs (commands left running, as omp's `/jobs` lists them), read
    again every few seconds while the pane is open. Each shows its status (*Running*, *Waiting for input*, *Done*,
    *Failed (exit N)*, *Cancelled*) and how long it has run (*bash · running for 12s*). **Show output** shows what it
    printed, updated while it runs (the row keeps only the latest part); **Stop** stops a running job, and the row then
    shows how it ended (*Could not stop it: …* with omp's reason when it can't). A job whose last line looks like a
    question is marked *Waiting for input*: a background command can't be answered from here — stop it, or ask the
    agent to run it without prompts.
- **Dictation** — the 🎤 button (or Ctrl/⌘+Shift+Space) records and transcribes on this computer. The first use asks before
  downloading the speech model (about 670 MB, verified against pinned hashes).
  After all captures and audio checks have closed, the next recording refreshes the native audio device list.
  An active recording is never restarted to refresh devices; after changing inputs or waking the computer, finish
  or cancel that take and start another.
- **Notifications** — when a run ends, omp stops or asks something in a chat you are not looking at (the window is
  in the background, or another chat is shown), the OS is told, with the chat's name when it is not the one shown,
  and the window's title gets a ● mark while the window is in the background: Windows flashes the taskbar button
  (bringing the window back opens that chat), macOS shows a Notification Center banner, Linux a desktop notification
  through `notify-send` if it is installed (a click opens that chat). **Settings → General → Notifications** turns
  that off.
- **Menus and tray** — on macOS the app menu has **Settings…** (⌘,) and the menu bar *File* (New Chat ⌘N, Open
  Folder… ⌘O), *Edit* (Undo, Redo, Cut, Copy, Paste, Select All, on the text that has the keyboard), *View*
  (Sessions, Terminal, Events) and *Window* (Minimize ⌘M, Zoom, Close ⌘W). Where the desktop has a tray or menu-bar
  area, an Ombra icon there offers **Show Ombra**, **New Chat** and **Quit** (which asks first while omp works,
  as closing the window does).
- **In the conversation** — **Retry** on a failed reply. Point at a reply's end (*Worked for 12s · 3 tools*) for
  **Copy** (as Markdown); point at one of your messages for **Copy** and **Rewind to here** (this chat goes on from
  before it, the message back in the box, like *Rewind…* in the session menu). The conversation
  follows a streaming reply while you are at its end: the reply grows at the bottom and nothing above it moves.
  Scroll up (wheel, trackpad, scroll bar or keys) and it stays exactly where you are for as long as the reply goes
  on, with a round **↓** button at the bottom right to jump back to the latest message; scrolling back down to the
  end follows the reply again. Rows opening or closing above what you are reading do not move it. Sending a
  message, or opening another session, always goes to the end. Once you click the conversation's text (the keys
  are then the conversation's, not the message box's), Page Up / Page Down scroll by a screen, ↑/↓ by a little, Home
  goes to the top and End to the latest message.
- **Links** — a web link (http or https) you click in Ombra — in a reply, in a terminal, on a card (**Open** on a
  shared conversation or the usage dashboard), in Settings — opens in the right panel's **Browser** tab next to the
  chat, in a tab of its own (the panel opens on it; Settings, the Browser page and the Team page close so you see it).
  It shows with the pane's engine: Nightly, or the built-in preview while the pane asks whether to use Nightly or
  offers to install it. Only when the pane has no engine at all (the system's web engine is missing) does the link go
  to your default browser. ⌘-click (Ctrl-click on Windows and Linux) or right-click a link for **Open in preview**,
  **Open in Browser page**, **Open in default browser**, **Copy link** and **Share with team…**. Sign-in pages
  (**Open** on omp's sign-in card, an MCP server's **Sign in**, `/mcp reauth <name>`) open in Ombra too: in a tab of
  the chat's **Browser**, or — when you start the sign-in from Settings — in Ombra's own **Sign in** window; the
  page's redirect back to omp (an address on this computer) completes there, and the tab or window closes once you
  are signed in. For passkeys or a company's single sign-on use **Open in default browser** on the sign-in card, or
  right-click a **Sign in** button. Files, folders and `mailto:` links open with the system as before.
- **Paths in replies** — a path omp writes as code (`src/app.ts`, `src/app.ts:42`, `assets/logo.png`, `docs/`) or as
  a link to a local file is looked up in the chat's project when the pointer rests on it. If it exists it is
  underlined and shows a preview: a file's first lines (around the line when it names one, that line marked), an
  image's thumbnail, a folder's first entries, with the size. A click opens it — an image in the image viewer, a
  file in the Files pane at its line, a folder in the Files tree; dragging over it selects the text as usual. Code
  that names nothing on disk stays plain.
- **Selecting text in a message** — a drag selects through your message's lines and, in a reply, on through its
  paragraphs, headings, list items, code and table cells, up to where the pointer is (it stays within that reply).
  Ctrl/⌘+C (the *Edit* menu, the selection's **Copy**) copies it as it reads: a blank line between paragraphs, a line
  break between a list's items, a tab between a table row's cells, a link as its label. Ctrl/⌘+A with the keyboard in a
  reply selects the whole reply. A click elsewhere clears the selection.
- **Right-click menus** — right-click (two-finger click, or Control+click on macOS) acts on what is under the
  pointer, not on what is selected: one of your messages offers **Copy** and **Rewind to here**; a reply **Copy**
  (the text as it reads), **Copy as Markdown** and, when it failed, **Retry**; a code block **Copy code** and
  **Insert into message**; a link **Open in preview**, **Open in Browser page**, **Open in default browser** and
  **Copy link** (see *Links*). Selected conversation words keep the native
  **Copy** menu and add **Quote in message**, inserting a Markdown quote at the composer caret without replacing
  the draft. Free-text transcript comments are not supported: anchored comments remain on code/diffs and pages.
  Errors and notices offer **Copy details** and **Show in Events** (failed replies also offer **Retry** when available).
  Right-click an event to **Copy** that line, **Copy all**, or **Clear** the visible log. A queued message offers
  **Copy**, **Edit**, **Send now** when eligible, and **Remove**; editing uses the same withdrawal/recovery path as ↑.
  Pasted-text chips offer **Preview** (a scrollable, selectable code viewer), **Copy**, **Expand into message** at the caret, and **Remove**.
  Message, path, code, link and image copies confirm only after the clipboard accepts them; clipboard failures
  show the actual error instead of claiming success. The image viewer keeps its copy error visible in the viewer.
  Text fields have **Cut**, **Copy**, **Paste** and **Select all**; the code viewer **Copy** and **Select all**;
  sessions, projects and files have the menus described with them. Esc or a click elsewhere closes a menu.

| Shortcut | Action |
|---|---|
| Enter / Shift+Enter | send / new line (Ctrl/⌘+Enter sends instead when chosen in Settings → General) |
| Alt+Enter (⌥Enter) | steer the running turn |
| Shift+Tab | next permission mode (Ask → Accept edits → Plan mode, when omp offers it → Bypass, which asks to confirm) |
| ↑ (in the empty message box) | edit the last queued message |
| Ctrl+K (⌘K) | command palette: actions, panels, settings pages, themes, chats and files |
| Ctrl+P (⌘P) | go to a file of the project (the palette on files only) |
| Ctrl+/ (⌘/) | keyboard shortcuts |
| Ctrl+L / Ctrl+Esc (⌘L / ⌘Esc) | focus the message box; ⌘L/Ctrl+L remains the address shortcut in either browser surface, while ⌘Esc/Ctrl+Esc returns from the Browser page to chat |
| Ctrl+Shift+I / Ctrl+Shift+E (⇧⌘I / ⇧⌘E) | choose a model / thinking effort |
| Ctrl+\\ (⌘\\) | hide the focused sidebar, right pane, terminal panel or event log; terminal processes keep running |
| ↑ / ↓, Enter, F2, Delete (session list) | move focus without opening, open, rename, or ask to delete the focused chat; double-click a chat to rename. In selection mode Enter or Space toggles the focused chat, Delete or Backspace asks to delete the selected ones, Esc leaves |
| ⌘-click / Shift-click a chat (Ctrl-click on Windows and Linux) | select it / select the range from the open or last picked chat |
| ← / →, Home, End (sidebar resize handle) | change width by 20 pixels, restore its default, or use the maximum; Tab reaches the handle |
| Ctrl+Shift+U (⇧⌘U), Shift+Esc | mark the focused/current chat read or unread; mark all chats read |
| Ctrl+W (⌘W) | close the focused terminal/panel/browser tab before closing the window |
| Ctrl+Enter (⌘+Enter) | send |
| Ctrl+N (⌘+N) | new chat (outside any project) |
| Ctrl+B (⌘+B) | show / hide the sidebar |
| Ctrl+Tab / Ctrl+Shift+Tab | next / previous session in the sidebar |
| Ctrl+F (⌘F) | find in chat: every loaded message, reply and tool call (its arguments and output), case-insensitive; the counter says which match of how many; words selected in a message are looked for. What hides a match (a turn's *Worked for…* line, a step group, a tool's output, the thinking) opens while it is shown |
| Enter / Shift+Enter, Ctrl+G / Ctrl+Shift+G (⌘G / ⇧⌘G) | in the find bar: next / previous match (⇧⌘G opens the Changes pane while the bar is closed); Esc closes it and returns to the message box |
| Ctrl+, (⌘+,) | settings |
| Ctrl+` (Ctrl on macOS too) | terminal panel |
| Ctrl+Shift+P (⇧⌘P) | Plan pane |
| Ctrl+Shift+T (⇧⌘T) | Background tasks pane |
| Ctrl+Shift+F (⇧⌘F) | Files pane |
| Ctrl+Shift+B (⇧⌘B) | browser preview |
| Ctrl+Shift+S (⇧⌘S) | annotate the preview: mark a point or an area to comment on |
| Ctrl+Shift+M (⇧⌘M) | device size in the preview (on: the device used last; off: fit the pane) |
| Ctrl+Shift+Space (⇧⌘Space) | dictation (Enter finishes, Esc cancels) |
| Ctrl+V (⌘V) | paste text, images or files (a long text becomes a chip) |
| 1 / 2 / 3 (approval card focused), localized Alt access keys (English: Alt+A / Alt+D) | allow / deny / deny and say why; approval requires a fresh released-key gesture after the appearance/activation guard; 1–9 pick an answer |
| Esc | close the image viewer, leave Settings, close the shortcut sheet, the *say why* box, a question card or the session card (when it has the focus), clear a search box (then leave it), or stop the run (from the message box or the conversation) |
| ← / →, Space | in the image viewer: previous / next image, actual size / fit |
| F12 | event log beside the conversation, or over it when the window is too narrow for both (also *Settings → Diagnostics → RPC events*) |

Closing an image viewer, request/session card or menu returns focus to its opener when that control still exists.
A deliberate click or an action that opens another editor keeps its new focus. Selecting a model deliberately returns
to the message box (so typing Space does not reopen its chip); cancelling the picker returns to its opener.
Repeated Esc is not a rewind gesture: each Esc follows the existing close/dismiss/stop ladder. Use **Rewind** in the
session menu or **Rewind to here** on a message instead.

The native **Help** menu opens **Keyboard shortcuts**, **Report a problem** (the existing Settings → Diagnostics
page, with its redacted diagnostic export), and **What's new** (Settings → Updates, with available release notes).
It does not send a report or open an invented external issue tracker.

### Session menu

Click the session's title in the header. Most items run omp's own command — shown on the right of the item — and
report on a card above the message box:

- **Rename** (edit the title in place) · **Copy session ID** (what `omp --resume` takes) · **Copy session file
  path**. A chat without a name gets one from omp's own title generator (the model omp uses for titles, as in its
  terminal) a moment after you send a message that says what it is about — "hi" or "ok" are skipped until one does;
  an old unnamed chat is named the next time you write in it, from its first message. The name shows when the reply
  ends. A name you gave is never replaced.
- **Compact conversation…** — omp summarizes the conversation so far and goes on from the summary, freeing context;
  you can say what the summary should keep. The card shows it running (**Stop** cancels) and then how many tokens
  the context went from and to. **Hand off…** is the same with a written handoff document; both stay in this session.
- **Fresh provider session** — the next reply opens a new session with the model provider and sends the whole
  conversation again (useful when a provider's cached state went wrong); nothing changes on screen.
- **Retry the last turn** — runs the last turn again after it failed or was stopped.
- **Rewind…** — pick one of your messages: this chat goes on from before it and the message comes back into the box
  to change and send. It stays one chat in one session file: what came after is kept in that file as a branch (omp's
  `/tree` in a terminal shows it), and no second chat appears in the list. **Fork** makes a separate copy instead.
- **Export as HTML** — one page with the whole conversation, saved in the chat's project folder; **Open** or
  **Show in folder**.
- **Share a link…** — says first what happens: omp uploads an encrypted copy of the whole conversation (tool output
  included) to its share server (or a secret gist, per omp's settings), and anyone with the link can read it. After
  **Upload and copy link** the link is on the clipboard.
  The consent card checks both `share.redactSecrets` and `secrets.enabled`. Until those settings are known,
  it says redaction is unverified rather than promising that secrets will be removed. If either setting is off,
  it warns that share redaction is off. Review the conversation before uploading even when redaction is enabled.
- **Workspace folders…** — the folders omp may read and edit in this session; **Add folder…**, or remove an added one.
- **Move to another folder…** — omp moves the conversation to that folder and works there from then on (your files
  do not move). **Pin session** keeps it at the top of the list (**Unpin session** undoes it). **Memory** shows what
  omp adds to the context from its memory, with **Update now** and **Clear memory…** (or says memory is off in omp's
  settings).
- **Delete session…** — asks first, then deletes the conversation's file and its artifacts and starts a new session.

### Context and usage

The ring with a percentage at the right end of the row under the message box shows how full the model's context
is, from the first message on (a new chat shows none: before it, omp's few percent are its own system prompt and
tools); it turns orange from 70 % and red from 85 % (omp compacts the conversation on its own near the end, when
Auto-compact is on). Click it for the breakdown as bars (the parts omp's `/context` reports, the auto-compact buffer,
free room), this session's tokens (input, output, cache) and cost (when omp prices the model), **Compact now**,
**Measure again** and a link to omp's **Usage dashboard** across all sessions. Under *Plan usage* it shows the limits
of the model's provider for each of its signed-in accounts (Claude's or ChatGPT's 5-hour and weekly windows, API
spend…) as meters with the share used and the time to reset, from `omp usage --json`: a side process run when the
popover opens (at most every 30 seconds, again after a reply), never a message in the conversation. The open popover
refreshes after each reply. When `get_state` reports an exceptional usage stage for the active account, a badge
shows **Low priority** or **Limit reached · wrapping up**, even in an empty chat. Details include the reported reset
time, extra-usage status and optional low-priority allowance; missing state shows no badge, and changing model or
session clears stale state. **All providers** opens Settings → Model providers with every signed-in provider's
limits.

Some providers give an account **saved resets** (ChatGPT and Claude subscriptions, now and then): each refreshes a
used-up limit when you spend it. Under the account's meters (the popover and Settings → Model providers) you can see
how many it has, how many can be spent now, when the soonest expires, or why none can be spent now (omp's reason,
such as a cooldown). **Use a saved reset** asks once more in place; **Use reset** has omp spend one of that account's
resets (`/usage reset`, without a message in the conversation), shows omp's answer under the account and reads the
limits again. It needs a running chat; a spent reset cannot be taken back.

### Model options

Under the model list: **Fast mode** (the provider's priority tier for this session: faster, costs more; omp says so
when the model has none), **Extended context** (larger context windows where offered, may cost more), **Advisor** (a
second model reviews each turn and adds notes, for this session; if omp has no model for its advisor role, **Choose
model** sets one and restarts omp on the session), **Auto-compact** and **Auto-retry** (after provider errors). The
switches show omp's current state when the menu opens. Extended context, Auto-compact and Auto-retry are saved in
omp's settings and apply to every session.

### Commands that run only in omp's terminal

Some omp commands exist only in its terminal UI (for example `/loop`, `/tree`, `/collab`, `/vibe`).
Typed in the message box they are never sent to omp as a message: where the window has its own way it uses it —
`/settings` → Settings, `/login` (`/setup`, `/providers`) and `/logout` → Model providers, `/new` → a new session in the current project, `/resume` → the
sessions list (text after it filters the list), `/model` without arguments → the model picker, `/hub` → Background
tasks, `/branch` or `/rewind` → Rewind, `/drop` → Delete session, `/plan` and `/plan-review` → plan mode (when omp
offers it), `/restart` → restarts omp, `/extensions` and
`/status` → a card that opens the Plugins page, `/hotkeys` → the keyboard shortcuts. `/quit`, `/exit`, `/copy`,
`/open` and `/queue` get a card that says what to use in the window instead (the `/` menu leaves them out).
Otherwise a card offers **Open omp terminal**, omp's own interface in the terminal panel (a separate omp with its own
session); the `/` menu lists these last, under *Terminal only*. A skill, prompt or
extension omp itself offers under such a name is sent to omp as usual. `!command` runs a shell command in the project
through omp (the output joins the conversation and the context); `!!command` offers to open a shell instead.

## Team

**Team** in the rail at the window's left edge (the people icon) opens the Team page in the conversation's place;
**Chats** in the rail goes back. A team is your own: it lives on a team server you or your organisation runs, and its
members are the people its admins invite — it is not taken from GitHub, GitLab or another Git provider's
organisations or repositories. What members send each other — names, shared items, comments, progress — is
end-to-end encrypted between their devices; the server passes it on without seeing names or contents. The page
refreshes every 15 seconds.

- **Create a team** — *Your team server: its address and, for an IP address, the certificate pin install.sh printed.
  The first team needs the one-time bootstrap code.* Fill in **Server** (`203.0.113.7` or `team.example.com`),
  **Certificate pin** (for a server reached by IP address; one with a domain certificate needs none) and **Bootstrap
  code**, then press **Create team**. You are its owner. The **Owner recovery key** is shown once: *Write it down or
  print it and keep it offline. It is shown only now: Ombra does not keep it. Without it a lost owner device cannot be
  replaced.* **Copy** copies it; **I saved it** removes it from the screen.
- **Join a team** — an admin sends you an invite link (`ombra://join?…`; it names the server and its pin). Paste it
  and press **Ask to join**: *Your request to join was sent. An admin of the team has to approve it; this page
  updates by itself.* Admins see it under **Join requests** (the role and how many devices) and press **Approve**.
- **Invite** (admins) — choose **Member** or **Admin** and press **Create link**: *One-time link ready · Member ·
  valid until it is used*, with **Copy link** and **Show link**. Send it over a channel you trust.
- **Members** — each with their name, role (*Owner*, *Admin*, *Member*) and *You* on your own row; the member ID is
  the name's tooltip. **Safety number** shows a number to compare with that member in person or over another
  channel. When a member's key changes, a banner and their row say so (*Key changed: compare the safety number with
  them first.*), and **Numbers match** confirms it. Admins **Remove** a member (never the owner). The owner hands the
  team over with **Make owner**: that member sees *The owner offers you the ownership of the team.* with **Accept**.
- **My devices** — the computers on your account (*This device*, *Revoked*); **Revoke** cuts one off. To add one:
  **Pair a device** shows a one-time link and a QR code; on the new computer, under **Add this computer to your
  account**, paste the link and press **Pair**. Both screens then show six words; if they are the same, press **The
  words match** on both.
- **Technical details** (folded) — **Team ID** and **My ID**, each with **Copy**.
- **Banners** — *You were removed from the team. Its data on this device was erased (keys, messages and shared
  items).* (**Dismiss**). When neither the owner nor enough admins confirmed the team's state in the last 72 hours,
  sending is paused and a banner says so. When the server showed two different versions of the team, the team is
  frozen until the owner chooses one: **Review** lists what each choice undoes; after *I reviewed the list and want
  these changes undone*, **Sign the decision**.

**Settings → Account** has your **Name** in the team (*How the team sees you*; **Save** — *The name travels only
inside the team, encrypted; the server never sees it.*), **Sign out** (stops the team's background work and forgets
the account on this computer; its keys stay in the system key store for signing in again), **Technical details**
(**My ID**, **Server**, **Certificate pin**) and *Share progress with the team* (below). Signed out, it says *Not
signed in to a team. Create or join one on the Team page.*

### Sharing and the Inbox

You can share with the team:

- a link in the conversation — right-click it → **Share with team…**;
- a file of the project — its menu in the Files pane → **Share with team…**;
- a skill — **Share with team…** on its page (**Plugins → Skills**, then the skill's row);
- the project's changes since the last commit, as a patch — the share button of the Files pane's **Changed** view
  (*Share the changes with the team as a patch*);
- the Browser page's open pages, as one collection — *Share open browser pages with team…* in the command palette.

The **Share with team** dialog names what goes (*Share a file “notes.md”*). **Send to**: **Everyone in the team** or
**Specific members** (find them by name, tick them); an optional **Comment** (up to 280 characters); then **Share**.
A file, skill or screenshot can be up to 50 MB. *Sent to everyone in the team.* or *Sent to 2 members.* confirms it;
offline, it goes out at the next refresh. Without a team the app says *Join a team to share*.

For the recipients a toast says *Anna shared a file “notes.md”* with **Open**, which opens the Team page at the
**Inbox**; with the window in the background the system notification says only *Something new from your team* (no
sender, no title: it can show on the lock screen). The Team button in the rail counts what waits (9+ above nine),
and the bell lists it too. Each Inbox item has who shared it, its title and comment, a preview of what is inside —
the address, the file's name and size, a skill's description, a patch's base commit and files, a collection's
pages — its kind (*Link*, *File*, *Skill*, *Patch*, *Screenshot*, *Collection*), *Waiting for you*, *Accepted* or
*Declined*, and when. **Nothing is saved, installed or opened before you press Accept**; **Decline** tells the
sender no. Accepting:

- a link or collection opens in the Browser page, each page in a tab;
- a file or screenshot is saved in the open project (in Downloads without one) and opened in Files;
- a skill is installed in omp's user skills folder (`~/.omp/agent/skills` unless omp says otherwise); omp offers it
  from its next start;
- a patch gets **Apply to project**: it applies only to a clone of the repository it was made in that has its base
  commit — the open project, or a folder you pick — and the changes show in the Changes view.

**Open** does it again later. Opening the Inbox marks what it lists as seen for the sender. What you shared is listed
under **Shared by you** with *N of M answered* and each recipient's status: *Not opened yet*, *Seen*, *Accepted* or
*Declined*.

### Comments for the team

Comments on code and pages in the message box (see *Comments (marks)*) can go to team members: while there are open
ones and someone else in the team, **Send N comments to the team…** under them opens *Send comments to*: **Everyone**
or the members you tick, then **Send**. Code comments carry the repository and commit they were left on. The sent
comments leave your message box.

A recipient gets a toast, *Comments from Anna in app.py*, and the Team page's **Comments** card lists them under
**For you**: the note, who left it, where (*app.py:41*, *localhost:5173/about*) and a status. **Open** goes to the
place — the file at its line in Files, or the page at its pin in the preview — and marks that author's comments
there *Seen* for them. A code comment left on another commit than yours warns *Left on commit abc1234: the line may
have moved.* Page comments for you show as pins on their pages until you finish them. Write an optional **Short
note** (up to 280 characters) and press **Mark as done** or **Dismiss comment**; **To my agent** adds the comment to
your message box for your agent, without changing its status. There are no replies: a new thought is a new comment.
Under **Sent by you** each comment shows *2 of 3 done*, *1 of 2 seen* or *Not seen yet*, and every recipient's
status (*Not opened*, *Seen*, *Done*, *Dismissed*) with their note.

### Project progress

**Settings → Account → Share progress with the team** lists your projects (the folders of your sessions), each with a
**Team project** box: *Not shared with the team* or one of the team's projects (admins can make one with **New team
project**; every member gets access). Once a project is linked, **Share progress** publishes your state there and
**Include the current task title** adds the task — a second, separate choice; both are off by default. *Shared: you,
the project, working / waiting for input / idle, and when it changed; the current task title only if you turn it
on. Never shared: prompts, replies, file contents, diffs.* Turning sharing off ends what was published at once.

The Team page's **Project progress** card shows, as it happens, who works on what: the member (*You* for yourself),
the project, *Working*, *Waiting for input* or *Idle* with a coloured dot, the task when its author shares it, and
when it changed; the box at the top filters by project (*All projects*). Progress goes only to members online now:
*End-to-end encrypted; the server relays it to online members and keeps nothing.* What arrives is kept in memory
only. With nobody sharing: *Nobody online shares progress right now. Turn it on for your projects in Settings ›
Account.*

### Nightly shares through Ombra (macOS)

A Nightly on the same Mac — the one Ombra installs, or one in `/Applications` — has no team account of its own. While
Ombra runs and is signed in to a team, Nightly shares a page, a tab group or a screenshot through Ombra's account, to
everyone or to the members chosen, and shows what was shared with you (opening an item there accepts it; it can also
decline it). Ombra answers only a Nightly it knows, run by the same user, with an intact code signature. What Nightly
sends arrives in the Inbox like anything else (*Link*, *Collection*, *Screenshot*).

## Settings

**Settings** at the bottom of the sidebar (in the header while the sidebar is hidden), Ctrl+, (⌘,) or
`/settings`. The settings fill the window: their page list takes the sidebar's place, and **Back to app** at its top
(or Esc) returns to the conversation. Pages: **General**, **Account**, **Permissions**; for omp, **Model providers**,
**Plugins** (also in the left rail), **Computer use**, **Git and worktrees**, **SSH hosts**; for this app, **Pets**
and **Updates**; for troubleshooting, **Advanced** and **Diagnostics**. In a narrow window the pages are a strip across the top that
scrolls sideways (its faded edge shows there are more). The ones without a section of
their own below:

- **General** — *Appearance* (System, Light or Dark), *Notifications* (see Daily use), *Send messages with*
  (Enter, or Ctrl/⌘+Enter), *Agent uses the Ombra browser* (see *omp browses in the preview*) and, on macOS, *Agent
  browser* (see *Nightly agent browser*).
  Settings that change the shared engine's executable or environment require a
  coordinated restart after all chats and sign-ins are idle; they must not silently restart just the shown chat
  with a different process configuration. The event panel (F12) reports engine processes and memory,
  not one independent omp process per chat.
- **Permissions** — the *Don't ask again* rules given on approval cards: what each covers, as the rule
  (`bash(npm test:*)`), and where (*This session*, *This project* with its folder, *All projects*), each with
  **Remove** — omp asks again from the next request. Project and always rules are kept in the GUI's settings file
  (`approvalRules`), session rules only until the app quits. *Trusted folders* lists the project folders whose own
  tools omp may run (see *Folder trust*), with the date each was trusted and **Revoke**: the folder's idle chats
  restart without its tools at once, a working one when its run ends, and a card offers to close omp terminals that
  started there while it was trusted. The list is kept in `workspace-trust.json` next to the client's settings file
  (readable and writable only by you).
- **Account** — your team account: see [Team](#team).
- **Model providers** — first, *Local and custom*: the model servers omp can use on this computer — **Ollama**,
  **LM Studio**, **llama.cpp** and **vLLM**, at the address omp would use (its default, or `OLLAMA_BASE_URL` /
  `OLLAMA_HOST`, `LM_STUDIO_BASE_URL`, `LLAMA_CPP_BASE_URL`) — each with *Running · N models* or *Not running*
  (the app asks the server for its model list when the page opens). **Add** asks for the address (another port or
  machine is fine; **Check** asks it again), then writes the server into omp's `models.yml` with no key and its models
  discovered when omp starts; an added server shows *Added* and a remove button (it asks first). **Add custom
  provider…** opens a form for any OpenAI- or Anthropic-compatible service: a name (omp's provider id is made from
  it: *My Server* → `my-server`), the address (base URL, usually ending in `/v1`; for *Anthropic Messages* the address
  before `/v1`), what it speaks (*OpenAI Chat Completions*, *OpenAI Responses*, *Anthropic Messages*), an API key
  (optional; the key itself or the name of an environment variable that holds it) and its models — discovered from
  the server each time omp starts, or listed by id. **Test connection** asks the server for its models with the key
  before anything is saved and says what it found or what went wrong (a server that answers only under `/v1` is
  pointed out); with *List them* and no ids yet, the ids it found fill the list. Your providers are listed under the
  local servers with **Edit** and **Remove** (it asks first); one with settings the form doesn't cover (headers,
  per-model limits…) is listed with a note to edit `models.yml` itself, and can still be removed. After each change
  the shared engine restarts once all chats and sign-ins are idle; the new models then appear in the model chip.
  A `models.yml` that doesn't parse is shown with the error and left unchanged.
  Then the providers you sign in to, each with **Sign in** or *Signed in*; the sign-in link and any code appear above
  the message box (**Open** shows the page in the chat's **Browser**, **Open in default browser** in your own). The sign-out button beside *Signed in* (its tooltip: *Sign out of …*) lists the accounts omp stored
  for that provider (the one in use marked *In use*); **Sign out** on one makes omp forget it. A provider signed in
  only by an API key in the environment or omp's settings has nothing stored to remove: the panel says so. The list
  appears while omp is running. API keys stay in omp's configuration: a key typed for a custom provider goes into
  `models.yml` (readable only by your user when the app creates it), never into the app's own files.
  Below it, *Plan usage* shows every signed-in account whose provider reports limits (subscription windows, spend),
  as meters with the share used and the time to reset, and the account's saved resets with **Use a saved reset**
  (see *Context and usage*), with a refresh button. Hidden when no provider reports usage.
- **Advanced** — *Agent engine*: the shared engine managed by Ombra. Leave **Command** empty for the managed
  engine. A developer override must launch a compatible shared-engine host (for Bun, pass the owned `engine/host.ts`
  in *Host arguments*), not an ordinary omp CLI. *Profile* (`OMP_PROFILE`), **Save and restart engine**,
  and **Set up engine**, **Update engine** or **Repair engine**. These fields apply only after Save and restart
  engine, once all chats and sign-ins are idle. Until then the page says *Unsaved changes* (with **Discard**),
  and the edits stay when you leave
  Settings and come back. *Network*: corporate network certificates and a connection check (next section); *Network
  privacy*: which providers omp may contact, and strict network privacy (see [Network privacy](#network-privacy)).

## Corporate network certificates

On a network that inspects HTTPS (a company proxy or security product re-signs traffic with its own certificate
authority), omp fails with `SELF_SIGNED_CERT_IN_CHAIN` or *unable to get local issuer certificate*: Bun, the runtime
omp runs on, does not trust that authority by default. **Settings → Advanced → Network → Set up corporate network
certificates…** fixes this for omp only when you ask; nothing runs at start and nothing changes before you press
**Enable** on the card that explains it.

- **macOS certificates** (macOS, the default there): omp trusts the certificate authorities macOS trusts — your
  Keychain with its trust settings, evaluated by macOS (the app reads and copies nothing). Every omp the app starts
  gets `NODE_USE_SYSTEM_CA=1`.
- **A certificate file** (every platform; the only way on Windows and Linux): choose your organisation's CA
  certificate (`.pem`, `.crt` or `.cer`, from IT). The app checks it first — certificate authorities only
  (`CA:true` with `keyCertSign`), valid now, no private key in the file — and shows how many it holds and until when
  they are valid. It keeps its own copy, readable only by you, in the `network-trust` folder next to the GUI settings
  file, and every omp it starts gets `NODE_EXTRA_CA_CERTS` pointing at that copy. Your file is not changed.

What it covers: omp's HTTPS connections (model providers, web tools) and the Bun and Node.js programs omp starts, as
they inherit its environment — every chat, sibling chats, restarts and omp terminal tabs opened from then on.
Certificate checks stay on: a server signed by an authority nobody trusts is still refused. What it does not change:
the system's trust store, VPN, proxies and routes; the built-in browser; and tools with their own network stack
(curl, git, Python and others), which need their own settings. Open chats restart omp to use it: idle ones at once,
a working one when its run ends (the page says how many are waiting); an omp terminal tab already open keeps the
setting it started with.

Your own settings win: when you set `NODE_USE_SYSTEM_CA`, `NODE_EXTRA_CA_CERTS` or a CA flag in `NODE_OPTIONS`
(`--use-system-ca`, `--use-openssl-ca`, `--use-bundled-ca`) yourself — in the environment the app starts with or in
the GUI settings' `environment` — the app adds nothing for that variable and the page shows what is set. If you
already use your own `NODE_EXTRA_CA_CERTS` file, the file mode is refused rather than replacing or merging it: add
your organisation's certificates to that file instead.

**Turn off** removes the setting and deletes only the copy the app made; omp restarts without it. The GUI settings
file keeps only the mode (`corporateTrust`) and the copy's location; diagnostics include the mode only.

**Connection check** (same card): one request without a key — **Network request to api.anthropic.com without a
key**, the same for api.openai.com, and for the current model's provider when omp lists its address — made by omp's
own runtime with the same settings as omp. It says whether HTTPS works (any answer from the server, 401 included:
no key was sent, so it does not show the model works), or why not: an untrusted certificate (with its code), a name
that does not resolve, a refused or cut connection, a proxy asking for sign-in (407), or no answer within 10 seconds.
It works only when omp runs on the Bun runtime (the one the app installs, or your own `bun` command).

## Network privacy

**omp contacts only the model providers you added.** On its own, omp 18.8.0 asks every provider it knows for its
model list — signed in or not — and probes local model servers (Ollama, llama.cpp, LM Studio). Each time the app
starts omp (every chat, restarts, omp terminal tabs), it first asks omp's own runtime which providers you added — a
sign-in, an API key in the environment or omp's settings, or an entry in `models.yml` — and starts omp with every
other provider in `disabledProviders`, passed with omp's `--config` option. Your `config.yml` is not changed, and
your own `disabledProviders` stay in effect. Local model servers count as providers: add one under **Settings → Model
providers → Local and custom** (or in `models.yml`) to use it. A provider in `models.yml` — a custom one too — is
added, and with strict network privacy its address is allowed. omp's built-ins that fetch no model list — web search
engines, on-device models — stay on. Signing in to a provider under **Settings → Model providers** adds it, and
signing out of its last account
removes it; open chats restart omp so the change applies. If the check fails, omp is not started and the start card
says why. With your own omp command (**Settings → Advanced → omp runtime**, not the Bun runtime) the check cannot
run: omp starts unchanged, and **Settings → Advanced → Network privacy** says so.

**Known limitation:** omp downloads its public model catalog from `catalog.stencil.so` (a copy of models.dev) in
the background. The request carries no keys and no chat content, and omp has no setting that turns it off (reported
upstream: `docs/upstream/omp-discovery-opt-in.md`). Strict network privacy blocks it.

**Strict network privacy** (off by default; the **Settings → Advanced → Network privacy → Strict network privacy** switch) sends omp and the
tools it runs through a filter in the app: every omp gets `PI_PROXY`, `HTTPS_PROXY`, `HTTP_PROXY` and `ALL_PROXY`
pointing at it (per-provider `PI_PROXY_<PROVIDER>` variables are removed), and it lets through only:

- the API and sign-in addresses of the providers you added (listed on the page);
- while you sign in to a provider from **Model providers**, that provider's addresses;
- the addresses you allow (`pypi.org`, or `*.example.com` for its subdomains).

Everything else is refused and listed under **Refused recently**, with **Allow** next to it — for example the
package index a connector starts from, or a site a web tool fetches. Allowing an address applies to the next
connection; no restart. Your own proxy (a company one in `HTTPS_PROXY` / `HTTP_PROXY` / `ALL_PROXY`, with its
`NO_PROXY`) is still used behind the filter. What it does not cover: programs that ignore proxy settings (an `ssh`
connection, for example), and addresses on this computer or in private network ranges (10.x, 172.16–31.x,
192.168.x), which omp always reaches directly. Turning it on or off restarts omp in open chats; omp terminal tabs
already open keep the setting they started with.

## Plugins, MCP servers and skills

**Plugins** in the left rail (or **+ → Plugins** / **+ → MCP servers** in the message box, or **Settings → Plugins**)
opens one page for everything that adds abilities to omp. At its top, **Browse catalog** and **Add ▾**
(*Plugin…*, *MCP server…*, *Skill folder…*); under them three tabs with counts — **Plugins**, **MCP**, **Skills** —
and a search for the current tab with a refresh button. Each tab is one list: a row shows the item's icon, name, one
line of what it does, where it comes from (*Personal* — yours, for every project; *Project* — the project's own
files; or the file name for a server from a shared `mcp.json`), its switch and a chevron to its own page. Project
items in a folder you haven't trusted say *Requires folder trust*, and what omp left out of such a folder gets rows
of its own. After a change that omp applies only when it starts, the page offers **Restart omp** (after the reply,
if one is running).

**Plugins** work through omp's `omp plugin` command line.

- **Add → Plugin…** takes an npm package, a git URL, a local folder or `name@marketplace`; omp's own error is
  shown if it fails.
- A plugin's page lists what it adds (skills, commands, agents, MCP servers, tools, hooks), its source, version,
  scope and folder, and says what it can run on this computer. It has **Update** (when the marketplace has a newer
  version), **Uninstall…** (asks first) and, for an npm plugin with settings, its settings with **Save**.
- After a plugin change omp reloads skills and commands at once; its tools, hooks and MCP servers load when omp
  restarts.

**The catalog** (**Browse catalog**) lists the plugins of every plugin source in sections: *Popular* when a
source's catalog marks some as featured, then the catalog's categories, then *Other* for plugins without one. **+**
installs a plugin; **⋯** on an installed one opens its page. Below are MCP servers from Smithery, a public registry:
search it, and **+** on a result shows the command that adds it in omp's own terminal (`/mcp smithery-login` signs in
to Smithery there). **Plugin sources** at the bottom lists the marketplaces the catalog reads — add one by GitHub
`owner/repo` (Anthropic's is `anthropics/claude-plugins-official`), git URL, catalog URL or folder; **⋯** on a
source updates its catalog or removes it — and **Plugin updates** sets whether omp checks for (or installs) plugin
updates when it starts. Skills have no catalog: they come with plugins or from your own folders.

**MCP servers** are the ones omp loads from its own files — yours (every project) and the project's
`.omp/mcp.json` — and from the project folder's `mcp.json` / `.mcp.json` (shared with Claude Code and other tools:
only their switch works here; change them in the file). A row says whether this session has the server's tools
(*Connected · N tools*) — omp connects to servers only when it starts. Recognized credential formats are masked in
commands, addresses and marketplace sources (URL passwords, `--api-key` arguments); this changes display text only,
and it is not a guarantee that arbitrary secret formats are recognized. The edit form still shows the
configuration you are editing.

- The **switch** turns a server on or off (omp's `/mcp enable|disable`). A server's page shows how omp starts or
  reaches it, the names of its environment variables or headers (never their values), and has **Test connection**
  (connects once and lists its tools, or shows omp's reason it failed), **Edit…** (command, arguments,
  environment, URL or headers; other fields are kept) and **Remove…** (asks first). A server that connected but
  offers no tools shows *Connected · 0 tools* there with a warning: omp can't use it.
- **Add → MCP server…**: a local command (stdio: command, one argument per line, `NAME=value` variables) or a
  remote server (HTTP or SSE: URL, an optional bearer token, other headers), for all projects or only this one. omp
  checks and saves it (`/mcp add`).
- A server that uses OAuth shows its sign-in on its row: *Signed in*, *Signed out* or *Sign-in expired*. **Sign in**
  (also offered when a remote server refused the test with 401) asks omp to sign in: the server's sign-in page opens
  in Ombra's **Sign in** window (from the chat's connectors list: a tab of the chat's **Browser**) — only for a
  sign-in you started here (or with `/mcp reauth <name>` in the message box), and only an `https` page or one on this
  computer. The page hands the sign-in back to omp on this computer right there; the window (or tab) closes and the
  row says *Connected · N tools*, without a restart. Right-click **Sign in** for **Open in default browser** when the
  sign-in needs your passkeys or your company's single sign-on. **Sign in again** on the server's page signs in anew;
  **Sign out** there has omp forget the sign-in it keeps; **Cancel** stops a sign-in waiting on the browser. When the
  server refuses the sign-in (for example a client registration it does not allow), the row shows omp's reason. A
  link omp opens on its own stays on the link card for you to open. When omp finds that a server needs you to sign in
  (again) — at start, or when a tool is refused — the MCP tab shows a card with **Sign in** for it until you sign in
  or dismiss it; the conversation gets a notice with **Show connectors**, which opens this list.
- A server omp could not connect shows *Couldn't connect* with omp's reason under the row, and **Reconnect**, which
  connects it again from its entry in `mcp.json` as it is now.
- **Reload config**, under the list, has omp read its `mcp.json` files again — servers you added, edited or removed
  there — and reconnect each one without restarting omp.
- **The chat's connectors**: the plug under the message box (beside the context ring) lists this chat's servers —
  *Connected · N tools*, *Sign-in needed*, *Sign-in expired*, *Couldn't connect* with omp's reason — with **Sign in**,
  **Reconnect**, **Reload config** and **Manage connectors** (this tab). When a server needs a sign-in or failed, the
  plug turns to the warning colour and says so (*Sign in to figma*), and stays that way after the notice and the card
  are dismissed, until you sign in. omp is asked for the list when you open it, not in the background.
- **Resources, prompts and options** asks the servers what they offer (a prompt can be used as a
  `/server:prompt` command); **Servers from project files** turns off the servers a project brings, for projects you
  don't trust.
- A server can ask you to fill in a form while omp works (MCP elicitation): a card in the conversation names the
  server and shows its fields (text, numbers, on/off, a choice, several choices). The app checks each field first
  (required, length, range, email/address/date format) and says what to fix; **Send** gives the answers to the
  server, **Decline** tells it no, × (Esc) closes the card without an answer. The answers go only to omp: they are
  not kept in the conversation or the app's logs.
- **Edit…**, and the variables and headers of a new server, are written into omp's MCP config file (`mcp.json`) by
  the app itself — the only omp file it changes directly; everything else goes through omp's commands. While omp is
  not running the tab offers **Start omp**.
- Servers set up in other tools' settings (Claude Code, Cursor, VS Code…) are loaded by omp too; they are not
  listed, but their tools are named at the end of the list.

**Skills** are omp's skills. The switch turns one off (omp's `disabledExtensions`); a skill's page shows its
command and folder, **Use in a message** starts a message with `/skill:<name>`, and **Share with team…** shares
it. **Where omp finds skills**, under the list, turns skills or skill commands off altogether and chooses where omp
looks (omp's, Claude Code's, `.agents`, Codex) and extra folders (**Add → Skill folder…**). Skill settings apply
when omp restarts.

## Computer use, Git and worktrees, SSH hosts

These settings are omp's own: the pages change them through omp (its commands and `omp config`), and the few that
omp reads only when it starts say so and offer **Restart omp**.

- **Computer use** — *Allow omp to use this computer* lets omp take screenshots, move the mouse, click, type and
  read controls, from its eval tool. It is saved for new sessions (`computer.enabled`) and switched on or off in the
  running one at once (`/computer on|off`); the badge shows whether it is really on in this session. While it is
  on, omp can act in any app or browser tab you are signed in to: stay close and turn it off when you are done.
  *Permission for computer actions*: **Default** follows the permission mode (screenshots run, clicks and typing
  ask, except with *Bypass permissions*), **Ask** asks every time, **Allow** never asks, **Deny** blocks it.
  *Screenshots* sets the display and the largest size. *What omp needs* checks this computer: omp's eval tool;
  on Linux an X11 display with the XTEST and RandR extensions (on Wayland omp can't take screenshots); on macOS
  the Screen Recording and Accessibility permissions for Ombra (the buttons open the right System Settings pane);
  Windows needs no permission.
- **Git and worktrees** — the project's branch, uncommitted changes, upstream and remote. *GitHub*: omp works with
  GitHub only through the GitHub CLI (`gh`); **Sign in…** runs `gh auth login` in the terminal panel, and *Let omp
  use GitHub* adds omp's github tool. *Worktrees* lists the repository's worktrees: **Open** starts a new session
  in one, **Remove** deletes a clean one (its branch stays; uncommitted changes or any open conversation using that
  folder or a subfolder prevent removal). Removal waits until all open conversations are idle and their input
  queues are empty. While Git is removing a worktree, new input and commands are refused without clearing the
  draft, and opening or moving a conversation into that worktree is blocked. Cleaning up a missing worktree
  removes only its own Git registration, not other missing worktrees.
  **New worktree…** runs omp's `/wt <branch>`: omp makes the branch from the current commit in a new folder,
  copies your uncommitted changes there and moves this conversation into it (the window follows). *Worktree
  options* and *Subagents* (each subagent in its own copy of the project) are omp's `worktree.*` and
  `task.isolation.*` settings. omp has no separate "environments": a session works in a folder (the project or a
  worktree), and SSH hosts are the other machines it reaches.
- **Branch in the header** — next to the project name: the branch (and "· worktree" in a worktree), hidden outside
  git. Its menu copies the name, starts a new worktree from here, or opens these settings.
- **The bell (teammates' pushes)** — the bell in the window's header lists what is new for you: per repository of
  an open chat, the commits a teammate pushed to your branch's upstream that you don't have (*main: 3 new commits
  from Bob, Alice — files: …*), and, while you are on a feature branch, new commits on the remote's default branch;
  under them the team Inbox's waiting items (Accept / Decline / Open Inbox). The badge counts them. Ombra runs
  `git fetch --quiet` (never a merge, with your own git credentials, never asking for a password) every 5 minutes,
  when the window comes to the front and after the agent finishes a turn — for folders you trusted (Workspace
  trust); an untrusted folder is fetched only when you press **Check now** in the bell. The folder's own git
  configuration can't run anything: an ssh command, credential helper, upload-pack, filter or merge driver it sets
  is replaced by your own (global) one or turned off. *Settings › General › Watch git remotes* turns it off; with
  strict network privacy on nothing is fetched. Each repository says how safe an update is:
  - nothing local → **Update** (`git pull --ff-only`);
  - uncommitted changes in other files than the incoming ones → **Update (keep my changes)**: the same
    fast-forward; git leaves your changes where they are and checks every file before writing any, so if one of
    your changes (or an untracked file) would be touched, nothing changes at all;
  - the incoming commits touch files you changed (uncommitted or unpushed), or you have unpushed commits → nothing
    is updated automatically: the files changed on both sides are listed, **Ask the agent to update** puts a careful
    request in the message box (fetch, rebase or merge keeping both sides, resolve conflicts, run the tests).
  **Show incoming changes** opens the incoming commits and their diff in the Files viewer. While the agent is working
  in that repository (a turn runs or input waits), **Update** is queued: the card says *Will update when the agent's
  turn ends* by a spinner, the button stays disabled, **Cancel** drops it, and the update runs when the turn ends. A
  long turn (an agent waiting for its subagents) need not be waited out: **Update now** is offered while the
  incoming commits change none of the files the turn changed or opened in the chat (its reads, edits and writes) —
  your uncommitted changes, a subagent's included, already keep it a fast-forward that touches none of them; both
  are read again when you press it. Otherwise the card names the files and the update stays queued. While an update
  runs, nothing new starts there (Send waits, the hint over the message box says why); a failed one says why in the
  card. When the remote refuses access the card says what to do instead of git's words: *GitHub says owner/repo was
  not found* (what a private repository looks like to an account without access, or a renamed or deleted one) — sign
  in with an account that has access (`gh auth login` in Terminal) or check the address (`git remote -v`); a sign-in
  git has no saved credentials for — `gh auth login`, then `gh auth setup-git` (another host: sign in once in
  Terminal); an SSH key or host key ssh refused — `ssh-add -l`, `ssh -T`. git gets the app's environment: the login
  shell's PATH (on macOS with Homebrew's folders added, for credential helpers) and `SSH_AUTH_SOCK`. Typing a
  message in a chat whose branch is behind shows a hint over the message box — *Branch main is 3 commits behind
  origin/main — update first?* — with **Review update** (opens the bell); it never blocks sending.
- **SSH hosts** — the machines omp's ssh tool can run commands on (keys only, no passwords), saved in omp's
  `ssh.json` for all projects or for this project. **Add host…** and **Remove** use omp's `/ssh add` and
  `/ssh remove`; **Test** connects the way omp does — the system `ssh` with your SSH config (its `HostName`, `Port`,
  `ProxyJump`, `ProxyCommand` apply), no password prompt, a new host's key added to `known_hosts`, a changed one
  refused — over a fresh connection, and runs `exit`. ssh gets 8 s for the TCP connection and another 8 s for the
  server's SSH greeting. A failed test lists the stages in order — address, TCP connection, the server's SSH
  greeting, protocol negotiation, host key check, sign-in — each marked passed (✓), failed (✗) or not reached, the
  route (direct with its port, or through a jump host / proxy command, whose own connection the test can't see), the
  check left to do outside the app when the cause isn't visible from this computer, ssh's last line, and a code such
  as `banner-timeout` without addresses or names. Example: *TCP connection established, but the server's SSH
  greeting did not arrive within 8 s* — ssh reached the server's port and sent its greeting, and no answer came;
  what to look at next is sshd on the server (the hosting provider's console) and the same test from another
  network. The test runs only when you press **Test**; ssh's log is read for the stages and not kept or put into
  the diagnostics file. While omp isn't running the page shows the saved hosts only.

## Pets

ONI — a small oni in a rigid mask, with red glowing eyes and two spirit flames, a red one and a violet one — sits on
the top edge of the message box, at its right end, and follows what omp does. Idle it breathes and its spirits drift
beside it; while the model thinks it ponders with bouncing dots over its head; while a tool runs it works at a laptop
inside a turning seal of light; when omp asks you something it raises a hand with a **?**; when a run finishes it
sways with joy (feet planted) for a couple of seconds; when a tool fails it winces with a **!!** for 2.4 s and goes
back to work, and when omp itself stops it stays alarmed, with flickering interference behind it, until omp is back.
A run you stop puts it back to idle without celebrating. After three quiet minutes it curls up and sleeps with rising
z's; typing, hovering or clicking it wakes it. Short changes do not make it flicker: a mood shows once it has held for
a moment, and stays at least 0.7 s. Every change goes through its entry animation, and a change in the middle of one
turns back smoothly from where it is. On the message box it has its own strip above the box, so it never covers the
conversation; in a window less than 520 px tall it steps aside and gives that strip back. It moves only while the
window is active and it is on screen: minimised, in the background, hidden or asleep for half a minute, it shows a
still picture and draws nothing more; back in view it carries on where it is now. A question or a fault nobody has
answered for two minutes, and the desktop pet behind other apps, keep moving at a calmer frame rate. With **Reduce
motion** (Settings → Pets, on by default when the system asks to reduce motion) it shows every pose still.

**Click the pet** to message omp from it: a small message box opens above it with the keyboard in it, and the pet
says what omp is doing (or a word of its own). **Enter** sends the text to the conversation just as the message box's
Send does (a new prompt, or queued for after the run while one goes on), without touching what is in the message box;
**Esc**, a click on the pet or a click elsewhere closes it (the text stays for next time) and the keyboard goes back
where it was.

**Drag the pet** (press and move it 6 points; a press that barely moves is a click) to carry it anywhere on your
screens, like a desktop pet: it lifts off the message box into a small window of its own that floats over every app,
hanging from the pointer by the scruff, its body swinging with your moves and its spirits circling it. Let go and it
keeps its swing, drops onto the spot under it with a puff of dust over its boots and goes back to whatever omp is
doing now (a question asked while you held it shows once it lands). It stays where you put it (also after a restart),
on any monitor. It keeps clear of the menu bar, the Dock and the taskbar, and crosses from one monitor to another
where they touch; unplug the monitor it is on and it comes over to one that is left (plug it back and it returns).
Only the pet itself takes clicks: the screen around it works as usual. Out there it stays while the settings page is
open and while the app's window is minimised; behind other apps it moves only while omp works, asks something or has
news, and otherwise rests on a still picture. On macOS it shows on every Space and over full-screen apps, and Mission
Control leaves it where it is. Clicking or dragging it makes the app the active one, as a click on any of its windows
does. Its speech bubble and message box open beside it there too; while it is off the message box, the box gives its
strip back. Put it down close to its place on the message box and it sits there again, or use **Reset position** in
Settings → Pets. If the pointer is taken away mid-carry (another window grabs it), the pet is put down where it is;
that never counts as a click.

**Roams: Inside the window** (Settings → Pets) keeps it in the app's window instead: dragged, it stays where you drop
it inside the window, also when the window is resized. Switching between the two puts the pet back on the message box.

**Settings → Pets** turns the pet on or off (on by default), sets its size (64, 80 — the default — or 96 points
tall), **Reduce motion**, and where it roams (the whole desktop, the default, or inside the window). **What it does**
shows a preview of ONI in each case: idle, thinking, working, waiting, done, a failed tool, omp stopped, a stopped run,
asleep. The pet belongs to the GUI: its settings are saved in its settings file (`pet`), and omp knows nothing about
it. Its drawings are decoded once, at the size they are shown, and let go when no pet is on screen.

## Updates

Ombra updates itself from this project's GitHub releases. A few seconds after every start, and then once a day,
it asks the release feed whether a newer version exists and, when one does, downloads and gets it ready by itself:
the bottom of the sidebar then says **Restart to update to …**, and the new version goes in when you restart or
simply quit the app. **Settings → Updates → Update automatically** turns this off; then a newer version only shows
as **Update available** and nothing is downloaded until you choose to (**Check for updates** asks at once either
way). Until the first release of this client is published, the check says *No release is published yet.*

- **Download and install** (done by itself with automatic updates) downloads this platform's package and checks it
  before anything changes: the release's
  list of packages (`update.json`) must carry a valid signature from the project's release key, which is built into
  the app, and the package must match the size and SHA-256 that list gives. The package is then unpacked next to
  the app and started once to check it runs on this computer.
- **Restart now** (or simply quitting Ombra) installs it: once the app has closed, the new version takes the old
  one's place — same folder, so shortcuts keep working — and, with Restart now, starts. The version before is kept
  next to the app as `.<folder name>.previous` until the next update (to go back, swap the folders by hand). If the
  swap fails, restoration of the old version is attempted. On Windows, a failed rollback reports the backup
  location instead of claiming restoration. Settings → Updates reports a failed update at the next start.
  Windows uses a separate copy of the app to apply updates, not PowerShell; transient access/sharing locks
  are retried for up to 60 seconds per filesystem operation.
- **omp moves with the app.** Each version of the app runs one pinned omp. When an update brings a newer omp, the
  Updates page says so; the new engine comes inside the update, so it runs after the restart with nothing more to
  download. An app without its engine (see [Install](#install)) installs it by itself after the restart (about
  550 MB, verified like the first install) while omp from the previous version keeps working, then switches over
  once no run is active. If that install fails, the old omp keeps working and **Settings → Advanced** offers to try
  again.
- Settings, sessions and omp's configuration live elsewhere and are never touched by an update.

The app downloads the package to your Downloads folder instead (**Download and verify**, then replace the app
yourself) when it cannot replace itself: when this user may not change the folder it is in (for example Program
Files), or when it runs from a development build. From a terminal, `Ombra --update` reports whether an update
exists and `Ombra --update --yes` installs it (`--restart` starts the new version afterwards); `Ombra --version`
prints the version. (`updateFeed` in the settings file points the check at another feed; its manifest must still
be signed with the release key.) The newest published release is offered, alpha versions included.

## Diagnostics for a bug report

**Settings → Diagnostics → Save diagnostics…** writes a zip where you choose: app and OS versions, the GUI
settings with every environment value and API key removed, the omp runtime record and its install log, omp's
state and last error, the names (not the contents) of recent events, and the app's own errors (`client.log`).
Known credential formats are masked everywhere and your home folder is shown as `~`. No conversation text is
included and nothing is uploaded. The page also shows where the settings file is, and *RPC events* (F12) shows the
raw events omp sends. When something goes wrong inside the app itself, it carries on and says so once under the
message box; the details go to `client.log` (on macOS in `~/Library/Logs/Ombra`, elsewhere in the local app data
folder under `Ombra/logs`; two files of up to 1 MB each).

**When the window lags**, start the app from a terminal with `OMBRA_PERF=1` — on macOS
`OMBRA_PERF=1 "/Applications/Ombra.app/Contents/MacOS/Ombra"`, elsewhere `OMBRA_PERF=1` before the app's
executable. The window then shows Avalonia's frame rate and its layout and render times in the top-left corner, and
the terminal gets a line for every update of the window from omp's state that took 4 ms or more (with the part that
took the time and how many messages it added or changed), and once a second how many such updates ran and how often
the expensive work (rebuilding a formatted reply, refreshing the Files pane…) happened. Paste those lines into the bug
report. Without the variable none of this runs.

## Where things are kept

| What | Windows | macOS | Linux |
|---|---|---|---|
| GUI settings (`ombra.local.json`) | `%APPDATA%\Ombra` | `~/Library/Application Support/Ombra` | `~/.config/Ombra` |
| Providers turned off for omp (`network-privacy/providers-*.yml`, one per project, rewritten at every start) | `%APPDATA%\Ombra\network-privacy` | `~/Library/Application Support/Ombra/network-privacy` | `~/.config/Ombra/network-privacy` |
| The engine the app carries (read-only) | `engine\` beside `Ombra.exe` | `Ombra.app/Contents/Resources/engine` | `engine/` beside `Ombra` |
| Engine set up or repaired from the network; Bun's transpiler cache for the app's own engine (`.transpiler-cache`) | `%LOCALAPPDATA%\Ombra\runtimes` | `~/Library/Application Support/Ombra/runtimes` | `~/.local/share/Ombra/runtimes` |
| Dictation model | `%LOCALAPPDATA%\Ombra\speech-models` | `~/Library/Application Support/Ombra/speech-models` | `~/.local/share/Ombra/speech-models` |
| Chats without a project (one empty folder each, deleted when the chat is closed with nothing in it) | `%LOCALAPPDATA%\Ombra\chats` | `~/Library/Application Support/Ombra/chats` | `~/.local/share/Ombra/chats` |
| The app's own errors (`client.log`) | `%LOCALAPPDATA%\Ombra\logs` | `~/Library/Logs/Ombra` | `~/.local/share/Ombra/logs` |
| Team account and team data (`account.json`, `progress.json`, a folder per team server with its encrypted `state.db`) | `%LOCALAPPDATA%\Ombra\teams` | `~/Library/Application Support/Ombra/teams` | `~/.local/share/Ombra/teams` |
| Team keys (this device only) | DPAPI-protected files in `%LOCALAPPDATA%\Ombra\teams\keys` | the Keychain (service `app.ombra.teams`) | the Secret Service keyring (libsecret) |
| Nightly runtime and its profile (macOS) | — | `~/Library/Application Support/Ombra/runtimes/nightly`; profile `nightly-profile` next to the GUI settings | — |
| omp's own data (sessions, sign-in, settings) | omp's folder (`~/.omp`), managed by omp | same | same |

On Linux `XDG_CONFIG_HOME` and `XDG_DATA_HOME` move the GUI's folders. `--config <file>` or `OMBRA_CONFIG` use
another settings file; `OMBRA_RUNTIME_DIR`, `OMBRA_STT_DIR`, `OMBRA_LOG_DIR`, `OMBRA_CHATS_DIR` and `OMBRA_TEAMS_DIR`
move the runtime, the speech model, the error log, the chats' folders and the team data; `OMBRA_NIGHTLY_PROFILE`
moves Nightly's profile.

Draft recovery uses the same settings path with its extension replaced by `.drafts.json`
(for example, `ombra.local.drafts.json`). It contains local plaintext drafts and image bytes; Unix files are created
owner-readable/writable only. This is not encrypted storage: sensitive text you type into an ordinary composer is
part of its draft. Corrupt or unsupported journals are left intact and reported, not silently overwritten.
Deleting a session file and replacing its recovery journal are separate operations, not an atomic or forensic
erase. A crash or journal-write failure between them can leave older recovery data on disk; write failures are
reported. Ordinary filesystem backups may also retain previously saved input.
Editor recovery is stored beside the settings file in `<settings-path>.editor-recovery`. It is local plaintext,
not team E2EE or encrypted gallery storage. Closing a file keeps its unsaved buffer; after an external change,
save a copy or explicitly discard and reload rather than overwriting the disk version.
Session search keeps the searchable text of saved sessions (up to 64K characters each) beside the settings file in
`<settings-path>.session-search.json`, owner-readable/writable only, scoped to that profile and session folder. A
session is read again only when its size or write time changes; a damaged file is rebuilt from the sessions.
Only one Ombra window can use a configuration at a time. A second launch with the same settings file (including a
symlinked path) shows "Ombra is already running" and changes nothing; a different `--config` opens independently.
The lock is held by the operating system, so it is released even after a crash; the `.drafts.json.lock` file next
to the journal is expected and should not be deleted. This coordinates new Ombra versions only: close older
versions before switching. A chat another Ombra window or omp in a terminal has open is shown read-only with
*Open in … (pid N)* above the message box; **Open here** asks the other one to let go and opens the chat here. A
reply running in that other Ombra window is stopped (it says so); omp in a terminal saves what it wrote and goes on
in a new chat file. One that does not answer within 30 seconds keeps the chat. If another window takes a chat over,
this one turns read-only (*This chat was opened elsewhere*); what you typed stays in the message box.

**Formerly OMP GUI.** Ombra was called OMP GUI. Its first start moves the old `OmpGui` folders above to `Ombra`
(the settings file becomes `ombra.local.json`) and leaves a link named `OmpGui` in their place, because omp's
sessions remember the chats' folders by their old path; nothing moves when an `Ombra` folder exists already or
`OMBRA_CONFIG` is set. On macOS an *OMP GUI.app* that updated itself to Ombra renames itself to *Ombra.app* at its
first start, and the new bundle id makes macOS ask once more for the microphone and other permissions.

Known settings fields with invalid JSON types use their defaults; other valid fields still load. The warning names
the rejected fields, not their values. Invalid and unknown fields remain in the file until explicitly changed.
Malformed JSON is never overwritten. If the settings directory cannot be written, Settings warns that changes
are kept for this run only. Restore write permissions and change a setting to retry saving. Pending local changes
win for fields you changed; valid external changes to other fields are retained.

The GUI writes its own settings file (atomically, keeping a `.bak` of a version you edited by hand), the folders
above, and what you save (update packages, diagnostics). It reads omp's session folder to list sessions. Of omp's
own files it changes only the MCP config (editing a connector, or the variables and headers of a new one), its
list of pinned sessions (`session-pins.json`, when you pin or unpin from the sidebar, the way omp's `/pin` does) and
`models.yml` in omp's agent folder (`~/.omp/agent`, or the profile's own; adding, editing or removing a local or
custom provider under **Settings → Model providers**: only that provider's lines change, the rest of the file and
its comments stay, and the file is replaced atomically);
everything else goes through omp.

## Uninstall

Delete the app folder (or *Ombra.app*) and, after an update, the `.<folder name>.previous` folder next to it;
then — if you want — the GUI's folders from the table above, the Linux menu entry
(`~/.local/share/applications/ombra.desktop`) and update packages you downloaded by hand. On macOS remove
Ombra from *System Settings → Privacy & Security* (Microphone, Screen Recording, Accessibility) if you granted
them. omp's own data is left alone; remove `~/.omp` only if you want to remove omp's sessions and sign-ins too.
Team keys stay in the system key store (on macOS the Keychain items of the service `app.ombra.teams`): sign out under
**Settings → Account**, then delete them in Keychain Access (or your keyring) if you want them gone too.

## Troubleshooting

- **omp didn't start** — the card says so; **Show details** has the error. Check the omp command in **Settings →
  Advanced**, then **Try again** (after omp stops in the middle of a session the button reads **Restart omp**; both
  continue the same session).
- **SELF_SIGNED_CERT_IN_CHAIN / unable to get local issuer certificate** — your network inspects HTTPS: see
  [Corporate network certificates](#corporate-network-certificates).
- **Dictation: the audio library needs libjack.so.0** (Linux) — no system recorder was found either. Install
  PulseAudio's or PipeWire's recorder (`pulseaudio-utils` / `pipewire-bin`) or `alsa-utils`, or the JACK client
  library (`libjack-jackd2-0` or `libjack0` on Debian/Ubuntu, `jack-audio-connection-kit` on Fedora).
- **Dictation: the microphone delivered no audio / recorded only silence** — no samples arrived, or every recorded
  sample was zero. The dictation bar keeps the error visible and leaves your draft untouched. Check the system's
  default input, microphone permission and mute settings, then use **Try again**. Quiet nonzero audio is still
  transcribed; the waveform's display floor is not used to reject speech. This check cannot diagnose a disconnected
  device that produces nonzero noise, and does not claim to detect whether speech is present.
- **The preview is empty** — the panel says which web engine is missing (WebKitGTK on Linux, WebView2 on
  Windows); **Open in browser** shows the page meanwhile.
- **The window looks wrong at a large text size** — the layout follows the OS scaling; report it with a
  screenshot.

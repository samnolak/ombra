# OMP GUI — user guide

OMP GUI is a desktop client for the original **omp** coding agent (oh-my-pi). The agent itself is always the
unmodified omp; the GUI starts it, talks to it over omp's own RPC and shows what it does. Nothing here replaces
or patches omp.

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

Each package is `omp-gui-<version>-<platform>` (`.tar.gz` on Linux, `.zip` on Windows and macOS) and unpacks into a
folder of the same name, with the licence notices and this guide. Every archive has a `.sha256` file next to it
(and a `.signing.txt` that says how it was signed). Check it before you install (`sha256sum -c
omp-gui-….tar.gz.sha256` on Linux, `shasum -a 256 -c …` on macOS, `Get-FileHash` on Windows).

The packages are **not signed** with a developer certificate (no signing certificate is available to this
project yet), so the OS warns on first start:

- **Windows** — extract the zip anywhere and run `OmpGui.exe`. SmartScreen: *More info → Run anyway*.
- **macOS** — unzip and move *OMP GUI.app* to Applications. The app is only ad-hoc signed and not notarized, so
  macOS blocks its first start. macOS 15 (Sequoia) and later: open the app once, close the warning, then
  *System Settings → Privacy & Security → Open Anyway* and confirm with *Open*. macOS 14 (Sonoma): Control-click the
  app → *Open* → *Open* (Sequoia removed this way). On any version you can instead run
  `xattr -dr com.apple.quarantine "/Applications/OMP GUI.app"`. After that it starts from its icon as usual. macOS
  asks for microphone access the first time you dictate.
- **Linux** — `tar -xzf omp-gui-….tar.gz` and run `./OmpGui` in the extracted folder. `./install-desktop-entry.sh`
  adds it to your application menu (only `~/.local/share/applications/omp-gui.desktop` is written; it points at the
  folder, so run it again if you move the folder).

## First start

1. **omp not found.** If omp is not on your PATH and you have not set one, the window says *Install omp to get
   started* and offers **Install omp 18.8.0**. The client downloads Bun 1.4.2 and omp 18.8.0 from the npm registry,
   checks them against the versions and hashes pinned in the app, and keeps them in the app's own folder (about
   1.3 GB on disk). Nothing global is changed. If you already have omp, choose **Use my own omp…** and set its
   command in **Settings → Advanced** instead; a command you set always wins over the installed one. Newer omp
   releases than 18.8.0 may exist on npm, but this client is verified only with 18.8.0 — another version may work or
   may miss features.
2. **No model provider.** omp needs a model. If it has none, the window says *Connect a model provider*.
   **Open omp setup** opens omp's own setup in the terminal beside the conversation, with the keyboard already in
   it: on *Set up your providers* choose **Sign in** (arrow keys, Enter), pick a provider and finish in the browser
   (or set an API key the way omp describes), then press **Try again**. API keys stay in omp's configuration; the GUI
   never stores them.
3. Pick a project folder — the folder chip under the message box (*Choose a folder*) or **Open folder…** in the
   project menu — and write your first message.

## Daily use

- **Sessions** — the sidebar lists omp's sessions of all your projects, grouped by project folder. Sessions are
  ordered by their last message (yours or the agent's), newest first, and groups by their newest session; opening a
  session does not reorder the list. Each row shows how long ago its last message was (*now*, *5m*, *3h*, *2d*, then
  the date), or a dot while omp is busy there: green and pulsing while it works, blue when it waits for your
  approval or answer, clay for a reply that came while the window was in the background. **Pinned** sessions are listed on their
  own at the top (pins are omp's own, so `/pin` and omp's session picker agree). **Search** filters by title or
  folder. Click a session to continue it; **New session** (Ctrl/⌘+N) starts a fresh one, and a project's **+** starts
  one in that project. Ctrl+Tab and Ctrl+Shift+Tab go to the next and previous session. Hover a session for **Pin**
  and **⋯** (or right-click it): **Rename** (opens it and edits its name in the header), **Pin / Unpin**, **Copy path**
  (the session file) and **Delete…**, after a confirmation; deleting the open session works like the session menu's
  Delete (a new session starts). The folder button at the top of the sidebar **adds a project folder** to the list;
  hover a project's header (or right-click it) to **remove it from the sidebar** (⊖) — only the list changes, the
  folder and its sessions stay on disk, **Undo** brings it back, and so does adding the folder again. Drag the
  sidebar's right edge to make it wider or narrower (220–480 px; double-click the edge for the default); Ctrl/⌘+B
  hides and shows it. Added and removed projects and the width are remembered in the client's settings file. The
  title opens the session menu (below).
- **Models and thinking** — in the message box: the *Thinking effort* chip sets omp's reasoning level (*Off*,
  *Minimal*, *Low*, *Medium*, *High*, *Extra high*, *Max* — each with a line on what it does; only the levels the
  model accepts; hidden for models that don't reason) and the model chip lists the models omp can use (type to
  search) with the model options below the list. The model menu opens above the message box, so what you are writing
  stays in view.
- **Permissions** — the mode chip under the message box says when omp asks before running tools: *Ask permissions*
  (edits and commands ask), *Accept edits* (edits run, commands ask; the default) or *Bypass permissions* (nothing
  asks). The mode colours the chip, the box's border while you type and the Send button: grey, violet, red. Shift+Tab
  in the message box steps through the modes; Bypass always asks for a second confirmation first. Changing the mode
  restarts omp on the same session: once you stop pressing Shift+Tab, and never in the middle of a run (it waits for
  the run to end). When omp asks, a card in the conversation says what it wants (*omp wants to run a command*) and
  shows the command or file, with **Allow** / **Deny** (keys 1 / 2 or Alt/⌥+A / Alt/⌥+D; 1–9 pick an answer to a
  question, omp's suggestion carries a *Recommended* badge); Esc closes the card without answering. omp offers only
  allow or deny for one call: there is no "don't ask again" — use the mode for that.
- **Message box** — Enter sends; Shift+Enter adds a line (*Settings → General → Send messages with* switches to
  Ctrl/⌘+Enter to send, Enter then adds a line). While omp is working, **Queue** (or Enter) adds a
  follow-up and **Steer** (or Alt/⌥+Enter) hands a message to the current run at its next step. Queued messages are
  listed in grey above the box until omp takes them: **×** takes one back, and ↑ in the empty box brings the last
  one back to edit (with its images). A long paste (over 800 characters or 3 lines) becomes a *Pasted text +N lines*
  chip instead of filling the box (hover it to see the start, × removes it); it is sent in full after what you typed.
  **+** opens *Attach images* (up to 3 per message; paste or drag & drop works too, other files are inserted as
  paths), *Commands and skills*, *Connectors*, *Plugins* and *Comment on the page*. Type `/` for commands: ↑/↓ or
  Tab/Shift+Tab choose, Tab or Enter completes, Esc closes; what only omp's terminal runs is listed last, under
  *Terminal only*. Before the first message the chips under the box also pick the project (a new session in another
  project, or **Open folder…**).
- **New session** — the page asks *What should we work on in ‹project›?*; the project opens the same project menu,
  and the starter prompts under it (*Explain how this project is organized*, *Find a bug and fix it*, *Add tests for
  the code I changed last*) fill the message box to edit and send. Notices from omp's start do not hide it.
- **Tool calls** — each is one line, as in Claude Code: a dot (grey and blinking while it runs, green when done, red
  when it failed), the tool's name and what it works on, and how long it took (from a tenth of a second). Under it,
  what it gave: *Read 120 lines*, *Found 12 files*, *Added 2 lines, removed 1 line*, or the first three lines of its
  output with **… +N lines** to see all of it; a running command shows its newest lines. Click the line to open the
  whole output; a file change shows as it happens. *Thinking…* counts the seconds while the model thinks, then
  reads *Thought for 4s*; click it for the thoughts.
- **Keyboard shortcuts** — Ctrl/⌘+/ (or `/hotkeys`) shows them all; Esc closes the sheet.
- **Stop** — the Stop button or Esc stops the current run; if omp does not stop, the button offers Force stop.
- **Terminal** — Ctrl+` (Ctrl on macOS too), the terminal button in the header (or **⋮ → Terminal**) opens a
  terminal beside the conversation (drag its edge to resize it; in a narrow window it covers the conversation until
  it is hidden), in the project folder, with the keyboard in it: **+ Shell** for a shell, **+ omp TUI** for omp's own
  terminal UI, for features omp offers only there (plan, goal, vibe…). With no terminal open the panel offers
  **New shell** and **omp TUI**.
- **Project menu** — click the project's name next to the session title: **Browse files** (the Files pane),
  **Show in Finder** / **Show in Explorer** / **Open in file manager**, **Open in VS Code** (or Cursor, Windsurf,
  Zed, Sublime Text — only the editors whose command is on your PATH), **Open terminal here**, **Copy path**, a new
  session in one of your recent projects, or **Open folder…**.
- **Files pane** — the project as a tree beside the conversation. Folders open on click; dependencies and build
  output (`node_modules`, `bin`, `obj`, `.venv`, `dist`…) and what `.gitignore` excludes are dimmed, and `.git` is
  never shown. **Go to file** finds a file by name anywhere in the project (letters in order work too: `fvm` finds
  `FilesViewModel.cs`); Enter opens the first match. In a git repository each changed file gets a mark — M
  modified, U new, A added, R renamed, ! conflict, D deleted (still listed, struck through) — and a folder with
  changes inside gets a dot. The segments above the tree: **All**, **Changed** (only in a git repository) and
  **This session** — the files omp edited or wrote in this conversation (marked ✦ in the tree). The pane follows the
  disk while it is open and after every tool omp runs. Click a file to read it below the list: line numbers and
  colours, Markdown rendered (the `<>` button shows its source), images shown; a very large file offers **Show the
  start**, a binary one to open it in an app. **@** puts `@path` in the message box — omp then reads the file with
  your message; the **⋯** menu opens it in an editor (those on your PATH) or the default app, reveals it in the file
  manager, or copies its path or relative path. Right-click a row to open it, add it to the message, open it in the
  default app, reveal it or copy its path. A file's path in the conversation (a tool's line, the header of a change)
  opens it here, at the changed line. Keys: Enter or Space opens, → / ← open or close a folder, Esc clears the
  search.
- **Preview** — the 🌐 button in the header (Ctrl/⌘+Shift+B) opens the web app you are building beside the
  conversation, with back, forward, reload, an address box and *open in your browser*. Until a page is open, the
  local addresses that appear in omp's tool output (dev servers it starts) are offered as chips. It uses the system's
  web engine: WebView2 on Windows, WebKit on macOS, WebKitGTK (`libwebkit2gtk-4.1`) on Linux; when the engine is
  missing the panel says what to install and offers **Open in browser**.
- **omp browses in the preview** — when omp opens a web page with its browser tool, the page opens here rather
  than in a hidden browser: the preview opens by itself and you watch each step; a pulsing **omp** pill in its
  toolbar shows while omp reads, clicks or types on the page. The preview shows one page: a second tab omp opens
  takes it over. omp reads pages as text (their elements and content); screenshots are not available in the
  preview, and only http and https pages open (for a local file, ask omp to serve it, e.g. `python3 -m http.server`).
  The terminal's omp uses the same preview. To give omp its own hidden browser back, run
  `omp config set browser.cmux false` (omp's setting for this kind of embedded browser) and restart omp.
- **Comments on the page** — **Select element** in the preview's toolbar (or **+ → Comment on the page**, or
  Ctrl/⌘+Shift+S) turns on the mode: point at an element (it is outlined), click it, write what to change and press
  **Comment** (or Enter). A numbered pin marks the element on the page and the comment becomes a chip in the message
  box (× removes it). The page's own links and buttons do nothing until you leave the mode (Esc, or Select element
  again). The comments go with your next message — with no text, Send asks omp to make the changes they ask for —
  each with what finds the element in the code: its selector, opening tag, text, position and size and — for
  React ≤ 18, Vue and Svelte dev builds — the source file. Comments on several pages can go together, up to 20 at a
  time.
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
    has run, its current tool, tool calls and tokens; open one to read its task, latest output and its own
    conversation. Finished ones stay listed for the session. Below them, omp's background jobs (commands left
    running, as omp's `/jobs` lists them), read again every few seconds while the pane is open.
- **Dictation** — the 🎤 button (or Ctrl/⌘+Shift+Space) records and transcribes on this computer. The first use asks before
  downloading the speech model (about 670 MB, verified against pinned hashes).
- **Notifications** — when the window is in the background and a run ends, omp stops or asks something, the title
  gets a ● mark and the OS is told: Windows flashes the taskbar button, macOS shows a Notification Center banner,
  Linux a desktop notification through `notify-send` if it is installed. **Settings → General → Notifications**
  turns that off.
- **Menus and tray** — on macOS the app menu has **Settings…** (⌘,) and the menu bar *File* (New Session, Open
  Folder…) and *View* (Sessions, Terminal, Events). Where the desktop has a tray or menu-bar area, an OMP GUI icon
  there offers **Show OMP GUI**, **New Session** and **Quit**.
- **In the conversation** — **Retry** on a failed reply. Point at a reply's end (*Worked for 12s · 3 tools*) for
  **Copy** (as Markdown); point at one of your messages for **Copy** and **Rewind to here** (a new session from
  before it, the message back in the box, like *Rewind…* in the session menu). The conversation
  follows a streaming reply while you are at (or near) its end; scroll up to read and it stays put, with a round
  **↓** button at the bottom right to jump back to the latest message. Sending a message, or opening another
  session, always goes to the end.

| Shortcut | Action |
|---|---|
| Enter / Shift+Enter | send / new line (Ctrl/⌘+Enter sends instead when chosen in Settings → General) |
| Alt+Enter (⌥Enter) | steer the running turn |
| Shift+Tab | next permission mode (Ask → Accept edits → Bypass, which asks to confirm) |
| ↑ (in the empty message box) | edit the last queued message |
| Ctrl+/ (⌘/) | keyboard shortcuts |
| Ctrl+Enter (⌘+Enter) | send |
| Ctrl+N (⌘+N) | new session |
| Ctrl+B (⌘+B) | show / hide the sidebar |
| Ctrl+Tab / Ctrl+Shift+Tab | next / previous session in the sidebar |
| Ctrl+, (⌘+,) | settings |
| Ctrl+` (Ctrl on macOS too) | terminal panel |
| Ctrl+Shift+P (⇧⌘P) | Plan pane |
| Ctrl+Shift+T (⇧⌘T) | Background tasks pane |
| Ctrl+Shift+F (⇧⌘F) | Files pane |
| Ctrl+Shift+B (⇧⌘B) | browser preview |
| Ctrl+Shift+S (⇧⌘S) | select an element in the preview to comment on |
| Ctrl+Shift+Space (⇧⌘Space) | dictation (Enter finishes, Esc cancels) |
| Ctrl+V (⌘V) | paste text, images or files (a long text becomes a chip) |
| 1 / 2, Alt+A / Alt+D (⌥A / ⌥D) | allow / deny on an approval card; 1–9 pick an answer |
| Esc | leave Settings, close the shortcut sheet, or a question card (when it has the focus), or stop the run |
| F12 | event log (also *Settings → Diagnostics → RPC events*) |

### Session menu

Click the session's title in the header. Most items run omp's own command — shown on the right of the item — and
report on a card above the message box:

- **Rename** (edit the title in place) · **Copy session ID** (what `omp --resume` takes) · **Copy session file
  path**.
- **Compact conversation…** — omp summarizes the conversation so far and goes on from the summary, freeing context;
  you can say what the summary should keep. The card shows it running (**Stop** cancels) and then how many tokens
  the context went from and to. **Hand off…** is the same with a written handoff document; both stay in this session.
- **Fresh provider session** — the next reply opens a new session with the model provider and sends the whole
  conversation again (useful when a provider's cached state went wrong); nothing changes on screen.
- **Retry the last turn** — runs the last turn again after it failed or was stopped.
- **Rewind…** — pick one of your messages: a new session goes on from before it and the message comes back into
  the box to change and send. The earlier session stays in the list.
- **Export as HTML** — one page with the whole conversation; **Open** or **Show in folder**.
- **Share a link…** — says first what happens: omp uploads an encrypted copy of the whole conversation (tool output
  included) to its share server (or a secret gist, per omp's settings), and anyone with the link can read it. After
  **Upload and copy link** the link is on the clipboard.
- **Workspace folders…** — the folders omp may read and edit in this session; **Add folder…**, or remove an added one.
- **Move to another folder…** — omp moves the conversation to that folder and works there from then on (your files
  do not move). **Pin session** keeps it at the top of the list (**Unpin session** undoes it). **Memory** shows what
  omp adds to the context from its memory, with **Update now** and **Clear memory…** (or says memory is off in omp's
  settings).
- **Delete session…** — asks first, then deletes the conversation's file and its artifacts and starts a new session.

### Context and usage

The ring with a percentage at the right end of the row under the message box shows how full the model's context
is; it turns orange from 70 % and red from 85 % (omp compacts the conversation on its own near the end, when
Auto-compact is on). Click it for the breakdown as bars (the parts omp's `/context` reports, the auto-compact buffer,
free room), this session's tokens (input, output, cache) and cost (when omp prices the model), **Compact now**,
**Measure again** and a link to omp's **Usage dashboard** across all sessions. When the model's provider reports plan
limits (for example a Claude or ChatGPT subscription: the 5-hour and weekly windows), *Plan usage* shows each as a
meter with the share used and when it resets ("resets in 2h 10m"); the bar turns orange from 80 % and red when the
limit is used up. **All providers** opens Settings → Model providers. Providers that report no usage show nothing.
While it is open it refreshes after each reply. The limits come from omp's own usage reports (`omp usage`, run in the
background: nothing is sent to the conversation); omp caches them, and the app asks again at most every 30 seconds,
after a reply ends, or when you click refresh.

### Model options

Under the model list: **Fast mode** (the provider's priority tier for this session: faster, costs more; omp says so
when the model has none), **Extended context** (larger context windows where offered, may cost more), **Advisor** (a
second model reviews each turn and adds notes, for this session; if omp has no model for its advisor role, **Choose
model** sets one and restarts omp on the session), **Auto-compact** and **Auto-retry** (after provider errors). The
switches show omp's current state when the menu opens. Extended context, Auto-compact and Auto-retry are saved in
omp's settings and apply to every session.

### Commands that run only in omp's terminal

Some omp commands exist only in its terminal UI (for example `/plan`, `/goal`, `/loop`, `/fork`, `/tree`, `/collab`).
Typed in the message box they are never sent to omp as a message: where the window has its own way it uses it —
`/settings` → Settings, `/login` (`/setup`, `/providers`) → Model providers, `/new` → a new session, `/resume` → the
sessions list (text after it filters the list), `/model` without arguments → the model picker, `/hub` → Background
tasks, `/branch` or `/rewind` → Rewind, `/drop` → Delete session, `/restart` → restarts omp, `/extensions` and
`/status` → a card that opens Plugins and skills, `/hotkeys` → the keyboard shortcuts. `/quit`, `/exit`, `/copy`,
`/open` and `/queue` get a card that says what to use in the window instead (the `/` menu leaves them out).
Otherwise a card offers **Open omp terminal**, omp's own interface in the terminal panel (a separate omp with its own
session); the `/` menu lists these last, under *Terminal only*. A skill, prompt or
extension omp itself offers under such a name is sent to omp as usual. `!command` runs a shell command in the project
through omp (the output joins the conversation and the context); `!!command` offers to open a shell instead.

## Settings

**Settings** at the bottom of the sidebar (in the header while the sidebar is hidden), Ctrl+, (⌘,) or
`/settings`. The settings fill the window: their page list takes the sidebar's place, and **Back to app** at its top
(or Esc) returns to the conversation. Pages: **General**; for omp, **Model providers**, **Connectors**, **Plugins and
skills**, **Computer use**, **Git and worktrees**, **SSH hosts**; for this app, **Pets** and **Updates**; for
troubleshooting, **Advanced** and **Diagnostics**. In a narrow window the pages are a strip across the top that
scrolls sideways (its faded edge shows there are more). The ones without a section of
their own below:

- **General** — *Appearance* (System, Light or Dark), *Notifications* (see Daily use) and *Send messages with*
  (Enter, or Ctrl/⌘+Enter).
- **Model providers** — the providers omp can use, each with **Sign in** or *Signed in*; the browser link and any
  code appear above the message box. The list appears while omp is running. API keys stay in omp's configuration.
  Below it, *Plan usage* shows every signed-in account whose provider reports limits (subscription windows, spend),
  as meters with the share used and the time to reset, with a refresh button. Hidden when no provider reports usage.
- **Advanced** — *omp runtime*: which omp the app starts. **Command** empty means the omp the app
  installed, otherwise omp from PATH; *Arguments before omp's own* (one per line; for your own Bun setup the command
  is `bun` with `--no-install` and the path to omp's `cli.ts`); *Profile* (`OMP_PROFILE`); **Save and restart omp**,
  and **Install omp 18.8.0** or **Reinstall**.

## Connectors (MCP servers)

**Settings → Connectors** (or **+ → Connectors** in the message box) lists the MCP servers omp loads from its
own files — yours (every project) and the project's `.omp/mcp.json` — and from the project folder's `mcp.json` /
`.mcp.json` (shared with Claude Code and other tools: only their switch works here; change them in the file).
Each row shows how omp starts or reaches the server,
the names of its environment variables or headers (never their values), and whether this session has its tools
(*Connected · N tools*) — omp connects to servers only when it starts.

- The **switch** turns a server on or off (omp's `/mcp enable|disable`); **⋯ → Test connection** connects once
  and lists its tools, or shows omp's reason it failed; **Edit…** changes the command, arguments, environment, URL
  or headers in the file (other fields are kept); **Remove…** asks first.
- **Add connector**: a local command (stdio: command, one argument per line, `NAME=value` variables) or a remote
  server (HTTP or SSE: URL, an optional bearer token, other headers), for all projects or only this one. omp
  checks and saves it (`/mcp add`).
- After a change the page offers **Restart omp**: the change applies when omp starts again (after the reply, if
  one is running).
- **Find connectors** searches Smithery, a public registry of MCP servers, and lists what it finds. **Sign in…**
  (servers that use OAuth), **Add…** on a Smithery result and the Smithery sign-in happen in omp's own terminal:
  the page shows the command to type (`/mcp reauth <name>`, `/mcp smithery-login`) and opens the terminal.
- **Edit…**, and the variables and headers of a new connector, are written into omp's MCP config file
  (`mcp.json`) by the app itself — the only omp file it changes directly; everything else goes through omp's
  commands. While omp is not running the page offers **Start omp**.
- **Resources and prompts** asks the servers what they offer; a prompt can be used as a `/server:prompt`
  command. **Servers from project files** turns off the servers a project brings, for projects you don't trust.
- Servers set up in other tools' settings (Claude Code, Cursor, VS Code…) are loaded by omp too; they are not
  listed, but their tools are named at the end of the list.

## Plugins and skills

**Settings → Plugins and skills** (or **+ → Plugins**) works through omp's `omp plugin` command line.

- **Install** takes an npm package, a git URL, a local folder or `name@marketplace`; omp's own error is shown if
  it fails. Each installed plugin has a switch, **Upgrade** (marketplace plugins) and **Uninstall…**.
- **Marketplaces**: add one by GitHub `owner/repo` (Anthropic's is `anthropics/claude-plugins-official`), git
  URL, catalog URL or folder; **Browse** lists its plugins with **Install**; **⋯** updates its catalog or removes
  it. **Plugin updates** sets whether omp checks for (or installs) updates when it starts.
- After a plugin change omp reloads skills and commands at once; its tools, hooks and MCP servers load when omp
  restarts (the page offers the restart).
- **Skills** lists omp's skills: **Use** starts a message with `/skill:<name>`, the switch turns a skill off
  (omp's `disabledExtensions`); *Use skills* and *Skill commands* turn them off altogether. **Skill sources** chooses
  where omp looks for skills (omp's, Claude Code's, `.agents`, Codex) and extra folders. Skill settings apply when
  omp restarts.

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
  the Screen Recording and Accessibility permissions for OMP GUI (the buttons open the right System Settings pane);
  Windows needs no permission.
- **Git and worktrees** — the project's branch, uncommitted changes, upstream and remote. *GitHub*: omp works with
  GitHub only through the GitHub CLI (`gh`); **Sign in…** runs `gh auth login` in the terminal panel, and *Let omp
  use GitHub* adds omp's github tool. *Worktrees* lists the repository's worktrees: **Open** starts a new session
  in one, **Remove** deletes a clean one (its branch stays; one with uncommitted changes is refused).
  **New worktree…** runs omp's `/wt <branch>`: omp makes the branch from the current commit in a new folder,
  copies your uncommitted changes there and moves this conversation into it (the window follows). *Worktree
  options* and *Subagents* (each subagent in its own copy of the project) are omp's `worktree.*` and
  `task.isolation.*` settings. omp has no separate "environments": a session works in a folder (the project or a
  worktree), and SSH hosts are the other machines it reaches.
- **Branch in the header** — next to the project name: the branch (and "· worktree" in a worktree), hidden outside
  git. Its menu copies the name, starts a new worktree from here, or opens these settings.
- **SSH hosts** — the machines omp's ssh tool can run commands on (keys only, no passwords), saved in omp's
  `ssh.json` for all projects or for this project. **Add host…** and **Remove** use omp's `/ssh add` and
  `/ssh remove`; **Test** connects the way omp does (no password prompt, 8 s to connect; a new host's key is added
  to `known_hosts`) and runs `exit`. While omp isn't running the page shows the saved hosts only.

## Pets

A small pixel pet sits on the top edge of the message box, at its right end, and lives there: it breathes, blinks,
looks around, and now and then twitches an ear, hops, stretches with a yawn or wiggles. It follows what omp does: it
looks up at a thought cloud with dots while the model thinks or writes, bounces busily next to a turning gear while a
tool runs, pops up a **!** and looks at you when omp asks you something, hops for joy among sparkles when a run
finishes, wobbles with a sweat drop when a tool fails or omp stops, and falls asleep with drifting Zzz after three
quiet minutes (typing or clicking it wakes it up). It looks at the pointer when you hover it, glances at the message
box while you type, and hops happily when clicked. On the message box it has its own strip above the box, so it never
covers the conversation; in a window less than 520 px tall it steps aside and gives that strip back. It moves only
while the window is active and redraws only when something changes: minimised, in the background, or asleep for a
while, it does not redraw at all.

**Click the pet** to message omp from it: a small message box opens above it with the keyboard in it, and the pet
says what omp is doing (or a word of its own). **Enter** sends the text to the conversation just as the message box's
Send does (a new prompt, or queued for after the run while one goes on), without touching what is in the message box;
**Esc**, a click on the pet or a click elsewhere closes it (the text stays for next time) and the keyboard goes back
where it was.

**Drag the pet** (press and move it a few pixels; a press that barely moves is a click) to put it anywhere in the
window; its speech bubble and message box go with it. It stays where you drop it, inside the window when the window
is resized, and after a restart; while it is off the message box, the box gives its strip back. Drop it close to its
place on the message box and it sits there again, or use **Reset position** in Settings → Pets.

**Settings → Pets** turns the pet on or off (on by default), sets its size (small 32 px or medium 48 px, the default)
and picks one of nine: Pi (omp's own), a cat, an owl, a robot, a slime, a cactus, a ghost, a fox and a turtle.
**Create pet** makes your own (body, color, accessory, name) with a live preview of every mood; **Customize** renames,
recolors or dresses the chosen one, deletes one you made, or resets a built-in one. Pets belong to the GUI: they are
saved in its settings file (`pet`), and omp knows nothing about them.

## Updates

OMP GUI updates itself from this project's GitHub releases. About 20 seconds after it starts, and then once a day,
it asks the release feed whether a newer version exists (**Settings → Updates → Check automatically** turns this
off; **Check for updates** asks at once). A newer version shows as **Update available** at the bottom of the
sidebar; nothing is downloaded or installed until you choose to. Until the first release of this client is
published, the check says *No release is published yet.*

- **Download and install** downloads this platform's package and checks it before anything changes: the release's
  list of packages (`update.json`) must carry a valid signature from the project's release key, which is built into
  the app, and the package must match the size and SHA-256 that list gives. The package is then unpacked next to
  the app and started once to check it runs on this computer.
- **Restart now** (or simply quitting OMP GUI) installs it: once the app has closed, the new version takes the old
  one's place — same folder, so shortcuts keep working — and, with Restart now, starts. The version before is kept
  next to the app as `.<folder name>.previous` until the next update (to go back, swap the folders by hand). If the
  swap fails, the old version is put back and Settings → Updates says why at the next start.
- **omp moves with the app.** Each version of the app runs one pinned omp. When an update brings a newer omp, the
  Updates page says so; after the restart the app installs it by itself (about 1.3 GB, verified like the first
  install) while omp from the previous version keeps working, then switches over once no run is active and removes
  the old one. If that install fails, the old omp keeps working and **Settings → Advanced** offers to try again.
- Settings, sessions and omp's configuration live elsewhere and are never touched by an update.

The app downloads the package to your Downloads folder instead (**Download and verify**, then replace the app
yourself) when it cannot replace itself: when this user may not change the folder it is in (for example Program
Files), or when it runs from a development build. From a terminal, `OmpGui --update` reports whether an update
exists and `OmpGui --update --yes` installs it (`--restart` starts the new version afterwards); `OmpGui --version`
prints the version. (`updateFeed` in the settings file points the check at another feed; its manifest must still
be signed with the release key.) The newest published release is offered, alpha versions included.

## Diagnostics for a bug report

**Settings → Diagnostics → Save diagnostics…** writes a zip where you choose: app and OS versions, the GUI
settings with every environment value and API key removed, the omp runtime record and its install log, omp's
state and last error, and the names (not the contents) of recent events. Known credential formats are masked
everywhere and your home folder is shown as `~`. No conversation text is included and nothing is uploaded. The page
also shows where the settings file is, and *RPC events* (F12) shows the raw events omp sends.

## Where things are kept

| What | Windows | macOS | Linux |
|---|---|---|---|
| GUI settings (`omp-gui.local.json`) | `%APPDATA%\OmpGui` | `~/Library/Application Support/OmpGui` | `~/.config/OmpGui` |
| Installed omp runtime | `%LOCALAPPDATA%\OmpGui\runtimes` | `~/Library/Application Support/OmpGui/runtimes` | `~/.local/share/OmpGui/runtimes` |
| Dictation model | `%LOCALAPPDATA%\OmpGui\speech-models` | `~/Library/Application Support/OmpGui/speech-models` | `~/.local/share/OmpGui/speech-models` |
| omp's own data (sessions, sign-in, settings) | omp's folder (`~/.omp`), managed by omp | same | same |

On Linux `XDG_CONFIG_HOME` and `XDG_DATA_HOME` move the GUI's folders. `--config <file>` or `OMPGUI_CONFIG` use
another settings file; `OMPGUI_RUNTIME_DIR` and `OMPGUI_STT_DIR` move the runtime and the speech model.

The GUI writes its own settings file (atomically, keeping a `.bak` of a version you edited by hand), the folders
above, and what you save (update packages, diagnostics). It reads omp's session folder to list sessions. Of omp's
own files it changes only the MCP config (editing a connector, or the variables and headers of a new one) and its
list of pinned sessions (`session-pins.json`, when you pin or unpin from the sidebar, the way omp's `/pin` does);
everything else goes through omp.

## Uninstall

Delete the app folder (or *OMP GUI.app*) and, after an update, the `.<folder name>.previous` folder next to it;
then — if you want — the GUI's folders from the table above, the Linux menu entry
(`~/.local/share/applications/omp-gui.desktop`) and update packages you downloaded by hand. On macOS remove
OMP GUI from *System Settings → Privacy & Security* (Microphone, Screen Recording, Accessibility) if you granted
them. omp's own data is left alone; remove `~/.omp` only if you want to remove omp's sessions and sign-ins too.

## Troubleshooting

- **omp didn't start** — the card says so; **Show details** has the error. Check the omp command in **Settings →
  Advanced**, then **Try again** (after omp stops in the middle of a session the button reads **Restart omp**; both
  continue the same session).
- **Dictation: the audio library needs libjack.so.0** (Linux) — no system recorder was found either. Install
  PulseAudio's or PipeWire's recorder (`pulseaudio-utils` / `pipewire-bin`) or `alsa-utils`, or the JACK client
  library (`libjack-jackd2-0` or `libjack0` on Debian/Ubuntu, `jack-audio-connection-kit` on Fedora).
- **The preview is empty** — the panel says which web engine is missing (WebKitGTK on Linux, WebView2 on
  Windows); **Open in browser** shows the page meanwhile.
- **The window looks wrong at a large text size** — the layout follows the OS scaling; report it with a
  screenshot.

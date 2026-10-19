# Security

## Reporting a vulnerability

Please report it privately: the repository's **Security** tab → **Report a vulnerability**. Do not open a public
issue for it. Include the client version (`Ombra --version`), your OS and the steps to reproduce.

## Open a folder only if you trust it

The client runs omp in the folder you open, and omp treats that folder as a project: it can start the project's
MCP servers and load its `.omp/` extensions, hooks and tools when the session starts. A folder from someone else
can therefore run code on your computer as you.

**Workspace trust (fail-closed).** Every folder you have not trusted runs restricted: the client opens its chats, and
omp's terminal UI in the terminal panel, with `--untrusted-project` (a flag of Ombra's owned engine). Under it omp
ignores the folder's own settings layer (`.omp/settings.json`, `.omp/config.yml`, `.claude/`, `.cursor/`,
`.gemini/`, `.codex/`, `opencode.json` settings), its MCP servers, extensions, hooks, TypeScript commands, custom
tools, skills, markdown slash commands, prompt templates and subagent definitions (`.omp/skills`, `.claude/skills`,
`.agents/skills`, `.omp/commands`, `.omp/prompts`, `.omp/agents`… and those of the ancestors discovery walks — neither listed nor invocable until
trusted) and project plugins (including the walked-up `.omp/plugins` registry, and `enabledPlugins` in its
`.claude/settings*.json`), never picks the folder's `.venv`/`venv` as the Python interpreter (nor one a trusted chat
of the same folder found earlier), and starts no language servers (they can run project build scripts) — not for
helper sessions such as `security_scan` or `/tan` either. Language-server and debug-adapter configs of the folder
(`lsp.*`, `dap.*`, in its root and config folders) are not read and no adapter or server is taken from its
`node_modules/.bin`, `.venv/bin` or `bin`. Shells and interpreters always take the chat's own settings, never those
of the engine's start folder, and the engine's own process-wide settings treat every folder as untrusted (work outside
a chat's scope fails closed). In a shared-engine
chat the project layer is only ever read for the folder the chat started in: a `/move` or `/wt` never takes another
folder's settings in place; the client restarts the chat under the new folder's trust. Your own profile's
extensions, hooks, plugins, MCP servers, tools, skills, subagents and settings still load. omp reports what it left out, from its own
loaders, and only when that list is not empty does a card in the chat name the items (MCP servers, hooks,
extensions, skills, subagents, language servers…) with **Trust and restart** (that chat only, same conversation) and **Keep
restricted**. Trust and restart trusts the folder as it was when the card was shown: if its configuration changed
since, nothing is trusted and the chat restarts restricted with a new card listing what the folder has now.
Client-run omp subcommands that load extensions (`usage`, `login`, `models`…) run from an empty private
folder of the app instead of an untrusted project; `config` and `plugin`, which read the project's settings and
plugin list as data, still run in it. Trust is kept per resolved folder (symbolic links resolved) in
`workspace-trust.json` next to the client's settings (owner-only file and folder, re-applied on every write, replaced
atomically; a file others can write is ignored). *Settings → Permissions → Trusted folders* lists them with
**Revoke**: the folder's idle chats restart restricted at once, a working one when its run ends, and the folder's
omp terminals that started trusted are offered to close. Folders opened before this release are not trusted.
AGENTS.md and rules still load in an untrusted folder: they are text for the model, not code, and every tool call
still goes through the approval mode. Skills, slash commands, prompt templates and subagent definitions do not: a
skill ships instructions the agent follows and often scripts it runs, and a subagent definition sets another agent's
instructions and tools, so they appear only once the folder is trusted.

The trust record carries a SHA-256 fingerprint of the folder's **configuration files**: the project config folders
(`.omp`, `.claude`, `.codex`, `.gemini`, `.cursor`, `.windsurf`, `.opencode`, `.agents`, `.agent`, `.github/skills`;
Markdown left out except under `skills`, `commands`, `prompts` and `agents`, so a skill, slash command or subagent added after trust asks
again), the root config files (`mcp.json`, `.mcp.json`, `opencode.json(c)`, `lsp.*`, `dap.*`, `.vscode/mcp.json`),
the walked-up plugin registry, the files omp reported when you trusted it (local language-server binaries, extension
targets, skill and subagent files) and scripts inside the folder that MCP servers or `shellPath` name. When any of them changes,
the folder is restricted and asked about again. It does not cover code those files import from elsewhere in the
repository, plugin packages under `node_modules`, or programs outside the folder: trusting a folder trusts its code,
not only today's configuration.

What the client already does against it:

- omp's runtime (Bun) never reads the folder's `bunfig.toml` or `.env`: Bun gets the runtime pack's own config and
  `--no-env-file`. omp still reads its own `.env` from your profile.
- Every omp the client starts, including the one in the terminal panel, gets an explicit approval mode (`write` by
  default); omp's own default would approve every tool.
- The git the client runs itself (Files pane, Git settings, header) ignores the repository's `core.fsmonitor` and
  its hooks.
- Downloads (runtime, updates) are checked against pinned SHA-512 / SHA-256 values over HTTPS, and updates must
  carry a valid signature of the project's release key.

Known open issues:

| Issue | What happens | Until it is fixed |
|---|---|---|
| Code of a trusted folder | once trusted, omp starts the folder's MCP servers, language servers and loads its extensions, hooks, commands and tools at session start; a change to code they import (not to the configuration) does not ask again | trust only folders whose code you trust; revoke before pulling someone else's changes |
| Chats that started trusted | a revoke or a change takes effect at the chat's next start; code already loaded stays in the shared engine process until the engine restarts | restart the app after revoking a folder you no longer trust |
| omp's terminal UI | a `/move` inside omp's own TUI re-scopes in place, as upstream omp does | open omp's terminal in the folder you mean to work in |
| git clean filters | a repository's `.gitattributes` with a `filter.<name>.clean` command in its `.git/config` runs when git re-reads a changed file | trusted folders only |
| NuGet restore | no source mapping or lock files yet | build from a clean machine with nuget.org only |

## Shared agent engine

GUI chats use separate logical sessions inside one app-managed host running Ombra's owned omp source.
Session keys route RPC replies, approvals and lifecycle events; selecting another chat does not transfer ownership.
This is application-level isolation, **not an OS security sandbox**. Trusted extensions execute code in the shared
process and must not be treated as mutually untrusted tenants. A host crash affects its open chats; recovery keeps
unsent input for review and does not resend it automatically.

The managed installer verifies the embedded source manifest and pinned dependency downloads before activating a
staged pack. It rejects an existing destination without the matching ownership marker and rejects reparse-point
destinations rather than replacing unrelated content. The owned derivative is not an upstream-signed release.
The browser's Tern connection may operate only on blocks it owns; a block ID alone does not grant access.

## Ombra ↔ Nightly trust (macOS)

Ombra embeds its own Nightly runtime and serves Nightly's team sharing over local Mach channels. Each side checks the
other by the kernel's audit token of every handshake (same user, then the peer's running code signature through
`SecCodeCheckValidity` with a code requirement):

- **Release without an Apple Developer ID (current).** Both apps are signed ad hoc with the hardened runtime
  (`codesign --force --deep -s - --options runtime` with their entitlements: `avalonia/tools/package/sign.sh`,
  `runtime/nightly/pack-nightly.py`). Ombra accepts only a Nightly whose code directory hash is the `cdhash` of the
  pinned runtime manifest `runtime/nightly/nightly-runtime.json` (built into Ombra, next to the package's SHA-256),
  on the embed channel (besides pid, executable path and user) and on the team link. Nightly accepts only an Ombra
  whose cdhash Ombra wrote into the runtime profile's `user.js` at launch (`zen.embed.peer-cdhash`); requirement
  `cdhash H"<hex>"`. A different, modified or unsigned binary is refused by both.
- **Boundary.** The runtime profile lives in Ombra's data directory, so the pin Nightly reads is only as protected as
  files of the same macOS user: this defends against other binaries talking to the channels, not against malware
  already running as the user (which can also rewrite either app). The Nightly entitlements leave out
  `get-task-allow` and `allow-dyld-environment-variables` so the pinned code cannot be debugged or injected into by
  another process of the user; library validation is off for both apps (ad-hoc code has no Team ID).
- **With a Developer ID (future).** Ombra writes its designated requirement into `zen.embed.peer-requirement`, which
  takes precedence over the cdhash pin.
- **Development only.** `OMBRA_NIGHTLY_ALLOW_UNSIGNED=1` (Ombra) and `zen.embed.allow-unsigned-peer` (Nightly) accept
  an unsigned peer of the same user and skip Ombra's cdhash check; release builds never set them.
- A standalone Nightly that is not the pinned runtime (another build, `/Applications/Nightly.app` of a different
  build) cannot use Ombra's team link in a release.

## What leaves your computer

- **Your conversations** go to the model provider of the model you chose, as omp sends them.
- **Providers you have not added** get nothing: every omp the client starts gets them in `disabledProviders` (a
  `--config` overlay), and if the client cannot tell which providers are added, omp is not started.
- **omp's background requests**: model lists from the providers you added, usage limits of signed-in accounts, and
  omp's public model catalog from `catalog.stencil.so` (no keys, no chat content; omp 18.8.0 has no switch for it).
- **Tools and connectors** reach whatever they are made for (web search, package indexes, MCP servers' own
  services). Connectors can send their own telemetry: `mcp-for-blender`, for example, reports usage to a Supabase
  project.
- **The client itself** checks GitHub for updates (Settings → Updates) and downloads the omp runtime from the npm
  registry and the dictation model from Hugging Face when you ask for them.

**Strict network privacy** (Settings → Advanced → Network privacy) limits omp and the tools it runs to the added
providers, sign-in while you add one, and the addresses you allow, through a filter in the client; refused
destinations are listed. It relies on proxy settings: programs that ignore them, and addresses on this computer or
in private network ranges (which omp always reaches directly), are not covered. It is not a firewall.

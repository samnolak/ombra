# Security

## Reporting a vulnerability

Please report it privately: the repository's **Security** tab → **Report a vulnerability**. Do not open a public
issue for it. Include the client version (`OmpGui --version`), your OS and the steps to reproduce.

## Open a folder only if you trust it

The client runs omp in the folder you open, and omp treats that folder as a project: it can start the project's
MCP servers and load its `.omp/` extensions, hooks and tools when the session starts. A folder from someone else
can therefore run code on your computer as you. Until the client has a "trust this folder" step, treat opening a
folder like running its code.

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
| Project MCP servers | omp starts the servers in the folder's `.mcp.json`, `mcp.json` or `.omp/mcp.json`; the folder's own `.omp/` settings can turn project servers back on after you turned them off | trusted folders only |
| Project extensions, hooks, tools | omp loads the folder's `.omp/extensions`, `.omp/hooks` and `.omp/tools` at session start | trusted folders only |
| git clean filters | a repository's `.gitattributes` with a `filter.<name>.clean` command in its `.git/config` runs when git re-reads a changed file | trusted folders only |
| NuGet restore | no source mapping or lock files yet | build from a clean machine with nuget.org only |

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

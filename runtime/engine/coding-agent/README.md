# @oh-my-pi/pi-coding-agent

Core implementation package for the `omp` coding agent in the `omp` monorepo.

For installation, setup, provider configuration, model roles, slash commands, and full CLI reference, see:
- [Monorepo README (local)](../../README.md)
- [Monorepo README (GitHub)](https://github.com/can1357/oh-my-pi#readme)

Package-specific references:
- [CHANGELOG](./CHANGELOG.md)
- [MCP configuration guide](../../docs/mcp-config.md)
- [MCP runtime lifecycle](../../docs/mcp-runtime-lifecycle.md)
- [MCP server/tool authoring](../../docs/mcp-server-tool-authoring.md)
- [DEVELOPMENT](./DEVELOPMENT.md)

## Memory backends

The agent supports three mutually-exclusive memory backends, selected via the `memory.backend` setting (Settings → Memory tab, or `~/.omp/config.yml`):

- `off` (default) — no memory subsystem runs.
- `local` — existing rollout-summarisation pipeline; writes `memory_summary.md` and consolidated artifacts under the agent dir.
- `hindsight` — talks to a [Hindsight](https://hindsight.vectorize.io) server (Cloud or self-hosted Docker), retains transcripts every Nth user turn, recalls memories on the first turn of a session, and exposes `retain`, `recall`, and `reflect`.

### Hindsight quickstart

1. Run a Hindsight server (Cloud or `docker run -p 8888:8888 ghcr.io/vectorize-io/hindsight:latest`).
2. Set `memory.backend = "hindsight"` and `hindsight.apiUrl = "http://localhost:8888"` (or your Cloud URL).
3. Optional environment overrides (env wins over settings):
   - `HINDSIGHT_API_URL`, `HINDSIGHT_API_TOKEN` — connection
   - `HINDSIGHT_BANK_ID`, `HINDSIGHT_DYNAMIC_BANK_ID`, `HINDSIGHT_AGENT_NAME` — bank addressing
   - `HINDSIGHT_AUTO_RECALL`, `HINDSIGHT_AUTO_RETAIN`, `HINDSIGHT_RETAIN_MODE` — lifecycle
   - `HINDSIGHT_RECALL_BUDGET`, `HINDSIGHT_RECALL_MAX_TOKENS` — recall sizing
   - `HINDSIGHT_BANK_MISSION`, `HINDSIGHT_DEBUG`

Switching backends mid-session immediately replaces the live backend, memory tools, listeners, and system-prompt context. Existing users with `memories.enabled = true|false` are migrated to `memory.backend = "local"|"off"` exactly once on first launch; afterward, `memory.backend` is the sole runtime selector.

## Session-file ownership in the owned engine

The shared RPC host, CLI and TUI use the same `FileSessionStorage.claimSession`
lease, keyed by journal session ID rather than pathname. Mutation claims
ownership, and so does a shared-engine open of a restored chat (or
`switch_session`) up front; catalog reads, inspection and export do not claim a
writer lease. That open is refused with `session_lease_held` and the holder while
another window or terminal writes the chat, so the desktop shows it read-only.
Another writer's append/title rewrite (CLI, TUI) still forks to a new identity
with an explicit persistence warning. RPC warnings include the new session ID
and file so the desktop state follows that fork. Move and deletion refuse a busy
journal instead.
Desktop sidebar deletion uses `engine_delete_session`, not filesystem deletion.
An idle desktop starts only its existing shared root-host lifetime for deletion,
using the same configuration/privacy and protocol-negotiation path as a logical
open; it does not open or resume an unrelated chat.
Deletion also checks nested session journals before removing their artifacts.
GC archive and collaboration replica publication participate in the same lease.

OS lease failures are persistence failures, never local-only authorization.
Close retains the claim through queued writes, storage drain and artifact copying;
it releases afterward. Native handles are strongly retained for the writer's
lifetime. There is no heartbeat, expiry, PID-reuse heuristic or timeout-based
takeover. Journal symlinks, hard links and renames preserve the header ID and
therefore meet the same lease in the shared ownership namespace.

**Holder records and “Open here”.** Next to each lease the holder writes `<lease>.holder.json` (`kind` `ombra` for
the desktop engine, `omp` for terminal and CLI, `pid`, `since`, and a random id of this claim). It is display
only: it is read while the lease is held and its pid is alive, and is withdrawn
before release. The root command `engine_take_session {path}` writes
`<lease>.takeover.json` naming that claim and waits up to 30 s for the lease to become free. A
holder polls for requests every 250 ms while it holds a lease: an RPC session
sends `session_lease_lost` (who asked, and `interrupted` when a reply was running)
and closes after its run is aborted and its last write landed — a running reply is
stopped, not finished; a CLI/TUI manager drains its writes, releases, and continues
in a sibling file on its next write. A request for another claim, from before the
current claim, or older than 30 s is removed unanswered, so one a requester left
behind never cuts off a later holder; each requester withdraws only its own request,
on success and on timeout. A holder that does not answer (an older omp, a stopped
process) keeps the lease and the requester gets `session_lease_held` again. Nothing
is taken by force.

**Typed approvals.** A request offers `scopes` `once` only while provider safety
checks are pending and whenever the tool or the user's `tools.approval` policy
insists on asking every time (a critical bash command, a `tools.bash.patterns`
rule with `approval: prompt`); hosts never keep such an answer. A bash call's
`cwd` is part of the request and of the text prompt; MCP tools carry `origin` and
`server` and show their arguments.

Pinned native implementation (18.8.0):
[Linux abstract socket](https://github.com/can1357/oh-my-pi/blob/v18.8.0/crates/pi-natives/src/file_lock/linux.rs),
[Windows named kernel object](https://github.com/can1357/oh-my-pi/blob/v18.8.0/crates/pi-natives/src/file_lock/windows.rs),
[other Unix flock](https://github.com/can1357/oh-my-pi/blob/v18.8.0/crates/pi-natives/src/file_lock/unix.rs).
The kernel releases ownership on process death. Unix sidecars are persistent:
unlinking one would split inode ownership and is forbidden. These are cooperative,
local-machine advisory guarantees, not protection against legacy/uncooperative
writers, users replacing lock sidecars, different ownership namespaces, or
cross-machine/network-filesystem writers.

Behavioral regressions are in `runtime/tests/session-ownership.test.ts` and its
real-process worker; typed approvals in `runtime/tests/structured-approval.test.ts`
(real host, local OpenAI-compatible model). They require the pack's **Bun 1.4.2**
and a staged copy of `runtime/engine` whose `node_modules` points at the pinned
pack's `omp/node_modules`. From the repository root, after preparing that staging:

```sh
OMBRA_ENGINE_ROOT="$PWD/.temp/ownership-runtime/engine" \
  .temp/runtime-installer-final/ombra-engine-18.8.0-bun-1.4.2-v1/bun/bun \
  test runtime/tests/session-ownership.test.ts runtime/tests/structured-approval.test.ts
dotnet test avalonia/tests/Ombra.Tests/Ombra.Tests.csproj \
  --filter 'FullyQualifiedName~ConversationStateTests|FullyQualifiedName~DraftDeletionTests|FullyQualifiedName~DraftFolderRecoveryTests'
```

These additions have not been run during implementation. macOS/Unix process tests
cover stop/kill and symlink/rename behavior; Windows still needs its native
termination/suspend and symlink-privilege gate. Linux and Windows skip the
filesystem-sidecar setup-error case (their lease providers do not use sidecars)
and require native provider failure-injection coverage on those platforms.

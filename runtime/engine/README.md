# Owned engine source and upstream patches

`coding-agent/` is the checked-in source of truth for builds. The build never downloads
upstream or runs the patcher. `upstream.json` pins the exact upstream npm version,
HTTPS tarball and SRI integrity of every `@oh-my-pi` package, and lists the mail
patches in application order. Only coding-agent `src/**`, `package.json`,
`CHANGELOG.md`, `README.md`, `LICENSE`, and `THIRD-PARTY-NOTICES.txt` are vendored.
Engine host/adapter files outside `coding-agent/` are maintained directly.

The patcher requires Python 3.9+ and Git; no pip/npm dependencies. Run commands from
any directory (examples below assume the repository root). Downloads use system
TLS trust, including `SSL_CERT_FILE` / `SSL_CERT_DIR`. If the shell sets
`NODE_EXTRA_CA_CERTS` for a corporate CA, Python also loads that PEM in addition
to its normal trust roots. TLS/integrity checks are never disabled.

## Reproduce and check

```sh
python3 runtime/engine/patcher.py status
python3 runtime/engine/patcher.py --check
# Equivalent:
python3 runtime/engine/patcher.py import --check
```

The check downloads/verifies all pinned tarballs, materializes pristine source in
a temporary Git repository, applies every ordered patch, and compares file bytes,
paths and executable bits with the checked-in source (executable bits are ignored
on Windows). It does not alter source, patches or manifest. Downloads are cached
by integrity under `.temp/engine-patcher/cache/` and reverified on every use.
`status` is offline and shows pins, patch order, Git changes and any pending rebase;
it does not claim source/patch equivalence.

`python3 runtime/engine/patcher.py import` installs the reconstructed source. It
refuses to overwrite local Git changes in `coding-agent/`. Prefer exporting
intentional edits rather than discarding them to make import pass. All work is
staged before publication; only the vendored paths are replaced. Unrelated engine
files, and non-vendored directories such as `coding-agent/node_modules/`, stay intact.
Do not edit source/patches/manifest concurrently with a patcher operation.

## Edit the checked-in source, then export

```sh
# Edit coding-agent/, then regenerate the existing ordered series:
python3 runtime/engine/patcher.py export
# Assign previously unowned feature paths to a patch (a new name appends):
python3 runtime/engine/patcher.py export \
  --patch 0090-example-feature.patch \
  --what 'Explain the user-visible change' \
  --why 'Explain why Ombra needs this difference' \
  --upstream-status 'Ombra-specific; not submitted'
python3 runtime/engine/patcher.py --check
python3 runtime/generate-engine-artifact.py
```

Each changed path belongs to the **last existing patch touching that path**,
including when `--patch` is supplied. `--patch` chooses the destination only for
paths no existing patch touches; it never reassigns existing paths. That choice
may be an existing patch or a new `.patch` basename; a new patch requires `--why`.
For example, a new RPC test can be assigned to the existing RPC patch while a
simultaneous changelog edit stays in the last patch touching the changelog.
When several features add new files together, repeat
`--assign src/path.ts=030-editor-documents.patch` to route individual previously
unowned paths to existing patches. This overrides `--patch` only for those new
paths; existing path ownership remains unchanged.
Export includes all current source edits, not only those assigned by `--patch`.
Existing names/order and original author/commit messages are retained; new mail
patches include `What:`, `Why:` and `Upstream-Status:` in the commit body.
Review those headers when ownership or upstream disposition changes. Export checks
that the regenerated series produces the complete current source before replacing
any patches, and never changes `coding-agent/`.

Empty patches are removed from the ordered list and patch files, reported on stdout,
and recorded in `upstream.json` under `droppedPatches` with their name, version and
reason. Commit the source, manifest, patches and regenerated engine artifact together.

### Split an existing area patch into feature/fix patches

Keep one patch per feature or fix rather than accumulating unrelated changes in
an area patch. Splitting changes patch history, not `coding-agent/`. Use a disposable
Git repository and ordinary Git staging; no extra patcher command is needed.
First export any intentional source edits and run `patcher.py --check`.

The following setup uses the same verified pins and ordered application as import.
Run it from the main repository root, replacing the patch name with an existing
manifest entry. It refuses to reuse an existing split directory.

```sh
PATCH=019-rpc-host-protocol.patch python3 - <<'PY'
import os
import runpy

p = runpy.run_path("runtime/engine/patcher.py")
manifest = p["manifest"]()
target = manifest["patches"].index(os.environ["PATCH"])
repo = p["WORK"] / "split"
p["initialize"](repo, manifest)
commits = p["apply_series"](repo, manifest)
p["git"](repo, "branch", "split-original", "HEAD")
p["git"](repo, "tag", "split-target", commits[target])
print(repo)
PY

# Only in the disposable repository: load the target patch without committing it,
# then unstage its changes so each feature can be staged separately.
git -C .temp/engine-patcher/split checkout --detach 'split-target^'
git -C .temp/engine-patcher/split cherry-pick --no-commit split-target
git -C .temp/engine-patcher/split reset
```

Stage each feature's module/tests with `git add <paths>` and its shared-file hunks
with `git add -p`; include deletions with `git add -u <paths>`. Commit each feature
with a meaningful subject and `What:`, `Why:` and `Upstream-Status:` paragraphs,
using `git -C .temp/engine-patcher/split commit`. Keep dependency order explicit:
shared integration may follow the modules it connects, but avoid turning that
commit into another unrelated-changes bucket. Repeat until the disposable
worktree and index are clean, including untracked files.

Before exporting, require that the final split commit has **exactly the same
tree** as the original area patch. This catches missing edits, extra edits,
deletions and mode changes; inspect `status` too because diff does not include
untracked files.

```sh
git -C .temp/engine-patcher/split status --short --untracked-files=all
git -C .temp/engine-patcher/split diff --exit-code split-target HEAD --
mkdir .temp/engine-patcher/split-mail
git -C .temp/engine-patcher/split format-patch \
  --full-index --binary --no-signature --no-stat --zero-commit \
  --output-directory ../split-mail 'split-target^..HEAD'
```

Copy those mail patches from `.temp/engine-patcher/split-mail/` into `patches/`,
giving them stable feature names. In `upstream.json`, replace only the old patch's
single list entry with the new filenames in commit order at that same position;
keep every other patch name/order unchanged. Remove only the superseded area
patch file. Do not add it to `droppedPatches`: this is a split, not an empty or
upstreamed change. Do not copy the disposable working tree over `coding-agent/`.

Finally run `python3 runtime/engine/patcher.py --check`. It replays the full series,
including the untouched later patches, and requires byte-for-byte equivalence
with checked-in source. Commit the new patches, removed area patch and manifest
together only after that gate passes; retain the disposable repository for
recovery until then, and remove the split workdirs when finished.

## Upgrade upstream, one patch at a time

First export all source edits and save your work. Then:

```sh
python3 runtime/engine/patcher.py rebase 18.12.2
```

The exact version is required (not `latest` or a semver range). The patcher obtains
registry metadata and integrity-verifies the target tarball for **every** package
already pinned in the manifest. It creates the old pristine Git tree, applies the
old ordered series, adds the new pristine tree, and cherry-picks each patch with
Git's three-way merge machinery. The old patch commits remain reachable through
`ombra-old`; the new pristine base is `ombra-upstream`.

At the first conflict it stops, prints the patch name, and retains the workdir:

```text
.temp/engine-patcher/rebase/repo/
.temp/engine-patcher/rebase/state.json
```

Resolve in that temporary repository, not in the checked-in `coding-agent/`:

```sh
git -C .temp/engine-patcher/rebase/repo status
# Edit files under .temp/engine-patcher/rebase/repo/ to resolve the conflict.
git -C .temp/engine-patcher/rebase/repo add <resolved-paths>
# Use git rm there for resolved deletions.
python3 runtime/engine/patcher.py rebase --continue
```

Stage all resolutions; remove scratch files from the temporary repository. Do not
manually abort/skip/rewrite its history. If upstream made a patch unnecessary,
resolve it to the current HEAD tree and stage that resolution: `--continue` drops
and records the resulting empty patch automatically. Continuation handles one
patch at a time and stops again on the next conflict. Source, patches and manifest
are unchanged until the **entire series** succeeds. On success they are published
together, and the rebase workdir is removed.

To abandon a rebase, move the entire `.temp/engine-patcher/rebase/` directory aside;
this preserves your conflict work, and no owned source needs reverting. A process
lock at `.temp/engine-patcher/lock/` prevents concurrent patcher commands. After a
hard-killed process, remove that lock only after confirming no patcher is running.
If a machine/process dies during publication, inspect any retained
`.temp/engine-patcher/publish-*/backups/` before proceeding; ordinary exceptions
roll back replacements automatically.

After a successful rebase, update the separately owned runtime dependency, lock
and pack-version pins for the new engine version, run the patch reproducibility
gate, regenerate the artifact, and run the engine/protocol/runtime gates in
[`docs/RELEASE_GATES.md`](../../docs/RELEASE_GATES.md). The patcher intentionally
does not rewrite pack names or application dependency manifests.

## Manifest contract

```json
{
  "schema": 1,
  "version": "18.12.2",
  "packages": {
    "@oh-my-pi/pi-coding-agent": {
      "version": "18.12.2",
      "tarball": "https://registry.npmjs.org/@oh-my-pi/pi-coding-agent/-/pi-coding-agent-18.12.2.tgz",
      "integrity": "sha512-<base64 digest from npm metadata>"
    }
  },
  "patches": ["0001-example.patch"]
}
```

The example omits other package pins: the real manifest must enumerate all pinned
`@oh-my-pi` packages. Unlisted `.patch` files, duplicate patch names, mixed upstream
versions, unsafe tar paths, links/devices, and package identity/integrity mismatches
are errors. Never hand-edit an integrity value to make a mismatching download pass.

## Recorded 18.8.0 → 18.12.2 sync

The initial owned delta was 137 files over the verified 18.8.0 npm source. It was
exported as 14 area-ordered patches, then upgraded with `rebase 18.12.2` and
`rebase --continue`, not by replacing the engine with an unpatched release.
All 18 namespace packages per version were downloaded and integrity-verified,
including all six platform-native packages. Bun remains 1.4.2; both upstream
releases require only Bun ≥1.3.14.

The real rebase stopped on patches 020 (browser), 030 (editor), 040 (MCP),
050 (jobs), 060 (approvals), 070 (context recovery), 080 (project trust),
100 (runtime isolation), 110 (tools), 130 (host integration), and 150 (notes).
Patches 010 (RPC), 090 (leases), and 120 (CLI) applied without a conflict.
After that real rebase, the broad RPC patch was split into dedicated plan, jobs,
MCP, editor, history, navigation and input-recovery patches plus shared dispatcher
integration: the maintained series has 21 patches with the identical source tree.
No entire area patch became empty. Removed individual redundant changes:

- Upstream now guards shared LSP process settings and tiny-client teardown by
  process ownership; the equivalent local guards are no longer separate changes.
- Upstream removed browser ambient declarations; the local declaration hunk was
  dropped, retaining cmux device/viewport behavior in canonical types.
- Upstream's bounded MCP argument formatter replaces the custom formatting core;
  typed provenance, normalization/fallbacks, denial reasons and mandatory scopes stay.

Compatibility review of 18.9–18.12.2:

- Framing and protocol versions remain unchanged. `get_state.queuedMessages` and
  `queue_update` gain `liveSteered`; Ombra preserves those delivered chips and
  prevents editing/removing them before their transcript echo.
- Typed approval selects may add `Approve <tool> for this session` between
  `Approve` and `Deny`. Ombra recognizes the typed request rather than requiring
  exactly two choices, preserving its approval card and session-grant policies.
- Thinking blocks gain `narration`; Anthropic identifies it at `thinking_end`.
  Delta RPC forwards that marker without cumulative snapshots, and Ombra moves
  narration into the answer in streamed, final and historical messages.
- `agent_start.resumedToolCallIds` reuses existing tool rows after resume.
  Compaction's `providerReplayThroughEntryId` stays engine-owned.
- Session migration code is unchanged. Runtime identity, prompt-cache/seance
  metadata and `model_usage.activeLeafId` are additive; read-only session paging
  uses upstream's replay-leaf selection rather than the physically last record.
- Core tool names remain. Removed Codex Code Mode settings have no Ombra callers.
  New upstream settings/defaults are honored rather than overridden by the host.
  Ordinary task finals no longer require structured yield; eval agents still do.
- The host rejects interactive-only `--restore`. Upstream approval dialogs/session
  grants use the canonical request helper with Ombra's typed scopes and denial
  callback; the select fallback can now offer engine-side session approval.

The complete upstream test run also exposed integration issues repaired before
release: compound collab/session-transfer operations now explicitly delegate one
writer-lease capability without allowing independent writers; the ambient settings
proxy no longer retains a parked overlay through a process-global method cache;
and editor mutation checks avoid inaccessible-file canonicalization when there
are no editor overlays, while remaining fail-closed when any overlay is open.
Owned regressions cover these cases and Biome's non-mutating stdin formatter.

Pristine upstream tests are an additional compatibility probe, not interchangeable
with the owned regression suite. Some deliberately assume contracts Ombra rejects:
independent live writers of one session, globally visible child registries/shared
Python kernels, critical-command approval bypass in yolo mode, local-command
`prompt_result` frames without an ACK, and disk-mutating Biome formatting.
Other upstream-only tests generate unvendored Python/Go/Rust RPC SDKs. Report
those differences explicitly; never weaken ownership/safety or skip assertions
merely to make an unchanged upstream fixture pass. Run the upstream workspace
with its own frozen development lockfile and verified native addon, in a separate
Git repository and an allowlisted environment (no inherited browser host settings
or provider credentials), not with a mixture of workspace and npm singletons.


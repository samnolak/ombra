#!/usr/bin/env python3
"""Reproduce the owned engine from integrity-pinned npm sources and mail patches.

Python 3 standard library + git only. See README.md for the editing/rebase workflow.
"""
from __future__ import annotations

import argparse
import base64
import contextlib
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import ssl
import subprocess
import sys
import tarfile
import tempfile
import urllib.parse
import urllib.request

ENGINE = Path(__file__).resolve().parent
ROOT = ENGINE.parent.parent
SOURCE = ENGINE / "coding-agent"
MANIFEST = ENGINE / "upstream.json"
PATCHES = ENGINE / "patches"
WORK = ROOT / ".temp" / "engine-patcher"
CACHE = WORK / "cache"
REBASE = WORK / "rebase"
AGENT = "@oh-my-pi/pi-coding-agent"
TOP_FILES = ("package.json", "CHANGELOG.md", "README.md", "LICENSE", "THIRD-PARTY-NOTICES.txt")
GIT_ENV = {
    "GIT_AUTHOR_NAME": "Ombra engine maintainers",
    "GIT_AUTHOR_EMAIL": "engine@ombra.local",
    "GIT_COMMITTER_NAME": "Ombra engine maintainers",
    "GIT_COMMITTER_EMAIL": "engine@ombra.local",
    "GIT_AUTHOR_DATE": "2000-01-01T00:00:00+00:00",
    "GIT_COMMITTER_DATE": "2000-01-01T00:00:00+00:00",
    "GIT_EDITOR": "true",
    "GIT_SEQUENCE_EDITOR": "true",
    "GIT_CONFIG_NOSYSTEM": "1",
    "GIT_CONFIG_GLOBAL": os.devnull,
}


class PatcherError(Exception):
    pass


def fail(message):
    raise PatcherError(message)


def git(repo, *args, check=True, env=None):
    # Never inherit an enclosing worktree/index or developer signing/hooks settings.
    process_env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
    process_env.update(GIT_ENV)
    process_env.update(env or {})
    result = subprocess.run(
        ["git", "-c", "core.autocrlf=false", "-c", "core.hooksPath=" + os.devnull,
         "-c", "gc.auto=0", "-c", "maintenance.auto=false",
         "-c", "commit.gpgsign=false", "-c", "core.quotePath=false", "-C", str(repo), *args],
        env=process_env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    if check and result.returncode:
        fail(f"git {' '.join(args)} failed in {repo}:\n"
             + result.stderr.decode("utf-8", "replace")
             + result.stdout.decode("utf-8", "replace"))
    return result


def text(repo, *args):
    return git(repo, *args).stdout.decode("utf-8").strip()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def patch_name(value):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*\.patch", value):
        fail(f"Unsafe patch name: {value!r}; use a basename ending in .patch")
    return value


def manifest():
    value = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if value.get("schema") != 1 or not isinstance(value.get("version"), str):
        fail("upstream.json must have schema: 1 and a version string")
    packages = value.get("packages")
    if not isinstance(packages, dict) or AGENT not in packages:
        fail(f"upstream.json must pin {AGENT}")
    for name, pin in packages.items():
        if not name.startswith("@oh-my-pi/") or not isinstance(pin, dict):
            fail(f"Invalid package pin: {name}")
        if pin.get("version") != value["version"]:
            fail(f"Package {name} is not pinned to {value['version']}")
        integrity_hash(pin.get("integrity", ""))
        https_url(pin.get("tarball", ""))
    names = value.get("patches")
    if not isinstance(names, list) or len(set(names)) != len(names):
        fail("upstream.json patches must be an ordered list without duplicates")
    for name in names:
        patch_name(name)
        path = PATCHES / name
        if path.is_symlink() or not path.is_file():
            fail(f"Missing regular patch file: {path}")
    if PATCHES.is_symlink():
        fail("patches must not be a symlink")
    extras = {p.name for p in PATCHES.glob("*.patch")} - set(names)
    if extras:
        fail("Unlisted patches: " + ", ".join(sorted(extras)))
    return value


def https_url(url):
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password:
        fail(f"Expected an HTTPS URL without credentials: {url!r}")
    return url


def ssl_context():
    # create_default_context honors SSL_CERT_FILE / SSL_CERT_DIR and system trust.
    context = ssl.create_default_context()
    extra = os.environ.get("NODE_EXTRA_CA_CERTS")
    if extra:
        context.load_verify_locations(cafile=extra)
    return context


def fetch(url, destination):
    request = urllib.request.Request(https_url(url), headers={"User-Agent": "Ombra-engine-patcher/1"})
    with urllib.request.urlopen(request, context=ssl_context(), timeout=120) as response:
        https_url(response.url)
        shutil.copyfileobj(response, destination)


def integrity_hash(integrity):
    choices = []
    for token in integrity.split():
        algorithm, separator, encoded = token.partition("-")
        if separator and algorithm in ("sha256", "sha384", "sha512"):
            try:
                digest = base64.b64decode(encoded, validate=True)
            except ValueError:
                fail("Invalid base64 in npm integrity")
            if len(digest) != hashlib.new(algorithm).digest_size:
                fail("Wrong digest length in npm integrity")
            choices.append((int(algorithm[3:]), algorithm, digest))
    if not choices:
        fail("A sha256/sha384/sha512 npm integrity pin is required")
    strength = max(item[0] for item in choices)
    return [(algorithm, digest) for size, algorithm, digest in choices if size == strength]


def verify(path, integrity):
    choices = integrity_hash(integrity)
    digest = hashlib.new(choices[0][0])
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    if not any(digest.digest() == expected for _, expected in choices):
        fail(f"Integrity mismatch: {path}; remove this cached file and retry only after checking the pin")


def tarball(pin):
    CACHE.mkdir(parents=True, exist_ok=True)
    key = hashlib.sha256(pin["integrity"].encode()).hexdigest()
    path = CACHE / (key + ".tgz")
    if not path.exists():
        stream = tempfile.NamedTemporaryFile(dir=CACHE, suffix=".download", delete=False)
        temporary = Path(stream.name)
        try:
            with stream:
                fetch(pin["tarball"], stream)
            # Close before publication: Windows does not rename open files.
            verify(temporary, pin["integrity"])
            os.replace(temporary, path)
        finally:
            stream.close()
            temporary.unlink(missing_ok=True)
    verify(path, pin["integrity"])
    return path


def owned(path):
    return path in TOP_FILES or path.startswith("src/")


def archive_files(path):
    """Validate every member, not just the vendored subset; never extractall."""
    with tarfile.open(path, "r:gz") as archive:
        seen = set()
        for member in archive:
            raw = member.name.rstrip("/")
            parts = raw.split("/")
            if (not parts or parts[0] != "package" or any(p in ("", ".", "..") for p in parts)
                    or any(p.lower() == ".git" for p in parts)
                    or "\\" in raw or ":" in raw or "\x00" in raw):
                fail(f"Unsafe tar member: {member.name!r}")
            if not member.isdir() and not member.isfile():
                fail(f"Tar links/devices are not allowed: {member.name!r}")
            if raw in seen:
                fail(f"Duplicate tar member: {raw}")
            seen.add(raw)
            if member.isfile():
                if len(parts) < 2:
                    fail("Tar package root is not a directory")
                yield "/".join(parts[1:]), member, archive


def download_packages(value, destination=None):
    for name, pin in value["packages"].items():
        archive_path = tarball(pin)
        package_info = None
        for relative, member, archive in archive_files(archive_path):
            if relative == "package.json":
                with archive.extractfile(member) as stream:
                    package_info = json.load(stream)
            if name == AGENT and destination is not None and owned(relative):
                target = destination / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.extractfile(member) as source, target.open("wb") as output:
                    shutil.copyfileobj(source, output)
                target.chmod(0o755 if member.mode & 0o111 else 0o644)
        if not package_info or package_info.get("name") != name or package_info.get("version") != pin["version"]:
            fail(f"Tarball package identity does not match pin: {name}@{pin['version']}")
    if destination is not None:
        missing = [name for name in TOP_FILES if not (destination / name).is_file()]
        if missing or not (destination / "src").is_dir():
            fail(f"Upstream package lacks vendored source/files: {missing}")


def snapshot(directory):
    result = {}
    if directory.is_symlink():
        fail(f"Source directory must not be a symlink: {directory}")
    for name in (*TOP_FILES, "src"):
        path = directory / name
        if path.is_symlink():
            fail(f"Symlinks are not supported in owned source: {path}")
        if not path.exists():
            continue
        if path.is_dir() != (name == "src"):
            fail(f"Expected {'directory' if name == 'src' else 'file'}: {path}")
        paths = sorted(path.rglob("*")) if path.is_dir() else [path]
        for item in paths:
            if any(part.lower() == ".git" for part in item.relative_to(directory).parts):
                fail(f"Nested Git metadata is not vendored source: {item}")
            if item.is_symlink():
                fail(f"Symlinks are not supported in owned source: {item}")
            if item.is_dir():
                continue
            if not item.is_file():
                fail(f"Not a regular source file: {item}")
            result[item.relative_to(directory).as_posix()] = (
                item.read_bytes(), bool(item.stat().st_mode & 0o111) if os.name != "nt" else False)
    return result


def put_files(directory, entries):
    # Delete first, deepest-first, to support file <-> directory transitions.
    for relative in sorted(entries, key=lambda name: (-name.count("/"), name)):
        parts = PurePosixPath(relative).parts
        if (not owned(relative) or ".." in parts or relative.startswith("/")
                or any(part.lower() == ".git" for part in parts) or "\\" in relative):
            fail(f"Patch touches an unsafe/non-vendored path: {relative}")
        path = directory / relative
        if path.is_symlink():
            fail(f"Unexpected symlink: {path}")
        if path.is_file():
            path.unlink()
            parent = path.parent
            while parent != directory and not any(parent.iterdir()):
                parent.rmdir()
                parent = parent.parent
    for relative, entry in sorted(entries.items()):
        if entry is None:
            continue
        path = directory / relative
        if path.is_dir():
            path.rmdir()  # Never remove a nonempty directory to make a patch fit.
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(entry[0])
        path.chmod(0o755 if entry[1] else 0o644)


def initialize(repo, value):
    repo.mkdir(parents=True)
    download_packages(value, repo)
    git(repo, "init", "--quiet")
    git(repo, "config", "core.fileMode", "false" if os.name == "nt" else "true")
    git(repo, "add", "--force", "--all")
    git(repo, "commit", "--quiet", "-m", "Pristine upstream " + value["version"])
    return text(repo, "rev-parse", "HEAD")


def validate_tree(repo):
    for entry in git(repo, "ls-tree", "-rz", "HEAD").stdout.split(b"\0"):
        if not entry:
            continue
        info, path = entry.split(b"\t", 1)
        if info.split()[0] not in (b"100644", b"100755") or not owned(path.decode("utf-8")):
            fail("Patch produced a non-vendored path or non-regular file: " + os.fsdecode(path))


def apply_series(repo, value):
    commits = []
    for name in value["patches"]:
        before = text(repo, "rev-parse", "HEAD")
        git(repo, "am", "--3way", "--keep-cr", str(PATCHES / name))
        commit = text(repo, "rev-parse", "HEAD")
        if text(repo, "rev-list", "--count", before + ".." + commit) != "1":
            fail(f"{name} must contain exactly one mail patch")
        validate_tree(repo)
        commits.append(commit)
    return commits


def differences(first, second):
    return sorted(name for name in first.keys() | second.keys() if first.get(name) != second.get(name))


def fingerprint(value):
    digest = hashlib.sha256()
    for name, data in [("upstream.json", MANIFEST.read_bytes())] + [
            ("patches/" + name, (PATCHES / name).read_bytes()) for name in value["patches"]]:
        digest.update(name.encode() + b"\0" + hashlib.sha256(data).digest())
    for name, (data, executable) in sorted(snapshot(SOURCE).items()):
        digest.update(name.encode() + b"\0" + bytes([executable]) + hashlib.sha256(data).digest())
    return digest.hexdigest()


def ensure_unchanged(value, expected):
    if fingerprint(value) != expected:
        fail("Source, patches or upstream.json changed during this operation; nothing will be overwritten")


def changed_paths(repo, commit):
    raw = git(repo, "diff-tree", "--no-commit-id", "--name-only", "--no-renames", "-r", "-z", commit).stdout
    return [name.decode("utf-8") for name in raw.split(b"\0") if name]


def commit_info(repo, commit):
    raw = git(repo, "show", "-s", "--format=%an%x00%ae%x00%aI%x00%B", commit).stdout.decode("utf-8")
    name, email, date, message = raw.split("\0", 3)
    return message, {"GIT_AUTHOR_NAME": name, "GIT_AUTHOR_EMAIL": email, "GIT_AUTHOR_DATE": date}


def tree_entry(repo, commit, path):
    raw = git(repo, "ls-tree", "-z", commit, "--", ":(literal)" + path).stdout
    if not raw:
        return None
    info, _ = raw.rstrip(b"\0").split(b"\t", 1)
    mode, kind, blob = info.split()
    if mode not in (b"100644", b"100755") or kind != b"blob":
        fail(f"Non-regular patch path: {path}")
    return git(repo, "cat-file", "blob", blob.decode()).stdout, mode == b"100755" and os.name != "nt"


def mail_patch(repo, commit):
    return git(repo, "format-patch", "--stdout", "--full-index", "--binary", "--no-signature",
               "--no-stat", "--zero-commit", "-1", commit).stdout


@contextlib.contextmanager
def publication_stage():
    # Unlike TemporaryDirectory, retain recovery material if rollback itself fails.
    staging = Path(tempfile.mkdtemp(prefix="publish-", dir=WORK))
    try:
        yield staging
    except BaseException:
        print(f"Publication recovery files retained at {staging}", file=sys.stderr)
        raise
    else:
        shutil.rmtree(staging)


def publish(old, expected, new, source=None, patches=None):
    """Stage first; swap only owned paths. Roll back every rename on exceptions."""
    ensure_unchanged(old, expected)
    with publication_stage() as staging:
        replacements = []
        if source is not None:
            for name in ("src", *TOP_FILES):
                original = source / name
                staged = staging / "source" / name
                staged.parent.mkdir(parents=True, exist_ok=True)
                if original.is_dir():
                    shutil.copytree(original, staged)
                elif original.exists():
                    shutil.copy2(original, staged)
                replacements.append((SOURCE / name, staged if staged.exists() else None))
        if patches is not None:
            for name in dict.fromkeys(old["patches"] + new["patches"]):
                staged = staging / "patches" / name
                if name in patches:
                    staged.parent.mkdir(parents=True, exist_ok=True)
                    staged.write_bytes(patches[name])
                replacements.append((PATCHES / name, staged if name in patches else None))
            staged = staging / "upstream.json"
            write_json(staged, new)
            replacements.append((MANIFEST, staged))
        ensure_unchanged(old, expected)
        backups = staging / "backups"
        backups.mkdir()
        write_json(staging / "transaction.json", [
            {"destination": str(destination), "backup": str(backups / str(index)),
             "replacement": str(staged) if staged is not None else None}
            for index, (destination, staged) in enumerate(replacements)
        ])
        completed = []
        try:
            for index, (destination, staged) in enumerate(replacements):
                destination.parent.mkdir(parents=True, exist_ok=True)
                backup = backups / str(index)
                if destination.exists():
                    os.replace(destination, backup)
                completed.append((destination, backup))
                if staged is not None:
                    os.replace(staged, destination)
        except BaseException:
            for destination, backup in reversed(completed):
                if destination.is_dir():
                    shutil.rmtree(destination)
                elif destination.exists():
                    destination.unlink()
                if backup.exists():
                    os.replace(backup, destination)
            raise


def require_no_rebase():
    if REBASE.exists():
        fail(f"A rebase workdir already exists: {REBASE}\n"
             "Use rebase --continue, or move the entire directory aside to abandon it (owned source is unchanged).")


def import_source(args):
    require_no_rebase()
    value = manifest()
    expected = fingerprint(value)
    with tempfile.TemporaryDirectory(prefix="import-", dir=WORK) as temporary:
        repo = Path(temporary) / "repo"
        initialize(repo, value)
        apply_series(repo, value)
        changed = differences(snapshot(repo), snapshot(SOURCE))
        if not changed:
            print(f"Engine matches {value['version']} + {len(value['patches'])} ordered patches")
            return
        print("Source differs:\n" + "\n".join("  " + name for name in changed))
        if args.check:
            fail("Owned coding-agent differs from upstream + patches; export intentional source edits")
        dirty = git(ROOT, "status", "--porcelain", "--untracked-files=all", "--", str(SOURCE)).stdout
        if dirty:
            fail("Refusing to overwrite local coding-agent edits; export them first (or save them outside the source tree)")
        publish(value, expected, value, source=repo)
        print("Imported owned coding-agent; unrelated engine files were not touched")


def record_drops(value, names, reason):
    for name in names:
        print(f"Dropped empty patch: {name} ({reason})")
        value.setdefault("droppedPatches", []).append({
            "name": name, "version": value["version"], "reason": reason,
        })


def export_source(args):
    require_no_rebase()
    value = manifest()
    expected = fingerprint(value)
    current = snapshot(SOURCE)
    with tempfile.TemporaryDirectory(prefix="export-", dir=WORK) as temporary:
        repo = Path(temporary) / "repo"
        base = initialize(repo, value)
        commits = apply_series(repo, value)
        changed = differences(snapshot(repo), current)
        if not changed:
            print("No source edits to export")
            return
        names = list(value["patches"])
        owner = {}
        touched = []
        for index, commit in enumerate(commits):
            paths = changed_paths(repo, commit)
            touched.append(paths)
            owner.update((path, index) for path in paths)
        selected = None
        if args.patch:
            name = patch_name(args.patch)
            if name not in names:
                if not args.why:
                    fail("A new --patch requires --why; optionally supply --what and --upstream-status")
                names.append(name)
                touched.append([])
            selected = names.index(name)
        explicit = {}
        for assignment in args.assign:
            path, separator, name = assignment.partition("=")
            if not separator or path not in changed or path in owner:
                fail("--assign requires a changed, previously unowned path: PATH=EXISTING.patch")
            if patch_name(name) not in names:
                fail(f"--assign patch is not in the series: {name}")
            if path in explicit:
                fail(f"Duplicate --assign path: {path}")
            explicit[path] = names.index(name)
        assignments = {}
        for path in changed:
            if path in owner:
                index = owner[path]
            elif path in explicit:
                index = explicit[path]
            elif selected is not None:
                index = selected
            else:
                fail(f"New path {path} has no owning patch; export with --patch NAME.patch --why REASON")
            assignments.setdefault(index, []).append(path)
        git(repo, "checkout", "--quiet", "--detach", base)
        output = {}
        dropped = []
        for index, name in enumerate(names):
            # Replay exact per-patch path versions, then layer owned edits at the last owner.
            entries = {path: tree_entry(repo, commits[index], path) for path in touched[index]}
            entries.update((path, current.get(path)) for path in assignments.get(index, []))
            put_files(repo, entries)
            git(repo, "add", "--force", "--all")
            if git(repo, "diff", "--cached", "--quiet", check=False).returncode == 0:
                dropped.append(name)
                continue
            if index < len(commits):
                message, author = commit_info(repo, commits[index])
            else:
                what = args.what or name.removesuffix(".patch").replace("-", " ")
                message = (f"{what}\n\nWhat: {what}\nWhy: {args.why}\n"
                           f"Upstream-Status: {args.upstream_status}\n")
                author = {}
            git(repo, "commit", "--quiet", "-m", message, env=author)
            validate_tree(repo)
            output[name] = mail_patch(repo, "HEAD")
        if differences(snapshot(repo), current):
            fail("Export would not reproduce current source; no patches were changed")
        updated = dict(value, patches=list(output))
        record_drops(updated, dropped, "empty after export")
        publish(value, expected, updated, patches=output)
        print(f"Exported {len(output)} patches, preserving names/order; coding-agent was not modified")


def target_manifest(old, version):
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", version):
        fail("rebase requires an exact npm version, not a tag or range")
    updated = dict(old, version=version, packages={})
    for name, previous in old["packages"].items():
        url = "https://registry.npmjs.org/" + urllib.parse.quote(name, safe="") + "/" + version
        with tempfile.TemporaryFile() as stream:
            fetch(url, stream)
            stream.seek(0)
            metadata = json.load(stream)
        if metadata.get("name") != name or metadata.get("version") != version:
            fail(f"Registry metadata identity mismatch for {name}@{version}")
        dist = metadata["dist"]
        integrity_hash(dist.get("integrity", ""))
        https_url(dist["tarball"])
        updated["packages"][name] = dict(previous, version=version,
                                           tarball=dist["tarball"], integrity=dist["integrity"])
    return updated


def save_state(state):
    write_json(REBASE / "state.json", state)


def conflict_instructions(name):
    print(f"\nStopped at patch: {name}\nWorkdir: {REBASE / 'repo'}\n"
          "Resolve conflict markers there, then stage each resolution:\n"
          f"  git -C '{REBASE / 'repo'}' add <resolved-paths>\n"
          "Use git rm for resolved deletions. Then, from the main repository:\n"
          "  python3 runtime/engine/patcher.py rebase --continue\n"
          "If a patch is entirely upstream, resolve it to the current HEAD tree and stage;\n"
          "--continue records/drops the empty patch automatically. Do not run git cherry-pick --skip.\n"
          "Owned source, patches and manifest remain unchanged until every patch succeeds.\n"
          "To abandon, move .temp/engine-patcher/rebase aside; it contains all conflict work.", file=sys.stderr)


def finish_pending(state):
    repo = REBASE / "repo"
    index = state["index"]
    name = state["old"]["patches"][index]
    if git(repo, "ls-files", "--unmerged", "-z").stdout:
        conflict_instructions(name)
        fail("Unresolved paths remain")
    if git(repo, "diff", "--quiet", check=False).returncode:
        conflict_instructions(name)
        fail("Unstaged resolutions remain; stage them before --continue")
    if git(repo, "ls-files", "--others", "--exclude-standard", "-z").stdout:
        fail("Untracked files remain in rebase workdir; stage intended source or move scratch files out")
    head = text(repo, "rev-parse", "HEAD")
    pending = (repo / ".git" / "CHERRY_PICK_HEAD").exists()
    if not pending and head != state["before"] and git(repo, "diff", "--cached", "--quiet", check=False).returncode:
        fail("Unexpected staged edits after the patch commit; commit only the active resolution before continuing")
    if head == state["before"] and git(repo, "diff", "--cached", "--quiet", check=False).returncode == 0:
        if pending:
            git(repo, "cherry-pick", "--skip")
        state["dropped"].append(name)
        print(f"Patch now empty/upstream: {name}")
    else:
        if pending:
            result = git(repo, "cherry-pick", "--continue", check=False)
            if result.returncode:
                conflict_instructions(name)
                fail(result.stderr.decode("utf-8", "replace"))
        elif head == state["before"]:
            fail("Missing cherry-pick state; do not manually abort/skip the active patch")
        head = text(repo, "rev-parse", "HEAD")
        if text(repo, "rev-list", "--count", state["before"] + ".." + head) != "1":
            fail("Expected exactly one resolved commit; do not rewrite the rebase workdir history")
        validate_tree(repo)
        state["rebased"].append({"name": name, "commit": head})
    state["index"] += 1
    state["pending"] = False
    save_state(state)


def rebase_source(args):
    if args.resume:
        if args.version:
            fail("Do not supply VERSION with rebase --continue")
        if not (REBASE / "state.json").is_file():
            fail(f"No resumable rebase at {REBASE}; inspect/move aside any incomplete setup")
        state = json.loads((REBASE / "state.json").read_text(encoding="utf-8"))
        ensure_unchanged(state["old"], state["fingerprint"])
    else:
        require_no_rebase()
        if not args.version:
            fail("Specify rebase VERSION or rebase --continue")
        old = manifest()
        expected = fingerprint(old)
        new = target_manifest(old, args.version)
        # Fetch and validate every pin before creating any persistent rebase state.
        download_packages(new)
        with tempfile.TemporaryDirectory(prefix="rebase-setup-", dir=WORK) as temporary:
            repo = Path(temporary) / "repo"
            base = initialize(repo, old)
            commits = apply_series(repo, old)
            if differences(snapshot(repo), snapshot(SOURCE)):
                fail("Source has unexported edits; run export before rebase")
            old_tip = text(repo, "rev-parse", "HEAD")
            git(repo, "branch", "ombra-old", old_tip)
            git(repo, "checkout", "--quiet", "--detach", base)
            with tempfile.TemporaryDirectory(prefix="upstream-", dir=WORK) as upstream:
                pristine = Path(upstream)
                download_packages(new, pristine)
                old_files, new_files = snapshot(repo), snapshot(pristine)
                put_files(repo, {path: new_files.get(path) for path in differences(old_files, new_files)})
            git(repo, "add", "--force", "--all")
            git(repo, "commit", "--allow-empty", "--quiet", "-m", "Pristine upstream " + new["version"])
            git(repo, "branch", "ombra-upstream", "HEAD")
            ensure_unchanged(old, expected)
            REBASE.mkdir()
            shutil.move(str(repo), str(REBASE / "repo"))
        state = {"old": old, "new": new, "fingerprint": expected, "commits": commits,
                 "index": 0, "pending": False, "rebased": [], "dropped": []}
        save_state(state)
    repo = REBASE / "repo"
    if state["pending"]:
        # A kill between saving state and starting git must not silently drop a patch.
        if (text(repo, "rev-parse", "HEAD") == state["before"]
                and not (repo / ".git" / "CHERRY_PICK_HEAD").exists()):
            if git(repo, "status", "--porcelain", "--untracked-files=all").stdout:
                fail("Pending patch never started but workdir changed; preserve edits and restore the workdir before resuming")
            state["pending"] = False
            save_state(state)
        else:
            finish_pending(state)
    while state["index"] < len(state["commits"]):
        index = state["index"]
        state["before"] = text(repo, "rev-parse", "HEAD")
        state["pending"] = True
        save_state(state)
        result = git(repo, "cherry-pick", state["commits"][index], check=False)
        if result.returncode and git(repo, "ls-files", "--unmerged", "-z").stdout:
            conflict_instructions(state["old"]["patches"][index])
            fail(result.stderr.decode("utf-8", "replace"))
        if result.returncode and not (repo / ".git" / "CHERRY_PICK_HEAD").exists():
            fail("Cherry-pick failed before starting:\n" + result.stderr.decode("utf-8", "replace"))
        finish_pending(state)
    output = {item["name"]: mail_patch(repo, item["commit"]) for item in state["rebased"]}
    updated = dict(state["new"], patches=list(output))
    record_drops(updated, state["dropped"], "empty/upstream after rebase")
    publish(state["old"], state["fingerprint"], updated, source=repo, patches=output)
    shutil.rmtree(REBASE)
    print(f"Rebased to {updated['version']}; published source, {len(output)} patches and all package pins")
    print("Update runtime dependency/pack pins and regenerate the engine artifact before release (see README.md)")


def status(args):
    value = manifest()
    print(f"Upstream: {value['version']}")
    for name, pin in value["packages"].items():
        print(f"  {name}@{pin['version']}")
    names = value["patches"]
    total = sum((PATCHES / name).stat().st_size for name in names)
    print(f"Ordered patches: {len(names)} ({total:,} bytes)")
    for name in names:
        patch = PATCHES / name
        stats = git(ROOT, "apply", "--numstat", "-z", str(patch)).stdout
        print(f"  {name} ({patch.stat().st_size:,} bytes)")
        for entry in stats.split(b"\0"):
            if entry:
                added, removed, path = entry.decode("utf-8").split("\t", 2)
                print(f"    +{added}/-{removed} {path}")
    if REBASE.exists():
        state_path = REBASE / "state.json"
        if state_path.exists():
            state = json.loads(state_path.read_text(encoding="utf-8"))
            print(f"Rebase to {state['new']['version']}: {state['index']}/{len(state['commits'])} patches complete")
            if state["index"] < len(state["commits"]):
                print("Next/active patch: " + state["old"]["patches"][state["index"]])
        else:
            print("Incomplete rebase setup; inspect workdir before moving it aside")
        print(f"Workdir: {REBASE / 'repo'}; resume with rebase --continue")
    dirty = git(ROOT, "status", "--short", "--", str(SOURCE), str(PATCHES), str(MANIFEST)).stdout.decode()
    print(f"Working-tree changed entries: {len(dirty.splitlines())} (git status shows details)")
    print("For source/patch equivalence (integrity-verified downloads), run: patcher.py --check")


@contextlib.contextmanager
def locked():
    WORK.mkdir(parents=True, exist_ok=True)
    path = WORK / "lock"
    try:
        path.mkdir()
    except FileExistsError:
        fail(f"Another patcher owns {path}. If no patcher is running, remove this stale lock directory.")
    try:
        (path / "pid").write_text(str(os.getpid()) + "\n", encoding="ascii")
        yield
    finally:
        shutil.rmtree(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="alias for import --check")
    commands = parser.add_subparsers(dest="command")
    importing = commands.add_parser("import", help="reconstruct upstream + patches into coding-agent")
    importing.add_argument("--check", dest="import_check", action="store_true", help="compare only; do not change owned files")
    exporting = commands.add_parser("export", help="export owned source edits as ordered mail patches")
    exporting.add_argument("--patch", help="patch for paths without an existing owner; new names append to the series")
    exporting.add_argument("--assign", action="append", default=[], metavar="PATH=PATCH",
                           help="assign one previously unowned path to an existing patch; repeat for multiple features")
    exporting.add_argument("--what", help="new patch subject/What header")
    exporting.add_argument("--why", help="required rationale for a new patch")
    exporting.add_argument("--upstream-status", default="Ombra-specific; not submitted", help="new patch upstream disposition")
    rebasing = commands.add_parser("rebase", help="3-way rebase each patch onto an exact upstream version")
    rebasing.add_argument("version", nargs="?")
    rebasing.add_argument("--continue", dest="resume", action="store_true", help="resume after staging conflict resolutions")
    commands.add_parser("status", help="show pins, patch order and pending rebase (no network)")
    args = parser.parse_args()
    args.check = args.check or getattr(args, "import_check", False)
    if args.check and args.command not in (None, "import"):
        parser.error("--check is only valid with import or without a command")
    if args.command is None:
        if not args.check:
            parser.error("choose import, export, rebase or status")
        args.command = "import"
    try:
        with locked():
            {"import": import_source, "export": export_source, "rebase": rebase_source, "status": status}[args.command](args)
    except (PatcherError, OSError, ValueError, KeyError, tarfile.TarError) as error:
        print(f"patcher: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("patcher: interrupted; inspect status before resuming", file=sys.stderr)
        return 130
    return 0


if __name__ == "__main__":
    sys.exit(main())

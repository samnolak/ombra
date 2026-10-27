#!/usr/bin/env python3
"""Regenerate the embedded owned engine manifest: python3 runtime/generate-engine-artifact.py.

Run after editing runtime/engine or harness/profile. --check compares without writing (release/CI gate).
The source artifact carries the engine (installed at <pack>/omp/engine) and the Ombra harness from harness/profile
(installed at <pack>/omp/harness, without caches and *.seed templates); file paths are relative to <pack>/omp.
No network, timestamps, git-derived identity, source transformations, or dependency installs.
"""
import argparse
import hashlib
import json
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parent.parent
NAME = "ombra-engine-18.12.2-bun-1.4.2-v1"
PACK = ROOT / "runtime" / "packs" / NAME
ENGINE = ROOT / "runtime" / "engine"
HARNESS = ROOT / "harness" / "profile"
MAX_FILES, MAX_FILE_BYTES, MAX_TOTAL_BYTES = 8192, 16 << 20, 128 << 20


def collect(base, prefix, skip, files, seen):
    """Appends every file under base to files as prefix/<relative path>, with size and sha256; returns the bytes."""
    total = 0
    for source in sorted(base.rglob("*"), key=lambda path: path.relative_to(base).as_posix()):
        relative = source.relative_to(base)
        if skip(relative):
            continue
        if source.is_symlink():
            raise ValueError(f"Symlinks are not permitted in the source artifact: {source}")
        if not source.is_file():
            continue
        path = f"{prefix}/{relative.as_posix()}"
        if path.casefold() in seen or any(c in path for c in "\\:\n\r\0"):
            raise ValueError(f"Unsafe or duplicate source path: {path}")
        seen.add(path.casefold())
        data = source.read_bytes()
        total += len(data)
        if len(data) > MAX_FILE_BYTES:
            raise ValueError("Source artifact exceeds installer size bounds")
        files.append({"path": path, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
    return total


def skip_engine(relative):
    if relative.parts[0] in {".temp", "patches", "patcher.py", "upstream.json", "README.md",
                             "check-upstream.py", "upstream-fixtures.patch", "upstream-expected-failures.json"}:
        return True
    if any(part in {"node_modules", ".git", "__pycache__"} for part in relative.parts):
        raise ValueError(f"Non-source directory in engine: {relative.as_posix()}")
    return False


def skip_harness(relative):
    """Caches and local templates (*.seed: a user's own config files, seeded by hand) never ship."""
    return (any(part in {"__pycache__", ".DS_Store", ".git", "node_modules"} for part in relative.parts)
            or relative.suffix in {".pyc", ".seed"})


def render():
    if not (HARNESS / "agent" / "AGENTS.md").is_file():
        raise SystemExit(f"Ombra harness missing: {HARNESS} must contain agent/AGENTS.md")
    files = []
    seen = set()
    total = collect(ENGINE, "engine", skip_engine, files, seen)
    total += collect(HARNESS, "harness", skip_harness, files, seen)
    if total > MAX_TOTAL_BYTES:
        raise ValueError("Source artifact exceeds installer size bounds")
    required = {"engine/host.ts", "engine/session-factory.ts", "engine/coding-agent/package.json", "engine/coding-agent/LICENSE",
                "engine/coding-agent/THIRD-PARTY-NOTICES.txt", "engine/coding-agent/src/cli.ts", "engine/coding-agent/src/sdk.ts",
                "harness/agent/AGENTS.md"}
    if not required.issubset({f["path"] for f in files}) or not 1 <= len(files) <= MAX_FILES:
        raise ValueError("Incomplete owned engine source tree")
    digest = hashlib.sha256("".join(f'{f["path"]}\0{f["sha256"]}\n' for f in files).encode()).hexdigest()
    manifest = {
        "schema": 3,
        "name": NAME,
        "revision": 1,
        "omp": {"package": "@oh-my-pi/pi-coding-agent", "version": "18.12.2", "integrity": "sha512-1qh9EwuiMBUTVix0ZWZNOMPTW++nWhL6t3EaRhGe05sVIBfDTyVl36iTyDv0TvM+R/EmOL4PvFrmIPgXd3xYQQ==", "entry": "engine/coding-agent/src/cli.ts"},
        "engine": {"entry": "engine/host.ts", "protocolVersion": 1},
        "bun": {"version": "1.4.2", "revision": "744846f84"},
        "sourceArtifact": {
            "identity": "ombra-engine-v1", "kind": "complete-owned-source",
            "upstreamBase": {
                "repository": "https://github.com/can1357/oh-my-pi",
                "revision": "1d5aff90b057ce7dbfe32ea557a527c1b3144b72",
                "attestation": "https://registry.npmjs.org/-/npm/v1/attestations/@oh-my-pi%2fpi-coding-agent@18.12.2",
                "verification": "npm tarball integrity verified; SLSA payload decoded, signatures not independently verified",
            },
            "sha256": digest, "bytes": total, "files": files,
        },
        "notes": "Complete repository-owned engine and host, with upstream license and notices, and the Ombra harness (harness/profile) every session loads. Paths are relative to the installed omp folder. Content hash identifies local source, not a fabricated fork commit. Frozen npm dependencies and Bun are integrity pinned; their installed sources are never rewritten.",
    }
    manifest_bytes = (json.dumps(manifest, indent=2, ensure_ascii=False) + "\n").encode()
    manifest_hash = hashlib.sha256(manifest_bytes).hexdigest()
    identity = ('// Generated by runtime/generate-engine-artifact.py; do not edit.\n'
                'namespace Ombra.App.Services.Runtime;\n\n'
                'public static partial class RuntimePack\n{\n'
                f'    public const string SourceSha256 = "{digest}";\n'
                f'    public const string ManifestSha256 = "{manifest_hash}";\n}}\n')
    resources = ['<!-- Generated by runtime/generate-engine-artifact.py; do not edit. -->', '<Project>', '  <ItemGroup>']
    roots = {"engine": "../../engine/", "harness": "../../../harness/profile/"}
    for file in files:
        root, relative = file["path"].split("/", 1)
        source = escape(roots[root] + relative, {'"': '&quot;'})
        path = escape(file["path"], {'"': '&quot;'})
        resources.append(f'    <EmbeddedResource Include="$(MSBuildThisFileDirectory){source}" LogicalName="runtime-pack/{path}" WithCulture="false" />')
    resources += ['  </ItemGroup>', '</Project>', '']
    return {
        PACK / "pack.json": manifest_bytes,
        PACK / "engine-resources.props": "\n".join(resources).encode(),
        ROOT / "avalonia/src/Ombra.App/Services/Runtime/RuntimePack.Identity.g.cs": identity.encode(),
    }

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if generated manifest, hashes or resource list are stale")
    parser.add_argument("--out", type=Path, help="write the artifacts under this folder (same relative layout) instead of the repository")
    args = parser.parse_args()
    stale = []
    for path, data in render().items():
        if args.check:
            if not path.exists() or path.read_bytes() != data:
                stale.append(str(path.relative_to(ROOT)))
        else:
            if args.out is not None:
                path = args.out / path.relative_to(ROOT)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
    if stale:
        parser.exit(1, "Owned engine artifacts are stale; run python3 runtime/generate-engine-artifact.py\n" + "\n".join(stale) + "\n")
    if not args.check:
        print(f"Generated {NAME} manifest, resource list and content identity")


if __name__ == "__main__":
    main()

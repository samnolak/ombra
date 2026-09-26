#!/usr/bin/env python3
"""Writes update.json (schema 2) for the packages in a folder, as the client's update check reads it.

    tools/release/make-manifest.py <dist-dir> --version 0.6.0 [--base-url URL] [--repo owner/name] [--notes-file F]

Every omp-gui-<version>-<rid>.(tar.gz|zip) in <dist-dir> becomes an asset (size and SHA-256 computed here; a
<package>.sha256 next to it must agree). The URL of each package is <base-url>/<file>; the default base URL is the
GitHub release of v<version> in --repo (or $GITHUB_REPOSITORY). "runtime" names the pinned omp pack this version
brings (RuntimePack.Name in the client source, with its runtime/packs/<name>/pack.json), so a client can tell the
user that omp moves too. Sign the result with tools/release/sign-manifest.sh.
"""
import argparse, hashlib, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
AVALONIA = os.path.dirname(os.path.dirname(HERE))
ROOT = os.path.dirname(AVALONIA)
RIDS = "linux-x64|linux-arm64|win-x64|win-arm64|osx-arm64|osx-x64"


def runtime_pack():
    src = open(os.path.join(AVALONIA, "src/OmpGui.App/Services/Runtime/RuntimePack.cs"), encoding="utf-8").read()
    name = re.search(r'public const string Name = "([^"]+)";', src).group(1)
    pack = json.load(open(os.path.join(ROOT, "runtime/packs", name, "pack.json"), encoding="utf-8"))
    if pack["name"] != name:
        sys.exit(f"runtime/packs/{name}/pack.json names {pack['name']}")
    return {"pack": name, "omp": pack["omp"]["version"], "bun": pack["bun"]["version"]}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dist")
    ap.add_argument("--version", required=True)
    ap.add_argument("--base-url")
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY"))
    ap.add_argument("--notes-file")
    ap.add_argument("--notes")
    ap.add_argument("--out")
    a = ap.parse_args()
    base = a.base_url or (a.repo and f"https://github.com/{a.repo}/releases/download/v{a.version}")
    if not base:
        sys.exit("--base-url or --repo is needed")
    notes = open(a.notes_file, encoding="utf-8").read().strip() if a.notes_file else (a.notes or "")
    assets = {}
    for name in sorted(os.listdir(a.dist)):
        m = re.match(rf"omp-gui-(.+)-({RIDS})\.(tar\.gz|zip)$", name)
        if not m:
            continue
        if m.group(1) != a.version:
            sys.exit(f"{name} is not version {a.version}")
        path = os.path.join(a.dist, name)
        sha = hashlib.sha256(open(path, "rb").read()).hexdigest()
        side = path + ".sha256"
        if os.path.exists(side) and open(side).read().split()[0].lower() != sha:
            sys.exit(f"{name}: SHA-256 differs from {os.path.basename(side)}")
        assets[m.group(2)] = {"file": name, "url": f"{base.rstrip('/')}/{name}", "sha256": sha, "size": os.path.getsize(path)}
    if not assets:
        sys.exit(f"no omp-gui-{a.version}-<rid> packages in {a.dist}")
    manifest = {"schema": 2, "version": a.version, "notes": notes, "runtime": runtime_pack(), "assets": assets}
    out = a.out or os.path.join(a.dist, "update.json")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(open(out, encoding="utf-8").read())


if __name__ == "__main__":
    main()

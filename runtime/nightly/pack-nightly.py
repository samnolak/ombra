#!/usr/bin/env python3
"""Packs a Nightly build as Ombra's pinned runtime package (K1 delivery).

Until a Developer ID signed Nightly exists, Ombra installs the desktop repo's
own build: this copies a Nightly.app (the dev ``dist/Nightly.app`` or the app
inside ``./mach package`` output), signs the copy ad hoc with the hardened
runtime (``codesign --force --deep -s - --options runtime`` with
``nightly.entitlements.plist``), zips it with ``ditto`` (bundle symlinks,
modes and extended attributes kept) and writes ``nightly-runtime.json`` next to
this script: the archive's SHA-256 and size, the app's Version and BuildID
(``Contents/Resources/application.ini``), the embed protocol it speaks
(``kProtocolVersion`` in ``src/zen/embed/ZenEmbedHost.mm``) and the cdhash of
its signed main executable. Ombra builds the manifest in, installs only an
archive with that hash holding that version, and accepts on its embed and team
channels only a Nightly process whose code directory hash is that cdhash.

    pack-nightly.py --app <Nightly.app | package.dmg> [--out DIR] [--stage]
    pack-nightly.py --check [--package FILE]

``--stage`` copies the archive where this machine's Ombra looks for it
(``<runtimes>/nightly/packages``, ``OMBRA_RUNTIME_DIR`` honoured). ``--check``
verifies the manifest against an archive (default: the staged one).
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
MANIFEST = HERE / "nightly-runtime.json"
ENTITLEMENTS = HERE / "nightly.entitlements.plist"
DESKTOP = Path(os.environ.get("NIGHTLY_DESKTOP", Path.home() / "Documents" / "ArcLike Browser" / "desktop"))
DEFAULT_APP = DESKTOP / "engine" / "obj-aarch64-apple-darwin" / "dist" / "Nightly.app"


def runtimes_root() -> Path:
    root = os.environ.get("OMBRA_RUNTIME_DIR")
    if root:
        return Path(root)
    return Path.home() / "Library" / "Application Support" / "Ombra" / "runtimes"


def read_ini(app: Path) -> tuple[str, str]:
    version = build = None
    section = None
    for line in (app / "Contents" / "Resources" / "application.ini").read_text().splitlines():
        line = line.strip()
        if line.startswith("["):
            section = line
        elif section == "[App]" and line.startswith("Version="):
            version = line[len("Version="):]
        elif section == "[App]" and line.startswith("BuildID="):
            build = line[len("BuildID="):]
    if not version or not build:
        sys.exit(f"{app}: no [App] Version/BuildID in application.ini")
    return version, build


def protocol_version() -> int:
    host = DESKTOP / "src" / "zen" / "embed" / "ZenEmbedHost.mm"
    match = re.search(r"kProtocolVersion\s*=\s*(\d+)", host.read_text())
    if not match:
        sys.exit(f"{host}: kProtocolVersion not found")
    return int(match.group(1))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def app_from(source: Path, scratch: Path) -> Path:
    if source.suffix == ".app":
        return source
    if source.suffix == ".dmg":
        mount = scratch / "mount"
        mount.mkdir()
        subprocess.run(["hdiutil", "attach", "-nobrowse", "-readonly", "-mountpoint", str(mount), str(source)], check=True,
                       stdout=subprocess.DEVNULL)
        try:
            apps = list(mount.glob("*.app"))
            if len(apps) != 1:
                sys.exit(f"{source}: expected one .app, found {[a.name for a in apps]}")
            copy = scratch / "Nightly.app"
            subprocess.run(["ditto", str(apps[0]), str(copy)], check=True)
            return copy
        finally:
            subprocess.run(["hdiutil", "detach", str(mount)], check=False, stdout=subprocess.DEVNULL)
    sys.exit(f"{source}: give a Nightly.app or a .dmg")


def materialize(app: Path) -> int:
    """Replaces symlinks that leave the bundle (a dev build links its chrome and modules into the source tree) with
    copies of what they point at: the package must stand alone, and the content sandbox may read only the bundle."""
    replaced = 0
    root = app.resolve()
    for dirpath, dirnames, filenames in os.walk(app):
        for name in dirnames + filenames:
            path = Path(dirpath) / name
            if not path.is_symlink():
                continue
            target = path.resolve()
            if target == root or root in target.parents:
                continue
            if not target.exists():
                sys.exit(f"{path}: dangling link to {target}")
            path.unlink()
            if target.is_dir():
                shutil.copytree(target, path, symlinks=False)
            else:
                shutil.copy2(target, path)
            replaced += 1
    return replaced


def sign(app: Path) -> str:
    """Signs the bundle ad hoc with the hardened runtime; returns the main executable's cdhash (40 hex).
    Extended attributes (FinderInfo, quarantine, provenance from a mounted .dmg) are cleared first: codesign
    rejects them as detritus."""
    subprocess.run(["xattr", "-cr", str(app)], check=True)
    subprocess.run(["codesign", "--force", "--deep", "-s", "-", "--options", "runtime", "--entitlements", str(ENTITLEMENTS),
                    str(app)], check=True)
    subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
    info = subprocess.run(["codesign", "-dvvv", str(app)], check=True, capture_output=True, text=True).stderr
    match = re.search(r"^CDHash=([0-9a-f]{40})$", info, re.M)
    if not match or "flags=0x10002(adhoc,runtime)" not in info:
        sys.exit(f"{app}: unexpected signature\n{info}")
    return match.group(1)


def pack(args) -> None:
    with tempfile.TemporaryDirectory() as tmp:
        scratch = Path(tmp)
        app = app_from(Path(args.app).resolve(), scratch)
        version, build = read_ini(app)
        protocol = protocol_version()
        name = f"nightly-runtime-{version}-{build}.zip"
        out = Path(args.out).resolve() if args.out else HERE.parent.parent / ".temp" / "nightly-runtime"
        out.mkdir(parents=True, exist_ok=True)
        archive = out / name
        # The bundle must be named Nightly.app inside the archive (Ombra's layout).
        staged = scratch / "pack" / "Nightly.app"
        staged.parent.mkdir()
        subprocess.run(["ditto", str(app), str(staged)], check=True)
        print(f"{materialize(staged)} links out of the bundle replaced by copies")
        cdhash = sign(staged)
        if archive.exists():
            archive.unlink()
        subprocess.run(["ditto", "-c", "-k", "--keepParent", str(staged), str(archive)], check=True)
    manifest = {
        "version": version,
        "buildId": build,
        "protocol": protocol,
        "sha256": sha256(archive),
        "size": archive.stat().st_size,
        "archive": name,
        "cdhash": cdhash,
    }
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"{archive}\n{json.dumps(manifest, indent=2)}")
    if args.stage:
        target = runtimes_root() / "nightly" / "packages"
        target.mkdir(parents=True, exist_ok=True)
        shutil.copy2(archive, target / name)
        print(f"staged in {target}")


def check(args) -> None:
    manifest = json.loads(MANIFEST.read_text())
    package = Path(args.package) if args.package else runtimes_root() / "nightly" / "packages" / manifest["archive"]
    if not package.exists():
        sys.exit(f"{package}: missing")
    actual = sha256(package)
    if actual != manifest["sha256"] or package.stat().st_size != manifest["size"]:
        sys.exit(f"{package}: sha256 {actual}, manifest {manifest['sha256']}")
    if manifest["protocol"] != protocol_version():
        sys.exit(f"manifest protocol {manifest['protocol']}, desktop kProtocolVersion {protocol_version()}")
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["ditto", "-x", "-k", str(package), tmp], check=True)
        app = Path(tmp) / "Nightly.app"
        subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
        info = subprocess.run(["codesign", "-dvvv", str(app)], check=True, capture_output=True, text=True).stderr
        if f"\nCDHash={manifest.get('cdhash')}\n" not in f"\n{info}":
            sys.exit(f"{package}: the app's cdhash is not the manifest's {manifest.get('cdhash')}\n{info}")
    print(f"ok: {package} matches {MANIFEST.name} ({manifest['version']} {manifest['buildId']}, protocol {manifest['protocol']},"
          f" cdhash {manifest['cdhash']})")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--app", default=str(DEFAULT_APP), help="Nightly.app or ./mach package .dmg")
    parser.add_argument("--out", help="where to write the archive (default .temp/nightly-runtime)")
    parser.add_argument("--stage", action="store_true", help="copy the archive where Ombra looks for it")
    parser.add_argument("--check", action="store_true", help="verify the manifest against an archive")
    parser.add_argument("--package", help="archive for --check")
    args = parser.parse_args()
    if args.check:
        check(args)
    else:
        pack(args)


if __name__ == "__main__":
    main()

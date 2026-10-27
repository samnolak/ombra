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
its signed main executable. Before signing, the copy gets a bundle identity of
its own (``RUNTIME_BUNDLE_ID``) and claims no URL schemes or document types:
it is Ombra's runtime, not the user's browser, so macOS never hands it a link
meant for the default browser (with Nightly as the default browser, a link
from any app came to the running runtime as a Nightly window outside Ombra).
Ombra builds the manifest in, installs only an
archive with that hash holding that version, and accepts on its embed and team
channels only a Nightly process whose code directory hash is that cdhash.

It also makes the standalone browser's disk image,
``Nightly-<version>-<build>-macos-arm64.dmg`` next to the archive: another copy
of the same build, signed the same way but keeping its own identity
(``STANDALONE_BUNDLE_ID``, URL schemes and document types), with an
Applications link. The image ``./mach package`` makes is never shipped: it holds
the app as the build signed it, a seal that no longer matches the bundle. The
image is gated as a user gets it: the app mounted from it must pass
``codesign --verify --deep --strict``.

From that same signed standalone copy it makes the installed browser's update:
``Nightly-<version>-<build>-macos-arm64.mar``, a complete MAR (the engine's
``make_full_update.sh``, channel ``release``) signed with the Nightly MAR key
(``mar_private_key.pem`` and ``mar_cert.pem`` in ``NIGHTLY_MAR_KEYS``, default
``~/Developer/arclike/keys``; the certificate must be the desktop repo's
``build/signing/public_key.der``, which ``scripts/mar_sign.sh -i`` builds into
the updater), and ``Nightly-<version>-<build>-update.xml``, the update manifest
naming that MAR as an asset of the standalone release ``v<version>-<build>``.
Nightly asks ``https://samnolak.github.io/nightly-browser/updates/browser/
Darwin_aarch64-gcc3/release/update.xml``, downloads the MAR in the background,
verifies it against the built-in certificate and swaps it in at the next
restart. ``--publish-feed`` puts a manifest there (gh-pages) once its MAR is
published: it first downloads the MAR from the manifest's URL and checks its
size and SHA-512.

    pack-nightly.py --app <Nightly.app | package.dmg> [--out DIR] [--stage]
    pack-nightly.py --check [--package FILE]
    pack-nightly.py --check-dmg FILE
    pack-nightly.py --publish-feed FILE

``--stage`` copies the archive where this machine's Ombra looks for it
(``<runtimes>/nightly/packages``, ``OMBRA_RUNTIME_DIR`` honoured). ``--check``
verifies the manifest against an archive (default: the staged one);
``--check-dmg`` runs the image gate on a standalone disk image.
"""

import argparse
import base64
import hashlib
import json
import os
import plistlib
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
MANIFEST = HERE / "nightly-runtime.json"
ENTITLEMENTS = HERE / "nightly.entitlements.plist"
RUNTIME_BUNDLE_ID = "io.github.samnolak.ombra.nightly-runtime"
STANDALONE_BUNDLE_ID = "io.github.samnolak.nightly"
# What would make macOS choose the runtime to open links and files.
HANDLER_KEYS = ("CFBundleURLTypes", "CFBundleDocumentTypes")
# Resolved: the default is a link to ~/Developer/arclike/desktop, and the engine's shell tools break on its space.
DESKTOP = Path(os.environ.get("NIGHTLY_DESKTOP", Path.home() / "Documents" / "ArcLike Browser" / "desktop")).resolve()
OBJ_DIST = DESKTOP / "engine" / "obj-aarch64-apple-darwin" / "dist"
DEFAULT_APP = OBJ_DIST / "Nightly.app"
MAR_KEYS = Path(os.environ.get("NIGHTLY_MAR_KEYS", Path.home() / "Developer" / "arclike" / "keys"))
# The certificate the updater is built with (scripts/mar_sign.sh -i); a MAR signed by any other key is refused.
MAR_CERTIFICATE = DESKTOP / "build" / "signing" / "public_key.der"
# ACCEPTED_MAR_CHANNEL_IDS / MAR_CHANNEL_ID in the desktop mozconfig; also the update channel in the feed's path.
UPDATE_CHANNEL = "release"
# %BUILD_TARGET% of the arm64 macOS build.
UPDATE_TARGET = "Darwin_aarch64-gcc3"
RELEASE_REPO = "samnolak/nightly-browser"
FEED_PATH = f"updates/browser/{UPDATE_TARGET}/{UPDATE_CHANNEL}/update.xml"
FEED_URL = f"https://samnolak.github.io/nightly-browser/{FEED_PATH}"


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


def make_runtime_identity(app: Path) -> None:
    """The runtime's own bundle identifier and no URL scheme or document type: LaunchServices never routes the
    default browser's links or files to it, nor offers it as a browser."""
    path = app / "Contents" / "Info.plist"
    with path.open("rb") as f:
        info = plistlib.load(f)
    info["CFBundleIdentifier"] = RUNTIME_BUNDLE_ID
    for key in HANDLER_KEYS:
        info.pop(key, None)
    with path.open("wb") as f:
        plistlib.dump(info, f, fmt=plistlib.FMT_XML)


def bundle_id(app: Path) -> str | None:
    with (app / "Contents" / "Info.plist").open("rb") as f:
        return plistlib.load(f).get("CFBundleIdentifier")


def runtime_identity_problem(app: Path) -> str | None:
    with (app / "Contents" / "Info.plist").open("rb") as f:
        info = plistlib.load(f)
    if info.get("CFBundleIdentifier") != RUNTIME_BUNDLE_ID:
        return f"bundle identifier {info.get('CFBundleIdentifier')}, expected {RUNTIME_BUNDLE_ID}"
    claimed = [key for key in HANDLER_KEYS if key in info]
    return f"claims {', '.join(claimed)}" if claimed else None


def check_signature(app: Path) -> str:
    """codesign --verify --deep --strict, then an ad-hoc hardened-runtime signature; returns its cdhash (40 hex)."""
    if subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)]).returncode:
        sys.exit(f"{app}: codesign --verify --deep --strict failed")
    info = subprocess.run(["codesign", "-dvvv", str(app)], check=True, capture_output=True, text=True).stderr
    match = re.search(r"^CDHash=([0-9a-f]{40})$", info, re.M)
    if not match or "flags=0x10002(adhoc,runtime)" not in info:
        sys.exit(f"{app}: unexpected signature\n{info}")
    return match.group(1)



def sign(app: Path) -> str:
    """Signs the bundle ad hoc with the hardened runtime; returns the main executable's cdhash (40 hex).
    Extended attributes (FinderInfo, quarantine, provenance from a mounted .dmg) are cleared first: codesign
    rejects them as detritus."""
    subprocess.run(["xattr", "-cr", str(app)], check=True)
    subprocess.run(["codesign", "--force", "--deep", "-s", "-", "--options", "runtime", "--entitlements", str(ENTITLEMENTS),
                    str(app)], check=True)
    return check_signature(app)


def standalone_copy(app: Path, scratch: Path) -> Path:
    """The standalone browser: a signed copy of ``app`` keeping its own identity, alone in a folder of ``scratch``.
    The image and the update are both made from it, so an update installs exactly the app the image holds."""
    standalone = scratch / "dmg" / "Nightly.app"
    standalone.parent.mkdir()
    subprocess.run(["ditto", str(app), str(standalone)], check=True)
    materialize(standalone)
    if (found := bundle_id(standalone)) != STANDALONE_BUNDLE_ID:
        sys.exit(f"{app}: bundle identifier {found}, expected {STANDALONE_BUNDLE_ID} for the standalone browser")
    sign(standalone)
    return standalone


def make_dmg(standalone: Path, dmg: Path) -> str:
    """The standalone browser's image: ``standalone`` next to an Applications link, compressed (UDZO), then gated
    from its mount. Returns the app's cdhash."""
    root = standalone.parent
    (root / "Applications").symlink_to("/Applications")
    subprocess.run(["hdiutil", "create", "-quiet", "-ov", "-volname", "Nightly", "-format", "UDZO", "-srcfolder", str(root),
                    str(dmg)], check=True)
    (root / "Applications").unlink()
    return check_dmg(dmg)


def mar_tool(name: str) -> Path:
    path = OBJ_DIST / ("host/bin" if name == "mar" else "bin") / name
    if not path.exists():
        sys.exit(f"{path}: missing (build the desktop engine first)")
    return path


def sign_mar(unsigned: Path, signed: Path) -> None:
    """Signs ``unsigned`` into ``signed`` with the Nightly MAR key (NSS signmar: RSA PKCS#1 v1.5, SHA-384) and
    verifies the result against the certificate the updater is built with. The key goes into a throwaway NSS database
    only for the duration of the call."""
    key, cert = MAR_KEYS / "mar_private_key.pem", MAR_KEYS / "mar_cert.pem"
    for path in (key, cert, MAR_CERTIFICATE):
        if not path.exists():
            sys.exit(f"{path}: missing (NIGHTLY_MAR_KEYS names the folder holding mar_private_key.pem and mar_cert.pem)")
    der = subprocess.run(["openssl", "x509", "-in", str(cert), "-outform", "DER"], check=True, capture_output=True).stdout
    if der != MAR_CERTIFICATE.read_bytes():
        sys.exit(f"{cert} is not {MAR_CERTIFICATE}: the updater would refuse MARs signed with this key")
    with tempfile.TemporaryDirectory() as tmp:
        db = Path(tmp)
        password = db / "password.txt"
        password.write_text(secrets.token_hex(16) + "\n")
        quiet = {"check": True, "stdout": subprocess.DEVNULL}
        subprocess.run([str(mar_tool("certutil")), "-N", "-d", str(db), "-f", str(password)], **quiet)
        subprocess.run(["openssl", "pkcs12", "-export", "-inkey", str(key), "-in", str(cert), "-name", "mar_sig",
                        "-passout", f"file:{password}", "-out", str(db / "key.p12")], check=True)
        subprocess.run([str(mar_tool("pk12util")), "-i", str(db / "key.p12"), "-d", str(db), "-w", str(password),
                        "-k", str(password)], **quiet)
        with password.open() as stdin:  # signmar reads the database password from stdin when it is not a tty
            subprocess.run([str(mar_tool("signmar")), "-d", str(db), "-n", "mar_sig", "-s", str(unsigned), str(signed)],
                           stdin=stdin, **quiet)
    # On macOS signmar verifies only against a DER file (-D), here the very certificate the updater embeds.
    subprocess.run([str(mar_tool("signmar")), "-D", str(MAR_CERTIFICATE), "-v", str(signed)], **quiet)


def make_mar(standalone: Path, mar: Path, version: str) -> None:
    """A complete update of ``standalone`` (the engine's make_full_update.sh), signed."""
    unsigned = mar.with_name(mar.stem + ".unsigned.mar")
    env = os.environ | {"MAR": str(mar_tool("mar")), "MOZ_PRODUCT_VERSION": version, "MAR_CHANNEL_ID": UPDATE_CHANNEL}
    script = DESKTOP / "engine" / "tools" / "update-packaging" / "make_full_update.sh"
    subprocess.run(["bash", str(script), "-q", str(unsigned), str(standalone)], check=True, env=env)
    if mar.exists():
        mar.unlink()
    try:
        sign_mar(unsigned, mar)
    finally:
        unsigned.unlink()


def sha512(path: Path) -> str:
    digest = hashlib.sha512()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_update_xml(standalone: Path, mar: Path, xml: Path, version: str, build: str) -> None:
    """The update manifest Nightly reads: this build, its MAR as an asset of the standalone release."""
    milestone = None
    for line in (standalone / "Contents" / "Resources" / "platform.ini").read_text().splitlines():
        if line.startswith("Milestone="):
            milestone = line[len("Milestone="):].strip()
    if not milestone:
        sys.exit(f"{standalone}: no Milestone in platform.ini")
    tag = f"v{version}-{build}"
    updates = ET.Element("updates")
    update = ET.SubElement(updates, "update", type="minor", displayVersion=version, appVersion=version,
                           platformVersion=milestone, buildID=build,
                           detailsURL=f"https://github.com/{RELEASE_REPO}/releases/tag/{tag}")
    ET.SubElement(update, "patch", type="complete", URL=f"https://github.com/{RELEASE_REPO}/releases/download/{tag}/{mar.name}",
                  hashFunction="sha512", hashValue=sha512(mar), size=str(mar.stat().st_size))
    ET.indent(updates)
    xml.write_bytes(ET.tostring(updates, encoding="UTF-8", xml_declaration=True) + b"\n")


def publish_feed(xml: Path) -> None:
    """Makes ``xml`` the feed installed browsers read (gh-pages ``FEED_PATH``), after checking that the MAR it names
    is published with the size and SHA-512 it states; then waits until GitHub Pages serves it."""
    patch = ET.parse(xml).getroot().find("update/patch")
    if patch is None:
        sys.exit(f"{xml}: no update/patch")
    url, size, digest = patch.get("URL"), int(patch.get("size")), patch.get("hashValue")
    with tempfile.TemporaryDirectory() as tmp:
        download = Path(tmp) / "update.mar"
        subprocess.run(["curl", "-fsSL", "--retry", "3", "-o", str(download), url], check=True)
        if download.stat().st_size != size or sha512(download) != digest:
            sys.exit(f"{url}: {download.stat().st_size} bytes, sha512 {sha512(download)}; the manifest says {size}, {digest}")
    print(f"ok: {url} is the manifest's MAR ({size} bytes, sha512 matches)")
    body = xml.read_bytes()
    api = f"repos/{RELEASE_REPO}/contents/{FEED_PATH}"
    current = subprocess.run(["gh", "api", f"{api}?ref=gh-pages", "--jq", ".sha"], capture_output=True, text=True)
    build = ET.parse(xml).getroot().find("update").get("buildID")
    command = ["gh", "api", "-X", "PUT", api, "-f", f"message=Nightly update feed: build {build}", "-f", "branch=gh-pages",
               "-f", f"content={base64.b64encode(body).decode()}"]
    if current.returncode == 0 and current.stdout.strip():
        command += ["-f", f"sha={current.stdout.strip()}"]
    subprocess.run(command, check=True, stdout=subprocess.DEVNULL)
    deadline = time.monotonic() + 600
    while time.monotonic() < deadline:
        served = subprocess.run(["curl", "-fsSL", "-H", "Cache-Control: no-cache", FEED_URL], capture_output=True)
        if served.returncode == 0 and served.stdout == body:
            print(f"ok: {FEED_URL} serves build {build}")
            return
        time.sleep(15)
    sys.exit(f"{FEED_URL} does not serve the new manifest after 10 minutes (GitHub Pages build?)")


def check_dmg(dmg: Path) -> str:
    """The gate on the image as a user gets it: the one app mounted from it passes codesign --verify --deep --strict,
    is signed ad hoc with the hardened runtime and is the standalone browser. Returns its cdhash."""
    with tempfile.TemporaryDirectory() as tmp:
        mount = Path(tmp) / "mount"
        mount.mkdir()
        subprocess.run(["hdiutil", "attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", str(mount), str(dmg)],
                       check=True, stdout=subprocess.DEVNULL)
        try:
            apps = [a.name for a in mount.glob("*.app")]
            if apps != ["Nightly.app"]:
                sys.exit(f"{dmg}: expected Nightly.app alone, found {apps}")
            app = mount / "Nightly.app"
            cdhash = check_signature(app)
            if (found := bundle_id(app)) != STANDALONE_BUNDLE_ID:
                sys.exit(f"{dmg}: bundle identifier {found}, expected {STANDALONE_BUNDLE_ID}")
        finally:
            subprocess.run(["hdiutil", "detach", str(mount)], check=False, stdout=subprocess.DEVNULL)
    print(f"ok: {dmg.name}: Nightly.app from the mounted image verifies (codesign --verify --deep --strict),"
          f" ad hoc + hardened runtime, {STANDALONE_BUNDLE_ID}, cdhash {cdhash}")
    return cdhash


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
        make_runtime_identity(staged)
        cdhash = sign(staged)
        if archive.exists():
            archive.unlink()
        subprocess.run(["ditto", "-c", "-k", "--keepParent", str(staged), str(archive)], check=True)
        standalone = standalone_copy(app, scratch)
        dmg = out / f"Nightly-{version}-{build}-macos-arm64.dmg"
        make_dmg(standalone, dmg)
        mar = out / f"Nightly-{version}-{build}-macos-arm64.mar"
        make_mar(standalone, mar, version)
        feed = out / f"Nightly-{version}-{build}-update.xml"
        write_update_xml(standalone, mar, feed, version, build)
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
    print(f"{dmg}\nsha256 {sha256(dmg)}, {dmg.stat().st_size} bytes")
    print(f"{mar}\nsigned MAR, {mar.stat().st_size} bytes; upload it to release v{version}-{build}, then"
          f" pack-nightly.py --publish-feed {feed}")
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
        cdhash = check_signature(app)
        if problem := runtime_identity_problem(app):
            sys.exit(f"{package}: not the runtime's identity: {problem}")
        if cdhash != manifest.get("cdhash"):
            sys.exit(f"{package}: the app's cdhash {cdhash} is not the manifest's {manifest.get('cdhash')}")
    print(f"ok: {package} matches {MANIFEST.name} ({manifest['version']} {manifest['buildId']}, protocol {manifest['protocol']},"
          f" cdhash {manifest['cdhash']})")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--app", default=str(DEFAULT_APP), help="Nightly.app or ./mach package .dmg")
    parser.add_argument("--out", help="where to write the archive, the image and the update (default .temp/nightly-runtime)")
    parser.add_argument("--stage", action="store_true", help="copy the archive where Ombra looks for it")
    parser.add_argument("--check", action="store_true", help="verify the manifest against an archive")
    parser.add_argument("--package", help="archive for --check")
    parser.add_argument("--check-dmg", metavar="FILE", help="gate a standalone disk image (the app mounted from it)")
    parser.add_argument("--publish-feed", metavar="FILE", help="make an update manifest the installed browsers' feed")
    args = parser.parse_args()
    if args.publish_feed:
        publish_feed(Path(args.publish_feed).resolve())
    elif args.check_dmg:
        check_dmg(Path(args.check_dmg).resolve())
    elif args.check:
        check(args)
    else:
        pack(args)


if __name__ == "__main__":
    main()

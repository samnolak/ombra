#!/usr/bin/env python3
"""Makes a drag-to-install disk image for a macOS app (Ombra: tools/package/package.sh; Nightly:
runtime/nightly/pack-nightly.py). Opened, the image shows one Finder window, no toolbar or sidebar: "Install <Name>"
and "Drag to Applications" above the app's icon, an arrow, and the Applications link (background.swift draws the text
and the arrow at 1x and 2x; the icons and their names are Finder's).

  make-dmg.py <App.app> <out.dmg> [--volume <name>] [--title <text>]

The app is copied with ditto (its signature and staple stay intact); the image is compressed (UDZO) and the volume
takes the app's icon. dmgbuild (pinned below) writes the window's .DS_Store without Finder: nothing is shown on screen
while the image is made. Without dmgbuild in this Python, a venv with it is made in $DMGBUILD_VENV
(default ~/.cache/ombra-dmgbuild) and the script reruns there.
"""
from __future__ import annotations

import argparse
import os
import plistlib
import subprocess
import sys
import tempfile
from pathlib import Path

DMGBUILD = "dmgbuild==1.6.7"
HERE = Path(__file__).resolve().parent

# The window, in points from its top left; background.swift draws at the same coordinates
WIDTH, HEIGHT = 660, 420
TITLE_Y, SUBTITLE_Y = 92, 140
ICON_Y, APP_X, LINK_X = 250, 180, 480
ARROW = (300, 360)


def ensure_dmgbuild() -> None:
    try:
        import dmgbuild  # noqa: F401
        return
    except ImportError:
        pass
    env = Path(os.environ.get("DMGBUILD_VENV", Path.home() / ".cache" / "ombra-dmgbuild"))
    python = env / "bin" / "python"
    if Path(sys.prefix).resolve() == env.resolve():
        sys.exit(f"{env}: has no {DMGBUILD}")
    if not python.exists() or subprocess.run([str(python), "-c", "import pip"], capture_output=True).returncode:
        # python -m venv, not venv.create: in-process creation breaks under a symlinked interpreter (uv's)
        subprocess.run([sys.executable, "-m", "venv", "--clear", str(env)], check=True)
    subprocess.run([str(python), "-m", "pip", "install", "-q", DMGBUILD], check=True)
    os.execv(str(python), [str(python), __file__, *sys.argv[1:]])


def app_icon(app: Path) -> Path | None:
    with (app / "Contents" / "Info.plist").open("rb") as f:
        name = plistlib.load(f).get("CFBundleIconFile")
    if not name:
        return None
    icon = app / "Contents" / "Resources" / name
    if icon.suffix != ".icns":
        icon = icon.with_name(icon.name + ".icns")
    return icon if icon.exists() else None


def backgrounds(folder: Path, title: str, subtitle: str) -> Path:
    """bg.png and bg@2x.png in folder (dmgbuild joins them into one HiDPI tiff); returns bg.png."""
    for scale, name in ((1, "bg.png"), (2, "bg@2x.png")):
        subprocess.run(["swift", str(HERE / "background.swift"), str(folder / name), str(scale), str(WIDTH), str(HEIGHT),
                        title, subtitle, str(TITLE_Y), str(SUBTITLE_Y), str(ARROW[0]), str(ARROW[1]), str(ICON_Y)],
                       check=True)
    return folder / "bg.png"


def verify(dmg: Path, app_name: str, mount: Path) -> None:
    """The app as the user gets it, from the mounted image: codesign --verify --deep --strict."""
    mount.mkdir()
    subprocess.run(["hdiutil", "attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", str(mount), str(dmg)],
                   check=True, stdout=subprocess.DEVNULL)
    try:
        if subprocess.run(["codesign", "--verify", "--deep", "--strict", str(mount / app_name)]).returncode:
            sys.exit(f"{dmg}: {app_name} from the mounted image fails codesign --verify --deep --strict")
    finally:
        subprocess.run(["hdiutil", "detach", "-force", str(mount)], check=False, stdout=subprocess.DEVNULL)


def main() -> None:
    ensure_dmgbuild()
    import dmgbuild

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("app")
    parser.add_argument("out")
    parser.add_argument("--volume", help="the volume's name (default: the app's)")
    parser.add_argument("--title", help='the window\'s title line (default: "Install <app>")')
    args = parser.parse_args()
    app = Path(args.app).resolve()
    out = Path(args.out).resolve()
    if app.suffix != ".app" or not (app / "Contents" / "Info.plist").exists():
        sys.exit(f"{app}: not an app bundle")
    name = app.stem
    out.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        settings = {
            "format": "UDZO",
            "files": [str(app)],
            "symlinks": {"Applications": "/Applications"},
            # No hide_extensions: it sets com.apple.FinderInfo on the app, which codesign --strict rejects as
            # detritus (Finder hides ".app" anyway unless the user shows every extension)
            "icon_locations": {app.name: (APP_X, ICON_Y), "Applications": (LINK_X, ICON_Y)},
            "background": str(backgrounds(Path(tmp), args.title or f"Install {name}", "Drag to Applications")),
            "window_rect": ((200, 160), (WIDTH, HEIGHT)),
            "icon_size": 128,
            "text_size": 14,
            "show_status_bar": False,
            "show_toolbar": False,
            "show_pathbar": False,
            "show_sidebar": False,
        }
        if icon := app_icon(app):
            settings["icon"] = str(icon)
        dmgbuild.build_dmg(str(out), args.volume or name, settings=settings)
        verify(out, app.name, Path(tmp) / "mount")
    print(f"{out}: {name} next to an Applications link")


if __name__ == "__main__":
    main()

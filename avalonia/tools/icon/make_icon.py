"""Ombra's app icons from the owner's presentation sheet (ombra-icon-sheet.png).
Usage (from avalonia/): python3 tools/icon/make_icon.py          all icon assets; Pillow + numpy, macOS iconutil
                        python3 tools/icon/make_icon.py --car DIR  only compile packaging/macos/Ombra.icon into DIR

The sheet's large tile is a presentation mock-up (own rounded corners, rim, top glow), not an icon. The swirl is
lifted off it (the tile's glow subtracted, everything away from the swirl dropped) and laid on a full-bleed black
square; every platform icon starts from that square:
- macOS 26: packaging/macos/Ombra.icon (Icon Composer: black fill + the swirl as one layer, no glass), compiled by
  Xcode 26's actool into packaging/macos/Assets.car (CFBundleIconName "Ombra"); the system applies its own mask.
  Without Xcode 26 the existing Assets.car stays; the macOS package job of avalonia-package.yml compiles the
  checked-in Ombra.icon and uploads the result (artifact ombra-icon-catalog).
- Older macOS: packaging/macos/ombra.icns (CFBundleIconFile), the square inside Apple's app icon shape and grid:
  824/1024 body, continuous corners of radius 185.4 (SwiftUI RoundedRectangle(style: .continuous)). macOS 26 also
  accepts this shape as its own and re-masks it, so even without the catalog it gets no grey plate.
- Window, greeting, Linux and Windows: icon-<size>.png / icon.ico, the same shaped tile (those systems do not mask
  app icons); small ICO frames and the tray use the sheet's circular variants.
"""
from pathlib import Path
import json
import plistlib
import shutil
import subprocess
import sys
import tempfile

TOOLS = Path(__file__).resolve().parent
AVALONIA = TOOLS.parent.parent
SOURCE = TOOLS / "ombra-icon-sheet.png"
ASSETS = AVALONIA / "src" / "Ombra.App" / "Assets"
MACOS = AVALONIA / "packaging" / "macos"
ICON = MACOS / "Ombra.icon"
SIZES = (16, 24, 32, 48, 64, 128, 256, 512, 1024)
TILE = (100, 100, 924, 924)      # Apple's macOS app icon grid on a 1024 canvas
RADIUS = 185.4                   # its corner radius at 824

# Apple's continuous ("squircle") corner, the one SwiftUI's RoundedRectangle(style: .continuous) draws, as (x, y)
# offsets from the top-left corner in units of the corner radius: the left edge ends 1.52866483 r below the corner,
# then three cubic Béziers reach the top edge as far to the right.
CORNER_START = (0.0, 1.52866483)
CORNER = (
    ((0.0, 1.08849296), (0.0, 0.86840694), (0.07491100, 0.63149399)),
    ((0.16906001, 0.37282401), (0.37282401, 0.16906001), (0.63149399, 0.07491100)),
    ((0.86840694, 0.0), (1.08849296, 0.0), (1.52866483, 0.0)),
)


def squircle(size, box, radius, scale=4):
    """Coverage mask of Apple's continuous rounded rectangle `box` on a `size` canvas."""
    from PIL import Image, ImageDraw
    corner, cur = [CORNER_START], CORNER_START
    for c1, c2, end in CORNER:
        for i in range(1, 17):
            t, s = i / 16, 1 - i / 16
            corner.append(tuple(s**3*p0 + 3*s*s*t*p1 + 3*s*t*t*p2 + t**3*p3 for p0, p1, p2, p3 in zip(cur, c1, c2, end)))
        cur = end
    x0, y0, x1, y1 = (v * scale for v in box)
    r = radius * scale
    points = []
    # Clockwise from the left edge; a mirrored corner is walked backwards.
    for cx, cy, dx, dy in ((x0, y0, 1, 1), (x1, y0, -1, 1), (x1, y1, -1, -1), (x0, y1, 1, -1)):
        points += [(cx + dx*u*r, cy + dy*v*r) for u, v in (corner if dx == dy else corner[::-1])]
    mask = Image.new("L", (size * scale, size * scale))
    ImageDraw.Draw(mask).polygon(points, fill=255)
    return mask.resize((size, size), Image.Resampling.BOX)


def swirl(sheet):
    """Full-bleed 1024 square: the large tile's swirl on pure black, sized and centred as on the tile."""
    import numpy as np
    from PIL import Image, ImageFilter

    def grow(mask, by):   # square dilation
        for axis in (0, 1):
            grown = mask.copy()
            for shift in range(1, by + 1):
                grown |= np.roll(mask, shift, axis) | np.roll(mask, -shift, axis)
            mask = grown
        return mask

    rgb = np.asarray(sheet.convert("RGB")).astype(np.float32)
    lum = rgb.max(axis=2)
    # The large tile: the convex area brighter than the sheet's black in the sheet's left part.
    lit = lum > 8
    lit[:, 1060:] = False
    tile = np.zeros_like(lit)
    for y in np.nonzero(lit.any(axis=1))[0]:
        xs = np.nonzero(lit[y])[0]
        tile[y, xs[0]:xs[-1] + 1] = True
    rows, cols = np.nonzero(tile.any(axis=1))[0], np.nonzero(tile.any(axis=0))[0]
    top, bottom, left, right = rows[0], rows[-1] + 1, cols[0], cols[-1] + 1
    # Clear of the tile's rim, even round its corners, the swirl is far brighter than glow and grain.
    inner = ~grow(~tile, 30)
    core = (lum > 48) & inner
    ys, xs = np.nonzero(core)
    near = grow(core, 24)
    # The tile's own glow (brightest at its top edge) is the per-row median away from the swirl.
    glow = np.zeros((rgb.shape[0], 3), np.float32)
    known = np.zeros(rgb.shape[0], bool)
    for y in range(top, bottom):
        background = inner[y] & ~near[y]
        if background.sum() > 20:
            glow[y] = np.median(rgb[y, background], axis=0)
            known[y] = True
    for c in range(3):
        glow[:, c] = np.interp(np.arange(rgb.shape[0]), np.nonzero(known)[0], glow[known, c])
    kernel = np.ones(31) / 31
    glow = np.stack([np.convolve(np.pad(glow[:, c], 15, mode="edge"), kernel, "valid") for c in range(3)], axis=1)
    keep = Image.fromarray((near * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(8))
    keep = np.asarray(keep).astype(np.float32)[..., None] / 255
    art = np.clip(rgb - glow[:, None, :], 0, 255) * keep
    art = Image.fromarray(art.round().astype(np.uint8))
    print("tile", (left, top, right, bottom), "swirl", (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1))
    # The tile's height becomes the square's side; the tile's centre its centre.
    scale = 1024 / (bottom - top)
    cx, cy = (left + right) / 2, (top + bottom) / 2
    half = 512 / scale
    region = (cx - half, cy - half, cx + half, cy + half)
    return art.resize((1024, 1024), Image.Resampling.LANCZOS, box=region)


def layer(square):
    """The swirl alone, un-premultiplied from black: drawn over the black fill it gives `square` back."""
    import numpy as np
    from PIL import Image
    rgb = np.asarray(square).astype(np.float32)
    alpha = rgb.max(axis=2, keepdims=True)
    colour = np.where(alpha > 0, rgb * 255 / np.maximum(alpha, 1), 0)
    return Image.fromarray(np.concatenate([colour, alpha], axis=2).round().clip(0, 255).astype(np.uint8), "RGBA")


def tile(square):
    from PIL import Image
    body = TILE[2] - TILE[0]
    canvas = Image.new("RGBA", (1024, 1024))
    canvas.paste(square.resize((body, body), Image.Resampling.LANCZOS).convert("RGBA"), TILE[:2])
    canvas.putalpha(squircle(1024, TILE, RADIUS))
    return canvas


def extract(sheet, region):
    """A circular sheet variant: its convex outer rim, black interior and swirl, on transparency."""
    from PIL import Image, ImageDraw

    def hull(points):
        points = sorted(set(points))
        def cross(o, a, b):
            return (a[0]-o[0])*(b[1]-o[1]) - (a[1]-o[1])*(b[0]-o[0])
        halves = []
        for sequence in (points, points[::-1]):
            half = []
            for p in sequence:
                while len(half) >= 2 and cross(half[-2], half[-1], p) <= 0:
                    half.pop()
                half.append(p)
            halves.extend(half[:-1])
        return halves

    image = sheet.crop(region).convert("RGB")
    points = []
    for y in range(image.height):
        xs = [x for x in range(image.width) if max(image.getpixel((x, y))) > 8]
        if xs:
            for x in (xs[0], xs[-1]):
                points.extend(((x, y), (x+1, y), (x, y+1), (x+1, y+1)))
    boundary = hull(points)
    mask = Image.new("L", (image.width*4, image.height*4))
    ImageDraw.Draw(mask).polygon([(x*4, y*4) for x, y in boundary], fill=255)
    mask = mask.resize(image.size, Image.Resampling.BOX)
    rgba = image.convert("RGBA")
    # Extend interior edge colour into fractional coverage instead of dividing noise by near-zero alpha.
    pixels = []
    for y in range(image.height):
        for x in range(image.width):
            alpha = mask.getpixel((x, y))
            if 0 < alpha < 255:
                ix = x + (2 if x < image.width / 2 else -2)
                iy = y + (2 if y < image.height / 2 else -2)
                rgb = image.getpixel((ix, iy))
            else:
                rgb = image.getpixel((x, y)) if alpha else (0, 0, 0)
            pixels.append((*rgb, alpha))
    rgba.putdata(pixels)
    xs, ys = [x for x, y in boundary], [y for x, y in boundary]
    return rgba.crop((min(xs), min(ys), max(xs), max(ys)))


def fit(art, size, body):
    from PIL import Image
    art = art.copy()
    art.thumbnail((body, body), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (size, size))
    canvas.alpha_composite(art, ((size-art.width)//2, (size-art.height)//2))
    return canvas


def compile_car(out):
    """Xcode 26's actool: Ombra.icon -> out/Assets.car. False when no Xcode 26 is selected."""
    try:
        actool = subprocess.run(["xcrun", "--find", "actool"], capture_output=True, text=True, check=True).stdout.strip()
        version = subprocess.run([actool, "--version"], capture_output=True, check=True).stdout
        version = plistlib.loads(version)["com.apple.actool.version"]["short-bundle-version"]
    except (OSError, subprocess.CalledProcessError, plistlib.InvalidFileException, KeyError):
        return False
    if int(version.split(".")[0]) < 26:  # Icon Composer icons need Xcode 26
        print("actool", version)
        return False
    out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        partial = Path(work) / "partial.plist"
        subprocess.run([actool, str(ICON), "--compile", work, "--app-icon", "Ombra", "--platform", "macosx",
                        "--target-device", "mac", "--minimum-deployment-target", "14.0",
                        "--output-partial-info-plist", str(partial),
                        "--output-format", "human-readable-text", "--notices", "--warnings", "--errors"], check=True)
        print("actool", version, "->", sorted(p.name for p in Path(work).iterdir()), plistlib.loads(partial.read_bytes()))
        shutil.copy(Path(work) / "Assets.car", out / "Assets.car")
    return True


def main():
    if sys.argv[1:2] == ["--car"]:
        if not compile_car(Path(sys.argv[2])):
            sys.exit("actool from Xcode 26 or later is needed (xcode-select / DEVELOPER_DIR)")
        return
    from PIL import Image
    sheet = Image.open(SOURCE)
    square = swirl(sheet)
    icon = tile(square)
    circle = fit(extract(sheet, (1120, 470, 1470, 815)), 512, 480)
    tiny = fit(extract(sheet, (1230, 820, 1360, 945)), 128, 120)

    for size in SIZES:
        icon.resize((size, size), Image.Resampling.LANCZOS).save(ASSETS / f"icon-{size}.png")
    circle.save(ASSETS / "icon-round.png")
    frames = [(tiny if s <= 32 else circle if s <= 64 else icon).resize((s, s), Image.Resampling.LANCZOS)
              for s in SIZES if s <= 256]
    frames[-1].save(ASSETS / "icon.ico", sizes=[f.size for f in frames], append_images=frames[:-1])

    MACOS.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as directory:
        iconset = Path(directory) / "ombra.iconset"
        iconset.mkdir()
        for size in (16, 32, 128, 256, 512):
            for scale in (1, 2):
                suffix = "@2x" if scale == 2 else ""
                icon.resize((size*scale, size*scale), Image.Resampling.LANCZOS).save(iconset / f"icon_{size}x{size}{suffix}.png")
        subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(MACOS / "ombra.icns")], check=True)

    shutil.rmtree(ICON, ignore_errors=True)
    (ICON / "Assets").mkdir(parents=True)
    layer(square).save(ICON / "Assets" / "swirl.png")
    (ICON / "icon.json").write_text(json.dumps({
        "fill": {"solid": "srgb:0.00000,0.00000,0.00000,1.00000"},
        "groups": [{
            "layers": [{"glass": False, "image-name": "swirl.png", "name": "swirl"}],
            "shadow": {"kind": "neutral", "opacity": 0.5},
            "specular": False,
            "translucency": {"enabled": False, "value": 0.5},
        }],
        "supported-platforms": {"squares": "shared"},
    }, indent=2) + "\n")
    if not compile_car(MACOS):
        print("Assets.car NOT rebuilt (needs Xcode 26's actool): take it from the ombra-icon-catalog artifact of an "
              "avalonia-package.yml run for osx-arm64")


if __name__ == "__main__":
    main()

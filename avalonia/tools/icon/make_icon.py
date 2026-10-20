"""Extract the owner's presentation sheet; Pillow required, iconutil on macOS.
Usage: python3 tools/icon/make_icon.py src/Ombra.App/Assets
The opaque sheet is not an icon: recover its convex outer rim, retaining the
black interior and complete swirl. No invented rim, shadow, or rounded mask.
"""
from pathlib import Path
import subprocess
import sys
import tempfile
from PIL import Image, ImageDraw

SOURCE = Path(__file__).with_name("ombra-icon-sheet.png")
SIZES = (16, 24, 32, 48, 64, 128, 256, 512, 1024)


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


def extract(sheet, region):
    image = sheet.crop(region).convert("RGB")
    # Compression noise on the black sheet is below 9; the continuous rim is
    # above it. Convexity bridges the dark bottom rim without keying out black
    # portions of the artwork itself.
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
    # Extend interior edge colour into fractional coverage, instead of dividing
    # compression noise by near-zero alpha (which produces bright fringes).
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
    bounds = (min(x for x, y in boundary), min(y for x, y in boundary),
              max(x for x, y in boundary), max(y for x, y in boundary))
    print("Detected sheet bounds:", tuple(v + region[i % 2] for i, v in enumerate(bounds)))
    return rgba.crop(bounds)


def grid(art, size=1024, body=824):
    art = art.copy()
    art.thumbnail((body, body), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (size, size))
    canvas.alpha_composite(art, ((size-art.width)//2, (size-art.height)//2))
    return canvas


def main():
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    sheet = Image.open(SOURCE)
    square = grid(extract(sheet, (85, 40, 1045, 960)))
    circle = grid(extract(sheet, (1120, 470, 1470, 815)), 512, 480)
    tiny = grid(extract(sheet, (1230, 820, 1360, 945)), 128, 120)
    for size in SIZES:
        square.resize((size, size), Image.Resampling.LANCZOS).save(out / f"icon-{size}.png")
    circle.save(out / "icon-round.png")
    frames = [(tiny if s <= 32 else circle if s <= 64 else square).resize((s, s), Image.Resampling.LANCZOS)
              for s in SIZES if s <= 256]
    frames[-1].save(out / "icon.ico", sizes=[f.size for f in frames], append_images=frames[:-1])
    with tempfile.TemporaryDirectory() as directory:
        iconset = Path(directory) / "ombra.iconset"
        iconset.mkdir()
        for size in (16, 32, 128, 256, 512):
            for scale in (1, 2):
                suffix = "@2x" if scale == 2 else ""
                square.resize((size*scale, size*scale), Image.Resampling.LANCZOS).save(iconset / f"icon_{size}x{size}{suffix}.png")
        subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(out / "ombra.icns")], check=True)


if __name__ == "__main__":
    main()

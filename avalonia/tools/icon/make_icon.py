# The app icon: the Ombra artwork (tools/icon/ombra-art.jpg, a smirking clown with a red nose on a dark card) cut out of its card,
# masked to the macOS-grid squircle (824/1024 body) with a soft drop shadow and a faint top rim. Needs Pillow.
# Usage: python3 tools/icon/make_icon.py src/Ombra.App/Assets  (writes icon-1024/512/256.png and icon.ico)
import math, os, sys
from PIL import Image, ImageChops, ImageDraw, ImageFilter

S = 2048  # supersampled canvas; downscaled at the end
U = S / 1024  # macOS icon grid units
ART = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ombra-art.jpg")
CARD = (79, 79, 1175, 1175)  # the artwork's card, just inside its drawn border

def superellipse(cx, cy, half, n=5.0, steps=720):
    pts = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        c, s = math.cos(t), math.sin(t)
        pts.append((cx + half * math.copysign(abs(c) ** (2 / n), c), cy + half * math.copysign(abs(s) ** (2 / n), s)))
    return pts

cx, cy = S / 2, S / 2 - 6 * U
half = 412 * U
body = superellipse(cx, cy, half)
left, top = round(cx - half), round(cy - half)
side = round(2 * half)

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

# Soft drop shadow
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(shadow).polygon([(x, y + 14 * U) for x, y in body], fill=(0, 0, 0, 90))
img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(18 * U)))

# Artwork, masked to the squircle
layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
layer.paste(Image.open(ART).convert("RGBA").crop(CARD).resize((side, side), Image.LANCZOS), (left, top))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).polygon(body, fill=255)
layer.putalpha(mask)
img.alpha_composite(layer)

# Faint light rim along the edge, strongest at the top and gone by mid-height
rim = Image.new("L", (S, S), 0)
ImageDraw.Draw(rim).line(body + body[:1], fill=255, width=round(3 * U))
fade = Image.linear_gradient("L").resize((S, S)).point(lambda v: max(0, 70 - v * 140 // 255))
glow = Image.new("RGBA", (S, S), (255, 255, 255, 0))
glow.putalpha(ImageChops.multiply(rim, fade))
img.alpha_composite(glow)

out = sys.argv[1]
for px in (1024, 512, 256):
    img.resize((px, px), Image.LANCZOS).save(f"{out}/icon-{px}.png")
img.resize((256, 256), Image.LANCZOS).save(f"{out}/icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

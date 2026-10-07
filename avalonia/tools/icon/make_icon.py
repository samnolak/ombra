# The app icon: a yellow macOS-grid squircle with a prompt and a sparkle. Needs Pillow.
# Usage: python3 tools/icon/make_icon.py src/OmpGui.App/Assets  (writes icon-1024/512/256.png and icon.ico)
import math, sys
from PIL import Image, ImageDraw, ImageFilter

S = 2048  # supersampled canvas; downscaled at the end
U = S / 1024  # macOS icon grid units

def superellipse(cx, cy, half, n=5.0, steps=720):
    pts = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        c, s = math.cos(t), math.sin(t)
        x = half * math.copysign(abs(c) ** (2 / n), c)
        y = half * math.copysign(abs(s) ** (2 / n), s)
        pts.append((cx + x, cy + y))
    return pts

def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
half = 412 * U  # 824/1024 body (Apple's grid)
cx = cy = S / 2
body = superellipse(cx, cy - 6 * U, half)

# Soft drop shadow
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(shadow).polygon([(x, y + 14 * U) for x, y in body], fill=(60, 40, 0, 90))
img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(18 * U)))

# Body: warm yellow vertical gradient
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).polygon(body, fill=255)
grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
top, bottom = (255, 226, 92, 255), (255, 176, 0, 255)
for y in range(S):
    gd.line([(0, y), (S, y)], fill=lerp(top, bottom, y / S))
img.paste(grad, (0, 0), mask)

# Top sheen and a hairline edge
sheen = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(sheen).ellipse([cx - 600 * U, cy - 900 * U, cx + 600 * U, cy - 120 * U], fill=(255, 255, 255, 34))
sheen = sheen.filter(ImageFilter.GaussianBlur(110 * U))
sm = Image.new("L", (S, S), 0)
sm.paste(sheen.getchannel("A"), (0, 0), mask)
sheen.putalpha(sm)
img.alpha_composite(sheen)
edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(edge).line(body + [body[0]], fill=(150, 95, 0, 70), width=round(3 * U))
img.alpha_composite(edge)

# Glyph: a bold prompt chevron and a cursor, ink on yellow
ink = (33, 26, 10, 255)
g = ImageDraw.Draw(img)
w = 92 * U
def stroke(points):
    g.line(points, fill=ink, width=round(w), joint="curve")
    r = w / 2
    for x, y in (points[0], points[-1]):
        g.ellipse([x - r, y - r, x + r, y + r], fill=ink)

ox, oy = cx - 30 * U, cy + 20 * U
stroke([(ox - 220 * U, oy - 190 * U), (ox - 30 * U, oy), (ox - 220 * U, oy + 190 * U)])
stroke([(ox + 70 * U, oy + 190 * U), (ox + 260 * U, oy + 190 * U)])

# A small four-point sparkle, top right: the agent
def sparkle(x, y, r, fill):
    k = 0.27
    pts = []
    for i in range(8):
        a = math.pi / 2 * (i // 2) + (math.pi / 4 if i % 2 else 0) - math.pi / 2
        rr = r if i % 2 == 0 else r * k
        pts.append((x + rr * math.cos(a), y + rr * math.sin(a)))
    g.polygon(pts, fill=fill)

sparkle(cx + 205 * U, cy - 215 * U, 120 * U, ink)
sparkle(cx + 330 * U, cy - 95 * U, 48 * U, ink)

out = sys.argv[1]
for size in (1024, 512, 256):
    img.resize((size, size), Image.LANCZOS).save(f"{out}/icon-{size}.png")
ico = img.resize((256, 256), Image.LANCZOS)
ico.save(f"{out}/icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

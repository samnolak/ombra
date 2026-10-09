# The app icon: a flat yellow macOS-grid squircle with two slanted plates (a white terminal plate over a graphite one)
# and a prompt on the white plate. Needs Pillow.
# Usage: python3 tools/icon/make_icon.py src/OmpGui.App/Assets  (writes icon-1024/512/256.png and icon.ico)
import math, sys
from PIL import Image, ImageDraw, ImageFilter

S = 2048  # supersampled canvas; downscaled at the end
U = S / 1024  # macOS icon grid units

YELLOW = (255, 221, 45, 255)
INK = (51, 51, 51, 255)
WHITE = (255, 255, 255, 255)

def superellipse(cx, cy, half, n=5.0, steps=720):
    pts = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        c, s = math.cos(t), math.sin(t)
        x = half * math.copysign(abs(c) ** (2 / n), c)
        y = half * math.copysign(abs(s) ** (2 / n), s)
        pts.append((cx + x, cy + y))
    return pts

def rounded(pts, r, steps=12):
    """The polygon with every corner filleted by a quadratic curve of radius ~r."""
    out = []
    for i, p1 in enumerate(pts):
        p0, p2 = pts[i - 1], pts[(i + 1) % len(pts)]
        a = (p0[0] - p1[0], p0[1] - p1[1])
        b = (p2[0] - p1[0], p2[1] - p1[1])
        la, lb = math.hypot(*a), math.hypot(*b)
        angle = math.acos(max(-1, min(1, (a[0] * b[0] + a[1] * b[1]) / (la * lb))))
        t = min(r / math.tan(angle / 2), la * 0.45, lb * 0.45)
        s = (p1[0] + a[0] / la * t, p1[1] + a[1] / la * t)
        e = (p1[0] + b[0] / lb * t, p1[1] + b[1] / lb * t)
        for k in range(steps + 1):
            u = k / steps
            out.append(tuple((1 - u) ** 2 * s[j] + 2 * (1 - u) * u * p1[j] + u ** 2 * e[j] for j in (0, 1)))
    return out

def isect(p, d, q, e):
    t = ((q[0] - p[0]) * e[1] - (q[1] - p[1]) * e[0]) / (d[0] * e[1] - d[1] * e[0])
    return (p[0] + t * d[0], p[1] + t * d[1])

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
cx = cy = S / 2
body = superellipse(cx, cy - 6 * U, 412 * U)  # 824/1024 body (Apple's grid)

# Soft drop shadow
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(shadow).polygon([(x, y + 14 * U) for x, y in body], fill=(0, 0, 0, 70))
img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(18 * U)))

# Body: flat yellow
g = ImageDraw.Draw(img)
g.polygon(body, fill=YELLOW)

# Two slanted plates, the white one in front; the pair is centred on the body
SLANT = 0.42  # horizontal shift per unit of height
W, H, DX, DY = 400, 250, 120, 150  # plate size and the graphite plate's offset, grid units

def plate(x, y):
    return [((x + H * SLANT) * U, y * U), ((x + W + H * SLANT) * U, y * U), ((x + W) * U, (y + H) * U), (x * U, (y + H) * U)]

x0 = 512 - (W + H * SLANT + DX) / 2
y0 = 506 - (H + DY) / 2
g.polygon(rounded(plate(x0 + DX, y0 + DY), 20 * U), fill=INK)
g.polygon(rounded(plate(x0, y0), 20 * U), fill=WHITE)

# Prompt on the white plate: a chevron with square-cut ends and a sharp tip, and a cursor bar
k = 0.40 * U
sw, arm = 100 * k, 180 * k  # stroke width; the arms' vertical reach

def chevron(xa, xb, yc):
    d = sw / 2
    L = math.hypot(xb - xa, arm)
    ux, uy = (xb - xa) / L, arm / L
    top, bot = (xa, yc - arm), (xa, yc + arm)
    nt, nb = (uy, -ux), (uy, ux)  # outer normals of the upper and lower arm
    to, ti = (top[0] + nt[0] * d, top[1] + nt[1] * d), (top[0] - nt[0] * d, top[1] - nt[1] * d)
    bo, bi = (bot[0] + nb[0] * d, bot[1] + nb[1] * d), (bot[0] - nb[0] * d, bot[1] - nb[1] * d)
    return [to, isect(to, (ux, uy), bo, (ux, -uy)), bo, bi, isect(ti, (ux, uy), bi, (ux, -uy)), ti]

mask = Image.new("L", (S, S), 0)
md = ImageDraw.Draw(mask)
md.polygon(chevron(cx - 215 * k, cx - 25 * k, cy), fill=255)
bar_y = cy + arm + 18 * k
md.rectangle([cx + 75 * k, bar_y - sw / 2, cx + 275 * k, bar_y + sw / 2], fill=255)
glyph = mask.crop(mask.getbbox())
centre = ((x0 + (W + H * SLANT) / 2) * U, (y0 + H / 2) * U)
img.paste(INK, (round(centre[0] - glyph.width / 2), round(centre[1] - glyph.height / 2)), glyph)

out = sys.argv[1]
for size in (1024, 512, 256):
    img.resize((size, size), Image.LANCZOS).save(f"{out}/icon-{size}.png")
ico = img.resize((256, 256), Image.LANCZOS)
ico.save(f"{out}/icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

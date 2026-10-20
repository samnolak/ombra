# The client's line icons come from Lucide (https://lucide.dev, ISC license; see THIRD-PARTY-NOTICES.md), drawn at its
# native stroke 2 on its 24-unit grid. This script downloads the pinned version's SVGs, turns every element (path,
# circle, ellipse, rect, line, polyline, polygon) into absolute path data on the same grid, and rewrites the
# <StreamGeometry> of each mapped key in src/Ombra.App/Views/Icons*.axaml in place (keys, comments and layout stay).
# Keys not in MAP (pets, the filled stop square) stay hand-drawn. Python 3 standard library only.
# Usage (from avalonia/): python3 tools/icons/lucide.py [--check]   (--check: report differences, write nothing)
import math, re, sys, urllib.request, xml.etree.ElementTree as ET
from pathlib import Path

VERSION = "1.57.0"
URL = "https://unpkg.com/lucide-static@{version}/icons/{name}.svg"

# Our key -> Lucide icon name, chosen by where the key is used
MAP = {
    # Window chrome, sidebar, composer (Icons.axaml)
    "IconPanelLeft": "panel-left",
    "IconCompose": "square-pen",
    "IconBrain": "brain",                      # thinking level, a reply's thinking
    "IconPlus": "plus",
    "IconFolder": "folder",
    "IconSearch": "search",
    "IconTerminal": "square-terminal",
    "IconSettings": "settings",                # the rail's Settings, General settings
    "IconArrowUp": "arrow-up",                 # send
    "IconArrowDown": "arrow-down",
    "IconAttach": "paperclip",
    "IconMic": "mic",
    "IconClose": "x",
    "IconSignOut": "log-out",
    "IconChevronDown": "chevron-down",
    "IconChevronUp": "chevron-up",
    "IconChevronRight": "chevron-right",
    "IconCopy": "copy",
    "IconShield": "shield",                    # approval modes
    "IconSparkle": "sparkle",                  # changed by this session, explain
    "IconCheck": "check",
    "IconLink": "link",
    "IconAlert": "triangle-alert",
    "IconArrowLeft": "arrow-left",
    "IconArrowRight": "arrow-right",
    "IconReload": "rotate-cw",
    "IconExternal": "external-link",
    "IconSelect": "square-dashed-mouse-pointer",
    "IconComment": "message-square-text",
    "IconChat": "message-circle",              # the rail's Chats
    "IconGlobe": "globe",
    "IconDevices": "monitor-smartphone",
    "IconRotate": "rotate-ccw-square",
    "IconKey": "key-round",
    "IconPlug": "plug",
    "IconGrid": "blocks",                      # plugins
    "IconMonitor": "monitor",
    "IconBranch": "git-branch",
    "IconServer": "server",
    "IconPaw": "paw-print",
    "IconDownload": "download",
    "IconCode": "code",
    "IconWrap": "text-wrap",
    "IconPulse": "activity",                   # diagnostics
    "IconFile": "file",
    "IconImage": "image",
    "IconExpand": "maximize-2",                # the file viewer takes the pane; the image at actual size
    "IconShrink": "minimize-2",
    "IconChecklist": "list-checks",
    "IconLayers": "layers",                    # tasks, agents
    "IconMoreHorizontal": "ellipsis",
    # Panels (Icons.Panes.axaml)
    "IconPanelRight": "panel-right",
    "IconChanges": "file-diff",
    "IconSkills": "book-open",
    "IconSummary": "list",
    "IconTools": "layout-grid",
    # Files (Icons.Files.axaml)
    "IconFiles": "files",
    "IconAt": "at-sign",
    "IconCollapse": "minimize-2",
    # Connectors, plugins, skills (Icons.Connectors.axaml)
    "IconPackage": "package",
    "IconStore": "store",
    "IconTrash": "trash",
    "IconPencil": "pencil",
    # Session menu, usage, model options (Icons.Session.axaml)
    "IconCompact": "fold-vertical",
    "IconHandoff": "file-output",
    "IconFresh": "refresh-ccw-dot",
    "IconRetry": "rotate-ccw",
    "IconRewind": "undo-2",
    "IconExport": "share",
    "IconShare": "share-2",
    "IconFolders": "folder-plus",
    "IconMove": "folder-input",
    "IconPin": "pin",
    "IconPinOff": "pin-off",                # unpin
    "IconChart": "chart-column",
    "IconMemory": "notebook",
    "IconHash": "hash",
    "IconFileText": "file-text",
    "IconBolt": "zap",                         # fast mode
    "IconExtendedContext": "move-horizontal",
    "IconAdvisor": "eye",
    "IconHand": "hand",                        # a session waits for the user
    "IconSpinner": "loader-circle",            # a running tool call (it turns)
    # Requirement states (Icons.Workspace.axaml)
    "IconCheckCircle": "circle-check",
    "IconCrossCircle": "circle-x",
    "IconAlertCircle": "circle-alert",
    "IconDashCircle": "circle-minus",
}

VIEWS = Path(__file__).resolve().parents[2] / "src" / "Ombra.App" / "Views"


def num(v):
    s = f"{v:.3f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


# Arguments per command, "f" a flag (one character, may run into the next number)
ARGS = {"M": "nn", "L": "nn", "H": "n", "V": "n", "C": "nnnnnn", "S": "nnnn", "Q": "nnnn", "T": "nn", "A": "nnnffnn", "Z": ""}
NUMBER = re.compile(r"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?")


def path_data(d):
    """SVG path data -> absolute M/L/C/Q/A/Z commands (H/V/S/T expanded) on the same grid."""
    out, i, cmd = [], 0, None
    x = y = sx = sy = 0.0
    last_c = last_q = None  # previous cubic / quadratic control point, for S / T

    def skip():
        nonlocal i
        while i < len(d) and d[i] in " ,\t\r\n":
            i += 1

    def read(kind):
        nonlocal i
        skip()
        if kind == "f":
            if d[i] not in "01":
                raise ValueError(f"bad arc flag at {i} in {d!r}")
            i += 1
            return float(d[i - 1])
        m = NUMBER.match(d, i)
        if not m:
            raise ValueError(f"number expected at {i} in {d!r}")
        i = m.end()
        return float(m.group())

    while True:
        skip()
        if i >= len(d):
            break
        if d[i].upper() in ARGS:
            cmd = d[i]
            i += 1
        elif cmd is None:
            raise ValueError(f"path data must start with a command: {d!r}")
        elif cmd in "Mm":
            cmd = "L" if cmd == "M" else "l"  # extra pairs after a move are lines
        up, rel = cmd.upper(), cmd.islower()
        a = [read(k) for k in ARGS[up]]
        ox, oy = (x, y) if rel else (0.0, 0.0)
        c, q = None, None
        if up == "M":
            x, y = a[0] + ox, a[1] + oy
            sx, sy = x, y
            out.append(f"M{num(x)} {num(y)}")
        elif up in "LHV":
            if up == "H":
                x = a[0] + ox
            elif up == "V":
                y = a[0] + oy
            else:
                x, y = a[0] + ox, a[1] + oy
            out.append(f"L{num(x)} {num(y)}")
        elif up in "CS":
            if up == "C":
                x1, y1, x2, y2, ex, ey = a[0] + ox, a[1] + oy, a[2] + ox, a[3] + oy, a[4] + ox, a[5] + oy
            else:
                x1, y1 = (2 * x - last_c[0], 2 * y - last_c[1]) if last_c else (x, y)
                x2, y2, ex, ey = a[0] + ox, a[1] + oy, a[2] + ox, a[3] + oy
            out.append(f"C{num(x1)} {num(y1)} {num(x2)} {num(y2)} {num(ex)} {num(ey)}")
            c, x, y = (x2, y2), ex, ey
        elif up in "QT":
            if up == "Q":
                x1, y1, ex, ey = a[0] + ox, a[1] + oy, a[2] + ox, a[3] + oy
            else:
                x1, y1 = (2 * x - last_q[0], 2 * y - last_q[1]) if last_q else (x, y)
                ex, ey = a[0] + ox, a[1] + oy
            out.append(f"Q{num(x1)} {num(y1)} {num(ex)} {num(ey)}")
            q, x, y = (x1, y1), ex, ey
        elif up == "A":
            x, y = a[5] + ox, a[6] + oy
            out.append(f"A{num(a[0])} {num(a[1])} {num(a[2])} {int(a[3])} {int(a[4])} {num(x)} {num(y)}")
        else:  # Z
            x, y = sx, sy
            out.append("Z")
        last_c, last_q = c, q
    return " ".join(out)


def ellipse(cx, cy, rx, ry):
    return (f"M{num(cx - rx)} {num(cy)} A{num(rx)} {num(ry)} 0 1 0 {num(cx + rx)} {num(cy)} "
            f"A{num(rx)} {num(ry)} 0 1 0 {num(cx - rx)} {num(cy)} Z")


def rect(x, y, w, h, rx, ry):
    if rx <= 0 or ry <= 0:
        return f"M{num(x)} {num(y)} L{num(x + w)} {num(y)} L{num(x + w)} {num(y + h)} L{num(x)} {num(y + h)} Z"
    rx, ry = min(rx, w / 2), min(ry, h / 2)
    arc = f"A{num(rx)} {num(ry)} 0 0 1"
    return (f"M{num(x + rx)} {num(y)} L{num(x + w - rx)} {num(y)} {arc} {num(x + w)} {num(y + ry)} "
            f"L{num(x + w)} {num(y + h - ry)} {arc} {num(x + w - rx)} {num(y + h)} "
            f"L{num(x + rx)} {num(y + h)} {arc} {num(x)} {num(y + h - ry)} "
            f"L{num(x)} {num(y + ry)} {arc} {num(x + rx)} {num(y)} Z")


def points(s, close):
    p = [float(v) for v in NUMBER.findall(s)]
    pairs = [f"{num(p[k])} {num(p[k + 1])}" for k in range(0, len(p) - 1, 2)]
    return "M" + " L".join(pairs) + (" Z" if close else "")


def svg_to_path(svg):
    root = ET.fromstring(svg)
    view = root.get("viewBox", "0 0 24 24").split()
    if [float(v) for v in view] != [0, 0, 24, 24]:
        raise ValueError(f"unexpected viewBox {view}")
    parts = []
    for el in root.iter():
        tag = el.tag.rsplit("}", 1)[-1]
        g = lambda k, default=0.0: float(el.get(k, default))
        if tag == "path":
            parts.append(path_data(el.get("d")))
        elif tag == "circle":
            parts.append(ellipse(g("cx"), g("cy"), g("r"), g("r")))
        elif tag == "ellipse":
            parts.append(ellipse(g("cx"), g("cy"), g("rx"), g("ry")))
        elif tag == "rect":
            rx, ry = el.get("rx"), el.get("ry")
            rx = float(rx) if rx is not None else float(ry) if ry is not None else 0.0
            ry = float(ry) if ry is not None else rx
            parts.append(rect(g("x"), g("y"), g("width"), g("height"), rx, ry))
        elif tag == "line":
            parts.append(f"M{num(g('x1'))} {num(g('y1'))} L{num(g('x2'))} {num(g('y2'))}")
        elif tag in ("polyline", "polygon"):
            parts.append(points(el.get("points"), tag == "polygon"))
        elif tag not in ("svg", "g", "title", "desc"):
            raise ValueError(f"unsupported SVG element <{tag}>")
    return " ".join(parts)


def fetch(name):
    with urllib.request.urlopen(URL.format(version=VERSION, name=name), timeout=30) as r:
        return r.read().decode("utf-8")


def main():
    check = "--check" in sys.argv[1:]
    geometry = {name: svg_to_path(fetch(name)) for name in sorted(set(MAP.values()))}
    entry = re.compile(r'(<StreamGeometry x:Key="(Icon\w+)">)(.*?)(</StreamGeometry>)', re.S)
    seen, changed = set(), []
    for file in sorted(VIEWS.glob("Icons*.axaml")):
        text = file.read_text(encoding="utf-8")

        def swap(m):
            key = m.group(2)
            if key not in MAP:
                return m.group(0)
            seen.add(key)
            return m.group(1) + geometry[MAP[key]] + m.group(4)

        new = entry.sub(swap, text)
        if new != text:
            changed.append(file.name)
            if not check:
                file.write_text(new, encoding="utf-8")
    missing = sorted(set(MAP) - seen)
    if missing:
        sys.exit(f"keys in MAP but not in Views/Icons*.axaml: {', '.join(missing)}")
    print(("would change: " if check else "rewrote: ") + (", ".join(changed) or "nothing"))
    if check and changed:
        sys.exit(1)


if __name__ == "__main__":
    main()

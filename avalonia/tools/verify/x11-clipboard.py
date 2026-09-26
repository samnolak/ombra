"""Owns the X11 CLIPBOARD selection with one file's bytes (e.g. image/png), as a screenshot tool would, so the package
E2E can paste a real image with Ctrl+V. Serves TARGETS and the given type until the selection is taken or it is killed.

    python x11-clipboard.py <file> <mime-type>        (needs python-xlib; DISPLAY set)
"""
import sys
from Xlib import X, Xatom, display
from Xlib.protocol import event

path, mime = sys.argv[1], sys.argv[2]
data = open(path, "rb").read()
d = display.Display()
w = d.screen().root.create_window(0, 0, 1, 1, 0, X.CopyFromParent)
CLIPBOARD, TARGETS, TYPE = d.intern_atom("CLIPBOARD"), d.intern_atom("TARGETS"), d.intern_atom(mime)
w.set_selection_owner(CLIPBOARD, X.CurrentTime)
d.flush()
if d.get_selection_owner(CLIPBOARD) != w:
    sys.exit("could not own CLIPBOARD")
print("owning CLIPBOARD with", len(data), "bytes of", mime, flush=True)
while True:
    e = d.next_event()
    if e.type == X.SelectionClear:
        break
    if e.type != X.SelectionRequest:
        continue
    prop = e.property if e.property != X.NONE else e.target
    if e.target == TARGETS:
        e.requestor.change_property(prop, Xatom.ATOM, 32, [TARGETS, TYPE])
    elif e.target == TYPE:
        e.requestor.change_property(prop, TYPE, 8, data)
    else:
        prop = X.NONE
    e.requestor.send_event(event.SelectionNotify(time=e.time, requestor=e.requestor, selection=e.selection,
                                                 target=e.target, property=prop), 0, 0)
    d.flush()

// Page annotations for the preview panel (Controls/PreviewPanel): injected into the page after each navigation.
// In annotate mode (as Codex's: "click, or drag to select an area") a click marks a point and a drag marks an area;
// nothing is outlined while the pointer moves. A comment box opens at the mark; the comment goes to the client with
// the mark (its place in the viewport and on the page), the element that holds it (a selector, its opening tag, its
// text, its box and, where the dev build exposes it, the source file) and, for an area, the elements inside it.
// Numbered pins (and an area's outline) stay on the page, anchored to that element so they follow scrolling; a click
// on a pin opens its comment again to change (Save) or delete it.
// Everything lives in a closed shadow root, so the page's CSS does not reach it and the page's scripts cannot read it;
// messages carry a token from this closure.
// Entry: window.__ompAnnotate.{setMode(bool), setPins([{id, number, kind, selector, offsetX, offsetY, width, height,
// pageX, pageY, comment}])}. Messages (JSON strings) through the web view's invokeCSharpAction:
// {type: "annotation" | "annotation-update" {id, comment} | "annotation-remove" {id} | "annotate-exit" | "ready", token, ...}.
(function (token) {
  "use strict";
  // Already here (injected again after a navigation inside the page): it keeps its own token, never takes one from
  // an object the page could have put there.
  if (window.__ompAnnotate) return;
  var TEXT_MAX = 160, TAG_MAX = 300, COMMENT_MAX = 2000, DRAG_MIN = 6, INSIDE_MAX = 6;
  var send = function (o) {
    o.token = token;
    try { window.invokeCSharpAction(JSON.stringify(o)); } catch (e) { /* no host bridge: nothing to tell */ }
  };

  var host = document.createElement("omp-annotate");
  host.style.cssText = "all:initial;position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;";
  var root = host.attachShadow({ mode: "closed" });
  root.innerHTML =
    "<style>" +
    ":host{all:initial}" +
    "*{box-sizing:border-box;font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif}" +
    ".sel,.area{position:fixed;pointer-events:none;border-radius:4px}" +
    ".sel{display:none;border:2px dashed #2f6feb;background:rgba(47,111,235,.10)}" +
    ".area{border:2px dashed #ffc21a;background:rgba(255,194,26,.10)}" +
    ".dot{position:fixed;display:none;pointer-events:none;width:14px;height:14px;margin:-7px 0 0 -7px;border-radius:7px;background:#2f6feb;" +
    "border:2px solid #fff;box-shadow:0 1px 4px rgba(0,0,0,.35)}" +
    ".pin{position:fixed;width:22px;height:22px;margin:-11px 0 0 -11px;border-radius:11px;background:#ffc21a;color:#211a0a;border:2px solid #fff;" +
    "box-shadow:0 1px 4px rgba(0,0,0,.35);font-size:11px;font-weight:600;line-height:18px;text-align:center;cursor:pointer;pointer-events:auto}" +
    ".pop{position:fixed;display:none;width:300px;background:#fff;color:#1f2328;border-radius:12px;padding:10px;pointer-events:auto;" +
    "box-shadow:0 8px 28px rgba(0,0,0,.22),0 0 0 1px rgba(0,0,0,.06)}" +
    ".what{font-size:11px;color:#6e7781;margin:0 2px 6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}" +
    "textarea{display:block;width:100%;min-height:64px;max-height:180px;resize:vertical;border:1px solid #d0d7de;border-radius:8px;padding:8px;" +
    "font-size:13px;line-height:1.4;color:#1f2328;background:#fff;outline:none}" +
    "textarea:focus{border-color:#2a78d6}" +
    ".row{display:flex;justify-content:flex-end;gap:6px;margin-top:8px}" +
    "button{border:0;border-radius:8px;height:30px;padding:0 12px;font-size:13px;font-weight:500;cursor:pointer;background:#f0f1f3;color:#1f2328}" +
    "button.del{display:none;margin-right:auto;background:none;color:#cf222e;padding:0 6px}" +
    "button.go{background:#ffc21a;color:#211a0a}" +
    "button:disabled{opacity:.5;cursor:default}" +
    ".hint{position:fixed;left:50%;bottom:12px;transform:translateX(-50%);display:none;background:#1f2328;color:#fff;font-size:12px;line-height:28px;" +
    "padding:0 12px;border-radius:14px;box-shadow:0 2px 10px rgba(0,0,0,.25);white-space:nowrap;pointer-events:none}" +
    "</style>" +
    "<div class='pins'></div>" +
    "<div class='sel'></div><div class='dot'></div>" +
    "<div class='hint'>Click to mark a point or drag to select an area · Esc to stop</div>" +
    "<div class='pop'><div class='what'></div><textarea></textarea>" +
    "<div class='row'><button class='del' type='button'>Delete</button><button class='no' type='button'>Cancel</button><button class='go' type='button'>Comment</button></div></div>";
  (document.documentElement || document.body).appendChild(host);

  var pinsBox = root.querySelector(".pins"), sel = root.querySelector(".sel"), dot = root.querySelector(".dot");
  var pop = root.querySelector(".pop"), what = root.querySelector(".what"), box = root.querySelector("textarea");
  var go = root.querySelector(".go"), no = root.querySelector(".no"), del = root.querySelector(".del"), hint = root.querySelector(".hint");
  // drag: the pointer is down on the page ({x, y} where it went down, {x1, y1} where it is). draft: the mark the
  // comment box is open for ({kind, el, offsetX, offsetY, width, height, inside}: its place follows el), or a pin
  // being edited (draft.edit: the box opened from its pin; Save changes the comment, Delete removes it).
  var on = false, drag = null, draft = null, pins = [], frame = 0;

  var esc = function (s) { return window.CSS && CSS.escape ? CSS.escape(s) : String(s).replace(/[^a-zA-Z0-9_-]/g, "\\$&"); };
  var unique = function (sel) { try { return document.querySelectorAll(sel).length === 1; } catch (e) { return false; } };
  var mine = function (e) { var p = e.composedPath ? e.composedPath() : []; return p.indexOf(host) >= 0; };
  var round = Math.round;

  // A selector that finds the element again: a unique id or data-testid, else a short path of tag, two classes and
  // :nth-of-type where siblings share the tag.
  function selectorOf(el) {
    var parts = [];
    for (var e = el; e && e.nodeType === 1 && e !== document.documentElement; e = e.parentElement) {
      var testId = e.getAttribute("data-testid");
      if (testId && unique("[data-testid=\"" + testId.replace(/"/g, "\\\"") + "\"]")) { parts.unshift("[data-testid=\"" + testId.replace(/"/g, "\\\"") + "\"]"); break; }
      if (e.id && unique("#" + esc(e.id))) { parts.unshift("#" + esc(e.id)); break; }
      if (e === document.body) { parts.unshift("body"); break; }
      var p = e.tagName.toLowerCase();
      var cls = Array.prototype.filter.call(e.classList, function (c) { return c.length < 40 && !/[:\[\]\/]/.test(c); }).slice(0, 2);
      if (cls.length) p += "." + cls.map(esc).join(".");
      var parent = e.parentElement;
      if (parent) {
        var same = Array.prototype.filter.call(parent.children, function (c) { return c.tagName === e.tagName; });
        if (same.length > 1) p += ":nth-of-type(" + (same.indexOf(e) + 1) + ")";
      }
      parts.unshift(p);
      if (parts.length >= 6) break;
    }
    return parts.join(" > ");
  }

  // Where a dev build says the element comes from: React (≤18) fibers, Vue's component file, Svelte's meta.
  function sourceOf(el) {
    try {
      for (var e = el; e; e = e.parentElement) {
        var key = Object.keys(e).find(function (k) { return k.indexOf("__reactFiber$") === 0; });
        if (key) for (var f = e[key]; f; f = f.return) if (f._debugSource) return f._debugSource.fileName + ":" + f._debugSource.lineNumber;
        if (e.__svelte_meta && e.__svelte_meta.loc) return e.__svelte_meta.loc.file + ":" + e.__svelte_meta.loc.line;
        var v = e.__vueParentComponent;
        if (v && v.type && v.type.__file) return v.type.__file;
      }
    } catch (x) { /* a page object we could not read */ }
    return "";
  }

  function openingTag(el) {
    var h = el.outerHTML || "", end = h.indexOf(">");
    return (end > 0 ? h.slice(0, end + 1) : h).slice(0, TAG_MAX);
  }

  function textOf(el) {
    var t = el.innerText || el.value || el.getAttribute("aria-label") || el.getAttribute("alt") || el.getAttribute("title") || "";
    return String(t).replace(/\s+/g, " ").trim().slice(0, TEXT_MAX);
  }

  function nameOf(el) {
    var c = Array.prototype.slice.call(el.classList, 0, 2).join(".");
    return el.tagName.toLowerCase() + (el.id ? "#" + el.id : c ? "." + c : "");
  }

  // The page's element at a viewport point (our own marks let the pointer through; a pin or the box counts as none).
  function at(x, y) {
    var el = document.elementFromPoint(x, y);
    return el && el !== host && el !== document.documentElement ? el : document.body;
  }

  // The element the area is about: from the one at its centre outwards, the first that covers at least half of it (a
  // box dragged around a card is a little larger than the card).
  function holderOf(r) {
    for (var e = at(r.left + r.width / 2, r.top + r.height / 2); e && e !== document.body; e = e.parentElement) {
      var b = e.getBoundingClientRect();
      var w = Math.min(b.right, r.left + r.width) - Math.max(b.left, r.left), h = Math.min(b.bottom, r.top + r.height) - Math.max(b.top, r.top);
      if (w > 0 && h > 0 && w * h >= r.width * r.height / 2) return e;
    }
    return document.body;
  }

  // What the area shows: the elements under a 4×3 grid of points in it, without the holder, each once.
  function insideOf(r, holder) {
    var seen = [], out = [];
    for (var j = 0; j < 3; j++) for (var i = 0; i < 4; i++) {
      var el = at(r.left + r.width * (i + 0.5) / 4, r.top + r.height * (j + 0.5) / 3);
      if (!el || el === holder || el === document.body || seen.indexOf(el) >= 0) continue;
      seen.push(el);
      out.push({ selector: selectorOf(el), tag: el.tagName.toLowerCase(), text: textOf(el) });
      if (out.length >= INSIDE_MAX) return out;
    }
    return out;
  }

  function place(div, left, top, width, height) {
    div.style.left = left + "px"; div.style.top = top + "px";
    if (width !== undefined) { div.style.width = width + "px"; div.style.height = height + "px"; }
  }

  // Where a mark is now: its element's box plus the offset (the element moved, scrolled or resized), else where it was
  // on the page. Null while its element is there but not shown.
  function whereIs(m) {
    if (m.el && !m.el.isConnected) m.el = null;
    if (!m.el && m.selector) { try { m.el = document.querySelector(m.selector); } catch (e) { m.el = null; } }
    if (m.el) {
      var b = m.el.getBoundingClientRect();
      if (b.width === 0 && b.height === 0) return null;
      return { left: b.left + m.offsetX, top: b.top + m.offsetY };
    }
    return { left: m.pageX - window.scrollX, top: m.pageY - window.scrollY };
  }

  function showDraft() {
    var p = draft && whereIs(draft);
    // A pin being edited is marked by its own pin (and outline)
    sel.style.display = p && !draft.edit && draft.kind === "area" ? "block" : "none";
    dot.style.display = p && !draft.edit && draft.kind === "point" ? "block" : "none";
    if (!p) return;
    if (draft.edit) { /* nothing more to draw */ }
    else if (draft.kind === "area") place(sel, p.left, p.top, draft.width, draft.height);
    else place(dot, p.left, p.top);
    var vw = window.innerWidth, vh = window.innerHeight, bottom = p.top + draft.height;
    var top = bottom + 8 + 150 < vh ? bottom + 8 : Math.max(8, p.top - 158);
    place(pop, Math.max(8, Math.min(p.left, vw - 308)), top);
  }

  function openDraft(kind, left, top, width, height) {
    var el = kind === "area" ? holderOf({ left: left, top: top, width: width, height: height }) : at(left, top);
    var b = el.getBoundingClientRect();
    draft = {
      kind: kind, el: el, selector: "", offsetX: left - b.left, offsetY: top - b.top, width: width, height: height,
      pageX: left + window.scrollX, pageY: top + window.scrollY,
      inside: kind === "area" ? insideOf({ left: left, top: top, width: width, height: height }, el) : []
    };
    var text = textOf(el);
    what.textContent = (kind === "area" ? "Area " + round(width) + "×" + round(height) + " in " : "Point on ") + nameOf(el) + (text ? " — " + text : "");
    box.placeholder = kind === "area" ? "Comment on this area" : "Comment on this point";
    openBox("", "Comment", false);
  }

  // A pin's comment, to change or delete (from its pin, in the mode or out of it)
  function openEdit(m) {
    drag = null;
    draft = m;
    m.edit = true;
    what.textContent = "Comment " + m.number;
    box.placeholder = "Comment";
    openBox(m.comment, "Save", true);
  }

  function openBox(value, label, editing) {
    box.value = value;
    go.textContent = label;
    go.disabled = !value.trim();
    del.style.display = editing ? "block" : "none";
    pop.style.display = "block";
    showDraft();
    setTimeout(function () { box.focus(); box.setSelectionRange(box.value.length, box.value.length); }, 0);
  }

  function closeDraft() {
    if (draft) draft.edit = false;
    draft = null;
    pop.style.display = sel.style.display = dot.style.display = "none";
  }

  function removeEdited() {
    var m = draft;
    if (!m || !m.edit) return;
    closeDraft();
    pins = pins.filter(function (p) { return p !== m; });
    drawPins();
    send({ type: "annotation-remove", id: m.id });
  }

  function submit() {
    var comment = box.value.trim().slice(0, COMMENT_MAX);
    var d = draft, p = d && whereIs(d);
    if (!d || !comment) return;
    if (d.edit) {
      d.comment = comment;
      closeDraft();
      drawPins();
      send({ type: "annotation-update", id: d.id, comment: comment });
      return;
    }
    if (!p) return;
    var el = d.el, r = el.getBoundingClientRect();
    var id = "a" + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
    var selector = selectorOf(el);
    var mark = {
      kind: d.kind, x: round(p.left), y: round(p.top), width: round(d.width), height: round(d.height),
      pageX: round(p.left + window.scrollX), pageY: round(p.top + window.scrollY), offsetX: round(d.offsetX), offsetY: round(d.offsetY),
      viewportWidth: window.innerWidth, viewportHeight: window.innerHeight
    };
    send({
      type: "annotation", id: id, comment: comment, url: location.href, title: document.title, mark: mark,
      element: {
        selector: selector, tag: el.tagName.toLowerCase(), html: openingTag(el), text: textOf(el), source: sourceOf(el),
        x: round(r.left), y: round(r.top), width: round(r.width), height: round(r.height)
      },
      inside: d.inside
    });
    // Shown at once; the client's list then numbers it (setPins)
    pins.push({
      id: id, number: pins.length + 1, kind: d.kind, selector: selector, el: el, offsetX: mark.offsetX, offsetY: mark.offsetY,
      width: mark.width, height: mark.height, pageX: mark.pageX, pageY: mark.pageY, comment: comment
    });
    closeDraft();
    drawPins();
  }

  function drawPins() {
    pinsBox.textContent = "";
    pins.forEach(function (m) {
      var p = whereIs(m);
      if (!p) return;
      if (m.kind === "area") {
        var a = document.createElement("div");
        a.className = "area";
        place(a, p.left, p.top, m.width, m.height);
        pinsBox.appendChild(a);
      }
      var d = document.createElement("div");
      d.className = "pin";
      d.textContent = String(m.number);
      d.title = m.comment + "\n(click to edit)";
      // Opens its comment; the page never sees the click
      d.addEventListener("mousedown", function (e) { e.stopPropagation(); e.preventDefault(); });
      d.addEventListener("click", function (e) { e.stopPropagation(); e.preventDefault(); openEdit(m); });
      place(d, p.left, p.top);
      pinsBox.appendChild(d);
    });
  }

  function reflow() {
    if (frame) return;
    frame = requestAnimationFrame(function () {
      frame = 0;
      if (pins.length) drawPins();
      if (draft) showDraft();
    });
  }

  function dragBox(d) {
    return { left: Math.min(d.x, d.x1), top: Math.min(d.y, d.y1), width: Math.abs(d.x1 - d.x), height: Math.abs(d.y1 - d.y) };
  }

  // While annotating, the page does not get the pointer: no link follows, no button fires, no text is selected. The
  // marks follow mouse events (every engine and input path sends them); pointer events only stop: cancelling
  // pointerdown would suppress the mouse events after it.
  function block(e) { if (on && !mine(e)) { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); } }
  function stop(e) { if (on && !mine(e)) { e.stopPropagation(); e.stopImmediatePropagation(); } }
  function onDown(e) {
    if (!on || mine(e)) return;
    block(e);
    if (e.button !== 0) return;
    closeDraft(); // a new mark replaces the one not commented on yet
    drag = { x: e.clientX, y: e.clientY, x1: e.clientX, y1: e.clientY, moved: false };
  }
  function onMove(e) {
    if (!on || !drag) return;
    block(e);
    drag.x1 = e.clientX; drag.y1 = e.clientY;
    if (!drag.moved && Math.abs(drag.x1 - drag.x) < DRAG_MIN && Math.abs(drag.y1 - drag.y) < DRAG_MIN) return;
    drag.moved = true;
    var r = dragBox(drag);
    sel.style.display = "block";
    place(sel, r.left, r.top, r.width, r.height);
  }
  function onUp(e) {
    if (!on || !drag) return;
    block(e);
    var d = drag, r = dragBox(d);
    drag = null;
    sel.style.display = "none";
    // A drag too thin to be an area is a click where it started
    if (d.moved && r.width >= DRAG_MIN && r.height >= DRAG_MIN) openDraft("area", r.left, r.top, r.width, r.height);
    else openDraft("point", d.x, d.y, 0, 0);
  }
  function onCancel() { drag = null; if (!draft) sel.style.display = "none"; }
  function onKey(e) {
    if (!on || e.key !== "Escape") return;
    e.preventDefault(); e.stopPropagation();
    if (drag) onCancel();
    else if (draft) closeDraft();
    else setMode(false, true);
  }

  box.addEventListener("input", function () { go.disabled = !box.value.trim(); });
  box.addEventListener("keydown", function (e) {
    e.stopPropagation();
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); submit(); }
    else if (e.key === "Escape") { e.preventDefault(); closeDraft(); }
  });
  go.addEventListener("click", submit);
  no.addEventListener("click", closeDraft);
  del.addEventListener("click", removeEdited);

  var BLOCKED = ["click", "dblclick", "auxclick", "contextmenu", "dragstart", "selectstart"];
  var STOPPED = ["pointerdown", "pointerup", "pointermove"];
  function setMode(value, fromPage) {
    value = !!value;
    if (value === on) return;
    on = value;
    var method = on ? "addEventListener" : "removeEventListener";
    BLOCKED.forEach(function (t) { document[method](t, block, true); });
    STOPPED.forEach(function (t) { document[method](t, stop, true); });
    document[method]("mousedown", onDown, true);
    document[method]("mousemove", onMove, true);
    document[method]("mouseup", onUp, true);
    document[method]("keydown", onKey, true);
    window[method]("blur", onCancel);
    document.documentElement.style.cursor = on ? "crosshair" : "";
    hint.style.display = on ? "block" : "none";
    if (!on) { drag = null; closeDraft(); }
    if (fromPage) send({ type: "annotate-exit" });
  }

  window.addEventListener("scroll", reflow, true);
  window.addEventListener("resize", reflow);

  window.__ompAnnotate = {
    setMode: function (v) { setMode(v, false); },
    setPins: function (list) {
      pins = (list || []).map(function (p) {
        return {
          id: p.id, number: p.number, kind: p.kind === "area" ? "area" : "point", selector: p.selector, el: null,
          offsetX: +p.offsetX || 0, offsetY: +p.offsetY || 0, width: +p.width || 0, height: +p.height || 0,
          pageX: +p.pageX || 0, pageY: +p.pageY || 0, comment: p.comment
        };
      });
      drawPins();
    },
    isOn: function () { return on; }
  };
  send({ type: "ready" });
})(__OMP_TOKEN__);

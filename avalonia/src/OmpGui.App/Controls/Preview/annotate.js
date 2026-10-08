// Page annotations for the preview panel (Controls/PreviewPanel): injected into the page after each navigation.
// In annotate mode the element under the pointer is outlined; a click opens a comment box on it; the comment goes to
// the client with what identifies the element (a selector, its opening tag, its text, its box and, where the dev build
// exposes it, the source file). Numbered pins mark the commented elements. Everything lives in a closed shadow root, so
// the page's CSS does not reach it and the page's scripts cannot read it; messages carry a token from this closure.
// Entry: window.__ompAnnotate.{setMode(bool), setPins([{id, number, selector, comment}])}. Messages (JSON strings)
// through the web view's invokeCSharpAction: {type: "annotation" | "annotate-exit" | "ready", token, ...}.
(function (token) {
  "use strict";
  // Already here (injected again after a navigation inside the page): it keeps its own token, never takes one from
  // an object the page could have put there.
  if (window.__ompAnnotate) return;
  var TEXT_MAX = 160, TAG_MAX = 300, COMMENT_MAX = 2000;
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
    ".hl{position:fixed;display:none;pointer-events:none;border:2px solid #2f6feb;background:rgba(47,111,235,.08);border-radius:4px}" +
    ".tag{position:absolute;left:-2px;top:-24px;white-space:nowrap;background:#1f2328;color:#fff;font-size:11px;line-height:20px;padding:0 6px;border-radius:4px;max-width:360px;overflow:hidden;text-overflow:ellipsis}" +
    ".pin{position:fixed;width:22px;height:22px;margin:-11px 0 0 -11px;border-radius:11px;background:#ffc21a;color:#211a0a;border:2px solid #fff;" +
    "box-shadow:0 1px 4px rgba(0,0,0,.35);font-size:11px;font-weight:600;line-height:18px;text-align:center;cursor:default;pointer-events:auto}" +
    ".pop{position:fixed;display:none;width:300px;background:#fff;color:#1f2328;border-radius:12px;padding:10px;pointer-events:auto;" +
    "box-shadow:0 8px 28px rgba(0,0,0,.22),0 0 0 1px rgba(0,0,0,.06)}" +
    ".what{font-size:11px;color:#6e7781;margin:0 2px 6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}" +
    "textarea{display:block;width:100%;min-height:64px;max-height:180px;resize:vertical;border:1px solid #d0d7de;border-radius:8px;padding:8px;" +
    "font-size:13px;line-height:1.4;color:#1f2328;background:#fff;outline:none}" +
    "textarea:focus{border-color:#2a78d6}" +
    ".row{display:flex;justify-content:flex-end;gap:6px;margin-top:8px}" +
    "button{border:0;border-radius:8px;height:30px;padding:0 12px;font-size:13px;font-weight:500;cursor:pointer;background:#f0f1f3;color:#1f2328}" +
    "button.go{background:#ffc21a;color:#211a0a}" +
    "button:disabled{opacity:.5;cursor:default}" +
    ".hint{position:fixed;left:50%;bottom:12px;transform:translateX(-50%);display:none;background:#1f2328;color:#fff;font-size:12px;line-height:28px;" +
    "padding:0 12px;border-radius:14px;box-shadow:0 2px 10px rgba(0,0,0,.25);white-space:nowrap;pointer-events:none}" +
    "</style>" +
    "<div class='hl'><span class='tag'></span></div>" +
    "<div class='hint'>Click an element to comment on it · Esc to stop</div>" +
    "<div class='pins'></div>" +
    "<div class='pop'><div class='what'></div><textarea placeholder='Comment on this element'></textarea>" +
    "<div class='row'><button class='no' type='button'>Cancel</button><button class='go' type='button'>Comment</button></div></div>";
  (document.documentElement || document.body).appendChild(host);

  var hl = root.querySelector(".hl"), tag = root.querySelector(".tag"), pinsBox = root.querySelector(".pins");
  var pop = root.querySelector(".pop"), what = root.querySelector(".what"), box = root.querySelector("textarea");
  var go = root.querySelector(".go"), no = root.querySelector(".no"), hint = root.querySelector(".hint");
  var on = false, target = null, pins = [], frame = 0;

  var esc = function (s) { return window.CSS && CSS.escape ? CSS.escape(s) : String(s).replace(/[^a-zA-Z0-9_-]/g, "\\$&"); };
  var unique = function (sel) { try { return document.querySelectorAll(sel).length === 1; } catch (e) { return false; } };
  var mine = function (e) { var p = e.composedPath ? e.composedPath() : []; return p.indexOf(host) >= 0; };

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

  function label(el) {
    var r = el.getBoundingClientRect(), c = Array.prototype.slice.call(el.classList, 0, 2).join(".");
    return el.tagName.toLowerCase() + (el.id ? "#" + el.id : c ? "." + c : "") + "  " + Math.round(r.width) + "×" + Math.round(r.height);
  }

  function outline(el) {
    if (!el) { hl.style.display = "none"; return; }
    var r = el.getBoundingClientRect();
    hl.style.display = "block";
    hl.style.left = r.left - 2 + "px"; hl.style.top = r.top - 2 + "px";
    hl.style.width = r.width + 4 + "px"; hl.style.height = r.height + 4 + "px";
    tag.textContent = label(el);
    tag.style.top = r.top < 28 ? "calc(100% + 4px)" : "-24px";
  }

  function under(e) {
    var el = document.elementFromPoint(e.clientX, e.clientY);
    return el && el !== host && el !== document.documentElement ? el : null;
  }

  function openBox(el) {
    target = el;
    outline(el);
    what.textContent = label(el) + (textOf(el) ? " — " + textOf(el) : "");
    box.value = "";
    go.disabled = true;
    var r = el.getBoundingClientRect(), vw = window.innerWidth, vh = window.innerHeight;
    var left = Math.max(8, Math.min(r.left, vw - 308));
    var top = r.bottom + 8 + 150 < vh ? r.bottom + 8 : Math.max(8, r.top - 158);
    pop.style.left = left + "px"; pop.style.top = top + "px"; pop.style.display = "block";
    setTimeout(function () { box.focus(); }, 0);
  }

  function closeBox() { pop.style.display = "none"; target = null; }

  function submit() {
    var comment = box.value.trim().slice(0, COMMENT_MAX);
    if (!target || !comment) return;
    var el = target, r = el.getBoundingClientRect();
    var id = "a" + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
    var selector = selectorOf(el);
    send({
      type: "annotation", id: id, comment: comment, url: location.href, title: document.title,
      element: {
        selector: selector, tag: el.tagName.toLowerCase(), html: openingTag(el), text: textOf(el), source: sourceOf(el),
        x: Math.round(r.left), y: Math.round(r.top), width: Math.round(r.width), height: Math.round(r.height),
        viewportWidth: window.innerWidth, viewportHeight: window.innerHeight
      }
    });
    // Shown at once; the client's list then numbers it (setPins)
    pins.push({ id: id, number: pins.length + 1, selector: selector, comment: comment, el: el });
    drawPins();
    closeBox();
    outline(null);
  }

  function drawPins() {
    pinsBox.textContent = "";
    pins.forEach(function (p) {
      var el = p.el && p.el.isConnected ? p.el : (function () { try { return document.querySelector(p.selector); } catch (e) { return null; } })();
      p.el = el;
      if (!el) return;
      var r = el.getBoundingClientRect();
      if (r.width === 0 && r.height === 0) return;
      var d = document.createElement("div");
      d.className = "pin";
      d.textContent = String(p.number);
      d.title = p.comment;
      d.style.left = Math.max(11, r.left) + "px";
      d.style.top = Math.max(11, r.top) + "px";
      pinsBox.appendChild(d);
    });
  }

  function reflow() {
    if (frame) return;
    frame = requestAnimationFrame(function () {
      frame = 0;
      if (pins.length) drawPins();
      if (target) outline(target);
    });
  }

  // While annotating, the page does not get the pointer: no link follows, no button fires.
  function block(e) { if (on && !mine(e)) { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); } }
  function onMove(e) { if (on && !target && !mine(e)) outline(under(e)); }
  function onClick(e) {
    if (!on || mine(e)) return;
    block(e);
    var el = under(e);
    if (el) openBox(el);
  }
  function onKey(e) {
    if (!on || e.key !== "Escape") return;
    e.preventDefault(); e.stopPropagation();
    if (target) { closeBox(); outline(null); } else setMode(false, true);
  }

  box.addEventListener("input", function () { go.disabled = !box.value.trim(); });
  box.addEventListener("keydown", function (e) {
    e.stopPropagation();
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); submit(); }
    else if (e.key === "Escape") { e.preventDefault(); closeBox(); outline(null); }
  });
  go.addEventListener("click", submit);
  no.addEventListener("click", function () { closeBox(); outline(null); });

  function setMode(value, fromPage) {
    value = !!value;
    if (value === on) return;
    on = value;
    var method = on ? "addEventListener" : "removeEventListener";
    ["mousedown", "mouseup", "pointerdown", "pointerup", "dblclick", "auxclick", "contextmenu"].forEach(function (t) { document[method](t, block, true); });
    document[method]("click", onClick, true);
    document[method]("mousemove", onMove, true);
    document[method]("keydown", onKey, true);
    document.documentElement.style.cursor = on ? "crosshair" : "";
    hint.style.display = on ? "block" : "none";
    if (!on) { closeBox(); outline(null); }
    if (fromPage) send({ type: "annotate-exit" });
  }

  window.addEventListener("scroll", reflow, true);
  window.addEventListener("resize", reflow);

  window.__ompAnnotate = {
    setMode: function (v) { setMode(v, false); },
    setPins: function (list) {
      pins = (list || []).map(function (p) { return { id: p.id, number: p.number, selector: p.selector, comment: p.comment, el: null }; });
      drawPins();
    },
    isOn: function () { return on; }
  };
  send({ type: "ready" });
})(__OMP_TOKEN__);

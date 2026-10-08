using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace OmpGui.ClientCore;

/// <summary>
/// The page side of <see cref="AgentBrowserBridge"/>: scripts the preview's web view runs for omp's browser tool.
/// Every request script is one expression; the bridge wraps it (<see cref="Wrap"/>) so its value, or what it threw,
/// comes back as JSON text whatever the engine (WKWebView, WebView2, WebKitGTK) does with script results.
/// </summary>
/// <remarks>
/// Where the page has the app's isolated world (<see cref="Browser.IIsolatedScripts"/>, macOS) the library runs there
/// (<see cref="IsolatedCall"/>). Elsewhere it runs in the page's own world, kept where the page cannot trivially swap
/// it: under a random global name chosen per bridge (<see cref="Name"/>, not guessable before the first install),
/// defined non-enumerable, non-writable and non-configurable, the object frozen and carrying a random seal. A library is
/// reused only when all of that holds; a configurable or unsealed value under the name is replaced, a non-configurable
/// one that is not ours is refused (<c>blocked</c>). omp's own <c>browser.eval</c> scripts always run in the page world.
/// </remarks>
internal sealed class AgentBrowserScripts
{
    /// <summary>
    /// The page library as a function of (name, seal), installed once per document: element refs of the latest
    /// snapshot, the snapshot itself and the input actions. It throws <c>Error("code: message")</c> for the errors the
    /// bridge reports with a code (not_found, invalid_params, invalid_target, blocked).
    /// </summary>
    private const string LibrarySource = """
((name, seal) => {
  const d = Object.getOwnPropertyDescriptor(globalThis, name);
  if (d) {
    const o = d.value;
    if (!d.configurable && !d.writable && !d.enumerable && !d.get && !d.set && o && typeof o === "object" &&
        Object.isFrozen(o) && Object.getPrototypeOf(o) === Object.prototype && o.v === 2 && o.seal === seal) return o;
    if (!d.configurable) throw new Error("blocked: the page defined a global under the name this browser's scripts use; reload the page");
  }
  const A = { v: 2, seal, refs: new Map() };
  const SKIP = new Set(["SCRIPT", "STYLE", "NOSCRIPT", "TEMPLATE", "HEAD", "META", "LINK", "TITLE", "OMP-ANNOTATE"]);
  const INTERACTIVE = new Set(["button", "link", "textbox", "searchbox", "checkbox", "radio", "combobox", "listbox", "option",
    "menuitem", "menuitemcheckbox", "menuitemradio", "tab", "switch", "slider", "spinbutton", "treeitem"]);
  const SEMANTIC = new Set(["heading", "img", "navigation", "main", "banner", "contentinfo", "complementary", "form", "dialog",
    "alertdialog", "alert", "status", "list", "listitem", "table", "row", "cell", "columnheader", "rowheader", "paragraph",
    "article", "region", "tabpanel", "figure", "blockquote", "code"]);
  // Containers are named by their labels only: their text is everything inside them
  const CONTAINER = new Set(["navigation", "main", "banner", "contentinfo", "complementary", "form", "dialog", "alertdialog",
    "list", "table", "row", "region", "tabpanel", "article", "figure", "generic"]);
  const clip = (s, n) => {
    s = String(s == null ? "" : s).replace(/\s+/g, " ").trim();
    return s.length > n ? s.slice(0, n - 1) + "\u2026" : s;
  };
  const implicitRole = el => {
    const t = el.tagName.toLowerCase();
    switch (t) {
      case "a": case "area": return el.hasAttribute("href") ? "link" : null;
      case "button": case "summary": return "button";
      case "textarea": return "textbox";
      case "select": return el.multiple || el.size > 1 ? "listbox" : "combobox";
      case "option": return "option";
      case "img": return el.getAttribute("alt") === "" ? "presentation" : "img";
      case "h1": case "h2": case "h3": case "h4": case "h5": case "h6": return "heading";
      case "nav": return "navigation";
      case "main": return "main";
      case "header": return "banner";
      case "footer": return "contentinfo";
      case "aside": return "complementary";
      case "form": return "form";
      case "dialog": return "dialog";
      case "ul": case "ol": return "list";
      case "li": return "listitem";
      case "table": return "table";
      case "tr": return "row";
      case "td": return "cell";
      case "th": return "columnheader";
      case "p": return "paragraph";
      case "article": return "article";
      case "section": return el.hasAttribute("aria-label") || el.hasAttribute("aria-labelledby") ? "region" : null;
      case "figure": return "figure";
      case "blockquote": return "blockquote";
      case "pre": return "code";
      case "input": {
        const type = (el.type || "text").toLowerCase();
        if (type === "hidden") return null;
        if (type === "checkbox") return "checkbox";
        if (type === "radio") return "radio";
        if (type === "range") return "slider";
        if (type === "number") return "spinbutton";
        if (type === "search") return "searchbox";
        if (["button", "submit", "reset", "image"].includes(type)) return "button";
        return "textbox";
      }
    }
    return null;
  };
  const roleOf = el => (el.getAttribute("role") || "").trim().split(/\s+/)[0] || implicitRole(el) || "generic";
  const nameOf = (el, role, active) => {
    const by = el.getAttribute("aria-labelledby");
    if (by) {
      const text = by.split(/\s+/).map(id => (document.getElementById(id) || {}).textContent || "").join(" ");
      if (text.trim()) return clip(text, 120);
    }
    const aria = el.getAttribute("aria-label");
    if (aria && aria.trim()) return clip(aria, 120);
    if (el.labels && el.labels.length) {
      const text = Array.from(el.labels).map(l => l.innerText || l.textContent || "").join(" ");
      if (text.trim()) return clip(text, 120);
    }
    const tag = el.tagName;
    if (tag === "INPUT" && ["button", "submit", "reset"].includes(el.type)) return clip(el.value || el.type, 120);
    if (tag === "IMG" || (tag === "INPUT" && el.type === "image")) return clip(el.getAttribute("alt") || el.getAttribute("title") || "", 120);
    if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT")
      return clip(el.getAttribute("placeholder") || el.getAttribute("title") || el.getAttribute("name") || "", 120);
    if (!active && CONTAINER.has(role)) return clip(el.getAttribute("title") || "", 120);
    const text = el.innerText;
    if (text && text.trim()) return clip(text, 120);
    return clip(el.getAttribute("title") || "", 120);
  };
  const isInteractive = (el, role) =>
    INTERACTIVE.has(role) ||
    (el.isContentEditable && !(el.parentElement && el.parentElement.isContentEditable)) ||
    el.hasAttribute("onclick") ||
    (el.hasAttribute("tabindex") && el.tabIndex >= 0);
  const hidden = el => {
    const s = getComputedStyle(el);
    return s.display === "none" || s.visibility === "hidden" || s.visibility === "collapse";
  };
  const hasBox = el => {
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  };
  const deepQuery = (root, selector) => {
    const found = root.querySelector(selector);
    if (found) return found;
    for (const el of root.querySelectorAll("*")) {
      if (el.shadowRoot) {
        const inner = deepQuery(el.shadowRoot, selector);
        if (inner) return inner;
      }
      if (el.tagName === "IFRAME" || el.tagName === "FRAME") {
        let doc = null;
        try { doc = el.contentDocument; } catch (e) { } // another origin: out of reach
        if (doc) {
          const inner = deepQuery(doc, selector);
          if (inner) return inner;
        }
      }
    }
    return null;
  };
  A.resolve = selector => {
    const ref = /^@?(e\d+)$/.exec(String(selector).trim());
    if (ref) {
      const el = A.refs.get(ref[1]);
      return el && el.isConnected ? el : null;
    }
    try {
      return deepQuery(document, selector);
    } catch (e) {
      throw new Error("invalid_params: " + JSON.stringify(selector) + " is not a valid CSS selector");
    }
  };
  A.must = selector => {
    const el = A.resolve(selector);
    if (el) return el;
    const ref = /^@?e\d+$/.test(String(selector).trim());
    throw new Error("not_found: no element matches " + JSON.stringify(selector) +
      (ref ? " (element refs come from the latest snapshot of this page: observe it again)" : ""));
  };
  A.snapshot = (interactiveOnly, maxDepth, withHtml) => {
    A.refs.clear();
    const refs = {};
    const lines = [];
    let count = 0;
    const limit = 1500;
    const depthCap = Math.max(1, Number(maxDepth) || 12);
    const walk = (parent, level) => {
      for (const el of parent.children) {
        if (count >= limit) return;
        if (SKIP.has(el.tagName) || el.getAttribute("aria-hidden") === "true" || hidden(el)) continue;
        const role = roleOf(el);
        let next = level;
        if (role !== "presentation" && role !== "none" && hasBox(el)) {
          const active = isInteractive(el, role);
          if (active || (!interactiveOnly && SEMANTIC.has(role))) {
            const id = "e" + (++count);
            const name = nameOf(el, role, active);
            A.refs.set(id, el);
            refs[id] = { role, name };
            let line = "  ".repeat(Math.min(level, depthCap)) + "- " + role + (name ? " " + JSON.stringify(name) : "");
            if (role === "heading") line += " [level=" + (Number(el.getAttribute("aria-level")) || Number(el.tagName.slice(1)) || 2) + "]";
            if ("checked" in el && (el.type === "checkbox" || el.type === "radio")) { if (el.checked) line += " [checked]"; }
            else if (el.getAttribute("aria-checked") === "true") line += " [checked]";
            if (el.disabled || el.getAttribute("aria-disabled") === "true") line += " [disabled]";
            if (el.getAttribute("aria-expanded")) line += " [expanded=" + el.getAttribute("aria-expanded") + "]";
            line += " [ref=" + id + "]";
            if (el.tagName === "SELECT") {
              const chosen = Array.from(el.selectedOptions || []).map(o => clip(o.text, 80)).join(", ");
              if (chosen) line += ": " + JSON.stringify(chosen);
            } else if ((role === "textbox" || role === "searchbox" || role === "combobox" || role === "spinbutton") && "value" in el && el.value)
              line += ": " + JSON.stringify(el.type === "password" ? "\u2022\u2022\u2022\u2022" : clip(el.value, 80));
            if (role === "link" && el.href) line += " -> " + el.href;
            lines.push(line);
            next = level + 1;
          }
        }
        if (el.shadowRoot) walk(el.shadowRoot, next);
        walk(el, next);
      }
    };
    if (document.body) walk(document.body, 0);
    const page = { url: location.href, title: document.title, ready_state: document.readyState };
    if (withHtml) {
      const copy = document.documentElement.cloneNode(true);
      for (const host of copy.querySelectorAll("omp-annotate")) host.remove();
      page.html = "<!DOCTYPE html>\n" + copy.outerHTML;
    }
    if (count >= limit) lines.push("- \u2026 (the page has more elements than a snapshot lists)");
    return { snapshot: lines.join("\n"), refs, url: page.url, title: page.title, ready_state: page.ready_state, page };
  };
  const center = el => {
    const r = el.getBoundingClientRect();
    return { clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 };
  };
  const fire = (el, type, init, Kind) => el.dispatchEvent(new (Kind || MouseEvent)(type,
    Object.assign({ bubbles: true, cancelable: true, composed: true, view: window }, init)));
  const pointer = (el, type, init) => {
    if (typeof PointerEvent === "function")
      fire(el, type, Object.assign({ pointerId: 1, pointerType: "mouse", isPrimary: true }, init), PointerEvent);
  };
  const press = (el, init, detail) => {
    const at = Object.assign({ button: 0, detail }, init);
    pointer(el, "pointerdown", Object.assign({ buttons: 1 }, at));
    fire(el, "mousedown", Object.assign({ buttons: 1 }, at));
    if (typeof el.focus === "function" && el.tabIndex >= 0) el.focus({ preventScroll: true });
    pointer(el, "pointerup", at);
    fire(el, "mouseup", at);
    if (typeof el.click === "function" && detail === 1) el.click();
    else fire(el, "click", at);
  };
  const reveal = el => el.scrollIntoView({ block: "center", inline: "center" });
  const textField = el =>
    el.tagName === "TEXTAREA" ||
    (el.tagName === "INPUT" && !["checkbox", "radio", "button", "submit", "reset", "image", "file", "hidden", "range", "color"].includes((el.type || "text").toLowerCase()));
  const setValue = (el, value) => {
    const proto = el.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : el.tagName === "SELECT" ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
    const d = Object.getOwnPropertyDescriptor(proto, "value");
    if (d && d.set) d.set.call(el, value); else el.value = value;
  };
  const input = (el, inputType, data) => {
    const init = { bubbles: true, cancelable: false, composed: true, inputType, data };
    el.dispatchEvent(typeof InputEvent === "function" ? new InputEvent("input", init) : new Event("input", init));
  };
  const insert = (el, text) => {
    if (textField(el)) {
      try {
        const start = el.selectionStart == null ? el.value.length : el.selectionStart;
        const end = el.selectionEnd == null ? start : el.selectionEnd;
        el.setRangeText(text, start, end, "end");
      } catch (e) {
        setValue(el, String(el.value || "") + text); // email, number: no selection API
      }
      input(el, "insertText", text);
      return true;
    }
    if (el.isContentEditable) return document.execCommand("insertText", false, text);
    return false;
  };
  const changed = el => el.dispatchEvent(new Event("change", { bubbles: true }));
  const activeElement = () => {
    let el = document.activeElement || document.body;
    while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
    return el || document.documentElement;
  };
  const keyCode = key => key.length === 1
    ? (/[a-z]/i.test(key) ? "Key" + key.toUpperCase() : /[0-9]/.test(key) ? "Digit" + key : key === " " ? "Space" : "")
    : key;
  const tabbables = () => Array.from(document.querySelectorAll("a[href], button, input, select, textarea, summary, [tabindex], [contenteditable='true']"))
    .filter(el => el.tabIndex >= 0 && !el.disabled && !hidden(el) && hasBox(el));
  A.act = (action, selector, args) => {
    const el = A.must(selector);
    args = args || {};
    if (action !== "focus") reveal(el);
    switch (action) {
      case "click": press(el, center(el), 1); return true;
      case "dblclick": {
        const at = center(el);
        press(el, at, 1);
        press(el, at, 2);
        fire(el, "dblclick", Object.assign({ button: 0, detail: 2 }, at));
        return true;
      }
      case "hover": {
        const at = center(el);
        pointer(el, "pointerover", at);
        pointer(el, "pointerenter", Object.assign({ bubbles: false }, at));
        fire(el, "mouseover", at);
        fire(el, "mouseenter", Object.assign({ bubbles: false }, at));
        pointer(el, "pointermove", at);
        fire(el, "mousemove", at);
        return true;
      }
      case "focus": if (typeof el.focus === "function") el.focus(); return true;
      case "scroll_into_view": return true;
      case "check": case "uncheck": {
        const want = action === "check";
        const native = el.tagName === "INPUT" && (el.type === "checkbox" || el.type === "radio");
        const role = (el.getAttribute("role") || "").toLowerCase();
        if (!native && !["checkbox", "radio", "switch", "menuitemcheckbox", "menuitemradio"].includes(role))
          throw new Error("invalid_target: " + JSON.stringify(selector) + " is not a checkbox, radio button or switch");
        const now = native ? el.checked : el.getAttribute("aria-checked") === "true";
        if (now !== want) press(el, center(el), 1);
        return true;
      }
      case "type": {
        const text = String(args.text == null ? "" : args.text);
        if (typeof el.focus === "function") el.focus();
        if (!textField(el) && !el.isContentEditable)
          throw new Error("invalid_target: " + JSON.stringify(selector) + " is not a text field");
        for (const ch of text) {
          const init = { key: ch === "\n" ? "Enter" : ch, code: keyCode(ch === "\n" ? "Enter" : ch), bubbles: true, cancelable: true, composed: true };
          if (el.dispatchEvent(new KeyboardEvent("keydown", init))) {
            el.dispatchEvent(new KeyboardEvent("keypress", init));
            insert(el, ch);
          }
          el.dispatchEvent(new KeyboardEvent("keyup", init));
        }
        if (textField(el)) changed(el);
        return true;
      }
      case "fill": {
        const text = String(args.text == null ? "" : args.text);
        if (typeof el.focus === "function") el.focus();
        if (el.tagName === "SELECT") {
          const option = Array.from(el.options).find(o => o.value === text) || Array.from(el.options).find(o => clip(o.text, 1000) === text);
          if (!option) throw new Error("not_found: no option " + JSON.stringify(text) + " in " + JSON.stringify(selector));
          el.selectedIndex = option.index;
          input(el, "insertReplacementText", null);
          changed(el);
          return true;
        }
        if (textField(el)) {
          setValue(el, text);
          input(el, "insertReplacementText", text);
          changed(el);
          return true;
        }
        if (el.isContentEditable) {
          const range = document.createRange();
          range.selectNodeContents(el);
          const sel = getSelection();
          sel.removeAllRanges();
          sel.addRange(range);
          if (text) document.execCommand("insertText", false, text); else document.execCommand("delete");
          return true;
        }
        throw new Error("invalid_target: " + JSON.stringify(selector) + " is not a text field or a select");
      }
      case "scroll": {
        el.scrollBy({ left: Number(args.dx) || 0, top: Number(args.dy) || 0, behavior: "instant" });
        return true;
      }
    }
    throw new Error("invalid_params: unknown action " + JSON.stringify(action));
  };
  A.press = combo => {
    const parts = String(combo) === "+" ? ["+"] : String(combo).split("+");
    let key = parts.pop() || "+";
    const mods = new Set(parts.map(p => p.trim().toLowerCase()));
    if (key === "Space" || key === "Spacebar") key = " ";
    const init = {
      key, code: keyCode(key), bubbles: true, cancelable: true, composed: true,
      shiftKey: mods.has("shift"), altKey: mods.has("alt") || mods.has("option"),
      ctrlKey: mods.has("control") || mods.has("ctrl"), metaKey: mods.has("meta") || mods.has("cmd") || mods.has("command"),
    };
    const el = activeElement();
    const go = el.dispatchEvent(new KeyboardEvent("keydown", init));
    const plain = !init.ctrlKey && !init.metaKey && !init.altKey;
    if (go) {
      if (key.length === 1 && plain) {
        el.dispatchEvent(new KeyboardEvent("keypress", init));
        if (!insert(el, key) && key === " " && typeof el.click === "function" && ["BUTTON", "SUMMARY", "INPUT"].includes(el.tagName)) el.click();
      } else if (key === "Enter" && plain) {
        el.dispatchEvent(new KeyboardEvent("keypress", init));
        if (el.tagName === "TEXTAREA" || (el.isContentEditable && !textField(el))) insert(el, "\n");
        else if (el.tagName === "INPUT" && el.form) {
          if (typeof el.form.requestSubmit === "function") el.form.requestSubmit(); else el.form.submit();
        } else if (typeof el.click === "function" && (el.tagName === "BUTTON" || el.tagName === "A" || el.tagName === "SUMMARY" || el.getAttribute("role") === "button")) el.click();
      } else if ((key === "Backspace" || key === "Delete") && plain && textField(el)) {
        let start = el.selectionStart, end = el.selectionEnd;
        try {
          if (start === end) { if (key === "Backspace") start = Math.max(0, start - 1); else end = Math.min(el.value.length, end + 1); }
          el.setRangeText("", start, end, "end");
        } catch (e) {
          setValue(el, String(el.value || "").slice(0, -1));
        }
        input(el, key === "Backspace" ? "deleteContentBackward" : "deleteContentForward", null);
      } else if (key === "Tab" && !init.ctrlKey && !init.metaKey) {
        const list = tabbables();
        if (list.length) {
          const i = list.indexOf(el);
          const next = list[(i < 0 ? 0 : i + (init.shiftKey ? list.length - 1 : 1)) % list.length];
          next.focus();
        }
      } else if (key.toLowerCase() === "a" && (init.ctrlKey || init.metaKey) && textField(el)) {
        el.select();
      }
    }
    el.dispatchEvent(new KeyboardEvent("keyup", init));
    return true;
  };
  A.scroll = (dx, dy) => {
    dx = Number(dx) || 0;
    dy = Number(dy) || 0;
    const x = scrollX, y = scrollY;
    window.scrollBy({ left: dx, top: dy, behavior: "instant" });
    if (scrollX !== x || scrollY !== y || (!dx && !dy)) return true;
    // The window does not scroll (an app shell): the scrollable box in the middle of the view does
    let el = document.elementFromPoint(innerWidth / 2, innerHeight / 2);
    while (el && el !== document.documentElement) {
      const s = getComputedStyle(el);
      if ((dy && /(auto|scroll)/.test(s.overflowY) && el.scrollHeight > el.clientHeight) ||
          (dx && /(auto|scroll)/.test(s.overflowX) && el.scrollWidth > el.clientWidth)) {
        el.scrollBy({ left: dx, top: dy, behavior: "instant" });
        return true;
      }
      el = el.parentElement;
    }
    return true;
  };
  // ── For input the engine treats like the user's (the bridge sends it): where to act, whether it got there, and
  //    when the page has settled. Coordinates are the top viewport's CSS pixels (same-origin frames' offsets added).
  const MENU_INPUTS = new Set(["color", "date", "datetime-local", "month", "week", "time"]);
  const offsetOf = doc => {
    let x = 0, y = 0, w = doc.defaultView;
    while (w && w !== window && w.frameElement) {
      const fe = w.frameElement, r = fe.getBoundingClientRect(), cs = fe.ownerDocument.defaultView.getComputedStyle(fe);
      x += r.left + fe.clientLeft + (parseFloat(cs.paddingLeft) || 0);
      y += r.top + fe.clientTop + (parseFloat(cs.paddingTop) || 0);
      w = w.parent;
    }
    return { x, y };
  };
  const reaches = (el, node) => {
    for (let n = node; n; n = n.parentNode || n.host) if (n === el) return true;
    return !!(node && node.control === el); // its label
  };
  const hitAt = (doc, x, y) => {
    let hit = doc.elementFromPoint(x, y);
    while (hit && hit.shadowRoot) {
      const inner = hit.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === hit) break;
      hit = inner;
    }
    return hit;
  };
  const describe = el => el.tagName.toLowerCase() + (el.id ? "#" + el.id : "") +
    (el.classList && el.classList.length ? "." + Array.from(el.classList).slice(0, 2).join(".") : "");
  const focusedIn = el => {
    let a = el.ownerDocument.activeElement;
    while (a && a.shadowRoot && a.shadowRoot.activeElement) a = a.shadowRoot.activeElement;
    return a === el || (el.isContentEditable && el.contains(a));
  };
  const checkable = el => (el.tagName === "INPUT" && (el.type === "checkbox" || el.type === "radio")) ||
    ["checkbox", "radio", "switch", "menuitemcheckbox", "menuitemradio"].includes((el.getAttribute("role") || "").toLowerCase());
  const isChecked = el => el.tagName === "INPUT" && "checked" in el ? !!el.checked : el.getAttribute("aria-checked") === "true";
  A.target = (selector, action) => {
    const el = A.must(selector);
    if (action !== "focus") reveal(el);
    const doc = el.ownerDocument, view = doc.defaultView, r = el.getBoundingClientRect(), off = offsetOf(doc);
    const type = el.tagName === "INPUT" ? (el.type || "text").toLowerCase() : "";
    const t = {
      tag: el.tagName.toLowerCase(), visible: !hidden(el) && r.width > 0 && r.height > 0,
      disabled: !!el.disabled || el.getAttribute("aria-disabled") === "true",
      editable: textField(el) || el.isContentEditable, focused: focusedIn(el),
      checkable: checkable(el), checked: isChecked(el), menu: el.tagName === "SELECT" || MENU_INPUTS.has(type),
      x: off.x + r.left + r.width / 2, y: off.y + r.top + r.height / 2, covered: null, inView: false,
    };
    if (!t.visible) return t;
    // The centre, else the first other point where a click would land on the element (not on what covers it)
    let hit = null;
    for (const [fx, fy] of [[0.5, 0.5], [0.25, 0.5], [0.75, 0.5], [0.5, 0.25], [0.5, 0.75], [0.2, 0.2], [0.8, 0.8]]) {
      const lx = r.left + r.width * fx, ly = r.top + r.height * fy;
      if (lx < 0 || ly < 0 || lx >= view.innerWidth || ly >= view.innerHeight) continue;
      const x = off.x + lx, y = off.y + ly;
      if (x < 0 || y < 0 || x >= innerWidth || y >= innerHeight) continue;
      t.inView = true;
      hit = hitAt(doc, lx, ly);
      if (reaches(el, hit)) {
        t.x = x;
        t.y = y;
        return t;
      }
    }
    if (hit) t.covered = describe(hit);
    return t;
  };
  A.viewport = () => ({ x: innerWidth / 2, y: innerHeight / 2 });
  A.exists = selector => !!A.resolve(selector);
  // Whether the next event of a type reached the element (a cover or a moved element takes the click instead)
  let armed = null;
  const watch = e => {
    if (!armed || armed.type !== e.type) return;
    const node = typeof e.composedPath === "function" ? e.composedPath()[0] : e.target;
    if (reaches(armed.el, node)) { armed.reached = true; armed.trusted = e.isTrusted; }
  };
  for (const type of ["click", "mouseover"]) addEventListener(type, watch, true);
  A.arm = (selector, type) => {
    const el = A.must(selector);
    armed = el.ownerDocument === document ? { el, type, reached: false, trusted: false } : null; // frames: not watched
    return !!armed;
  };
  A.armed = () => {
    const r = armed && { reached: armed.reached, trusted: armed.trusted };
    armed = null;
    return r;
  };
  // How long the page has been still: DOM mutations and finished resource loads, in ms
  let lastMutation = performance.now(), lastResource = 0;
  try {
    new MutationObserver(() => { lastMutation = performance.now(); })
      .observe(document, { subtree: true, childList: true, attributes: true, characterData: true });
    new PerformanceObserver(list => { for (const e of list.getEntries()) if (e.responseEnd > lastResource) lastResource = e.responseEnd; })
      .observe({ type: "resource", buffered: true });
  } catch (e) { }
  A.quiet = () => {
    const now = performance.now();
    return { mutation: Math.round(now - lastMutation), network: Math.round(now - lastResource), ready: document.readyState };
  };
  Object.freeze(A);
  Object.defineProperty(globalThis, name, { value: A, configurable: false, writable: false, enumerable: false });
  return A;
})
""";

    /// <param name="name">The global the library lives under (an identifier the page cannot guess).</param>
    /// <param name="seal">What a library of ours carries (<c>seal</c>); checked before it is reused.</param>
    /// <param name="runMark">The global marking the latest run (<see cref="RanCheck"/>).</param>
    internal AgentBrowserScripts(string name, string seal, string runMark)
    {
        Name = name;
        RunMark = runMark;
        Library = "(" + LibrarySource + ")(" + Literal(name) + ", " + Literal(seal) + ")";
        IsolatedCall = "(name, args) => " + Library + "[name](...args)";
    }

    /// <summary>Scripts with a fresh random name, seal and run mark (one set per bridge).</summary>
    internal static AgentBrowserScripts CreateRandom() => new("_" + RandomHex(8), RandomHex(16), "_" + RandomHex(8));

    private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    /// <summary>The global the library is installed under.</summary>
    internal string Name { get; }

    /// <summary>The global the run mark is set on.</summary>
    internal string RunMark { get; }

    /// <summary>The expression whose value is the page library (installed, or the verified one already there).</summary>
    internal string Library { get; }

    /// <summary>
    /// The library as a function of (name, args) for the app's isolated world (<see cref="Browser.IIsolatedScripts"/>):
    /// there the page can neither see nor replace it, and its built-ins are the engine's own.
    /// </summary>
    internal string IsolatedCall { get; }

    /// <summary>A call into the page library: <c>(library).name(args…)</c>, the arguments as JSON.</summary>
    internal string Call(string name, params JsonNode?[] args) =>
        Library + "." + name + "(" + string.Join(", ", args.Select(a => a?.ToJsonString() ?? "null")) + ")";

    /// <summary>A JavaScript string literal.</summary>
    internal static string Literal(string s) => JsonValue.Create(s)!.ToJsonString();

    /// <summary>
    /// <paramref name="expression"/> wrapped so the engine returns JSON text: <c>{"ok": value}</c>, <c>{"err": message}</c>
    /// or <c>{"promise": true}</c>. The first statement marks the run with <paramref name="run"/> (a non-enumerable global
    /// under <see cref="RunMark"/>): a script that does not parse as an expression leaves the mark unset, and the bridge
    /// runs it again through indirect eval (<paramref name="viaEval"/>), which also takes statements.
    /// </summary>
    internal string Wrap(string expression, long run, bool viaEval)
    {
        var body = viaEval ? "(0, eval)(" + Literal(expression) + ")" : "(\n" + expression + "\n)";
        return "Object.defineProperty(globalThis, " + Literal(RunMark) + ", { value: " + run.ToString(CultureInfo.InvariantCulture)
            + ", configurable: true, writable: true, enumerable: false });\n"
            + "(() => {\n"
            + "  let r;\n"
            + "  try {\n"
            + "    const v = " + body + ";\n"
            + "    r = v !== null && (typeof v === \"object\" || typeof v === \"function\") && typeof v.then === \"function\" ? { promise: true } : { ok: v === undefined ? null : v };\n"
            + "  } catch (e) {\n"
            + "    const message = e && e.message !== undefined ? String(e.message) : String(e);\n"
            + "    r = { err: message, stack: e && e.stack ? String(e.stack) : \"\", name: e && e.name ? String(e.name) : \"\" };\n"
            + "  }\n"
            + "  try { return JSON.stringify(r); }\n"
            + "  catch (e) { return JSON.stringify({ err: \"the result cannot be sent back as JSON: \" + (e && e.message) }); }\n"
            + "})()";
    }

    /// <summary>Whether the run marked <paramref name="run"/> started (answers "true" or "false").</summary>
    internal string RanCheck(long run) => "String(globalThis[" + Literal(RunMark) + "] === " + run.ToString(CultureInfo.InvariantCulture) + ")";
}

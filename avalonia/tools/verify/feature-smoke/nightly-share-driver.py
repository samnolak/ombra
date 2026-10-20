"""The Nightly side of the feature smoke's "nightly-share" section (FeatureSmoke.NightlyShare.cs), over Marionette.

Run with the desktop tree's Python (marionette_driver), next to `run.sh <work> <runtime> nightly-share` started with
OMBRA_NIGHTLY_APP=<the dev Nightly.app> OMBRA_NIGHTLY_ALLOW_UNSIGNED=1:

    ./mach python <omp>/avalonia/tools/verify/feature-smoke/nightly-share-driver.py <Nightly.app> <profile-dir> <work>/shots

Starts the dev Nightly on a fresh profile (-no-remote; zen.embed.allow-unsigned-peer), then, handing off through files
in <shots>/nightly-share/: shares a page with a comment (its ?token= flagged and stripped), a tab group and a gallery
screenshot through Ombra; waits for Ivy's link and group, opens them from the toasts. Screenshots next to Ombra's.

Release trust instead (ad-hoc signed apps, no unsigned switches on either side): OMBRA_PEER_CDHASH=<the cdhash of the
running Ombra.app> makes the profile pin Ombra by zen.embed.peer-cdhash, as Ombra does for its runtime profile; then
Ombra runs without OMBRA_NIGHTLY_ALLOW_UNSIGNED and accepts only the Nightly of its pinned manifest cdhash.
"""

import os
import shutil
import subprocess
import sys
import time

from marionette_driver.marionette import Marionette

APP, PROFILE, SHOTS = sys.argv[1:4]
HAND = os.path.join(SHOTS, "nightly-share")
PORT = 2931


def log(line):
    print(time.strftime("%H:%M:%S"), line, flush=True)


def wait_file(name, seconds):
    end = time.time() + seconds
    while not os.path.exists(os.path.join(HAND, name)):
        if time.time() > end:
            raise SystemExit(f"timed out waiting for {name}")
        time.sleep(0.5)


def mark(name):
    with open(os.path.join(HAND, name), "w") as f:
        f.write("")


shutil.rmtree(PROFILE, ignore_errors=True)
os.makedirs(PROFILE)
os.makedirs(HAND, exist_ok=True)
with open(os.path.join(PROFILE, "user.js"), "w") as f:
    for name, value in [
        ("marionette.port", PORT),
        *([("zen.embed.peer-cdhash", os.environ["OMBRA_PEER_CDHASH"])] if os.environ.get("OMBRA_PEER_CDHASH")
          else [("zen.embed.allow-unsigned-peer", True)]),
        ("zen.window-sync.enabled", False),
        ("zen.welcome-screen.seen", True),
        ("browser.shell.checkDefaultBrowser", False),
        ("browser.aboutwelcome.enabled", False),
        ("zen.tabs.auto-groups.enabled", True),
        ("zen.tabs.auto-groups.threshold", 4),
        # Never the front app (OMBRA_TEST_NO_ACTIVATE, src/Ombra.App/Platform/TestFocus.cs): Gecko activates the app to
        # raise a window it focuses unless this is off.
        *([("mozilla.widget.raise-on-setfocus", False)] if os.environ.get("OMBRA_TEST_NO_ACTIVATE", "1") != "0" else []),
    ]:
        f.write(f"user_pref({name!r}, {str(value).lower() if isinstance(value, bool) else repr(value)});\n".replace("'", '"'))

binary = os.path.join(APP, "Contents", "MacOS", "nightly")
nightly = subprocess.Popen(
    [binary, "-no-remote", "-profile", PROFILE, "-marionette", "-remote-allow-system-access"],
    stdout=open(os.path.join(SHOTS, "nightly.log"), "w"),
    stderr=subprocess.STDOUT,
)
m = Marionette(host="127.0.0.1", port=PORT)
m.raise_for_port(120, check_process_status=False)
m.start_session()
m.set_context("chrome")
m.timeout.script = 600


def chrome(script, *args):
    return m.execute_async_script(
        "const [resolve] = [arguments[arguments.length - 1]];"
        "(async () => {" + script + "})().then(resolve, e => resolve({error: String(e) + ' ' + e.stack}));",
        script_args=list(args),
        new_sandbox=False,
    )


def check(name, result):
    log(f"{name}: {result}")
    if isinstance(result, dict) and result.get("error"):
        raise SystemExit(f"{name} failed: {result['error']}")
    return result


WINDOW_ID = r"""
import CoreGraphics
let pid = Int32(CommandLine.arguments[1])!
let list = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as! [[String: Any]]
var best: (Int, Double) = (0, 0)
for w in list where (w[kCGWindowOwnerPID as String] as? Int32) == pid && (w[kCGWindowLayer as String] as? Int) == 0 {
  let b = w[kCGWindowBounds as String] as! [String: Double]
  if b["Width"]! * b["Height"]! > best.1 { best = (w[kCGWindowNumber as String] as! Int, b["Width"]! * b["Height"]!) }
}
print(best.0)
"""
WINDOW_ID_TOOL = os.path.join(PROFILE + "-tools", "window-id")
os.makedirs(os.path.dirname(WINDOW_ID_TOOL), exist_ok=True)
with open(WINDOW_ID_TOOL + ".swift", "w") as f:
    f.write(WINDOW_ID)
subprocess.run(["swiftc", "-O", WINDOW_ID_TOOL + ".swift", "-o", WINDOW_ID_TOOL], check=True)


def may_shoot():
    """screencapture needs Screen Recording; without it a run that never takes the focus (OMBRA_TEST_NO_ACTIVATE) does
    not start it: macOS would ask in a window that takes the focus (src/Ombra.App/Platform/TestFocus.cs)."""
    if os.environ.get("OMBRA_TEST_NO_ACTIVATE", "1") == "0":
        return True
    import ctypes
    cg = ctypes.CDLL("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")
    cg.CGPreflightScreenCaptureAccess.restype = ctypes.c_bool
    return cg.CGPreflightScreenCaptureAccess()


def shot(name):
    """Nightly's window (its dialogs and toasts are inside it), even under Ombra's."""
    time.sleep(0.6)
    path = os.path.join(SHOTS, name + ".png")
    if not may_shoot():
        log(f"SHOT {path} skipped: no Screen Recording")
        return
    number = subprocess.run([WINDOW_ID_TOOL, str(nightly.pid)], capture_output=True, text=True, check=True).stdout.strip()
    subprocess.run(["/usr/sbin/screencapture", "-x", "-o", "-l", number, path], check=True)
    log(f"SHOT {path}")


PRELUDE = """
const win = Services.wm.getMostRecentWindow('navigator:browser');
const { ZenTeamLink } = ChromeUtils.importESModule('resource:///modules/zen/teams/ZenTeamLink.sys.mjs');
const { ZenTeamsUI } = ChromeUtils.importESModule('resource:///modules/zen/teams/ZenTeamsUI.sys.mjs');
const until = async (f, what, ms = 90000) => {
  const end = Date.now() + ms;
  for (;;) { const v = await f(); if (v) return v; if (Date.now() > end) throw new Error('timed out: ' + what); await new Promise(r => win.setTimeout(r, 200)); }
};
const dialog = () => until(() => {
  const d = win.gDialogBox?.dialog;
  const doc = d?._frame?.contentDocument;
  return doc?.getElementById('zen-team-share-comment') && doc.readyState === 'complete' ? doc : null;
}, 'share dialog');
"""

wait_file("ready", 600)
check("state", chrome(PRELUDE + "return await until(async () => { const s = await ZenTeamLink.state({ fresh: true }); return s.ready ? s : null; }, 'Ombra ready', 60000);"))

# 1. A page with a comment; its ?token= is flagged (the warning) and stripped.
check("open page", chrome(PRELUDE + """
const tab = win.gBrowser.addTab('https://example.com/?keep=1&token=abc123secret', { triggeringPrincipal: Services.scriptSecurityManager.getSystemPrincipal() });
win.gBrowser.selectedTab = tab;
await until(() => tab.linkedBrowser.currentURI.spec.includes('token=') && !tab.hasAttribute('busy'), 'page loaded');
win.document.getElementById('contentAreaContextMenu').dispatchEvent(new win.Event('popupshowing'));
const item = win.document.getElementById('context-zen-team-share-page');
await until(() => !item.disabled, 'share page enabled');
item.doCommand();
const doc = await dialog();
await until(() => !doc.getElementById('zen-team-share-secrets').hidden, 'secret warning');
doc.getElementById('zen-team-share-comment').value = 'Read before standup';
return { warning: doc.getElementById('zen-team-share-secrets-text').textContent };
"""))
shot("90-nightly-share-page-dialog")
check("send page", chrome(PRELUDE + """
const doc = await dialog();
doc.querySelector('dialog').getButton('accept').click();
await until(() => !win.gDialogBox?.dialog, 'dialog closed');
return { ok: true };
"""))
shot("91-nightly-share-page-sent")

# 2. A tab group (B1 auto group of four example.com tabs).
check("share group", chrome(PRELUDE + """
const tabs = [];
for (let i = 0; i < 4; i++) {
  const tab = win.gBrowser.addTab('https://example.com/?ombra-group=' + i, { triggeringPrincipal: Services.scriptSecurityManager.getSystemPrincipal() });
  win.gBrowser.selectedTab = tab;
  await until(() => tab.linkedBrowser.currentURI.spec.includes('ombra-group'), 'group tab loaded');
  tabs.push(tab);
}
let group = await until(() => tabs.every(t => t.group && t.group === tabs[0].group) && tabs[0].group, 'auto group', 15000).catch(() => null);
if (!group) {
  group = win.gBrowser.addTabGroup(tabs, { label: 'example.com', insertBefore: tabs[0] });
}
win.gBrowser.selectedTab = tabs[1];
win.TabContextMenu.contextTab = tabs[1];
win.document.getElementById('tabContextMenu').dispatchEvent(new win.Event('popupshowing'));
const item = win.document.getElementById('context-zen-team-share-group');
await until(() => !item.disabled && !item.hidden, 'share group enabled');
item.doCommand();
const doc = await dialog();
await until(() => /\d+ tabs/.test(doc.getElementById('zen-team-share-what-text').textContent), 'the group in the dialog');
return { label: group.label, auto: group.hasAttribute('zen-auto-group'), what: doc.getElementById('zen-team-share-what-text').textContent };
"""))
shot("92-nightly-share-group-dialog")
check("send group", chrome(PRELUDE + """
const doc = await dialog();
doc.querySelector('dialog').getButton('accept').click();
await until(() => !win.gDialogBox?.dialog, 'dialog closed');
return { ok: true };
"""))

# 3. A gallery screenshot: the gallery unlocked with the test-only OSKeyStore login (as browser_gallery_storage.js),
# one shot added, its viewer's "Share with team" pressed in about:gallery.
check("gallery shot", chrome(PRELUDE + """
const defaults = Services.prefs.getDefaultBranch(null);
Services.prefs.unlockPref('app.update.channel');
defaults.setCharPref('app.update.channel', 'default');
const { OSKeyStore } = ChromeUtils.importESModule('resource://gre/modules/OSKeyStore.sys.mjs');
OSKeyStore.STORE_LABEL = 'test-ombra-b2-' + Math.random().toString(36).slice(2);
Services.prefs.setStringPref('toolkit.osKeyStore.unofficialBuildOnlyLogin', 'pass');
const { ZenGalleryStore, GalleryStore } = ChromeUtils.importESModule('resource:///modules/ZenGalleryStore.sys.mjs');
await ZenGalleryStore.unlock();
const canvas = new win.OffscreenCanvas(1200, 800);
const ctx = canvas.getContext('2d');
for (let i = 0; i < 600; i++) { ctx.fillStyle = `hsl(${(i * 37) % 360} 70% 50%)`; ctx.fillRect((i * 97) % 1200, (i * 53) % 800, 60, 60); }
ctx.fillStyle = 'white'; ctx.font = '64px sans-serif'; ctx.fillText('Ombra B2 screenshot', 200, 400);
const png = Cu.cloneInto(new Uint8Array(await (await canvas.convertToBlob({ type: 'image/png' })).arrayBuffer()), Cu.getGlobalForObject(GalleryStore));
await ZenGalleryStore.add(png, { source: 'area', title: 'B2 gallery shot', url: 'https://example.com/?shot=1' });
const tab = win.gBrowser.addTab('about:gallery', { triggeringPrincipal: Services.scriptSecurityManager.getSystemPrincipal() });
win.gBrowser.selectedTab = tab;
const doc = await until(() => { const d = tab.linkedBrowser.contentDocument; return d?.querySelector('.shot .preview') ? d : null; }, 'gallery card');
doc.querySelector('.shot .preview').click();
const share = await until(() => { const b = doc.getElementById('share-team'); return b && !b.disabled ? b : null; }, 'share-team enabled');
share.click();
const dlg = await dialog();
await until(() => !dlg.getElementById('zen-team-share-preview').hidden, 'preview');
dlg.getElementById('zen-team-share-comment').value = 'From the gallery';
return { ok: true };
"""))
shot("93-nightly-share-screenshot-dialog")
check("send screenshot", chrome(PRELUDE + """
const doc = await dialog();
doc.querySelector('dialog').getButton('accept').click();
await until(() => !win.gDialogBox?.dialog, 'dialog closed');
return { ok: true };
"""))
mark("sent")


def incoming(kind, marker, label, expect):
    wait_file(marker, 300)
    check(f"toast {kind}", chrome(PRELUDE + """
const [kind, label] = arguments;
win.gBrowser.selectedTab = win.gBrowser.tabs[0];
await until(() => [...win.document.querySelectorAll('.zen-toast label')].find(l => l.textContent.includes(label)), 'toast ' + label, 90000);
return { toast: [...win.document.querySelectorAll('.zen-toast label')].map(l => l.textContent) };
""", kind, label))
    shot(f"9{4 if kind == 'link' else 6}-nightly-toast-{kind}")
    check(f"open {kind}", chrome(PRELUDE + """
const [kind, expect] = arguments;
win.document.getElementById('zen-team-toast-open-' + kind).doCommand();
if (kind === 'link') {
  await until(() => win.gBrowser.selectedTab.linkedBrowser.currentURI.spec === expect, 'tab ' + expect);
  return { tab: win.gBrowser.selectedTab.linkedBrowser.currentURI.spec };
}
const g = await until(() => win.gBrowser.tabGroups.find(g => g.label === expect && g.tabs.length === 2), 'group ' + expect);
return { group: g.label, tabs: g.tabs.map(t => t.linkedBrowser.currentURI.spec) };
""", kind, expect))
    shot(f"9{5 if kind == 'link' else 7}-nightly-opened-{kind}")


incoming("link", "incoming-link", "Ivy shared a link", "https://example.com/?from-ivy")
mark("opened-link")
incoming("collection", "incoming-group", "Ivy shared a collection", "Ivy refs")
mark("opened-group")
wait_file("done", 120)
try:
    m.execute_script("Services.startup.quit(Ci.nsIAppStartup.eForceQuit)", new_sandbox=False)
except Exception:
    pass  # the session ends with the app
try:
    nightly.wait(30)
except subprocess.TimeoutExpired:
    nightly.kill()
log(f"nightly exit {nightly.returncode}")

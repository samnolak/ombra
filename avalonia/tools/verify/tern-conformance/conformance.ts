// Tern conformance: drives the OMP GUI's Tern host (avalonia/src/OmpGui.ClientCore/Browser/TernHost.cs) with omp's
// own Tern client — TernSocketClient and TernTab from the pinned omp's source, imported as they are — and prints one
// JSON object per check on stdout ({"check","ok","detail"}), then {"done":true,"passed","failed"}.
//
//   OMP_SRC=<omp>/node_modules/@oh-my-pi/pi-coding-agent/src TERN_PANE_SOCKET=… TERN_PANE=… \
//     bun --no-install conformance.ts wire|webkit [base-url]
//
// wire   — against the headless tests' scripted page (tests/OmpGui.Tests/TernHostTests.cs): every op's request and
//          answer shape, ordering, timeouts, late answers, errors.
// webkit — against real WKWebViews in the windowless harness (tools/verify/webview-harness, scenario "tern"):
//          the same ops on real pages served at base-url (Server/Pages/tern*.html).
// Never opens a window or reads the clipboard; files go to a temporary folder that is removed afterwards.
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

const src = process.env.OMP_SRC;
if (!src) throw new Error("OMP_SRC must name omp's pi-coding-agent/src folder");
// The modules live in the omp the app pins (OMP_SRC), not in this folder: their path is only known at run time.
const wire = await import(path.join(src, "tools/browser/tern/wire.ts"));
const { TernTab } = await import(path.join(src, "tools/browser/tern/tern-tab.ts"));
const { TernSocketClient, TernError } = wire;

const mode = process.argv[2] ?? "wire";
const base = (process.argv[3] ?? "http://127.0.0.1:9").replace(/\/$/, "");
const socketPath = process.env.TERN_PANE_SOCKET!;
const pane = Number(process.env.TERN_PANE ?? "1");
const work = fs.mkdtempSync(path.join(os.tmpdir(), "tern-conformance-"));
let passed = 0;
let failed = 0;

function report(check: string, ok: boolean, ms: number, detail?: unknown): void {
	if (ok) passed++;
	else failed++;
	console.log(JSON.stringify({ check, ok, ms, detail: detail === undefined ? null : detail }));
}

async function check(name: string, body: () => Promise<unknown>): Promise<void> {
	const started = Date.now();
	try {
		const detail = await body();
		report(name, true, Date.now() - started, detail);
	} catch (error) {
		report(name, false, Date.now() - started, error instanceof Error ? `${error.name}: ${error.message}` : String(error));
	}
}

function expect(condition: unknown, message: string, detail?: unknown): void {
	if (!condition) throw new Error(`${message}${detail === undefined ? "" : ` — got ${JSON.stringify(detail)}`}`);
}

/** A field of an untyped value (page results and the client's answers are plain JSON). */
function prop(value: unknown, key: string): unknown {
	return value !== null && typeof value === "object" ? Reflect.get(value, key) : undefined;
}

/** The tab's Tern events after seq `after`, read with omp's own `events` op: their types and the last seq. */
async function eventTypes(block: number, after = 0): Promise<{ last: number; types: string[] }> {
	const events = prop(await client.request({ op: "events", block, after }), "events");
	const list: unknown[] = Array.isArray(events) ? events : [];
	const last = prop(list.at(-1), "seq");
	return { last: typeof last === "number" ? last : after, types: list.map(e => String(prop(e, "type"))) };
}

async function rejects(promise: Promise<unknown>): Promise<unknown> {
	try {
		await promise;
	} catch (error) {
		return error;
	}
	throw new Error("expected a failure");
}

function runContext(timeoutMs = 30_000) {
	return {
		session: { cwd: work },
		output: [] as unknown[],
		screenshots: [] as unknown[],
		signal: new AbortController().signal,
		timeoutMs,
	};
}

const client = new TernSocketClient({ socketPath });
const viewport = { width: 1000, height: 700 };

await check("connect: hello → welcome, no fork offered", async () => {
	await client.connect();
	expect(client.connected, "not connected");
	expect(!client.supports("fork"), "fork offered");
});

if (mode === "wire") await wireChecks();
else await webkitChecks();

client.close();
fs.rmSync(work, { recursive: true, force: true });
console.log(JSON.stringify({ done: true, passed, failed }));
process.exit(failed === 0 ? 0 : 1);

// ── wire: the scripted page of the headless tests ──

async function wireChecks(): Promise<void> {
	let tab: InstanceType<typeof TernTab> | undefined;
	await check("open: configure (dialogs, scripts), first goto, readyInfo", async () => {
		tab = await TernTab.open(client, {
			name: "main",
			pane,
			url: `${base}/first`,
			waitUntil: "load",
			viewport,
			timeoutMs: 10_000,
			initScripts: ["window.__conformance = 1;"],
		});
		const info = await tab.readyInfo();
		expect(typeof tab.block === "number", "no block");
		expect(info.url === `${base}/first`, "url", info.url);
		expect(info.title === "Title of /first", "title", info.title);
		return { block: tab.block, info };
	});
	if (!tab) return;
	const t = tab;
	const block = t.block;

	await check("goto, back, forward, reload follow events and state", async () => {
		await t.goto(`${base}/second`);
		expect(t.url() === `${base}/second`, "after goto", t.url());
		expect((await t.back()) === `${base}/first`, "back", t.url());
		expect((await t.forward()) === `${base}/second`, "forward", t.url());
		await t.reload();
		return t.url();
	});

	await check("viewport: the size omp sets is what state reports", async () => {
		await t.setViewport({ width: 800, height: 600 });
		const info = await t.readyInfo();
		expect(info.viewport.width === 800 && info.viewport.height === 600, "viewport", info.viewport);
		return info.viewport;
	});

	await check("eval: function, args, world, frame reach the page; the value comes back", async () => {
		const answer = await client.request({ op: "eval", block, function: "function (a, b) { return a + b; }", args: [1, 2], world: "isolated", frame: "0.1" });
		expect(JSON.stringify(prop(answer, "value")) === JSON.stringify({ function: "function (a, b) { return a + b; }", args: [1, 2], world: "isolated", frame: "0.1" }), "echo", answer);
		const undef = await client.request({ op: "eval", block, function: "function () { return undefined; }", args: [] });
		expect(prop(undef, "value") === undefined && typeof undef === "object" && undef !== null && !("value" in undef), "undefined has no value", undef);
		return answer;
	});

	await check("eval: a page exception is kind js with its text", async () => {
		const error = (await rejects(client.request({ op: "eval", block, function: "function () { throw new Error('boom'); }", args: [] }))) as InstanceType<typeof TernError>;
		expect(error instanceof TernError && error.kind === "js" && error.message.includes("Error: boom"), "js error", String(error));
		return error.message;
	});

	await check("input: a trusted click; the confirm it opens is held, reported, answered", async () => {
		await t.clickAt(13, 13);
		const dialog = await t.dialog();
		expect(dialog.open && dialog.type === "confirm" && dialog.message === "Delete it?", "dialog", dialog);
		const blocked = (await rejects(client.request({ op: "eval", block, function: "function () { return 1; }", args: [] }))) as InstanceType<typeof TernError>;
		expect(blocked.kind === "failed" && blocked.message.includes("confirm"), "eval while held", String(blocked));
		await t.handleDialog({ accept: true });
		expect(!(await t.dialog()).open, "still open");
		const none = await rejects(t.handleDialog({ accept: false }));
		expect(String(none).includes("no pending confirm or prompt"), "no dialog", String(none));
		return dialog;
	});

	await check("input: keyboard typing and text steps", async () => {
		await t.keyboardType("hé");
		await t.wheel(0, 120);
		return true;
	});

	await check("capture: jpeg at quality 50; rect and full page reach the page", async () => {
		const jpeg = await t.captureBytes({ format: "jpeg", quality: 50 });
		expect(jpeg.length > 0, "bytes");
		const full = await t.captureBytes({ format: "png", fullPage: true });
		expect(full.subarray(1, 4).toString() === "PNG", "png", full.subarray(0, 8).toString("hex"));
		return { jpeg: jpeg.length, png: full.length };
	});

	await check("pdf: written where omp asked", async () => {
		t.setRunContext(runContext());
		const file = await t.pdf({ path: path.join(work, "page.pdf") });
		t.clearRunContext();
		expect(fs.readFileSync(file).subarray(0, 5).toString() === "%PDF-", "pdf");
		return path.basename(file);
	});

	await check("cookies: set, read for the page's URL, clear", async () => {
		await t.setCookies({ name: "a", value: "1", url: `${base}/` });
		const cookies = await t.cookies();
		expect(cookies.length === 1 && cookies[0].name === "a" && cookies[0].value === "1", "cookies", cookies);
		await t.clearCookies();
		expect((await t.cookies()).length === 0, "cleared");
		return cookies;
	});

	await check("emulate: colour scheme and user agent", async () => {
		const result = await t.emulate({ colorScheme: "dark", userAgent: "ConformanceAgent/1.0" });
		await t.emulate({ colorScheme: "no-preference", userAgent: null });
		return result;
	});

	await check("files and downloads presets are taken; relative paths refused", async () => {
		await client.request({ op: "files", block, paths: [path.join(work, "upload.txt")] });
		await client.request({ op: "files", block, paths: null });
		const relative = (await rejects(client.request({ op: "files", block, paths: ["upload.txt"] }))) as InstanceType<typeof TernError>;
		expect(relative.kind === "invalid", "relative", String(relative));
		await client.request({ op: "downloads", block, dir: path.join(work, "downloads") });
		return true;
	});

	await check("unsupported ops say so: invalid certificates, clipboard reads, paste, fork", async () => {
		const insecure = (await rejects(client.request({ op: "insecure", block, value: true }))) as InstanceType<typeof TernError>;
		expect(insecure.kind === "unsupported", "insecure", String(insecure));
		const read = (await rejects(t.clipboardRead())) as InstanceType<typeof TernError>;
		expect(read.kind === "unsupported", "clipboard read", String(read));
		const paste = (await rejects(t.clipboardPaste())) as InstanceType<typeof TernError>;
		expect(paste.kind === "unsupported", "paste", String(paste));
		const fork = (await rejects(client.fork({ block: pane }))) as InstanceType<typeof TernError>;
		expect(fork.kind === "unsupported", "fork", String(fork));
		await t.clipboardWrite("from omp");
		return [insecure.kind, read.kind, paste.kind, fork.kind];
	});

	await check("timeouts are omp's: a script that never ends times out, the connection stays", async () => {
		const error = (await rejects(client.request({ op: "eval", block, function: "function () { return never; }", args: [] }, { timeoutMs: 300 }))) as InstanceType<typeof TernError>;
		expect(error.kind === "timeout", "timeout", String(error));
		const state = await client.request({ op: "state", block });
		return { error: error.kind, state };
	});

	await check("late open: an open answered after omp gave up is closed by omp", async () => {
		const error = await rejects(TernTab.open(client, { name: "late", pane: 99, viewport, timeoutMs: 200 }));
		await Bun.sleep(1_500);
		return String(error);
	});

	await check("events: seq increases; after skips what omp has", async () => {
		const events = prop(await client.request({ op: "events", block, after: 0 }), "events");
		const list: unknown[] = Array.isArray(events) ? events : [];
		const seqs = list.map(e => Number(prop(e, "seq")));
		expect(seqs.every((s, i) => i === 0 || s > seqs[i - 1]), "seq order", seqs);
		const all = await eventTypes(block);
		for (const type of ["url", "committed", "title", "loaded", "dialog"]) expect(all.types.includes(type), `no ${type} event`, all.types);
		const after = await eventTypes(block, all.last);
		expect(after.types.length === 0, "after", after);
		return { count: seqs.length, types: [...new Set(all.types)] };
	});

	await check("allowed domains: a blocked first navigation fails the open", async () => {
		const error = await rejects(TernTab.open(client, { name: "fenced", pane, url: `${base}/third`, viewport, timeoutMs: 10_000, allowedDomains: ["*.example.com"] }));
		expect(String(error).includes("blocked by allowed_domains"), "blocked", String(error));
		return String(error);
	});

	await check("close: the tab goes; a second close is not_found, which omp ignores", async () => {
		await t.close({ timeoutMs: 5_000 });
		const again = (await rejects(client.request({ op: "close", block }))) as InstanceType<typeof TernError>;
		expect(again.kind === "not_found", "second close", String(again));
		await t.close({ timeoutMs: 5_000 });
		const gone = (await rejects(client.request({ op: "state", block }))) as InstanceType<typeof TernError>;
		expect(gone.kind === "not_found", "state after close", String(gone));
		return true;
	});
}

// ── webkit: real pages in the windowless harness ──

async function webkitChecks(): Promise<void> {
	let tab: InstanceType<typeof TernTab> | undefined;
	await check("open a real page: title, url, viewport", async () => {
		tab = await TernTab.open(client, { name: "main", pane, url: `${base}/tern`, waitUntil: "load", viewport, timeoutMs: 20_000 });
		const info = await tab.readyInfo();
		expect(info.title === "Tern conformance", "title", info);
		expect(info.url === `${base}/tern`, "url", info);
		return info;
	});
	if (!tab) return;
	const t = tab;
	t.setRunContext(runContext());

	await check("eval in the page world, Promise awaited; globals of omp's kit are not in it", async () => {
		const sum = await t.evaluate((a: number, b: number) => {
			const { promise, resolve } = Promise.withResolvers<number>();
			setTimeout(() => resolve(a + b), 20);
			return promise;
		}, 2, 3);
		expect(sum === 5, "sum", sum);
		const leaks = await t.evaluate(() => ({ kit: typeof Reflect.get(globalThis, "__ompTernKit"), webdriver: navigator.webdriver }));
		expect(prop(leaks, "kit") === "undefined" && prop(leaks, "webdriver") === false, "leaks", leaks);
		return { sum, leaks };
	});

	await check("eval exception: the page's error text", async () => {
		const error = await rejects(t.evaluate(() => { throw new Error("page boom"); }));
		expect(String(error).includes("page boom"), "text", String(error));
		return String(error);
	});

	await check("isolated world: omp's kit reads the page (text, count, observe)", async () => {
		expect((await t.text("#heading")) === "Tern conformance", "text");
		expect((await t.count("li")) === 3, "count");
		const snapshot = await t.ariaSnapshot();
		expect(typeof snapshot === "string" && snapshot.includes("Tern conformance"), "snapshot", snapshot);
		return true;
	});

	await check("frames: a child frame registers its path; evaluate inside it", async () => {
		const frames = await t.frames();
		const child = frames.find(f => f.parentId === "main");
		expect(child && child.id === "0", "frames", frames);
		const frame = await t.frame("0");
		const text = await frame.text("#inner");
		expect(text === "inside the frame", "frame text", text);
		return frames;
	});

	await check("trusted click: isTrusted and user activation in the page", async () => {
		await t.click("#trusted");
		const state = await t.evaluate(() => Reflect.get(globalThis, "clickState"));
		expect(prop(state, "isTrusted") === true && prop(state, "activation") === true, "click", state);
		return state;
	});

	await check("typing: fill and keyboard reach a text field (Unicode as text)", async () => {
		await t.fill("#name", "Ünïcødé 日本");
		await t.type("#name", "!");
		const value = await t.value("#name");
		expect(value === "Ünïcødé 日本!", "value", value);
		await t.press("Enter", { selector: "#name" });
		const submitted = await t.evaluate(() => Reflect.get(globalThis, "submitted"));
		expect(submitted === "Ünïcødé 日本!", "Enter submitted", submitted);
		return value;
	});

	await check("dialogs: alert accepted, confirm held for omp, answered", async () => {
		await t.click("#alert");
		await t.click("#confirm");
		const dialog = await t.dialog();
		expect(dialog.open && dialog.type === "confirm" && dialog.message === "Proceed?", "dialog", dialog);
		await t.handleDialog({ accept: true });
		const answer = await t.evaluate(() => Reflect.get(globalThis, "confirmAnswer"));
		expect(answer === true, "answer", answer);
		const alerted = await t.evaluate(() => Reflect.get(globalThis, "alerted"));
		expect(alerted === true, "alert returned", alerted);
		return dialog;
	});

	await check("console and network capture (stencil handler, page world)", async () => {
		await t.evaluate(() => { console.log("conformance log", 42); return fetch("/ping").then(r => r.status); });
		await Bun.sleep(300);
		const log = await t.console();
		expect(log.entries.some((e: unknown) => String(prop(e, "text")).includes("conformance log")), "console", log);
		const requests = await t.requests();
		expect(requests.some((r: unknown) => String(prop(r, "url")).endsWith("/ping")), "requests", requests);
		return { console: log.entries.length, requests: requests.length };
	});

	await check("capture: viewport png, element, full page, jpeg", async () => {
		const png = await t.captureBytes({ format: "png" });
		const element = await t.captureBytes({ format: "png", selector: "#heading" });
		const full = await t.captureBytes({ format: "png", fullPage: true });
		const jpeg = await t.captureBytes({ format: "jpeg", quality: 60 });
		const size = (b: Buffer) => ({ w: b.readUInt32BE(16), h: b.readUInt32BE(20) });
		expect(png.subarray(1, 4).toString() === "PNG", "png");
		const viewportSize = size(png);
		expect(viewportSize.w === viewport.width && viewportSize.h === viewport.height, "viewport size at scale 1", viewportSize);
		expect(size(full).h > viewportSize.h, "full page taller", size(full));
		expect(jpeg[0] === 0xff && jpeg[1] === 0xd8, "jpeg");
		return { viewport: viewportSize, element: size(element), full: size(full), jpeg: jpeg.length };
	});

	await check("pdf of the page", async () => {
		const file = await t.pdf({ path: path.join(work, "page.pdf") });
		expect(fs.readFileSync(file).subarray(0, 5).toString() === "%PDF-", "pdf");
		return fs.statSync(file).size;
	});

	await check("viewport, appearance, user agent", async () => {
		await t.setViewport({ width: 600, height: 500 });
		const inner = await t.evaluate(() => [innerWidth, innerHeight]);
		expect(JSON.stringify(inner) === "[600,500]", "inner size", inner);
		await t.emulate({ colorScheme: "dark", userAgent: "ConformanceAgent/1.0" });
		await t.reload();
		const seen = await t.evaluate(() => [matchMedia("(prefers-color-scheme: dark)").matches, navigator.userAgent]);
		expect(Array.isArray(seen) && seen[0] === true && seen[1] === "ConformanceAgent/1.0", "dark + agent", seen);
		await t.emulate({ colorScheme: "light", userAgent: null });
		await t.setViewport(viewport);
		return seen;
	});

	await check("cookies: set, read, clear", async () => {
		await t.setCookies({ name: "flavour", value: "oat", url: `${base}/` });
		const cookies = await t.cookies();
		expect(cookies.some((c: unknown) => prop(c, "name") === "flavour" && prop(c, "value") === "oat"), "cookie", cookies);
		const pageSees = await t.evaluate(() => document.cookie);
		expect(String(pageSees).includes("flavour=oat"), "document.cookie", pageSees);
		await t.clearCookies();
		expect(!(await t.cookies()).some((c: unknown) => prop(c, "name") === "flavour"), "cleared");
		return cookies.length;
	});

	await check("file chooser: the preset answers the native chooser", async () => {
		const file = path.join(work, "upload.txt");
		fs.writeFileSync(file, "conformance upload");
		const before = await eventTypes(t.block);
		await t.uploadFile("#upload", file);
		const input = await t.evaluate(() => {
			const element = document.querySelector("#upload");
			return element instanceof HTMLInputElement ? element.files?.[0]?.name : undefined;
		});
		const after = await eventTypes(t.block, before.last);
		expect(input === "upload.txt", "file", input);
		expect(after.types.includes("chooser"), "the native chooser took the preset (not the drop fallback)", after.types);
		return input;
	});

	await check("download into the folder omp chose", async () => {
		const waiting = t.waitForDownload({ timeout: 10_000 });
		await t.click("#download");
		const download = await waiting;
		expect(fs.existsSync(download.path) && fs.readFileSync(download.path, "utf8") === "hello download\n", "download", download);
		return { name: download.suggestedFilename, bytes: download.bytes };
	});

	await check("navigation: goto, back, forward; same-document url change", async () => {
		await t.goto(`${base}/tern?second=1`);
		expect(t.url().endsWith("?second=1"), "goto", t.url());
		await t.back();
		expect(t.url() === `${base}/tern`, "back", t.url());
		await t.forward();
		await t.pushState(`${base}/tern#pushed`);
		const url = await t.waitForUrl("#pushed", { timeout: 5_000 });
		return url;
	});

	await check("allowed domains block a navigation", async () => {
		const error = await rejects(TernTab.open(client, { name: "fenced", pane, url: `${base}/tern`, viewport, timeoutMs: 10_000, allowedDomains: ["*.example.com"] }));
		expect(String(error).includes("blocked by allowed_domains"), "blocked", String(error));
		return String(error);
	});

	await check("close", async () => {
		t.clearRunContext();
		await t.close({ timeoutMs: 5_000 });
		const gone = (await rejects(client.request({ op: "state", block: t.block }))) as InstanceType<typeof TernError>;
		expect(gone.kind === "not_found", "gone", String(gone));
		return true;
	});
}

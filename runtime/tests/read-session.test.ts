import { afterEach, expect, test } from "bun:test";
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import type { BackwardMessagesPage } from "../engine/coding-agent/src/modes/rpc/rpc-messages-backward";

const engineRoot = process.env.OMBRA_ENGINE_ROOT;
if (!engineRoot) throw new Error("Set OMBRA_ENGINE_ROOT to a staged runtime/engine with pinned dependencies");
if (Bun.version !== "1.4.2") throw new Error("Use the pack's pinned Bun 1.4.2");
// Staging selects the canonical host paired with the integrity-pinned dependency tree at runtime.
const { EngineHost } = await import(join(engineRoot, "engine-host.ts"));
const roots: string[] = [];
afterEach(async () => { await Promise.all(roots.splice(0).map(root => rm(root, { recursive: true, force: true }))); });
type Frame = { type?: string; id?: string; success?: boolean; code?: string; error?: string; data?: BackwardMessagesPage };
function page(frame: Frame): BackwardMessagesPage {
	if (!frame.success || !frame.data) throw new Error(frame.error ?? "Read did not return a page");
	return frame.data;
}

async function fixture() {
	const base = resolve(import.meta.dir, "../../.temp/ombra-tests");
	await mkdir(base, { recursive: true });
	const root = await mkdtemp(join(base, "read-session-"));
	roots.push(root);
	const path = join(root, "saved.jsonl");
	const header = { type: "session", version: 3, id: "saved", cwd: join(root, "deleted-project"), timestamp: "2026-01-01T00:00:00Z" };
	const message = (id: string, parentId: string | null, text: string) => ({ type: "message", id, parentId,
		timestamp: "2026-01-01T00:00:00Z", message: { role: "user", content: text, timestamp: 1 } });
	const entries = [header, message("root", null, "root"), message("abandoned", "root", "must not appear"),
		...Array.from({ length: 205 }, (_, i) => message(`m${i}`, i ? `m${i - 1}` : "root", `saved ${i}`))];
	const original = entries.map(entry => JSON.stringify(entry)).join("\n") + "\n";
	await writeFile(path, original);
	let opens = 0;
	const pending = new Map<string, (frame: Frame) => void>();
	const frames: Frame[] = [];
	const host = new EngineHost({
		output(value: object) {
			// EngineHost's in-process output erases the concrete response type.
			const frame = value as Frame;
			frames.push(frame);
			if (frame.id) pending.get(frame.id)?.(frame);
		},
		negotiate() {},
		async createSession() { opens++; throw new Error("A read must not open a logical session or provider"); },
	});
	let sequence = 0;
	function request(type: string, fields: object = {}) {
		const id = `request-${++sequence}`;
		const response = new Promise<Frame>(done => pending.set(id, done));
		host.dispatch({ id, type, ...fields });
		return response.finally(() => pending.delete(id));
	}
	host.start();
	await request("negotiate_protocol", { protocolVersion: 2 });
	return { root, path, original, host, request, frames, opens: () => opens };
}

test("root read uses persisted leaf, pages completely, leaves files unchanged and never creates a session", async () => {
	const f = await fixture();
	const before = await readdir(f.root, { recursive: true });
	const all: BackwardMessagesPage["messages"] = [];
	let cursor: string | undefined;
	do {
		const response = await f.request("engine_read_session", { path: f.path, readId: crypto.randomUUID(), limit: 100, cursor });
		expect(response.success).toBe(true);
		const data = page(response);
		expect(data.messages.length).toBeLessThanOrEqual(100);
		expect(data.totalMessages).toBe(206);
		all.unshift(...data.messages);
		cursor = data.nextCursor;
	} while (cursor);
	expect(all.map(m => "content" in m ? m.content : undefined)).toEqual(["root", ...Array.from({ length: 205 }, (_, i) => `saved ${i}`)]);
	expect(f.opens()).toBe(0);
	expect(f.frames.some(frame => frame.type === "engine_session_frame")).toBe(false);
	expect(await readFile(f.path, "utf8")).toBe(f.original);
	expect(await readdir(f.root, { recursive: true })).toEqual(before);
	await f.host.shutdown();
});

test("changed source invalidates cursor even with identical message count; explicit reload works", async () => {
	const f = await fixture();
	const first = await f.request("engine_read_session", { path: f.path, readId: "first", limit: 100 });
	await writeFile(f.path, f.original.replace("saved 0", "edited 0"));
	const stale = await f.request("engine_read_session", { path: f.path, readId: "older", cursor: page(first).nextCursor });
	expect(stale).toMatchObject({ success: false, code: "stale_cursor" });
	const reload = await f.request("engine_read_session", { path: f.path, readId: "reload" });
	expect(reload.success).toBe(true);
	expect(page(reload).snapshotId).not.toBe(page(first).snapshotId);
	await f.host.shutdown();
});

test("missing, corrupt and cancelled reads fail honestly without writes or logical opens", async () => {
	const f = await fixture();
	expect((await f.request("engine_read_session", { path: join(f.root, "missing.jsonl"), readId: "missing" })).success).toBe(false);
	await writeFile(f.path, f.original + "{broken record\n");
	const corrupt = await f.request("engine_read_session", { path: f.path, readId: "corrupt" });
	expect(corrupt.success).toBe(false);
	expect(corrupt.error).toContain("corrupt");
	await writeFile(f.path, f.original);
	const reading = f.request("engine_read_session", { path: f.path, readId: "cancelled" });
	await f.request("engine_cancel_read", { requestId: "cancelled" });
	expect((await reading).success).toBe(false);
	expect(f.opens()).toBe(0);
	expect(await readFile(f.path, "utf8")).toBe(f.original);
	await f.host.shutdown();
});

test("transcript keeps failed assistant tails and enforces the ordinary page byte budget", async () => {
	const f = await fixture();
	const entry = (id: string, parentId: string, text: string) => JSON.stringify({
		type: "message", id, parentId, timestamp: "2026-01-01T00:00:00Z",
		message: { role: "assistant", content: [{ type: "text", text }], stopReason: "error",
			errorMessage: "persisted failure", provider: "test", model: "unavailable", api: "openai-responses", timestamp: 1 },
	}) + "\n";
	const original = f.original + entry("failed-a", "m204", "a".repeat(500_000)) + entry("failed-b", "failed-a", "b".repeat(500_000));
	await writeFile(f.path, original);
	const recent = page(await f.request("engine_read_session", { path: f.path, readId: "recent", limit: 100 }));
	expect(recent.totalMessages).toBe(208);
	expect(recent.messages).toHaveLength(1);
	expect(recent.messages[0].role).toBe("assistant");
	expect(Buffer.byteLength(JSON.stringify(recent))).toBeLessThan(768 * 1024);
	const older = page(await f.request("engine_read_session", { path: f.path, readId: "older", cursor: recent.nextCursor }));
	expect(older.endOffset).toBe(recent.startOffset);
	expect(older.messages.at(-1)?.role).toBe("assistant");
	expect(await readFile(f.path, "utf8")).toBe(original);
	await f.host.shutdown();
});

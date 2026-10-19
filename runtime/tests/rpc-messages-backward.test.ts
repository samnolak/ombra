import { describe, expect, test } from "bun:test";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

// Run with the owned installed pack's Bun and OMBRA_HISTORY_RPC_DIR pointing to its src/modes/rpc.
// Import real installed modules (including their real npm dependencies), never a mock pager.
const rpcDirectory = process.env.OMBRA_HISTORY_RPC_DIR;
if (!rpcDirectory) throw new Error("Set OMBRA_HISTORY_RPC_DIR to the owned installed pack's src/modes/rpc directory");
const { pageRpcMessagesBackward } = await import(pathToFileURL(join(rpcDirectory, "rpc-messages-backward.ts")).href);
const { pageRpcMessages, RpcMessagesPageError } = await import(pathToFileURL(join(rpcDirectory, "rpc-messages.ts")).href);
const message = (content: string) => ({ role: "user", content, timestamp: 0 });
const messages = (count: number) => Array.from({ length: count }, (_, index) => message(String(index)));
const snapshot = (count: number) => ({ sessionId: "session", leafId: "leaf", messageCount: count });
const encode = (value: unknown) => Buffer.from(JSON.stringify(value)).toString("base64url");
const byteLength = (value: unknown) => Buffer.byteLength(JSON.stringify(value), "utf8");

describe("owned history1 backwards contract", () => {
	test("starts at the tail and prepends bounded chronological pages without gaps", () => {
		const all = messages(250);
		const state = snapshot(all.length);
		const tail = pageRpcMessagesBackward(all, state);
		expect(tail.messages).toEqual(all.slice(150));
		expect(tail.messages[0]).toBe(all[150]);
		expect(tail).toMatchObject({ direction: "backward", totalMessages: 250, startOffset: 150, endOffset: 250, sessionId: "session" });
		const middle = pageRpcMessagesBackward(all, state, { cursor: tail.nextCursor });
		const first = pageRpcMessagesBackward(all, state, { cursor: middle.nextCursor });
		expect(middle).toMatchObject({ startOffset: 50, endOffset: 150, snapshotId: tail.snapshotId });
		expect(first).toMatchObject({ startOffset: 0, endOffset: 50, snapshotId: tail.snapshotId });
		expect(first.nextCursor).toBeUndefined();
		expect([...first.messages, ...middle.messages, ...tail.messages]).toEqual(all);
	});

	test("empty snapshots are complete, and explicit page limits remain bounded", () => {
		expect(pageRpcMessagesBackward([], snapshot(0))).toMatchObject({ messages: [], totalMessages: 0, startOffset: 0, endOffset: 0 });
		expect(pageRpcMessagesBackward([], snapshot(0)).nextCursor).toBeUndefined();
		const all = messages(300);
		expect(pageRpcMessagesBackward(all, snapshot(300), { limit: 1 }).messages).toEqual(all.slice(-1));
		expect(pageRpcMessagesBackward(all, snapshot(300), { limit: 256 }).messages.length).toBe(256);
		for (const limit of [0, -1, 257, 1.5, Number.NaN])
			expect(() => pageRpcMessagesBackward(all, snapshot(300), { limit })).toThrow("limit");
		expect(() => pageRpcMessagesBackward(all, snapshot(299))).toThrow("snapshot");
	});

	test("byte budget counts UTF-8 and accepts exactly the boundary without dropping the next older message", () => {
		const cap = 768 * 1024;
		const older = message("older");
		const first = message("first");
		const available = cap - 3 - byteLength(first) - byteLength(message(""));
		const last = message("é".repeat(Math.floor(available / 2)) + (available % 2 ? "a" : ""));
		expect(byteLength([first, last])).toBe(cap);
		const all = [older, first, last];
		const tail = pageRpcMessagesBackward(all, snapshot(3));
		expect(tail.messages).toEqual([first, last]);
		expect(tail).toMatchObject({ startOffset: 1, endOffset: 3 });
		const previous = pageRpcMessagesBackward(all, snapshot(3), { cursor: tail.nextCursor });
		expect(previous.messages).toEqual([older]);
		expect(previous.nextCursor).toBeUndefined();

		const over = [older, first, message(last.content + "a")];
		expect(pageRpcMessagesBackward(over, snapshot(3)).messages).toEqual(over.slice(-1));
	});

	test("one oversized message is indivisible and still makes progress", () => {
		const all = [message("older"), message("x".repeat(1024 * 1024))];
		const tail = pageRpcMessagesBackward(all, snapshot(2));
		expect(tail.messages).toEqual(all.slice(-1));
		expect(tail.startOffset).toBe(1);
		const older = pageRpcMessagesBackward(all, snapshot(2), { cursor: tail.nextCursor });
		expect(older.messages).toEqual(all.slice(0, 1));
		expect(older.nextCursor).toBeUndefined();
	});

	test("session, branch and message-count changes produce structured stale_cursor", () => {
		const all = messages(5);
		const state = snapshot(5);
		const cursor = pageRpcMessagesBackward(all, state, { limit: 2 }).nextCursor;
		for (const changed of [{ ...state, sessionId: "other-session" }, { ...state, leafId: "other-leaf" }, { ...state, leafId: null }]) {
			try {
				pageRpcMessagesBackward(all, changed, { cursor });
				throw new Error("stale cursor was accepted");
			} catch (error) {
				expect(error).toBeInstanceOf(RpcMessagesPageError);
				expect(error).toMatchObject({ code: "stale_cursor" });
			}
		}
		try {
			pageRpcMessagesBackward([...all, message("new")], snapshot(6), { cursor });
			throw new Error("stale message count was accepted");
		} catch (error) {
			expect(error).toBeInstanceOf(RpcMessagesPageError);
			expect(error).toMatchObject({ code: "stale_cursor" });
		}
	});

	test("malformed or wrong-direction cursors cannot silently select a different page", () => {
		const all = messages(5);
		const state = snapshot(5);
		const valid = { version: 2, direction: "backward", ...state, offset: 3 };
		const malformed = ["", "not+base64", "a".repeat(2049), encode(null), encode({}), encode({ ...valid, offset: -1 }),
			encode({ ...valid, offset: 6 }), encode({ ...valid, offset: 1.5 }), encode({ ...valid, direction: "forward" }),
			encode({ ...valid, version: 1 }), encode({ ...valid, sessionId: "" }), encode({ ...valid, leafId: 7 })];
		for (const cursor of malformed)
			expect(() => pageRpcMessagesBackward(all, state, { cursor })).toThrow("cursor");
		const forward = pageRpcMessages(all, state, { limit: 2 });
		expect(() => pageRpcMessagesBackward(all, state, { cursor: forward.nextCursor })).toThrow("cursor");
		const backward = pageRpcMessagesBackward(all, state, { limit: 2 });
		expect(() => pageRpcMessages(all, state, { cursor: backward.nextCursor })).toThrow("cursor");
	});

	test("upstream forward paging still starts at the head with its original response shape", () => {
		const all = messages(250);
		const state = snapshot(250);
		const first = pageRpcMessages(all, state);
		expect(first.messages).toEqual(all.slice(0, 100));
		expect(Object.keys(first).sort()).toEqual(["messages", "nextCursor", "totalMessages"]);
		const next = pageRpcMessages(all, state, { cursor: first.nextCursor });
		expect(next.messages).toEqual(all.slice(100, 200));
		const last = pageRpcMessages(all, state, { cursor: next.nextCursor });
		expect(last.messages).toEqual(all.slice(200));
		expect(last.nextCursor).toBeUndefined();
	});
});

// Ombra history contract 1, layered over the integrity-pinned omp 18.8.0 sources.
// Forward get_messages_page remains upstream's contract. No client decodes these cursors.
import type { AgentMessage } from "@oh-my-pi/pi-agent-core";
import { RpcMessagesPageError, type RpcMessageSnapshot } from "./rpc-messages";

const MAX_PAGE_BYTES = 768 * 1024;
interface Cursor extends RpcMessageSnapshot { version: 2; direction: "backward"; offset: number }
export interface BackwardMessagesPage {
	messages: AgentMessage[];
	nextCursor?: string;
	totalMessages: number;
	direction: "backward";
	startOffset: number;
	endOffset: number;
	sessionId: string;
	snapshotId: string;
}
function decode(value: string): Cursor {
	if (!value || value.length > 2048 || !/^[A-Za-z0-9_-]+$/.test(value)) throw new Error("Invalid backward RPC message cursor");
	const bytes = Buffer.from(value, "base64url");
	if (bytes.toString("base64url") !== value) throw new Error("Invalid backward RPC message cursor");
	let cursor: Cursor;
	try { cursor = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)); }
	catch { throw new Error("Invalid backward RPC message cursor"); }
	if (!cursor || cursor.version !== 2 || cursor.direction !== "backward" ||
		typeof cursor.sessionId !== "string" || !cursor.sessionId || cursor.sessionId.length > 256 ||
		!(cursor.leafId === null || (typeof cursor.leafId === "string" && cursor.leafId.length > 0 && cursor.leafId.length <= 256)) ||
		!Number.isSafeInteger(cursor.messageCount) || cursor.messageCount < 0 ||
		!Number.isSafeInteger(cursor.offset) || cursor.offset < 0 || cursor.offset > cursor.messageCount)
		throw new Error("Invalid backward RPC message cursor");
	return cursor;
}

/** Newest page first, each page chronological. Strict snapshot identity prevents cross-branch merges. */
export function pageRpcMessagesBackward(messages: readonly AgentMessage[], snapshot: RpcMessageSnapshot,
	options: { cursor?: string; limit?: number } = {}): BackwardMessagesPage {
	if (snapshot.messageCount !== messages.length) throw new Error("RPC message snapshot does not match current messages");
	const limit = options.limit ?? 100;
	if (!Number.isSafeInteger(limit) || limit < 1 || limit > 256) throw new Error("RPC message page limit must be between 1 and 256");
	let end = messages.length;
	if (options.cursor !== undefined) {
		const cursor = decode(options.cursor);
		if (cursor.sessionId !== snapshot.sessionId || cursor.leafId !== snapshot.leafId || cursor.messageCount !== snapshot.messageCount)
			throw new RpcMessagesPageError("RPC message cursor is stale", "stale_cursor");
		end = cursor.offset;
	}
	let start = end;
	let bytes = 2;
	while (start > 0 && end - start < limit) {
		const size = Buffer.byteLength(JSON.stringify(messages[start - 1]), "utf8") + (start === end ? 0 : 1);
		// Like upstream, one oversized message is indivisible and uses negotiated v2 framing.
		if (start < end && bytes + size > MAX_PAGE_BYTES) break;
		bytes += size;
		start--;
	}
	return {
		messages: messages.slice(start, end),
		...(start > 0 ? { nextCursor: Buffer.from(JSON.stringify({ version: 2, direction: "backward", ...snapshot, offset: start }), "utf8").toString("base64url") } : {}),
		totalMessages: messages.length, direction: "backward", startOffset: start, endOffset: end,
		sessionId: snapshot.sessionId, snapshotId: Buffer.from(JSON.stringify(snapshot), "utf8").toString("base64url"),
	};
}

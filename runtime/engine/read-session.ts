import { createHash } from "node:crypto";
import { stat } from "node:fs/promises";
import { resolve } from "node:path";
import { MAX_RPC_REASSEMBLED_BYTES } from "./coding-agent/src/modes/rpc/rpc-frame";
import { RpcMessagesPageError } from "./coding-agent/src/modes/rpc/rpc-messages";
import { pageRpcMessagesBackward } from "./coding-agent/src/modes/rpc/rpc-messages-backward";
import { loadSessionMessagesReadOnly } from "./coding-agent/src/session/session-loader";

async function sourceIdentity(path: string): Promise<string> {
	const info = await stat(path, { bigint: true });
	if (!info.isFile()) throw new Error("Read only history requires a regular session file");
	return createHash("sha256").update(`${path}\0${info.dev}:${info.ino}:${info.size}:${info.mtimeNs}:${info.ctimeNs}`).digest("hex");
}

/** No writer, logical runtime, project discovery or retained per-file cache. */
export async function readSessionPage(path: string, options: { cursor?: string; limit?: number }, signal: AbortSignal) {
	signal.throwIfAborted();
	const canonicalPath = resolve(path);
	const before = await sourceIdentity(canonicalPath);
	const messages = await loadSessionMessagesReadOnly(canonicalPath, { strict: true, signal });
	const after = await sourceIdentity(canonicalPath);
	signal.throwIfAborted();
	if (before !== after) throw new RpcMessagesPageError("The session file changed while reading; reload recent history", "stale_cursor");
	const page = pageRpcMessagesBackward(messages, {
		sessionId: createHash("sha256").update(canonicalPath).digest("hex"),
		leafId: before, messageCount: messages.length,
	}, options);
	// One indivisible message may exceed the normal page budget, but never the negotiated v2 budget.
	if (Buffer.byteLength(JSON.stringify(page)) > MAX_RPC_REASSEMBLED_BYTES - 4096)
		throw new Error("A session message exceeds the RPC transport budget; history was not truncated");
	return page;
}

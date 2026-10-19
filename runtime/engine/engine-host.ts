import { isAbsolute } from "node:path";
import { isRecord } from "@oh-my-pi/pi-utils";
import { MAX_RPC_FRAME_BYTES, MAX_RPC_REASSEMBLED_BYTES, RpcFrameDecoder } from "./coding-agent/src/modes/rpc/rpc-frame";
import type { RpcSessionRuntime } from "./coding-agent/src/modes/rpc/rpc-mode";
import { FileSessionStorage } from "./coding-agent/src/session/session-storage";
import { SessionLeaseHeldError } from "./coding-agent/src/session/session-lease-error";
import { SessionModelUnavailableError } from "./coding-agent/src/session/session-model-error";
import { RpcMessagesPageError } from "./coding-agent/src/modes/rpc/rpc-messages";
import { readSessionPage } from "./read-session";
import { takeSessionLease } from "./take-session";

export interface EngineHostOptions {
	output(frame: object): void;
	negotiate(): void;
	createSession(
		cwd: string, args: readonly string[], output: (frame: object) => void,
		envelopeBytes: number, publish: (runtime: RpcSessionRuntime) => void,
		resumeOnCurrentModel: boolean,
	): Promise<RpcSessionRuntime>;
}

interface SessionSlot {
	opening: Promise<RpcSessionRuntime>;
	runtime?: RpcSessionRuntime;
	closing?: Promise<void>;
	closed: boolean;
	ready: boolean;
}

/** One transport, many logical sessions. A key names a lifetime, never a selected tab. */
export class EngineHost {
	readonly #sessions = new Map<string, SessionSlot>();
	readonly #usedKeys = new Set<string>();
	readonly #deletions = new Set<Promise<void>>();
	readonly #reads = new Map<string, { abort: AbortController; work: Promise<void> }>();
	/** "Open here": waits for another writer to release a session (abort ends the wait on shutdown). */
	readonly #takeovers = new Map<AbortController, Promise<void>>();
	#negotiated = false;
	#stopping = false;
	#shutdown: Promise<void> | undefined;
	#pendingInputBytes = 0;
	#pendingInputCount = 0;
	readonly #closed = Promise.withResolvers<void>();
	readonly closed = this.#closed.promise;

	constructor(private readonly options: EngineHostOptions) {
		void this.closed.catch(() => {});
	}

	start(): void {
		this.options.output({ type: "ready", engineProtocolVersion: 1, protocolVersion: 1,
			supportedProtocolVersions: [1, 2], maxFrameBytes: MAX_RPC_FRAME_BYTES,
			maxReassembledFrameBytes: MAX_RPC_REASSEMBLED_BYTES });
	}

	/** Dispatch synchronously so one session's opening or admission never stalls its peers. */
	dispatch(value: unknown, byteLength = 0): void {
		if (!isRecord(value) || typeof value.type !== "string") {
			this.options.output({ type: "response", command: "parse", success: false, error: "Engine command must be an object with a type" });
			return;
		}
		const id = typeof value.id === "string" ? value.id : undefined;
		const respond = (success: boolean, data?: object, error?: string, code?: string): void => {
			this.options.output({ type: "response", command: value.type, id, success, ...(data ? { data } : {}), ...(error ? { error } : {}), ...(code ? { code } : {}) });
		};
		if (value.type === "engine_session_command") {
			const key = value.sessionKey;
			if (typeof key !== "string" || !isRecord(value.frame) || typeof value.frame.type !== "string") {
				respond(false, undefined, "Session command requires sessionKey and an object frame");
				return;
			}
			const slot = this.#sessions.get(key);
			const sidechannel = value.frame.type === "extension_ui_response" || value.frame.type === "host_tool_result" ||
				value.frame.type === "host_tool_update" || value.frame.type === "host_uri_result";
			if (this.#stopping || !slot?.runtime || slot.closing || slot.closed || (!slot.ready && !sidechannel)) {
				this.options.output({ type: "engine_session_frame", sessionKey: key, frame: {
					type: "response", id: value.frame.id, command: value.frame.type, success: false, error: "Logical session is not open",
				} });
				return;
			}
			if (!sidechannel && (this.#pendingInputBytes + byteLength > 128 * 1024 * 1024 || this.#pendingInputCount >= 4096)) {
				this.options.output({ type: "engine_session_frame", sessionKey: key, frame: {
					type: "response", id: value.frame.id, command: value.frame.type, success: false,
					error: "Engine input backlog exceeded the transport budget",
				} });
				return;
			}
			this.#pendingInputBytes += byteLength;
			this.#pendingInputCount++;
			let released = false;
			const release = (): void => {
				if (released) return;
				released = true;
				this.#pendingInputBytes -= byteLength;
				this.#pendingInputCount--;
			};
			try { slot.runtime.dispatch(value.frame, release); }
			catch (error) {
				release();
				this.options.output({ type: "engine_session_frame", sessionKey: key, frame: {
					type: "response", id: value.frame.id, command: value.frame.type, success: false,
					error: error instanceof Error ? error.message : String(error),
				} });
			}
			return;
		}
		if (this.#stopping) { respond(false, undefined, "Engine is shutting down"); return; }
		if (value.type === "negotiate_protocol") {
			if (value.protocolVersion !== 2) { respond(false, undefined, "Engine requires RPC protocol version 2"); return; }
			respond(true, { protocolVersion: 2 });
			this.options.negotiate();
			this.#negotiated = true;
			return;
		}
		if (!this.#negotiated) { respond(false, undefined, "Negotiate RPC protocol version 2 before opening sessions"); return; }
		if (value.type === "engine_cancel_read") {
			if (typeof value.requestId === "string") this.#reads.get(value.requestId)?.abort.abort();
			respond(true);
			return;
		}
		if (value.type === "engine_read_session") {
			const readId = value.readId;
			if (typeof readId !== "string" || !readId || readId.length > 256 || this.#reads.has(readId) || this.#reads.size >= 4) {
				respond(false, undefined, "Read request requires a unique bounded readId and an available read slot"); return;
			}
			if (typeof value.path !== "string" || !isAbsolute(value.path) || !value.path.endsWith(".jsonl") ||
				(value.cursor !== undefined && typeof value.cursor !== "string") ||
				(value.limit !== undefined && (typeof value.limit !== "number" || !Number.isSafeInteger(value.limit) || value.limit < 1 || value.limit > 256))) {
				respond(false, undefined, "Read requires an absolute .jsonl path, optional cursor and limit from 1 to 256"); return;
			}
			const abort = new AbortController();
			const work = readSessionPage(value.path, { cursor: value.cursor as string | undefined, limit: value.limit as number | undefined }, abort.signal).then(
				page => respond(true, page),
				error => respond(false, undefined, error instanceof Error ? error.message : String(error),
					error instanceof RpcMessagesPageError ? error.code : undefined),
			);
			this.#reads.set(readId, { abort, work });
			void work.finally(() => this.#reads.delete(readId));
			return;
		}
		if (value.type === "engine_delete_session") {
			if (typeof value.path !== "string" || !isAbsolute(value.path) || !value.path.endsWith(".jsonl")) {
				respond(false, undefined, "Deletion requires an absolute .jsonl session path"); return;
			}
			const deletion = new FileSessionStorage().deleteSessionWithArtifacts(value.path).then(
				() => respond(true),
				error => respond(false, undefined, error instanceof Error ? error.message : String(error)),
			);
			this.#deletions.add(deletion);
			void deletion.finally(() => this.#deletions.delete(deletion));
			return;
		}
		if (value.type === "engine_take_session") {
			if (typeof value.path !== "string" || !isAbsolute(value.path) || !value.path.endsWith(".jsonl")) {
				respond(false, undefined, "Takeover requires an absolute .jsonl session path"); return;
			}
			const abort = new AbortController();
			const takeover = takeSessionLease(value.path, abort.signal).then(
				() => respond(true),
				error => respond(false, codedError(error)?.data, error instanceof Error ? error.message : String(error), codedError(error)?.code),
			);
			this.#takeovers.set(abort, takeover);
			void takeover.finally(() => this.#takeovers.delete(abort));
			return;
		}
		if (value.type === "engine_shutdown") {
			void this.shutdown().then(() => respond(true), error => respond(false, undefined, String(error)));
			return;
		}
		if (value.type !== "engine_open" && value.type !== "engine_close") { respond(false, undefined, `Unknown engine command: ${value.type}`); return; }
		const key = value.sessionKey;
		if (typeof key !== "string" || !key.length || Buffer.byteLength(key) > 1024) {
			respond(false, undefined, "sessionKey must be a nonempty string of at most 1024 bytes"); return;
		}
		if (value.type === "engine_close") {
			const slot = this.#sessions.get(key);
			if (!slot) { respond(false, undefined, "Logical session is not open"); return; }
			void this.#close(key, slot).then(() => respond(true), error => respond(false, undefined, String(error)));
			return;
		}
		if (this.#usedKeys.has(key)) { respond(false, undefined, "sessionKey cannot be reused; generate a new lifetime key"); return; }
		if (this.#sessions.size >= 64 || this.#usedKeys.size >= 65536) { respond(false, undefined, "Engine session capacity exceeded"); return; }
		if (typeof value.cwd !== "string" || !isAbsolute(value.cwd) || !Array.isArray(value.arguments) || !value.arguments.every((arg: unknown) => typeof arg === "string")) {
			respond(false, undefined, "engine_open requires an absolute cwd and string arguments array"); return;
		}
		if (value.resumeOnCurrentModel !== undefined && typeof value.resumeOnCurrentModel !== "boolean") {
			respond(false, undefined, "resumeOnCurrentModel must be a boolean"); return;
		}
		if (this.#pendingInputBytes + byteLength > 128 * 1024 * 1024) {
			respond(false, undefined, "Engine input backlog exceeded the transport budget"); return;
		}
		this.#usedKeys.add(key);
		const output = (frame: object): void => {
			// A disposed runtime may still unwind async hooks. Never route late frames.
			if (!slot.closed) this.options.output({ type: "engine_session_frame", sessionKey: key, frame });
		};
		const envelopeBytes = Buffer.byteLength(JSON.stringify({ type: "engine_session_frame", sessionKey: key, frame: {} }));
		// Reserve the key before invoking SDK startup, including synchronous extension callbacks.
		const opening = Promise.withResolvers<RpcSessionRuntime>();
		const slot: SessionSlot = { opening: opening.promise, closed: false, ready: false };
		this.#sessions.set(key, slot);
		this.#pendingInputBytes += byteLength;
		const publish = (runtime: RpcSessionRuntime): void => {
			slot.runtime = runtime;
			if (slot.closing || this.#stopping) void runtime.close().catch(() => {});
		};
		void Promise.resolve().then(() => this.options.createSession(
			value.cwd as string, value.arguments as string[], output, envelopeBytes, publish, value.resumeOnCurrentModel === true,
		)).then(opening.resolve, opening.reject);
		void slot.opening.then(
			() => { this.#pendingInputBytes -= byteLength; },
			() => { this.#pendingInputBytes -= byteLength; },
		);
		void slot.opening.then(runtime => {
			slot.runtime = runtime;
			slot.ready = true;
			void runtime.closed.then(
				() => this.#finish(key, slot),
				error => this.#finish(key, slot, error),
			);
			if (slot.closing || this.#stopping) respond(false, undefined, "Logical session closed during startup");
			else respond(true);
		}, error => {
			this.#finish(key, slot, error);
			respond(false, codedError(error)?.data, error instanceof Error ? error.message : String(error), codedError(error)?.code);
		});
	}

	#finish(key: string, slot: SessionSlot, error?: unknown): void {
		if (slot.closed) return;
		slot.closed = true;
		this.#sessions.delete(key);
		this.options.output({ type: "engine_session_closed", sessionKey: key, exitCode: error ? 1 : 0,
			...(error ? { error: error instanceof Error ? error.message : String(error) } : {}) });
	}

	#close(key: string, slot: SessionSlot): Promise<void> {
		if (!slot.closing) {
			const earlyClose = slot.runtime?.close();
			slot.closing = slot.opening.then(async runtime => { await earlyClose; await runtime.close(); });
			void earlyClose?.catch(() => {});
			void slot.closing.then(() => this.#finish(key, slot), error => this.#finish(key, slot, error));
		}
		return slot.closing;
	}

	/** Root EOF and explicit shutdown both abort and dispose every owned session. */
	shutdown(): Promise<void> {
		if (this.#shutdown) return this.#shutdown;
		this.#stopping = true;
		for (const read of this.#reads.values()) read.abort.abort();
		for (const abort of this.#takeovers.keys()) abort.abort();
		this.#shutdown = (async () => {
			const results = await Promise.allSettled([
				...Array.from(this.#sessions, ([key, slot]) => this.#close(key, slot)),
				...this.#deletions,
				...this.#takeovers.values(),
				...Array.from(this.#reads.values(), read => read.work),
			]);
			const errors = results.flatMap(result => result.status === "rejected" ? [result.reason] : []);
			if (errors.length) throw new AggregateError(errors, "Engine session shutdown failed");
		})();
		void this.#shutdown.then(this.#closed.resolve, this.#closed.reject);
		return this.#shutdown;
	}
}

/** Typed failures the client acts on: their code and data travel with the error response. */
function codedError(error: unknown): SessionModelUnavailableError | SessionLeaseHeldError | undefined {
	return error instanceof SessionModelUnavailableError || error instanceof SessionLeaseHeldError ? error : undefined;
}

/** Bounded physical input lines, one aggregate v2 reassembler, parsed routing. */
export async function readEngineInput(
	input: ReadableStream<Uint8Array>,
	onFrame: (frame: object, byteLength: number) => void,
	onError: (message: string) => void,
): Promise<void> {
	let decoder = new RpcFrameDecoder();
	const textDecoder = new TextDecoder("utf-8", { fatal: true });
	let parts: Uint8Array[] = [];
	let length = 0;
	let discarding = false;
	const consume = (part: Uint8Array, complete: boolean): void => {
		if (!discarding && length + part.byteLength + 1 > MAX_RPC_FRAME_BYTES) {
			parts = []; length = 0; discarding = true;
			decoder = new RpcFrameDecoder();
			onError("Engine input frame exceeded the transport limit");
		}
		if (!discarding) {
			if (part.byteLength) { parts.push(part); length += part.byteLength; }
			if (complete && length) {
				try {
					const text = textDecoder.decode(parts.length === 1 ? parts[0] : Buffer.concat(parts, length)).trim();
					if (text) {
						const parsed: unknown = JSON.parse(text);
						const frame = decoder.push(parsed);
						if (frame) onFrame(frame, isRecord(parsed) && parsed.type === "rpc_chunk" && typeof parsed.byteLength === "number"
							? parsed.byteLength : length);
					}
				} catch (error) {
					decoder = new RpcFrameDecoder();
					onError(error instanceof Error ? error.message : String(error));
				}
			}
		}
		if (complete) { parts = []; length = 0; discarding = false; }
	};
	for await (const chunk of input) {
		let start = 0;
		for (let index = 0; index < chunk.length; index++) {
			if (chunk[index] !== 10) continue;
			consume(chunk.subarray(start, index), true);
			start = index + 1;
		}
		if (start < chunk.length) consume(chunk.subarray(start), false);
	}
	if (length) consume(new Uint8Array(0), true);
}

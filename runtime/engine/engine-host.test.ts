import { describe, expect, test } from "bun:test";
import { Writable } from "node:stream";
import { isRecord } from "@oh-my-pi/pi-utils";
import { EngineHost, readEngineInput } from "./engine-host";
import type { EngineHostOptions } from "./engine-host";
import { MAX_RPC_FRAME_BYTES, MAX_RPC_REASSEMBLED_BYTES, RpcFrameDecoder, RpcFrameEncoder } from "./coding-agent/src/modes/rpc/rpc-frame";
import type { RpcSessionRuntime } from "./coding-agent/src/modes/rpc/rpc-mode";
import { RpcOutputWriter } from "./coding-agent/src/modes/rpc/rpc-output";
import { SessionModelUnavailableError } from "./coding-agent/src/session/session-model-error";

function harness(createSession: EngineHostOptions["createSession"]) {
	const frames: Record<string, unknown>[] = [];
	const waiting = new Map<string, (frame: Record<string, unknown>) => void>();
	const host = new EngineHost({
		createSession,
		negotiate: () => {},
		output: frame => {
			if (!isRecord(frame)) throw new Error("Invalid output");
			frames.push(frame);
			if (frame.type === "response" && typeof frame.id === "string") {
				waiting.get(frame.id)?.(frame);
				waiting.delete(frame.id);
			}
		},
	});
	host.start();
	host.dispatch({ type: "negotiate_protocol", id: "negotiate", protocolVersion: 2 });
	return { host, frames, request(frame: Record<string, unknown>): Promise<Record<string, unknown>> {
		const id = crypto.randomUUID();
		const response = Promise.withResolvers<Record<string, unknown>>();
		waiting.set(id, response.resolve);
		host.dispatch({ ...frame, id });
		return response.promise;
	} };
}

function sessionDouble(output: (frame: object) => void): RpcSessionRuntime {
	const completion = Promise.withResolvers<void>();
	let closed = false;
	return {
		closed: completion.promise,
		dispatch(frame, processed) {
			if (closed) throw new Error("Dispatch after close");
			output({ type: "echo", frame });
			processed?.();
		},
		async close() { closed = true; completion.resolve(); },
	};
}

describe("one-process engine session lifetimes", () => {
	test("missing saved model reports typed identity and requires explicit recovery consent", async () => {
		const consent: boolean[] = [];
		const h = harness(async (_cwd, _args, output, _budget, publish, resumeOnCurrentModel) => {
			consent.push(resumeOnCurrentModel);
			if (!resumeOnCurrentModel) throw new SessionModelUnavailableError("removed-provider/missing-model");
			const runtime = sessionDouble(output);
			publish(runtime);
			return runtime;
		});
		const args = { type: "engine_open", cwd: "/project", arguments: ["--resume", "/project/saved.jsonl", "--model", "current/model"] };
		for (const sessionKey of ["first", "retry"]) {
			expect(await h.request({ ...args, sessionKey })).toMatchObject({
				success: false, code: "session_model_unavailable",
				data: { provider: "removed-provider", modelId: "missing-model", selector: "removed-provider/missing-model" },
			});
		}
		expect((await h.request({ ...args, sessionKey: "recovery", resumeOnCurrentModel: true })).success).toBe(true);
		expect(consent).toEqual([false, false, true]);
		expect(h.frames.some(frame => frame.type === "echo")).toBe(false);
		await h.host.shutdown();
	});

	test("concurrent openings and commands keep cwd, approvals and late frames scoped", async () => {
		const aOpening = Promise.withResolvers<void>();
		const aStarted = Promise.withResolvers<void>();
		const outputs = new Map<string, (frame: object) => void>();
		const h = harness(async (cwd, args, output, _budget, publish) => {
			outputs.set(cwd, output);
			const session = sessionDouble(output);
			publish(session);
			if (cwd === "/a") { aStarted.resolve(); await aOpening.promise; }
			output({ type: "ready", cwd, args });
			return session;
		});
		const openingA = h.request({ type: "engine_open", sessionKey: "A", cwd: "/a", arguments: ["--thinking", "off"] });
		await aStarted.promise;
		expect((await h.request({ type: "engine_open", sessionKey: "B", cwd: "/b", arguments: [] })).success).toBe(true);
		h.host.dispatch({ type: "engine_session_command", sessionKey: "B", frame: { type: "get_state", id: "b-state" } });
		// Startup UI responses must also reach a runtime before engine_open resolves.
		h.host.dispatch({ type: "engine_session_command", sessionKey: "A", frame: { type: "extension_ui_response", id: "a-approval", confirmed: true } });
		aOpening.resolve();
		expect((await openingA).success).toBe(true);
		expect(h.frames.some(frame => frame.sessionKey === "A" && isRecord(frame.frame) && frame.frame.type === "echo")).toBe(true);
		expect(h.frames.some(frame => frame.sessionKey === "B" && isRecord(frame.frame) && frame.frame.type === "echo")).toBe(true);
		expect((await h.request({ type: "engine_close", sessionKey: "A" })).success).toBe(true);
		const beforeLate = h.frames.length;
		outputs.get("/a")!({ type: "message_update", text: "late A" });
		expect(h.frames.length).toBe(beforeLate);
		h.host.dispatch({ type: "engine_session_command", sessionKey: "B", frame: { type: "bash", command: "pwd", id: "b-after-close" } });
		expect(h.frames.at(-1)?.sessionKey).toBe("B");
		expect((await h.request({ type: "engine_open", sessionKey: "A", cwd: "/replacement", arguments: [] })).success).toBe(false);
		await h.host.shutdown();
		expect(h.frames.filter(frame => frame.type === "engine_session_closed").map(frame => frame.sessionKey).sort()).toEqual(["A", "B"]);
	});

	test("root EOF shutdown closes a session still awaiting startup UI", async () => {
		const started = Promise.withResolvers<void>();
		const h = harness(async (_cwd, _args, output, _budget, publish) => {
			const session = sessionDouble(output);
			publish(session);
			started.resolve();
			await session.closed;
			return session;
		});
		const opening = h.request({ type: "engine_open", sessionKey: "pending", cwd: "/pending", arguments: [] });
		await started.promise;
		await h.host.shutdown();
		expect((await opening).success).toBe(false);
		expect(h.frames.filter(frame => frame.type === "engine_session_closed")).toHaveLength(1);
	});

	test("input backlog is bounded across sessions and releases when processing completes", async () => {
		const releases: (() => void)[] = [];
		const h = harness(async (_cwd, _args, output) => {
			const runtime = sessionDouble(output);
			return { ...runtime, dispatch: (_frame, processed) => { if (processed) releases.push(processed); } };
		});
		await h.request({ type: "engine_open", sessionKey: "A", cwd: "/a", arguments: [] });
		await h.request({ type: "engine_open", sessionKey: "B", cwd: "/b", arguments: [] });
		for (const sessionKey of ["A", "B", "A"]) h.host.dispatch({ type: "engine_session_command", sessionKey,
			frame: { type: "prompt", id: sessionKey, message: "held" } }, MAX_RPC_REASSEMBLED_BYTES);
		expect(releases).toHaveLength(2);
		expect(h.frames.at(-1)?.sessionKey).toBe("A");
		const error = h.frames.at(-1)?.frame;
		expect(isRecord(error) && error.success === false).toBe(true);
		releases[0]!();
		h.host.dispatch({ type: "engine_session_command", sessionKey: "B", frame: { type: "get_state" } }, 128);
		expect(releases).toHaveLength(3);
		releases[1]!(); releases[2]!();
		await h.host.shutdown();
	});
});

describe("bounded multiplex v2 framing", () => {
	test("large nested frames roundtrip without losing session identity", async () => {
		const encoder = new RpcFrameEncoder(); encoder.setProtocolVersion(2);
		const frame = { type: "engine_session_command", sessionKey: "B", frame: { type: "prompt", message: "x".repeat(MAX_RPC_FRAME_BYTES + 10) } };
		const lines = Array.from(encoder.encodeFrames(frame));
		expect(lines.length).toBeGreaterThan(1);
		const input = new ReadableStream<Uint8Array>({ start(controller) {
			for (const line of lines) controller.enqueue(Buffer.from(line));
			controller.close();
		} });
		const received: object[] = [];
		const errors: string[] = [];
		await readEngineInput(input, value => received.push(value), error => errors.push(error));
		expect(errors).toEqual([]);
		expect(received).toEqual([frame]);
	});

	test("terminal message compaction tracks each interleaved logical session separately", () => {
		const encoder = new RpcFrameEncoder(); encoder.setProtocolVersion(2);
		const a = { role: "assistant", content: [{ type: "text", text: "A".repeat(MAX_RPC_FRAME_BYTES) }] };
		const b = { role: "assistant", content: [{ type: "text", text: "B".repeat(MAX_RPC_FRAME_BYTES) }] };
		for (const [sessionKey, message] of [["A", a], ["B", b]] as const) {
			Array.from(encoder.encodeFrames({ type: "engine_session_frame", sessionKey, frame: { type: "agent_start" } }));
			Array.from(encoder.encodeFrames({ type: "engine_session_frame", sessionKey, frame: { type: "message_end", message } }));
		}
		for (const [sessionKey, message] of [["A", a], ["B", b]] as const) {
			const frames = Array.from(encoder.encodeFrames({
				type: "engine_session_frame", sessionKey, frame: { type: "agent_end", messages: [message] },
			}));
			expect(frames).toHaveLength(1);
			expect(JSON.parse(frames[0]!)).toEqual({
				type: "engine_session_frame", sessionKey,
				frame: { type: "agent_end", messages: [], messageCount: 1 },
			});
		}
	});

	test("oversized physical input is discarded through newline, subsequent frame survives", async () => {
		const input = new ReadableStream<Uint8Array>({ start(controller) {
			controller.enqueue(Buffer.alloc(MAX_RPC_FRAME_BYTES, 120));
			controller.enqueue(Buffer.from('\n{"type":"engine_shutdown"}\n'));
			controller.close();
		} });
		const errors: string[] = [];
		const received: object[] = [];
		await readEngineInput(input, frame => received.push(frame), error => errors.push(error));
		expect(errors).toHaveLength(1);
		expect(received).toEqual([{ type: "engine_shutdown" }]);
	});

	test("overflow response retains session owner and request id", () => {
		const encoder = new RpcFrameEncoder(); encoder.setProtocolVersion(2);
		const decoder = new RpcFrameDecoder();
		let result: object | undefined;
		for (const line of encoder.encodeFrames({ type: "engine_session_frame", sessionKey: "A", frame: {
			type: "response", command: "get_messages", id: "history", success: true, data: "x".repeat(MAX_RPC_REASSEMBLED_BYTES),
		} })) result = decoder.push(JSON.parse(line));
		expect(result).toEqual({ type: "engine_session_frame", sessionKey: "A", frame: {
			type: "response", command: "get_messages", id: "history", success: false, error: "RPC response exceeded the transport limit",
		} });
	});

	test("blocked output fails at aggregate backlog ceiling instead of growing forever", async () => {
		const sink = new Writable({ highWaterMark: 1, write(_chunk, _encoding, _callback) {} });
		let failure: Error | undefined;
		const writer = new RpcOutputWriter(sink, error => { failure = error; }, 16);
		writer.write(["12345678", "12345678", "x"]);
		expect(failure?.message).toContain("backlog exceeded");
		await expect(writer.close()).rejects.toThrow("backlog exceeded");
		sink.destroy();
	});
});

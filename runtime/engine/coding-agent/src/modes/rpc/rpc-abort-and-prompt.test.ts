import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { realpathSync } from "node:fs";
import { mkdir, mkdtemp, rm } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import type { Context } from "@oh-my-pi/pi-ai";
import { createMockModel, type MockResponse, registerMockApi } from "@oh-my-pi/pi-ai/providers/mock";
import { isRecord } from "@oh-my-pi/pi-utils";
import { getAgentDir, setAgentDir } from "@oh-my-pi/pi-utils/dirs";
import { Settings } from "../../config/settings";
import { withActiveSettings } from "../../config/settings-context";
import { AgentRegistry, createAgentSession } from "../../sdk";
import { SessionManager } from "../../session/session-manager";
import { createRpcSessionRuntime } from "./rpc-mode";

type Frame = Record<string, unknown>;

const textOf = (message: Context["messages"][number]): string => {
	if (!("content" in message)) return "";
	const content: unknown = message.content;
	if (typeof content === "string") return content;
	if (!Array.isArray(content)) return "";
	return content.map(part => (isRecord(part) && typeof part.text === "string" ? part.text : "")).join("");
};

/**
 * The stand-in model: a reply to a user message with "SLOW" streams until aborted, one to "WARM" waits for the test to
 * release it, anything else answers at once. `requested(n)` resolves once the model has been asked n times.
 */
function standInModel() {
	const warm = Promise.withResolvers<void>();
	const waiters: Array<{ count: number; resolve: () => void }> = [];
	const model = createMockModel({
		id: "abort-and-prompt-mock",
		handler: async (context): Promise<MockResponse> => {
			for (const waiter of waiters.filter(w => model.calls.length >= w.count)) {
				waiters.splice(waiters.indexOf(waiter), 1);
				waiter.resolve();
			}
			const lastUser = context.messages.findLast(message => message.role === "user");
			const text = lastUser ? textOf(lastUser) : "";
			if (text.includes("SLOW")) return { content: ["Streaming a long reply."], delayMs: 60_000 };
			if (text.includes("WARM")) await warm.promise;
			return { content: [`Reply to: ${text.slice(0, 40)}`] };
		},
	});
	return {
		model,
		releaseWarm: warm.resolve,
		requested: (count: number): Promise<void> => {
			if (model.calls.length >= count) return Promise.resolve();
			const reached = Promise.withResolvers<void>();
			waiters.push({ count, resolve: reached.resolve });
			return reached.promise;
		},
		/** Serialized context of each model call, in call order. */
		calls: () => model.calls.map(call => JSON.stringify(call.context.messages)),
		aborted: (index: number) => model.calls[index]?.options?.signal?.aborted === true,
	};
}

describe("RPC abort_and_prompt", () => {
	const root = path.join(realpathSync(os.tmpdir()), "ombra-tests", `run-${process.pid}`, "rpc-abort-and-prompt");
	const previousAgentDir = getAgentDir();
	beforeAll(async () => {
		await mkdir(path.join(root, "agent"), { recursive: true });
		setAgentDir(path.join(root, "agent"));
		registerMockApi();
	});
	afterAll(async () => {
		setAgentDir(previousAgentDir);
		await rm(root, { recursive: true, force: true });
	});

	async function open() {
		const cwd = await mkdtemp(path.join(root, "project-"));
		const sessionManager = SessionManager.create(cwd, path.join(cwd, ".sessions"));
		const settings = await Settings.loadIsolated({ cwd, agentDir: getAgentDir(), projectRoot: cwd, inMemory: true });
		const standIn = standInModel();
		const registry = new AgentRegistry();
		const { session } = await AgentRegistry.runWithRegistry(registry, () =>
			withActiveSettings(settings, () =>
				createAgentSession({
					cwd, settings, sessionManager, agentRegistry: registry, model: standIn.model, bindProcessState: false,
					disableExtensionDiscovery: true, skills: [], rules: [], enableLsp: false,
				}),
			),
		);
		session.modelRegistry.authStorage.keys.setRuntime(standIn.model.provider, "mock-key");
		const frames: Frame[] = [];
		const waiters: Array<{ match: (frame: Frame) => boolean; resolve: (frame: Frame) => void }> = [];
		const runtime = await withActiveSettings(settings, () =>
			createRpcSessionRuntime(session, {
				protocolVersion: 2,
				output: frame => {
					if (!isRecord(frame)) return;
					frames.push(frame);
					for (const waiter of waiters.filter(w => w.match(frame))) {
						waiters.splice(waiters.indexOf(waiter), 1);
						waiter.resolve(frame);
					}
				},
			}),
		);
		const next = (match: (frame: Frame) => boolean): Promise<Frame> => {
			const seen = frames.find(match);
			if (seen) return Promise.resolve(seen);
			const waiting = Promise.withResolvers<Frame>();
			waiters.push({ match, resolve: waiting.resolve });
			return waiting.promise;
		};
		let ids = 0;
		/** Writes a command frame without waiting for anything, as a client pipelining frames on stdin does. */
		const send = (command: Frame): string => {
			const id = `r${++ids}`;
			runtime.dispatch({ ...command, id });
			return id;
		};
		const response = (id: string) => next(frame => frame.type === "response" && frame.id === id);
		const promptResult = (id: string) => next(frame => frame.type === "prompt_result" && frame.id === id);
		return { session, runtime, standIn, send, response, promptResult, request: (command: Frame) => response(send(command)) };
	}

	test("replaces a run whose Send-now steer is still queued: the run stops, the new prompt runs next, queued input follows it", async () => {
		const h = await open();
		try {
			const slow = h.send({ type: "prompt", message: "SLOW first reply" });
			await h.standIn.requested(1);
			// Queue while it streams, then Send now: the follow-up moves to the steering queue, still unread.
			expect(await h.request({ type: "follow_up", message: "queued alpha" })).toMatchObject({ success: true });
			expect(await h.request({ type: "promote_queued_message", message: "queued alpha" })).toMatchObject({
				success: true,
				data: { promoted: true },
			});
			expect(await h.request({ type: "follow_up", message: "queued bravo" })).toMatchObject({ success: true });

			const interrupt = h.send({ type: "abort_and_prompt", message: "interrupt bravo" });
			expect(await h.response(interrupt)).toMatchObject({
				success: true,
				data: { inputDisposition: "admitted", agentInvoked: true },
			});
			expect(await h.promptResult(slow)).toMatchObject({ status: "aborted" });
			expect(await h.promptResult(interrupt)).toMatchObject({ status: "completed" });

			const calls = h.standIn.calls();
			expect(h.standIn.aborted(0)).toBe(true);
			// The replacement prompt is the next thing the model reads; the steer and the follow-up come after it.
			expect(calls[1]).toContain("interrupt bravo");
			expect(calls[1]!.indexOf("interrupt bravo")).toBeLessThan(calls[1]!.indexOf("queued alpha"));
			expect(calls.at(-1)).toContain("queued bravo");
			expect(h.session.queuedMessageCount).toBe(0);
		} finally {
			await h.runtime.close();
		}
	}, 60_000);

	test("after a turn a Send-now steer caused, stops a prompt sent just before it, whether or not that run started", async () => {
		const h = await open();
		try {
			// A turn whose Send-now message is delivered at its end, as an interjection; then the session goes idle.
			const warm = h.send({ type: "prompt", message: "WARM up" });
			await h.standIn.requested(1);
			await h.request({ type: "follow_up", message: "queued alpha" });
			expect(await h.request({ type: "promote_queued_message", message: "queued alpha" })).toMatchObject({
				data: { promoted: true },
			});
			h.standIn.releaseWarm();
			expect(await h.promptResult(warm)).toMatchObject({ status: "completed" });
			const warmCalls = h.standIn.calls().length;
			expect(h.standIn.calls().at(-1)).toContain("queued alpha");

			// Pipelined: the prompt frame and the interrupt frame arrive together.
			const pipelined = h.send({ type: "prompt", message: "SLOW second" });
			const first = h.send({ type: "abort_and_prompt", message: "interrupt charlie" });
			expect(await h.response(first)).toMatchObject({ success: true, data: { inputDisposition: "admitted" } });
			expect(await h.response(pipelined)).toMatchObject({ success: true });
			expect(await h.promptResult(first)).toMatchObject({ status: "completed" });
			let calls = h.standIn.calls();
			expect(calls.at(-1)).toContain("interrupt charlie");
			for (let index = warmCalls; index < calls.length; index++) {
				if (calls[index]!.includes("SLOW second")) expect(h.standIn.aborted(index)).toBe(true);
			}

			// The prompt's run has started (the client saw it running) when the interrupt arrives.
			const before = calls.length;
			const started = h.send({ type: "prompt", message: "SLOW third" });
			await h.standIn.requested(before + 1);
			const second = h.send({ type: "abort_and_prompt", message: "interrupt delta" });
			expect(await h.response(second)).toMatchObject({ success: true, data: { inputDisposition: "admitted" } });
			expect(await h.promptResult(started)).toMatchObject({ status: "aborted" });
			expect(await h.promptResult(second)).toMatchObject({ status: "completed" });
			calls = h.standIn.calls();
			expect(h.standIn.aborted(before)).toBe(true);
			expect(calls[before + 1]).toContain("interrupt delta");
		} finally {
			await h.runtime.close();
		}
	}, 60_000);
});

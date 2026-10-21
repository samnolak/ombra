import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { realpathSync } from "node:fs";
import { mkdir, mkdtemp, rm } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { createMockModel, registerMockApi } from "@oh-my-pi/pi-ai/providers/mock";
import { isRecord } from "@oh-my-pi/pi-utils";
import { getAgentDir, setAgentDir } from "@oh-my-pi/pi-utils/dirs";
import { Settings } from "../../config/settings";
import { withActiveSettings } from "../../config/settings-context";
import { AgentRegistry, createAgentSession } from "../../sdk";
import { SessionManager } from "../../session/session-manager";
import { createRpcSessionRuntime } from "./rpc-mode";

type Frame = Record<string, unknown>;

/**
 * `/fresh` starts a new provider session in the same chat: `session.sessionId` (the provider's) changes, the
 * transcript does not. The prompt that ran it is still the current input, so its ACK is a local command, not a
 * cancelled one (the GUI's "Fresh provider session" reported "Not refreshed" on every press).
 */
describe("RPC /fresh admission", () => {
	const root = path.join(realpathSync(os.tmpdir()), "ombra-tests", `run-${process.pid}`, "rpc-fresh-admission");
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

	test("the prompt that ran /fresh is acknowledged as a local command, and the next prompt is admitted", async () => {
		const cwd = await mkdtemp(path.join(root, "project-"));
		const sessionManager = SessionManager.create(cwd, path.join(cwd, ".sessions"));
		const settings = await Settings.loadIsolated({ cwd, agentDir: getAgentDir(), projectRoot: cwd, inMemory: true });
		const model = createMockModel({ id: "fresh-mock", handler: () => ({ content: ["Reply."] }) });
		const registry = new AgentRegistry();
		const { session } = await AgentRegistry.runWithRegistry(registry, () =>
			withActiveSettings(settings, () =>
				createAgentSession({
					cwd, settings, sessionManager, agentRegistry: registry, model, bindProcessState: false,
					disableExtensionDiscovery: true, skills: [], rules: [], enableLsp: false,
				}),
			),
		);
		session.modelRegistry.authStorage.keys.setRuntime(model.provider, "mock-key");
		const frames: Frame[] = [];
		const waiters: Array<{ match: (frame: Frame) => boolean; resolve: (frame: Frame) => void }> = [];
		const runtime = await withActiveSettings(settings, () =>
			createRpcSessionRuntime(session, {
				protocolVersion: 2,
				output: frame => {
					if (!isRecord(frame)) return;
					frames.push(frame);
					for (const waiter of [...waiters]) {
						if (!waiter.match(frame)) continue;
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
		try {
			const transcript = session.sessionManager.getSessionId();
			const providerBefore = session.sessionId;
			runtime.dispatch({ type: "prompt", id: "fresh", message: "/fresh" });
			expect(await next(frame => frame.type === "response" && frame.id === "fresh")).toMatchObject({
				success: true,
				data: { inputDisposition: "local", agentInvoked: false },
			});
			expect(frames.some(frame => frame.type === "command_output" && String(frame.text).startsWith("Fresh provider session started"))).toBe(true);
			expect(session.sessionId).not.toBe(providerBefore);
			expect(session.sessionManager.getSessionId()).toBe(transcript);

			runtime.dispatch({ type: "prompt", id: "after", message: "hello" });
			expect(await next(frame => frame.type === "response" && frame.id === "after")).toMatchObject({
				success: true,
				data: { inputDisposition: "admitted", agentInvoked: true },
			});
			expect(await next(frame => frame.type === "prompt_result" && frame.id === "after")).toMatchObject({ status: "completed" });
		} finally {
			await runtime.close();
		}
	}, 60_000);
});

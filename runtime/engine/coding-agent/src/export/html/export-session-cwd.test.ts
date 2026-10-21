import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { existsSync, realpathSync } from "node:fs";
import { mkdir, mkdtemp, rm } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { createMockModel, registerMockApi } from "@oh-my-pi/pi-ai/providers/mock";
import { isRecord } from "@oh-my-pi/pi-utils";
import { getAgentDir, setAgentDir } from "@oh-my-pi/pi-utils/dirs";
import { Settings } from "../../config/settings";
import { withActiveSettings } from "../../config/settings-context";
import { createRpcSessionRuntime } from "../../modes/rpc/rpc-mode";
import { AgentRegistry, createAgentSession } from "../../sdk";
import { SessionManager } from "../../session/session-manager";

type Frame = Record<string, unknown>;

/**
 * The engine host serves sessions of many folders from one process whose cwd is none of them: `/export` (and RPC
 * `export_html`) without an absolute path writes into the session's folder, as a one-session omp started there did,
 * not into the process's cwd (Ombra's "Export as HTML" showed a file that was elsewhere, or not written at all).
 */
describe("HTML export in a shared engine process", () => {
	const root = path.join(realpathSync(os.tmpdir()), "ombra-tests", `run-${process.pid}`, "export-session-cwd");
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

	test("/export and export_html write into the session's folder, not the process's", async () => {
		const cwd = await mkdtemp(path.join(root, "project-"));
		expect(cwd).not.toBe(process.cwd());
		const sessionManager = SessionManager.create(cwd, path.join(cwd, ".sessions"));
		const settings = await Settings.loadIsolated({ cwd, agentDir: getAgentDir(), projectRoot: cwd, inMemory: true });
		const model = createMockModel({ id: "export-mock", handler: () => ({ content: ["Reply."] }) });
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
		const written: string[] = [];
		try {
			runtime.dispatch({ type: "prompt", id: "hello", message: "hello" });
			expect(await next(frame => frame.type === "prompt_result" && frame.id === "hello")).toMatchObject({ status: "completed" });

			runtime.dispatch({ type: "prompt", id: "export", message: "/export" });
			expect(await next(frame => frame.type === "response" && frame.id === "export")).toMatchObject({ success: true });
			const printed = frames.find(frame => frame.type === "command_output" && String(frame.text).startsWith("Session exported to: "));
			const file = String(printed?.text).slice("Session exported to: ".length);
			written.push(path.resolve(process.cwd(), file));
			expect(path.dirname(file)).toBe(cwd);
			expect(existsSync(file)).toBe(true);
			expect(existsSync(path.join(process.cwd(), path.basename(file)))).toBe(false);

			runtime.dispatch({ type: "export_html", id: "named", outputPath: "named.html" });
			const named = await next(frame => frame.type === "response" && frame.id === "named");
			const namedPath = isRecord(named.data) ? String(named.data.path) : "";
			written.push(path.resolve(process.cwd(), namedPath));
			expect(namedPath).toBe(path.join(cwd, "named.html"));
			expect(existsSync(namedPath)).toBe(true);
		} finally {
			await runtime.close();
			// Before the fix both went to the process's cwd: never leave them there.
			for (const file of written) if (!file.startsWith(cwd)) await rm(file, { force: true });
		}
	}, 60_000);
});

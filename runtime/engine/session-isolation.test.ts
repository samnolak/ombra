import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { AsyncLocalStorage } from "node:async_hooks";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { getBundledModels } from "@oh-my-pi/pi-catalog";
import { AsyncJobManager } from "./coding-agent/src/async/job-manager";
import { getDisabledProviders } from "./coding-agent/src/capability";
import { Settings, settings } from "./coding-agent/src/config/settings";
import { withActiveSettings } from "./coding-agent/src/config/settings-context";
import { IrcBus } from "./coding-agent/src/irc/bus";
import { MCPManager } from "./coding-agent/src/mcp/manager";
import { AgentLifecycleManager } from "./coding-agent/src/registry/agent-lifecycle";
import { AgentRegistry } from "./coding-agent/src/registry/agent-registry";
import { createAgentSession, type CreateAgentSessionResult } from "./coding-agent/src/sdk";
import { FileSessionStorage } from "./coding-agent/src/session/session-storage";
import { defaultEvalSessionId } from "./coding-agent/src/eval/session-id";
import { SessionManager } from "./coding-agent/src/session/session-manager";
import { cfgToolsApprovalMode } from "./coding-agent/src/tools/settings";
import { resolvePromptInput } from "./coding-agent/src/system-prompt";
import { createEngineSession, parseEngineSessionArgs } from "./session-factory";

describe("shared-engine session isolation", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-session-isolation");
	beforeAll(() => mkdir(testRoot, { recursive: true }));
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	test("real CLI parser preserves model, thinking, approval, resume and extension flags", () => {
		const args = Object.freeze([
			"--model=anthropic/claude-opus-4-5", "--thinking", "high",
			"--approval-mode", "write", "--resume", "/project/chat.jsonl",
			"--config", "/project/overlay.yml", "--extension", "/project/extension.ts",
		]);
		const parsed = parseEngineSessionArgs(args);
		expect(parsed.model).toBe("anthropic/claude-opus-4-5");
		expect(parsed.thinking).toBe("high");
		expect(parsed.approvalMode).toBe("write");
		expect(parsed.resume).toBe("/project/chat.jsonl");
		expect(parsed.config).toEqual(["/project/overlay.yml"]);
		expect(parsed.extensions).toEqual(["/project/extension.ts"]);
		expect(args[0]).toBe("--model=anthropic/claude-opus-4-5");
		expect(() => parseEngineSessionArgs(["--profile", "other"])).toThrow("session option");
		expect(() => parseEngineSessionArgs(["--resume"])).toThrow("requires a session");
		expect(() => parseEngineSessionArgs(["--no-session", "--continue"])).toThrow("cannot be combined");
	});

	test("real factory resumes saved models despite launch defaults and replaces only with explicit consent", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "resume-model-"));
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("resume-regression", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-regression-key", api: "openai-completions",
			models: ["saved", "current"].map(id => ({ id, name: id, reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }))
		});`);
		const flags = ["--extension", extension, "--no-extensions", "--no-lsp", "--no-tools", "--no-skills", "--no-rules", "--no-prewalk"];
		const manager = SessionManager.create(cwd, path.join(cwd, "sessions"));
		manager.appendModelChange("resume-regression/saved");
		manager.appendMessage({ role: "user", content: "saved question", timestamp: 1 });
		manager.appendMessage({
			role: "assistant", content: [{ type: "text", text: "saved answer" }],
			api: "openai-completions", provider: "resume-regression", model: "saved",
			usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
			stopReason: "stop", timestamp: 2,
		});
		await manager.ensureOnDisk();
		const file = manager.getSessionFile()!;
		await manager.close();
		const args = [...flags, "--resume", file, "--model", "resume-regression/current"];
		let opened = await createEngineSession(cwd, args);
		try {
			expect(opened.session.model?.id).toBe("saved");
			expect(opened.session.messages.filter(message => message.role === "user")).toHaveLength(1);
			expect(opened.session.isStreaming).toBe(false);
		} finally { await opened.runInContext(() => opened.session.dispose()); }

		const saved = await SessionManager.open(file);
		// A surviving saved default must not silently replace the unavailable last-used role model either.
		saved.appendModelChange("removed-provider/missing-model", "slow");
		await saved.close();
		const before = await readFile(file, "utf8");
		for (let attempt = 0; attempt < 2; attempt++) {
			await expect(createEngineSession(cwd, args)).rejects.toMatchObject({
				code: "session_model_unavailable",
				data: { provider: "removed-provider", modelId: "missing-model", selector: "removed-provider/missing-model" },
			});
			expect(await readFile(file, "utf8")).toBe(before);
		}
		opened = await createEngineSession(cwd, args, true);
		try {
			expect(opened.session.model?.id).toBe("current");
			expect(opened.session.messages.filter(message => message.role === "user")).toHaveLength(1);
			expect(opened.session.isStreaming).toBe(false);
		} finally { await opened.runInContext(() => opened.session.dispose()); }
		// Consent changes the saved selection once, not the meaning of future ordinary resume.
		opened = await createEngineSession(cwd, [...flags, "--resume", file, "--model", "resume-regression/saved"]);
		try { expect(opened.session.model?.id).toBe("current"); }
		finally { await opened.runInContext(() => opened.session.dispose()); }
	}, 60_000);

	test("interleaved roots retain private Main, settings, MCP, jobs, IRC and lifecycle", async () => {
		const a = new AgentRegistry();
		const b = new AgentRegistry();
		const settingsA = Settings.isolated({ "tools.approvalMode": "always-ask", disabledProviders: ["provider-a"] });
		const settingsB = Settings.isolated({ "tools.approvalMode": "yolo", disabledProviders: ["provider-b"] });
		const enteredA = Promise.withResolvers<void>();
		const enteredB = Promise.withResolvers<void>();
		let resumeA: CreateAgentSessionResult["runInContext"] | undefined;
		let resumeB: CreateAgentSessionResult["runInContext"] | undefined;
		const jobsA = new AsyncJobManager({});
		const jobsB = new AsyncJobManager({});
		const mcpA = new MCPManager("/project-a");
		const mcpB = new MCPManager("/project-b");
		await Promise.all([
			AgentRegistry.runWithRegistry(a, () => withActiveSettings(settingsA, async () => {
				a.register({ id: "Main", displayName: "A", kind: "main", session: null });
				AsyncJobManager.setInstance(jobsA);
				MCPManager.setInstance(mcpA);
				resumeA = AsyncLocalStorage.snapshot();
				enteredA.resolve();
				await enteredB.promise;
				expect(AgentRegistry.global().get("Main")?.displayName).toBe("A");
				expect(Settings.instance).toBe(settingsA);
				expect(cfgToolsApprovalMode.get(settings)).toBe("always-ask");
				expect(getDisabledProviders()).toEqual(["provider-a"]);
				expect(AsyncJobManager.instance()).toBe(jobsA);
				expect(MCPManager.instance()).toBe(mcpA);
			})),
			AgentRegistry.runWithRegistry(b, () => withActiveSettings(settingsB, async () => {
				await enteredA.promise;
				b.register({ id: "Main", displayName: "B", kind: "main", session: null });
				AsyncJobManager.setInstance(jobsB);
				MCPManager.setInstance(mcpB);
				resumeB = AsyncLocalStorage.snapshot();
				enteredB.resolve();
				await Promise.resolve();
				expect(AgentRegistry.global().get("Main")?.displayName).toBe("B");
				expect(Settings.instance).toBe(settingsB);
				expect(cfgToolsApprovalMode.get(settings)).toBe("yolo");
				expect(getDisabledProviders()).toEqual(["provider-b"]);
				expect(AsyncJobManager.instance()).toBe(jobsB);
				expect(MCPManager.instance()).toBe(mcpB);
			})),
		]);
		if (!resumeA || !resumeB) throw new Error("Missing captured session scopes");
		const lifecycleB = resumeB(() => AgentLifecycleManager.global());
		const ircB = resumeB(() => IrcBus.global());
		expect(resumeA(() => IrcBus.global())).not.toBe(ircB);
		expect(resumeA(() => AgentLifecycleManager.global())).not.toBe(lifecycleB);
		await resumeA(async () => {
			await AgentLifecycleManager.global().dispose();
			await AsyncJobManager.instance()?.dispose();
			AsyncJobManager.setInstance(undefined);
			await MCPManager.instance()?.disconnectAll();
			MCPManager.setInstance(undefined);
		});
		await resumeB(async () => {
			expect(AgentLifecycleManager.global()).toBe(lifecycleB);
			expect(IrcBus.global()).toBe(ircB);
			expect(AsyncJobManager.instance()).toBe(jobsB);
			expect(jobsB.atCapacity).toBe(false);
			expect(MCPManager.instance()).toBe(mcpB);
			expect(AgentRegistry.global().get("Main")?.displayName).toBe("B");
			await lifecycleB.dispose();
			await jobsB.dispose();
			await mcpB.disconnectAll();
			AsyncJobManager.setInstance(undefined);
			MCPManager.setInstance(undefined);
		});
	});

	test("disposing one real SDK chat leaves its peer's registry, bus and extension alive", async () => {
		const root = await mkdtemp(path.join(testRoot, "sdk-"));
		const sessions: CreateAgentSessionResult[] = [];
		const shutdowns: string[] = [];
		const callerScope = new AsyncLocalStorage<string>();
		const shutdownCallerScopes: Array<string | undefined> = [];
		try {
			for (const name of ["a", "b"]) {
				const cwd = path.join(root, name);
				await mkdir(cwd);
				await writeFile(path.join(cwd, "marker.txt"), `project-${name}`);
				const result = await createAgentSession({
					cwd,
					agentDir: path.join(cwd, ".omp"),
					settings: Settings.isolated(),
					sessionManager: SessionManager.inMemory(cwd),
					agentRegistry: new AgentRegistry(),
					bindProcessState: false,
					model: getBundledModels("anthropic")[0]!,
					hasUI: true,
					autoApprove: true,
					deferRetryFallbackValidation: true,
					enableMCP: false,
					enableLsp: false,
					skipPythonPreflight: true,
					toolNames: ["read", "bash"],
					skills: [], rules: [], contextFiles: [], promptTemplates: [], slashCommands: [],
					disableExtensionDiscovery: true,
					extensions: [api => {
						api.on("session_shutdown", async () => {
							await Promise.resolve();
							shutdownCallerScopes.push(callerScope.getStore());
							shutdowns.push(`${name}:${AgentRegistry.global().get("Main")?.session?.sessionId}`);
						});
					}],
				});
				sessions.push(result);
			}
			const [a, b] = sessions;
			expect(a.session.sessionId).not.toBe(b.session.sessionId);
			expect(a.eventBus).not.toBe(b.eventBus);
			const reads = await Promise.all(sessions.map(result => result.runInContext(async () => {
				const read = result.session.getToolByName("read");
				if (!read) throw new Error("SDK read tool missing");
				return read.execute("read-marker", { path: "marker.txt:raw" });
			})));
			expect(JSON.stringify(reads[0])).toContain("project-a");
			expect(JSON.stringify(reads[0])).not.toContain("project-b");
			expect(JSON.stringify(reads[1])).toContain("project-b");
			const shells = await Promise.all(sessions.map(result => result.runInContext(async () => {
				const bash = result.session.getToolByName("bash");
				if (!bash) throw new Error("SDK bash tool missing");
				return bash.execute("shell-marker", { command: "cat marker.txt", pty: false, timeout: 10 });
			})));
			expect(JSON.stringify(shells[0])).toContain("project-a");
			expect(JSON.stringify(shells[1])).toContain("project-b");
			const received: unknown[] = [];
			b.eventBus.on("isolation", value => { received.push(value); });
			a.eventBus.emit("isolation", "wrong chat");
			expect(received).toEqual([]);
			// Deliberately call dispose outside runInContext: SDK method binding
			// must recover A's scope rather than disposing the ambient lifecycle.
			await callerScope.run("close-a-request", () => a.session.dispose());
			expect(shutdownCallerScopes).toEqual(["close-a-request"]);
			expect(shutdowns).toEqual([`a:${a.session.sessionId}`]);
			expect(b.session.isDisposed).toBe(false);
			expect(b.runInContext(() => AgentRegistry.global().get("Main")?.session)).toBe(b.session);
			await callerScope.run("peer-request", () => b.runInContext(async () => {
				await Promise.resolve();
				expect(callerScope.getStore()).toBe("peer-request");
				expect(AgentRegistry.global().get("Main")?.session).toBe(b.session);
			}));
			b.eventBus.emit("isolation", "peer still alive");
			expect(received).toEqual(["peer still alive"]);
			const peerShell = await b.runInContext(async () => {
				const bash = b.session.getToolByName("bash");
				if (!bash) throw new Error("Peer SDK bash tool missing");
				return bash.execute("peer-survives-close", { command: "cat marker.txt", pty: false, timeout: 10 });
			});
			expect(JSON.stringify(peerShell)).toContain("project-b");
			await b.session.dispose();
			expect(shutdowns).toEqual([`a:${a.session.sessionId}`, `b:${b.session.sessionId}`]);
		} finally {
			await Promise.allSettled(sessions.map(result => result.session.dispose()));
			await rm(root, { recursive: true, force: true });
		}
	}, 30_000);

	test("same-project and same-transcript eval kernels have distinct lifetime identities", () => {
		const a = defaultEvalSessionId({ cwd: "/same-project", getSessionFile: () => "/same-session.jsonl", getEvalKernelOwnerId: () => "lifetime-a" });
		const b = defaultEvalSessionId({ cwd: "/same-project", getSessionFile: () => "/same-session.jsonl", getEvalKernelOwnerId: () => "lifetime-b" });
		expect(a).not.toBe(b);
	});

	test("file storage lease excludes a second same-process writer until release", async () => {
		const root = await mkdtemp(path.join(testRoot, "writer-"));
		const first = new FileSessionStorage();
		const second = new FileSessionStorage();
		const id = Bun.randomUUIDv7();
		const file = path.join(root, `${id}.jsonl`);
		let releaseFirst: (() => void) | null = null;
		let releaseSecond: (() => void) | null = null;
		try {
			releaseFirst = first.claimSession(id, file);
			expect(releaseFirst).not.toBeNull();
			expect(second.claimSession(id, file)).toBeNull();
			releaseFirst?.();
			releaseFirst = null;
			releaseSecond = second.claimSession(id, file);
			expect(releaseSecond).not.toBeNull();
		} finally {
			releaseFirst?.();
			releaseSecond?.();
			await rm(root, { recursive: true, force: true });
		}
	});

	test("relative prompt files and in-memory transcripts use explicit project cwd", async () => {
		const originalCwd = process.cwd();
		const root = await mkdtemp(path.join(testRoot, "session-"));
		try {
			await writeFile(path.join(root, "prompt.txt"), "project prompt");
			expect(await resolvePromptInput("prompt.txt", "system prompt", root)).toBe("project prompt");
			expect(await resolvePromptInput("literal prompt", "system prompt", root)).toBe("literal prompt");
			const first = SessionManager.inMemory(root);
			const second = SessionManager.inMemory(root);
			expect(first.getCwd()).toBe(root);
			expect(first.getSessionId()).not.toBe(second.getSessionId());
			expect(process.cwd()).toBe(originalCwd);
		} finally {
			await rm(root, { recursive: true, force: true });
		}
	});
});

import { afterAll, beforeAll, describe, expect, spyOn, test } from "bun:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { getAgentDir, refreshDirsFromEnv, setAgentDir } from "@oh-my-pi/pi-utils";
import { cfgCompactionKeepRecentTokens, cfgCompactionMethodOrder } from "./coding-agent/src/session/context-settings";
import { createEngineSession } from "./session-factory";

// An LLM compaction summary that is not smaller than the history it replaces must not be committed: it would
// leave the context as full as before (or fuller) while discarding the verbatim history.
describe("compaction summary shrink guard", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-compaction-checkpoint");
	// The scripted model must be the only compaction candidate: a user's own credentials would add real models
	// that the automatic path falls back to.
	let originalAgentDir: string;
	let originalAgentEnv: string | undefined;
	let restoreHome: () => void;
	beforeAll(async () => {
		await mkdir(testRoot, { recursive: true });
		originalAgentDir = getAgentDir();
		originalAgentEnv = process.env.PI_CODING_AGENT_DIR;
		const home = spyOn(os, "homedir").mockReturnValue(testRoot);
		restoreHome = () => home.mockRestore();
		refreshDirsFromEnv();
		setAgentDir(path.join(testRoot, "agent"));
	});
	afterAll(async () => {
		restoreHome();
		refreshDirsFromEnv();
		setAgentDir(originalAgentDir);
		if (originalAgentEnv === undefined) delete process.env.PI_CODING_AGENT_DIR;
		else process.env.PI_CODING_AGENT_DIR = originalAgentEnv;
		await rm(testRoot, { recursive: true, force: true });
	});

	// `checkpoint/model` is the live model and first compaction candidate; `checkpoint/big` (the largest window)
	// is the next one. Each answers summary requests with its own text.
	async function runScenario(summaryText: string, bigSummaryText = summaryText) {
		const cwd = await mkdtemp(path.join(testRoot, "run-"));
		const summaryPrompts: string[] = [];
		const summaryModels: string[] = [];
		const sse = (delta: Record<string, unknown>, finish: string | null = null) =>
			`data: ${JSON.stringify({ id: "t", object: "chat.completion.chunk", created: 0, model: "m", choices: [{ index: 0, delta, finish_reason: finish }] })}\n\n`;
		const server = Bun.serve({
			port: 0,
			async fetch(req) {
				const body = (await req.json()) as { model: string; messages: { role: string; content: unknown }[] };
				const lastUser = JSON.stringify(body.messages.filter(m => m.role === "user").at(-1)?.content ?? "");
				let reply = "Noted.";
				if (lastUser.includes("<conversation>") && lastUser.includes("## Current Work")) {
					summaryPrompts.push(lastUser);
					summaryModels.push(body.model);
					reply = body.model === "big" ? bigSummaryText : summaryText;
				} else if (lastUser.includes("pull request description")) {
					reply = "I reviewed the notes.";
				}
				return new Response(`${sse({ role: "assistant", content: "" })}${sse({ content: reply })}${sse({}, "stop")}data: [DONE]\n\n`, {
					headers: { "content-type": "text/event-stream" },
				});
			},
		});
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("checkpoint", {
			baseUrl: "http://127.0.0.1:${server.port}/v1", apiKey: "unused-test-key", api: "openai-completions",
			models: [{ id: "model", name: "model", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 131072, maxTokens: 4096 },
				{ id: "big", name: "big", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 200000, maxTokens: 4096 }]
		});`);
		const created = await createEngineSession(cwd, [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--no-tools", "--model", "checkpoint/model", "--no-session",
		]);
		const session = created.session;
		// Keep only the newest turn verbatim, so the first turn is what the summary replaces.
		cfgCompactionKeepRecentTokens.set(session.settings, 1);
		const notes = Array.from({ length: 300 }, (_, i) => `note ${i}: the parser rejects ';' as a separator`).join("\n");
		await created.runInContext(() => session.prompt(`Remember these notes:\n${notes}`));
		await session.waitForIdle();
		await created.runInContext(() => session.prompt("Now say hi."));
		await session.waitForIdle();
		return {
			session,
			summaryPrompts,
			summaryModels,
			compact: () => created.runInContext(() => session.compact(undefined, { mode: "soft" })),
			// The automatic flow (most-fired path), restricted to the LLM summary method.
			autoCompact: async () => {
				cfgCompactionMethodOrder.set(session.settings, ["soft"]);
				const errors: string[] = [];
				const unsubscribe = session.subscribe(event => {
					if (event.type === "auto_compaction_end" && event.errorMessage) errors.push(event.errorMessage);
				});
				try {
					await created.runInContext(() => session.runIdleCompaction());
				} finally {
					unsubscribe();
				}
				return errors;
			},
			dispose: async () => {
				await session.dispose();
				server.stop(true);
			},
		};
	}

	test("a summary smaller than its source is committed", async () => {
		const run = await runScenario("## Goal\n- Remember the parser separator notes.");
		try {
			const result = await run.compact();
			expect(result.summary).toContain("Remember the parser separator notes.");
			expect(run.session.messages.some(m => m.role === "compactionSummary")).toBe(true);
			// The summarizer received Ombra's checkpoint template, not the upstream one.
			expect(run.summaryPrompts).toHaveLength(1);
			expect(run.summaryPrompts[0]).toContain("## Next Steps");
			expect(run.summaryPrompts[0]).toContain("NEVER call tools");
		} finally {
			await run.dispose();
		}
	}, 60_000);

	test("a summary that does not shrink its source fails and leaves history intact", async () => {
		const bloated = `## Goal\n${Array.from({ length: 2000 }, (_, i) => `- restated note ${i} in full detail`).join("\n")}`;
		const run = await runScenario(bloated);
		try {
			const before = run.session.messages.length;
			await expect(run.compact()).rejects.toThrow("Compaction summary is not smaller than the history it replaces");
			// Every candidate was tried before the failure was reported.
			expect(run.summaryModels).toEqual(["model", "big"]);
			expect(run.session.messages.some(m => m.role === "compactionSummary")).toBe(false);
			expect(run.session.messages.length).toBe(before);
		} finally {
			await run.dispose();
		}
	}, 60_000);

	test("manual compaction tries the next candidate model after a non-shrinking summary", async () => {
		const bloated = `## Goal\n${Array.from({ length: 2000 }, (_, i) => `- restated note ${i} in full detail`).join("\n")}`;
		const run = await runScenario(bloated, "## Goal\n- Remember the parser separator notes.");
		try {
			const result = await run.compact();
			expect(run.summaryModels).toEqual(["model", "big"]);
			expect(result.summary).toContain("Remember the parser separator notes.");
			expect(run.session.messages.some(m => m.role === "compactionSummary")).toBe(true);
		} finally {
			await run.dispose();
		}
	}, 60_000);

	test("automatic compaction reports a non-shrinking summary as failed and commits nothing", async () => {
		const bloated = `## Goal\n${Array.from({ length: 2000 }, (_, i) => `- restated note ${i} in full detail`).join("\n")}`;
		const run = await runScenario(bloated);
		try {
			const before = run.session.messages.length;
			const errors = await run.autoCompact();
			expect(run.summaryPrompts.length).toBeGreaterThan(0);
			expect(errors.join("\n")).toContain("Compaction summary is not smaller than the history it replaces");
			expect(run.session.messages.some(m => m.role === "compactionSummary")).toBe(false);
			expect(run.session.messages.length).toBe(before);
		} finally {
			await run.dispose();
		}
	}, 60_000);
});

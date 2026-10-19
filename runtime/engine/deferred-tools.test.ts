import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { createEngineSession } from "./session-factory";

// K5 (runtime/harness-bench, Claude Code `defer_loading`): `tools.deferred` moves named built-ins — even essential
// or pinned ones — out of the request's tools array into the xd:// catalog; read and write cannot be deferred.
describe("tools.deferred", () => {
	const testRoot = path.join(os.tmpdir(), "ombra-tests", `run-${process.pid}`, "engine-deferred-tools");
	beforeAll(() => mkdir(testRoot, { recursive: true }));
	afterAll(() => rm(testRoot, { recursive: true, force: true }));

	test("deferred tools leave the top level and stay dispatchable as xd:// devices", async () => {
		const cwd = await mkdtemp(path.join(testRoot, "run-"));
		const extension = path.join(cwd, "provider.ts");
		await writeFile(extension, `export default api => api.registerProvider("deferred", {
			baseUrl: "http://127.0.0.1:1", apiKey: "unused-test-key", api: "openai-completions",
			models: [{ id: "model", name: "model", reasoning: false, input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 32768, maxTokens: 1024 }]
		});`);
		const overlay = path.join(cwd, "deferred.yml");
		await writeFile(overlay, "tools:\n  deferred: [task, eval, todo, web_search, read]\n");
		const created = await createEngineSession(cwd, [
			"--extension", extension, "--no-extensions", "--no-lsp", "--no-skills", "--no-rules", "--no-prewalk",
			"--model", "deferred/model", "--no-session", "--config", overlay,
		]);
		try {
			const topLevel = created.session.agent.state.tools.map(tool => tool.name);
			for (const name of ["task", "eval", "todo", "web_search"]) expect(topLevel).not.toContain(name);
			expect(topLevel).toEqual(expect.arrayContaining(["read", "write", "bash", "edit"]));
			const prompt = [created.session.systemPrompt].flat().join("\n");
			for (const name of ["task", "eval", "todo", "web_search"]) expect(prompt).toContain(`xd://${name} — `);
		} finally {
			await created.session.dispose();
		}
	}, 30_000);
});
